#!/usr/bin/dotnet run
#:package Microsoft.CodeAnalysis.CSharp@5.0.0

// Nullable-migration rewriter: the "provably returns null" mechanical case.
// A method whose body literally does `return null;` (or `=> null;`) has an
// implicit null contract whether or not the type says so. This makes it
// honest by adding `?` to the return type — nothing else.
//
// It deliberately SKIPS cases that need judgement, not proof:
//   - already-annotated return types (`string?`)            -- nothing to do
//   - value types (`int`, structs)                           -- `return null;` doesn't compile there
//   - a bare generic type parameter `T`                       -- nullability of T is a generic-contract
//                                                                decision (`T?` vs `where T : class` vs
//                                                                leave it), not a mechanical one
//   - `override` methods and interface implementations        -- changing the return type here is an
//                                                                API/contract change that ripples to
//                                                                every override/caller; a human has to
//                                                                decide whether the base/interface
//                                                                should widen too
//
// Uses a plain CSharpCompilation (this repo's trusted-platform assemblies as
// references) instead of MSBuildWorkspace, since the check only needs "is
// this a reference type / type parameter / interface implementation", not
// project-level build context. For a real multi-project migration, load the
// actual solution via MSBuildWorkspace (see roslyn-rewriters `03`) so the
// semantic model sees the real reference graph.
//
// Run:  dotnet run annotate-null-returns.cs -- /path/to/src
// Dry run (default): prints what it WOULD change. Add --write to apply.
//   dotnet run annotate-null-returns.cs -- /path/to/src --write

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: dotnet run annotate-null-returns.cs -- <src-folder> [--write]");
    return 1;
}

var srcFolder = args[0];
var write = args.Contains("--write");

var paths = EnumerateSource(srcFolder).ToList();
var trees = new List<(string Path, SyntaxTree Tree)>();
foreach (var path in paths)
{
    var text = await File.ReadAllTextAsync(path);
    trees.Add((path, CSharpSyntaxTree.ParseText(text, path: path)));
}

// A throwaway compilation just to get a semantic model — not a real build.
// References come from the trusted platform assemblies of the SDK running
// this script, which is enough to resolve BCL types like object/string/List<T>.
var compilation = CSharpCompilation.Create(
    "NullableMigrationScan",
    trees.Select(t => t.Tree),
    GetReferences(),
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

var changed = 0;
foreach (var (path, tree) in trees)
{
    var model = compilation.GetSemanticModel(tree);
    var root = await tree.GetRootAsync();

    var rewriter = new NullReturnAnnotator(model);
    var newRoot = rewriter.Visit(root);
    if (newRoot == root)
    {
        continue;   // nothing in this file qualified — leave it completely alone
    }

    changed++;
    if (write)
    {
        await File.WriteAllTextAsync(path, newRoot.ToFullString());
        Console.WriteLine($"rewrote {path} ({rewriter.Annotated} method(s))");
    }
    else
    {
        Console.WriteLine($"would rewrite {path} ({rewriter.Annotated} method(s))");
    }
}

Console.WriteLine($"\n{changed} file(s) {(write ? "rewritten" : "would change")}.");
if (!write && changed > 0)
{
    Console.WriteLine("re-run with --write to apply, then enable <Nullable>enable</Nullable> and build.");
}

return 0;

static IEnumerable<string> EnumerateSource(string root) =>
    Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
        .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                    !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

static IEnumerable<MetadataReference> GetReferences()
{
    var tpa = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
        ?? throw new InvalidOperationException("no trusted platform assemblies — run on the .NET SDK, not AOT.");
    return tpa.Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p));
}

// Adds `?` to a method's return type when the body provably returns a null
// literal and none of the "needs judgement" exclusions apply. Semantic model
// is required for three of the four checks below — this can't be done from
// syntax alone.
sealed class NullReturnAnnotator(SemanticModel model) : CSharpSyntaxRewriter
{
    public int Annotated { get; private set; }

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        if (!ReturnsNullLiteralDirectly(node) || !ShouldAnnotate(node, out var reason))
        {
            return base.VisitMethodDeclaration(node);
        }

        Annotated++;
        var newType = SyntaxFactory.NullableType(node.ReturnType.WithoutTrivia())
            .WithLeadingTrivia(node.ReturnType.GetLeadingTrivia())
            .WithTrailingTrivia(node.ReturnType.GetTrailingTrivia());

        Console.WriteLine($"  {node.Identifier.Text}: {reason}");
        return node.WithReturnType(newType);
    }

    // Syntax-only: does the method body/expression contain `return null;` in
    // its OWN control flow (not inside a nested local function or lambda,
    // whose return type is unrelated to the enclosing method's).
    private static bool ReturnsNullLiteralDirectly(MethodDeclarationSyntax node)
    {
        if (node.ExpressionBody is { Expression: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NullLiteralExpression } })
        {
            return true;
        }

        if (node.Body is null)
        {
            return false;
        }

        return node.Body.DescendantNodes(descendIntoChildren: n => n is not (LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax))
            .OfType<ReturnStatementSyntax>()
            .Any(r => r.Expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NullLiteralExpression });
    }

    // Semantic checks: the four exclusions that syntax alone can't answer.
    private bool ShouldAnnotate(MethodDeclarationSyntax node, out string reason)
    {
        reason = "";

        // Already `string?` etc. — nothing to do.
        if (node.ReturnType is NullableTypeSyntax)
        {
            return false;
        }

        if (model.GetDeclaredSymbol(node) is not IMethodSymbol symbol)
        {
            return false;
        }

        var returnType = symbol.ReturnType;

        // Value types: `return null;` wouldn't compile unless it's already
        // Nullable<T>/`?`, which the syntax check above already excludes.
        if (returnType.IsValueType)
        {
            return false;
        }

        // Bare `T` — nullability of a generic parameter is a contract
        // decision (`T?`, `where T : class`, or leave as-is), not mechanical.
        if (returnType.TypeKind == TypeKind.TypeParameter)
        {
            return false;
        }

        // `override` — changing this return type is a signature covariance
        // change; the base method (and every other override) needs the same
        // change decided deliberately, not mechanically per-override.
        if (symbol.IsOverride)
        {
            return false;
        }

        // Explicit or implicit interface implementation — same reasoning:
        // the interface's contract is what should change, if anything, and
        // that affects every other implementer.
        if (symbol.ExplicitInterfaceImplementations.Length > 0)
        {
            return false;
        }

        var containingType = symbol.ContainingType;
        foreach (var iface in containingType.AllInterfaces)
        {
            foreach (var member in iface.GetMembers().OfType<IMethodSymbol>())
            {
                if (SymbolEqualityComparer.Default.Equals(containingType.FindImplementationForInterfaceMember(member), symbol))
                {
                    return false;
                }
            }
        }

        reason = "return null; -> annotate return type ?";
        return true;
    }
}
