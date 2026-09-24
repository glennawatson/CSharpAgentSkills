#!/usr/bin/dotnet run
#:package Microsoft.CodeAnalysis.CSharp@5.0.0

// Syntax-only rewrite (no MSBuildWorkspace needed — every check below is name-based
// pattern matching over one class's own syntax tree, not cross-type semantics).
//
// Converts the classic non-thread-safe disposed-flag pattern:
//
//     private bool _disposed;
//     public void Dispose()
//     {
//         if (_disposed) return;
//         _disposed = true;
//         ...cleanup...
//     }
//     ...
//     if (_disposed) throw new ObjectDisposedException(nameof(Foo));
//
// into the lock-free Interlocked form:
//
//     private int _disposed;
//     public void Dispose()
//     {
//         if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0) return;
//         ...cleanup...
//     }
//     ...
//     ObjectDisposedException.ThrowIf(System.Threading.Volatile.Read(ref _disposed) != 0, this);
//
// Only rewrites a type when ALL of the following hold, otherwise it is reported and left alone:
//   - the flag field is `private bool <name>;` with no non-`false` initializer
//   - the field is assigned only inside that type's own Dispose() method (never elsewhere)
//   - Dispose()'s first two statements are exactly `if (<flag>) return;` then `<flag> = true;`
//   - the type does NOT declare an unsealed `protected virtual void Dispose(bool)` override
//     (the flag there commonly gates more than the two statements this tool recognizes, so
//     rewriting it could silently change what's guarded — report instead of guessing)
//
// Run:
//   dotnet run thread-safe-dispose.cs -- /path/to/src                    # dry run (default)
//   dotnet run thread-safe-dispose.cs -- /path/to/src --write            # apply
//   dotnet run thread-safe-dispose.cs -- /path/to/src --report-owned-fields
//
// --report-owned-fields is a separate, report-only pass (never rewrites): lists types that
// implement IDisposable, directly `new` a disposable into a field, but never reference that
// field inside their own Dispose()/Dispose(bool) body — the CA2213/CA1001 shape in miniature.
// It deliberately does not reproduce CA2213/CA2000's full points-to dataflow analysis; it is a
// syntactic heuristic meant to flag candidates for a human to look at, nothing more.

using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: dotnet run thread-safe-dispose.cs -- <src-folder> [--write] [--report-owned-fields]");
    return 1;
}

var srcFolder = args[0];
var write = args.Contains("--write");
var reportOwnedFields = args.Contains("--report-owned-fields");

if (reportOwnedFields)
{
    return RunOwnedFieldsReport(srcFolder);
}

var changed = 0;
var reported = 0;
foreach (var path in EnumerateSource(srcFolder))
{
    var original = await File.ReadAllTextAsync(path);
    var tree = CSharpSyntaxTree.ParseText(original);
    var root = (CompilationUnitSyntax)await tree.GetRootAsync();

    var rewriter = new ThreadSafeDisposeRewriter();
    var newRoot = (CompilationUnitSyntax)rewriter.Visit(root)!;

    foreach (var candidate in rewriter.ReportedTypes)
    {
        Console.WriteLine($"skip (not provably safe to rewrite): {path} — {candidate}");
        reported++;
    }

    if (newRoot == root || !rewriter.Rewrote)
    {
        continue;
    }

    changed++;
    if (write)
    {
        var text = string.Join('\n', newRoot.ToFullString().ReplaceLineEndings("\n")
            .Split('\n').Select(line => line.TrimEnd()));
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(false));
        Console.WriteLine($"rewrote {path}");
    }
    else
    {
        Console.WriteLine($"would rewrite {path}");
    }
}

Console.WriteLine($"{(write ? "rewrote" : "would rewrite")} {changed} file(s); {reported} type(s) reported, not rewritten.");
return 0;

static IEnumerable<string> EnumerateSource(string folder) =>
    Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)
        .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                 && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

static int RunOwnedFieldsReport(string folder)
{
    // Syntax-plus-symbol, not full dataflow: build one throwaway compilation over every file so
    // field types can be resolved against the real IDisposable interface (CA1001/CA2213's own
    // starting point — see DisposeAnalysisHelper.IsDisposable) instead of guessing from a type
    // name. What we deliberately do NOT reproduce is CA2213's points-to/dispose dataflow that
    // traces ownership through method calls — this only looks at a field initialized directly
    // with `new`/target-typed `new`, same as CA1001's own (simpler) syntactic check.
    var paths = EnumerateSource(folder).ToArray();
    var trees = paths.ToDictionary(p => p, p => CSharpSyntaxTree.ParseText(File.ReadAllText(p), path: p));
    var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator)
        .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p));
    // The real project may rely on SDK-generated ImplicitUsings (System, System.IO, ...) that
    // this throwaway compilation never sees — add the common ones as global usings so ordinary
    // BCL type names resolve without needing the target project's actual generated file.
    var implicitUsings = CSharpSyntaxTree.ParseText(
        "global using System;\nglobal using System.IO;\nglobal using System.Collections.Generic;\nglobal using System.Linq;\nglobal using System.Threading;\nglobal using System.Threading.Tasks;\n",
        path: "__ImplicitUsings.g.cs");
    var compilation = CSharpCompilation.Create("owned-fields-report", trees.Values.Append(implicitUsings), references,
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    var disposableType = compilation.GetTypeByMetadataName("System.IDisposable");

    var count = 0;
    foreach (var (path, tree) in trees)
    {
        var model = compilation.GetSemanticModel(tree);
        var root = tree.GetRoot();
        foreach (var classDecl in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            if (!ImplementsIDisposableByName(classDecl))
            {
                continue;
            }

            var disposeBodies = classDecl.Members.OfType<MethodDeclarationSyntax>()
                .Where(m => m.Identifier.ValueText == "Dispose")
                .Select(m => (SyntaxNode?)m.Body ?? m.ExpressionBody)
                .Where(b => b is not null)
                .ToArray();

            foreach (var field in classDecl.Members.OfType<FieldDeclarationSyntax>())
            {
                foreach (var v in field.Declaration.Variables)
                {
                    if (v.Initializer?.Value is not (ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax)) continue;

                    var fieldType = model.GetDeclaredSymbol(v) is IFieldSymbol fs ? fs.Type : null;
                    var isDisposable = fieldType is not null && disposableType is not null &&
                        (SymbolEqualityComparer.Default.Equals(fieldType, disposableType) ||
                         fieldType.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, disposableType)));
                    if (!isDisposable) continue;

                    var name = v.Identifier.ValueText;
                    var referencedInDispose = disposeBodies.Any(b =>
                        b!.DescendantNodes().OfType<IdentifierNameSyntax>().Any(id => id.Identifier.ValueText == name));

                    if (!referencedInDispose)
                    {
                        Console.WriteLine($"{path}: {classDecl.Identifier.ValueText}.{name} — owns a disposable field, never referenced in Dispose()");
                        count++;
                    }
                }
            }
        }
    }

    Console.WriteLine($"{count} candidate field(s) reported.");
    return 0;
}

static bool ImplementsIDisposableByName(ClassDeclarationSyntax classDecl) =>
    classDecl.BaseList?.Types.Any(t => t.Type.ToString() is "IDisposable" or "System.IDisposable") == true;

// A syntax-only rewriter: every check is name-based pattern matching within one class's own
// tree, deliberately avoiding a full semantic model (see roslyn-rewriters — step up to a
// workspace only when the change needs actual symbol resolution, which this doesn't).
sealed class ThreadSafeDisposeRewriter : CSharpSyntaxRewriter
{
    public bool Rewrote { get; private set; }

    public List<string> ReportedTypes { get; } = [];

    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        var (candidate, reason) = TryAnalyze(node);
        if (candidate is null)
        {
            if (reason is not null)
            {
                ReportedTypes.Add($"{node.Identifier.ValueText} — {reason}");
            }

            return base.VisitClassDeclaration(node);
        }

        if (HasUnsealedDisposeBoolOverride(node))
        {
            ReportedTypes.Add($"{node.Identifier.ValueText} — has a protected virtual Dispose(bool); rewrite could change what the flag guards");
            return base.VisitClassDeclaration(node);
        }

        Rewrote = true;
        return Rewrite(node, candidate);
    }

    private sealed record Candidate(FieldDeclarationSyntax Field, VariableDeclaratorSyntax Variable, MethodDeclarationSyntax Dispose, IfStatementSyntax GuardIf, ExpressionStatementSyntax SetTrue);

    /// <summary>Looks for the classic disposed-flag shape. Returns the rewritable candidate, or a
    /// human-readable disqualification reason when a field matches the flag shape but fails a
    /// safety precondition (nothing is returned at all when the type simply has no such field).</summary>
    private static (Candidate? Candidate, string? Reason) TryAnalyze(ClassDeclarationSyntax node)
    {
        foreach (var field in node.Members.OfType<FieldDeclarationSyntax>())
        {
            if (!field.Modifiers.Any(SyntaxKind.PrivateKeyword) || field.Modifiers.Any(SyntaxKind.ReadOnlyKeyword))
            {
                continue;
            }

            if (field.Declaration.Type is not PredefinedTypeSyntax { Keyword.ValueText: "bool" })
            {
                continue;
            }

            foreach (var variable in field.Declaration.Variables)
            {
                if (variable.Initializer is { Value: LiteralExpressionSyntax { Token.ValueText: "true" } })
                {
                    continue; // starts disposed — not the pattern we convert
                }

                var name = variable.Identifier.ValueText;
                var disposeMethod = node.Members.OfType<MethodDeclarationSyntax>().FirstOrDefault(m =>
                    m.Identifier.ValueText == "Dispose" &&
                    m.ParameterList.Parameters.Count == 0 &&
                    m.Modifiers.Any(SyntaxKind.PublicKeyword));

                if (disposeMethod?.Body is not { } body || body.Statements.Count < 2)
                {
                    continue;
                }

                if (body.Statements[0] is not IfStatementSyntax
                    {
                        Else: null,
                        Condition: IdentifierNameSyntax cond,
                        Statement: var thenStatement,
                    } ifStatement)
                {
                    continue;
                }

                if (cond.Identifier.ValueText != name || !IsReturnOnly(thenStatement))
                {
                    continue;
                }

                if (body.Statements[1] is not ExpressionStatementSyntax
                    {
                        Expression: AssignmentExpressionSyntax
                        {
                            Left: IdentifierNameSyntax setTarget,
                            Right: LiteralExpressionSyntax { Token.ValueText: "true" },
                        },
                    } setTrueStatement || setTarget.Identifier.ValueText != name)
                {
                    continue;
                }

                if (WrittenOutsideDispose(node, name, disposeMethod))
                {
                    return (null, $"field '{name}' matches the disposed-flag shape but is written outside Dispose() too; rewriting could change semantics elsewhere");
                }

                return (new Candidate(field, variable, disposeMethod, ifStatement, setTrueStatement), null);
            }
        }

        return (null, null);
    }

    private static bool IsReturnOnly(StatementSyntax statement) => statement switch
    {
        ReturnStatementSyntax { Expression: null } => true,
        BlockSyntax { Statements: [ReturnStatementSyntax { Expression: null }] } => true,
        _ => false,
    };

    private static bool WrittenOutsideDispose(ClassDeclarationSyntax node, string fieldName, MethodDeclarationSyntax disposeMethod)
    {
        foreach (var assignment in node.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (assignment.Left is not IdentifierNameSyntax id || id.Identifier.ValueText != fieldName)
            {
                continue;
            }

            if (!disposeMethod.Contains(assignment))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasUnsealedDisposeBoolOverride(ClassDeclarationSyntax node)
    {
        if (node.Modifiers.Any(SyntaxKind.SealedKeyword))
        {
            return false;
        }

        return node.Members.OfType<MethodDeclarationSyntax>().Any(m =>
            m.Identifier.ValueText == "Dispose" &&
            m.ParameterList.Parameters.Count == 1 &&
            m.ParameterList.Parameters[0].Type is PredefinedTypeSyntax { Keyword.ValueText: "bool" } &&
            m.Modifiers.Any(SyntaxKind.VirtualKeyword) &&
            m.Modifiers.Any(SyntaxKind.ProtectedKeyword));
    }

    private static ClassDeclarationSyntax Rewrite(ClassDeclarationSyntax node, Candidate c)
    {
        // 1. bool -> int on the flag field, dropping a `= false` initializer (0 is already the default).
        var newDeclaration = c.Field.Declaration
            .WithType(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword)).WithTriviaFrom(c.Field.Declaration.Type))
            .WithVariables(SyntaxFactory.SeparatedList(c.Field.Declaration.Variables.Select(v =>
                v.Identifier.ValueText == c.Variable.Identifier.ValueText ? v.WithInitializer(null) : v)));
        var newField = c.Field.WithDeclaration(newDeclaration);

        // 2. `if (_disposed) return; _disposed = true;` -> one Interlocked.Exchange guard.
        var fieldName = c.Variable.Identifier.ValueText;
        var newGuard = (IfStatementSyntax)SyntaxFactory.ParseStatement(
                $"if (System.Threading.Interlocked.Exchange(ref {fieldName}, 1) != 0) return;\n")
            .WithLeadingTrivia(c.GuardIf.GetLeadingTrivia())
            .WithTrailingTrivia(c.SetTrue.GetTrailingTrivia());

        // Build the new statement list in one pass over the ORIGINAL statements: chaining
        // SyntaxList.Replace(...) then .Remove(...) looks equivalent but isn't — Replace hands
        // back a list whose untouched siblings are fresh red-node wrappers, so the later
        // .Remove(c.SetTrue) no longer matches anything and silently leaves the old
        // `_disposed = true;` statement behind (now invalid once the field is an int).
        var rebuiltStatements = new List<StatementSyntax>();
        foreach (var statement in c.Dispose.Body!.Statements)
        {
            if (statement == c.SetTrue)
            {
                continue;
            }

            rebuiltStatements.Add(statement == c.GuardIf ? newGuard : statement);
        }

        var newBody = c.Dispose.Body.WithStatements(SyntaxFactory.List(rebuiltStatements));
        var newDispose = c.Dispose.WithBody(newBody);

        // 3. `if (_disposed) throw new ObjectDisposedException(...)` elsewhere -> ThrowIf.
        var guardRewriter = new ObjectDisposedGuardRewriter(fieldName);

        var newMembers = node.Members.Select(m =>
        {
            if (ReferenceEquals(m, c.Field)) return newField;
            if (ReferenceEquals(m, c.Dispose)) return newDispose;
            return (MemberDeclarationSyntax)guardRewriter.Visit(m)!;
        });

        return node.WithMembers(SyntaxFactory.List(newMembers));
    }
}

/// <summary>Rewrites `if (_disposed) throw new ObjectDisposedException(...);` to the throw-helper form.</summary>
sealed class ObjectDisposedGuardRewriter(string fieldName) : CSharpSyntaxRewriter
{
    public override SyntaxNode? VisitIfStatement(IfStatementSyntax node)
    {
        if (node.Else is null &&
            node.Condition is IdentifierNameSyntax cond && cond.Identifier.ValueText == fieldName &&
            TryGetThrowNew(node.Statement, out var throwArgs))
        {
            var argsText = throwArgs.Length == 0 ? "this" : $"this /* was: {throwArgs} */";
            return SyntaxFactory.ParseStatement(
                    $"ObjectDisposedException.ThrowIf(System.Threading.Volatile.Read(ref {fieldName}) != 0, {argsText});\n")
                .WithTriviaFrom(node);
        }

        return base.VisitIfStatement(node);
    }

    private static bool TryGetThrowNew(StatementSyntax statement, out string arguments)
    {
        arguments = string.Empty;
        var throwStatement = statement switch
        {
            ThrowStatementSyntax t => t,
            BlockSyntax { Statements: [ThrowStatementSyntax t] } => t,
            _ => null,
        };

        if (throwStatement?.Expression is not ObjectCreationExpressionSyntax { } creation ||
            creation.Type.ToString() is not ("ObjectDisposedException" or "System.ObjectDisposedException"))
        {
            return false;
        }

        arguments = creation.ArgumentList?.ToString() ?? string.Empty;
        return true;
    }
}
