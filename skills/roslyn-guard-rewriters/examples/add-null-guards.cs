#!/usr/bin/dotnet run
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// Parameter-validation rules ported from dotnet/sdk (Microsoft.CodeAnalysis.NetAnalyzers, CA1062/CA1510).
#:package Microsoft.CodeAnalysis.CSharp@5.0.0

// Nullable-migration rewriter: CA1062-shaped null guards at public boundaries.
//
// For externally visible methods/constructors of externally visible types,
// finds reference-type parameters that are NOT annotated `?` and are
// dereferenced (member access, element access, delegate invocation) without
// a prior null check, and inserts `ArgumentNullException.ThrowIfNull(p);`
// at the top of the body — after any `base(...)`/`this(...)` constructor
// initializer, since those already run before the body regardless.
//
// It also converts the hand-written CA1510 shape
//   if (p == null) throw new ArgumentNullException(nameof(p));
//   if (p is null) throw new ArgumentNullException(nameof(p));
// into `ArgumentNullException.ThrowIfNull(p);` wherever it finds it in a
// scanned body, whether or not that parameter also needed a *new* guard.
//
// Deliberately SKIPS / reports-only, rather than mechanically "fixing":
//   - parameters annotated `?`                       -- already opted into null
//   - parameters already validated before their first use (see "What this
//     tool checks" in SKILL.md for the exact patterns recognised)
//   - value types, bare type parameters without a class constraint          -- not
//     reference types, `ArgumentNullException.ThrowIfNull` isn't meaningful
//   - overrides of a sealed type's method chain that never leaves the
//     assembly                                        -- mirrors CA1062's own
//                                                         bail-out; see SKILL.md
//   - iterator methods (a `yield` anywhere in the method's own body)        -- a
//     guard inside an iterator doesn't run until the first `MoveNext()`,
//     not at call time, so it changes the method's contract; these are
//     REPORTED as "needs manual wrapper", never rewritten
//   - methods/constructors with no body at all (abstract/extern/partial
//     declaration without an implementation)
//
// Async methods ARE guarded normally: the synchronous prologue of an async
// method runs eagerly when it's called (before the first `await` suspends
// it), so a guard at the top of the body throws at call time exactly like a
// non-async method. See SKILL.md for why this differs from iterators.
//
// This is a conservative, syntax + semantic-model approximation of CA1062,
// not a reimplementation of its points-to/dataflow analysis. See SKILL.md,
// "Adding null guards at public boundaries", for exactly what it does and
// does not catch compared to the real analyzer. No analyzer is required to
// run this tool; the post-rewrite check is build + tests + an idempotent
// re-run (see SKILL.md, "Recommended order") — enabling the real CA1062 is
// an optional extra cross-check, never a prerequisite.
//
// Run:  dotnet run add-null-guards.cs -- /path/to/src
// Dry run (default) prints CA1062-style findings: file:line + what guard it
// would add / which if-throw it would convert. Nothing is written.
//   dotnet run add-null-guards.cs -- /path/to/src --write

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: dotnet run add-null-guards.cs -- <src-folder> [--write]");
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

var compilation = CSharpCompilation.Create(
    "NullGuardScan",
    trees.Select(t => t.Tree),
    GetReferences(),
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

var changedFiles = 0;
var totalGuards = 0;
var totalConversions = 0;
var totalManualWrapper = 0;

foreach (var (path, tree) in trees)
{
    var model = compilation.GetSemanticModel(tree);
    var root = await tree.GetRootAsync();

    var rewriter = new NullGuardRewriter(model, path);
    var newRoot = rewriter.Visit(root);

    foreach (var finding in rewriter.Findings)
    {
        Console.WriteLine(finding);
    }

    totalGuards += rewriter.GuardsAdded;
    totalConversions += rewriter.IfThrowsConverted;
    totalManualWrapper += rewriter.ManualWrapperCount;

    if (newRoot == root)
    {
        continue;
    }

    changedFiles++;
    if (write)
    {
        await File.WriteAllTextAsync(path, newRoot!.ToFullString());
        Console.WriteLine($"rewrote {path}");
    }
    else
    {
        Console.WriteLine($"would rewrite {path}");
    }
}

Console.WriteLine();
Console.WriteLine($"{changedFiles} file(s) {(write ? "rewritten" : "would change")}.");
Console.WriteLine($"{totalGuards} new guard(s), {totalConversions} if-throw conversion(s), {totalManualWrapper} iterator finding(s) needing a manual wrapper.");
if (!write && (totalGuards > 0 || totalConversions > 0))
{
    Console.WriteLine("re-run with --write to apply, then build, run tests, and re-run this tool to confirm idempotency (see SKILL.md).");
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

// Top-level rewriter: dispatches to methods and constructors. Nothing else
// (properties, indexers, operators, local functions, lambdas) is in scope —
// see SKILL.md for why the boundary is drawn there.
sealed class NullGuardRewriter(SemanticModel model, string path) : CSharpSyntaxRewriter
{
    public List<string> Findings { get; } = [];
    public int GuardsAdded { get; private set; }
    public int IfThrowsConverted { get; private set; }
    public int ManualWrapperCount { get; private set; }

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node) => RewriteMember(node);

    public override SyntaxNode? VisitConstructorDeclaration(ConstructorDeclarationSyntax node) => RewriteMember(node);

    private SyntaxNode RewriteMember(BaseMethodDeclarationSyntax node)
    {
        if (model.GetDeclaredSymbol(node) is not IMethodSymbol symbol)
        {
            return node;
        }

        if (!IsExternallyVisible(symbol))
        {
            return node;
        }

        if (symbol.Parameters.IsEmpty || !symbol.Parameters.Any(p => p.Type.IsReferenceType))
        {
            return node;
        }

        // Mirrors CA1062's own bail-out: a protected override of a sealed type
        // whose entire overridden chain never leaves this assembly can't
        // actually be reached from outside it.
        if (symbol.IsOverride && symbol.ContainingType.IsSealed && AllOverriddenInSameAssembly(symbol))
        {
            return node;
        }

        var candidates = symbol.Parameters
            .Where(p => p.Type.IsReferenceType && p.NullableAnnotation != NullableAnnotation.Annotated)
            .ToList();
        if (candidates.Count == 0)
        {
            return node;
        }

        var body = node.Body;
        var exprBody = node.ExpressionBody;
        if (body is null && exprBody is null)
        {
            return node; // abstract/extern/partial declaration with no implementation
        }

        // Iterator methods: a guard inside the body wouldn't run until the
        // first MoveNext(), not at call time. Report, don't rewrite.
        if (body != null && ContainsOwnYield(body))
        {
            foreach (var p in candidates)
            {
                if (HasHazardousUseBeforeValidation(body, p, model, out var hazardNode))
                {
                    ManualWrapperCount++;
                    Findings.Add(
                        $"{path}:{Line(hazardNode!)}  {Describe(symbol)}: parameter '{p.Name}' used without a null check, " +
                        "but method is an iterator (yield) — cannot guard eagerly; needs a manual non-iterator wrapper (see SKILL.md)");
                }
            }

            return node;
        }

        // Convert existing `if (p == null) throw new ArgumentNullException(nameof(p));`
        // (and the `is null` form) into ThrowIfNull calls first — these already
        // count as validation, and converting first means the hazard scan below
        // only needs to recognise ONE validation shape (the ThrowIfNull call).
        BlockSyntax? newBody = body;
        var converted = 0;
        if (body != null)
        {
            var ifRewriter = new IfThrowToThrowIfNullRewriter(model, symbol);
            newBody = (BlockSyntax)ifRewriter.Visit(body)!;
            converted = ifRewriter.Converted;
            if (converted > 0)
            {
                Findings.Add($"{path}:{Line(body)}  {Describe(symbol)}: converted {converted} if-throw guard(s) to {GuardNames.ResolveThrowIfNull(model, node.SpanStart)}");
            }
        }

        SyntaxNode? scanRoot = (SyntaxNode?)newBody ?? exprBody;
        var toGuard = new List<IParameterSymbol>();
        if (scanRoot != null)
        {
            foreach (var p in candidates)
            {
                if (HasHazardousUseBeforeValidation(scanRoot, p, model, out var hazardNode))
                {
                    toGuard.Add(p);
                    Findings.Add(
                        $"{path}:{Line(hazardNode!)}  {Describe(symbol)}: parameter '{p.Name}' used without a null check " +
                        $"-> add {GuardNames.ResolveThrowIfNull(model, node.SpanStart)}({p.Name});");
                }
            }
        }

        if (toGuard.Count == 0 && converted == 0)
        {
            return node;
        }

        GuardsAdded += toGuard.Count;
        IfThrowsConverted += converted;

        var methodIndent = GetIndent(node);
        var stmtIndent = methodIndent + "    ";
        var guardStatements = toGuard
            .Select(p => (StatementSyntax)SyntaxFactory.ExpressionStatement(ThrowIfNullInvocation(model, node.SpanStart, p.Name)))
            .Select(s => s.WithLeadingTrivia(SyntaxFactory.Whitespace(stmtIndent)).WithTrailingTrivia(SyntaxFactory.EndOfLine("\n")))
            .ToList();

        BlockSyntax finalBody;
        if (newBody != null)
        {
            finalBody = newBody.WithStatements(newBody.Statements.InsertRange(0, guardStatements));
            return node.WithBody(finalBody);
        }

        // Expression-bodied member with a guard to add: convert to a block.
        var innerStatement = ToStatement(exprBody!.Expression, symbol)
            .WithLeadingTrivia(SyntaxFactory.Whitespace(stmtIndent))
            .WithTrailingTrivia(SyntaxFactory.EndOfLine("\n"));

        finalBody = SyntaxFactory.Block(
                SyntaxFactory.Token(SyntaxKind.OpenBraceToken).WithTrailingTrivia(SyntaxFactory.EndOfLine("\n")),
                SyntaxFactory.List(guardStatements.Append(innerStatement)),
                SyntaxFactory.Token(SyntaxKind.CloseBraceToken).WithLeadingTrivia(SyntaxFactory.Whitespace(methodIndent)))
            .WithLeadingTrivia(SyntaxFactory.EndOfLine("\n"), SyntaxFactory.Whitespace(methodIndent))
            .WithTrailingTrivia(node.GetTrailingTrivia());

        return node
            .WithExpressionBody(null)
            .WithSemicolonToken(default)
            .WithBody(finalBody);
    }

    // Emits the short name (`ArgumentNullException.ThrowIfNull`) when that
    // binds to the real System.ArgumentNullException at `position` — i.e. a
    // `using System;` (explicit, global, or implicit) is in scope there and
    // nothing shadows it — and only qualifies (`System.ArgumentNullException`,
    // or `global::System.ArgumentNullException` if `System` itself is
    // shadowed) when it doesn't. See `GuardNames.ResolveThrowIfNull` below.
    private static InvocationExpressionSyntax ThrowIfNullInvocation(SemanticModel model, int position, string paramName) =>
        SyntaxFactory.InvocationExpression(
            SyntaxFactory.ParseExpression(GuardNames.ResolveThrowIfNull(model, position)),
            SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(SyntaxFactory.IdentifierName(paramName)))));

    private static StatementSyntax ToStatement(ExpressionSyntax expr, IMethodSymbol symbol)
    {
        var trimmed = expr.WithoutTrivia();
        return symbol.MethodKind == MethodKind.Constructor || symbol.ReturnsVoid
            ? SyntaxFactory.ExpressionStatement(trimmed)
            : SyntaxFactory.ReturnStatement(
                SyntaxFactory.Token(SyntaxKind.ReturnKeyword).WithTrailingTrivia(SyntaxFactory.Space),
                trimmed,
                SyntaxFactory.Token(SyntaxKind.SemicolonToken));
    }

    private static string Describe(IMethodSymbol symbol) =>
        symbol.MethodKind == MethodKind.Constructor ? $"{symbol.ContainingType.Name}..ctor" : symbol.Name;

    private static string GetIndent(SyntaxNode node)
    {
        var leading = node.GetLeadingTrivia().ToFullString();
        var lastNewline = leading.LastIndexOf('\n');
        return lastNewline >= 0 ? leading[(lastNewline + 1)..] : leading;
    }

    private static int Line(SyntaxNode node) =>
        node.SyntaxTree.GetLineSpan(node.Span).StartLinePosition.Line + 1;

    private static bool ContainsOwnYield(BlockSyntax body) =>
        body.DescendantNodes(descendIntoChildren: n => n is not (LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax))
            .OfType<YieldStatementSyntax>()
            .Any();

    private static bool AllOverriddenInSameAssembly(IMethodSymbol symbol)
    {
        var overridden = symbol.OverriddenMethod;
        while (overridden != null)
        {
            if (!SymbolEqualityComparer.Default.Equals(overridden.ContainingAssembly, symbol.ContainingAssembly))
            {
                return false;
            }

            overridden = overridden.OverriddenMethod;
        }

        return true;
    }

    private static bool IsExternallyVisible(ISymbol symbol)
    {
        for (ISymbol? s = symbol; s is not null; s = s.ContainingSymbol)
        {
            if (s is INamespaceSymbol)
            {
                break;
            }

            switch (s.DeclaredAccessibility)
            {
                case Accessibility.Public:
                case Accessibility.Protected:
                case Accessibility.ProtectedOrInternal:
                    continue;
                default:
                    return false;
            }
        }

        return true;
    }

    // Linear, span-ordered approximation of CA1062's flow analysis (see
    // SKILL.md for exactly how this differs): walks the method's own
    // top-level code (not descending into nested lambdas/local functions,
    // whose parameter usage doesn't reflect what runs eagerly at call time)
    // in document order, and reports the first dereference of `parameter`
    // that occurs before any recognised validation of it.
    private static bool HasHazardousUseBeforeValidation(SyntaxNode root, IParameterSymbol parameter, SemanticModel model, out SyntaxNode? hazardNode)
    {
        hazardNode = null;
        var validated = false;

        foreach (var node in root.DescendantNodesAndSelf(descendIntoChildren: n => n is not (LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax)))
        {
            if (validated)
            {
                continue;
            }

            switch (node)
            {
                case InvocationExpressionSyntax inv when IsThrowIfNullCall(inv, parameter, model):
                    validated = true;
                    continue;
                case BinaryExpressionSyntax bin when IsNullCoalesceThrow(bin, parameter, model):
                    validated = true;
                    continue;
                case IdentifierNameSyntax id when IsParam(id, parameter, model) && IsHazardousUse(id):
                    hazardNode = id;
                    return true;
            }
        }

        return false;
    }

    private static bool IsThrowIfNullCall(InvocationExpressionSyntax inv, IParameterSymbol parameter, SemanticModel model)
    {
        if (inv.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "ThrowIfNull" })
        {
            return false;
        }

        if (inv.ArgumentList.Arguments.Count == 0)
        {
            return false;
        }

        return IsParam(inv.ArgumentList.Arguments[0].Expression, parameter, model);
    }

    private static bool IsNullCoalesceThrow(BinaryExpressionSyntax bin, IParameterSymbol parameter, SemanticModel model) =>
        bin.IsKind(SyntaxKind.CoalesceExpression) && bin.Right is ThrowExpressionSyntax && IsParam(bin.Left, parameter, model);

    private static bool IsParam(ExpressionSyntax expr, IParameterSymbol parameter, SemanticModel model)
    {
        if (expr is not IdentifierNameSyntax id)
        {
            return false;
        }

        try
        {
            return SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(id).Symbol, parameter);
        }
        catch (ArgumentException)
        {
            // A synthetic node from the if-throw -> ThrowIfNull conversion pass
            // earlier in this same run — not part of the tree the model was
            // built from. It was generated directly from parameter.Name, so a
            // text match is exact, not a heuristic.
            return id.Identifier.Text == parameter.Name;
        }
    }

    // Mirrors CA1062's IsHazardousIfNull: a member access, element access, or
    // delegate invocation where the parameter is the receiver is hazardous;
    // `p?.Foo` (the ConditionalAccessExpression's own Expression) is not —
    // it's already null-safe by construction.
    private static bool IsHazardousUse(IdentifierNameSyntax id) => id.Parent switch
    {
        MemberAccessExpressionSyntax ma => ma.Expression == id,
        ElementAccessExpressionSyntax ea => ea.Expression == id,
        ConditionalAccessExpressionSyntax => false,
        InvocationExpressionSyntax inv => inv.Expression == id,
        // foreach (x in p) calls p.GetEnumerator() under the hood — hazardous
        // even though there's no MemberAccessExpressionSyntax in the syntax.
        ForEachStatementSyntax fe => fe.Expression == id,
        _ => false,
    };
}

// Converts the CA1510 shape into a ThrowIfNull call. Does not descend into
// nested lambdas/local functions — an if-throw there guards a different
// (possibly shadowed) scope, not the enclosing method's eager prologue.
sealed class IfThrowToThrowIfNullRewriter(SemanticModel model, IMethodSymbol method) : CSharpSyntaxRewriter
{
    public int Converted { get; private set; }

    public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node) => node;
    public override SyntaxNode? VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node) => node;
    public override SyntaxNode? VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node) => node;
    public override SyntaxNode? VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node) => node;

    public override SyntaxNode? VisitIfStatement(IfStatementSyntax node)
    {
        var visited = (IfStatementSyntax)base.VisitIfStatement(node)!;
        if (visited.Else != null)
        {
            return visited; // an else branch is out of scope for this mechanical conversion
        }

        if (!TryMatch(visited, out var paramName))
        {
            return visited;
        }

        Converted++;
        return SyntaxFactory.ExpressionStatement(
                SyntaxFactory.InvocationExpression(
                    SyntaxFactory.ParseExpression(GuardNames.ResolveThrowIfNull(model, visited.SpanStart)),
                    SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(SyntaxFactory.IdentifierName(paramName))))))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
            .WithLeadingTrivia(visited.GetLeadingTrivia())
            .WithTrailingTrivia(visited.GetTrailingTrivia());
    }

    private bool TryMatch(IfStatementSyntax ifStmt, out string paramName)
    {
        paramName = "";

        var stmt = ifStmt.Statement is BlockSyntax block
            ? (block.Statements.Count == 1 ? block.Statements[0] : null)
            : ifStmt.Statement;

        if (stmt is not ThrowStatementSyntax { Expression: ObjectCreationExpressionSyntax oce })
        {
            return false;
        }

        if (model.GetTypeInfo(oce).Type is not { Name: "ArgumentNullException" })
        {
            return false;
        }

        if (oce.ArgumentList is not { Arguments: [var arg0] })
        {
            return false;
        }

        if (arg0.Expression is not InvocationExpressionSyntax
            {
                Expression: IdentifierNameSyntax { Identifier.Text: "nameof" },
                ArgumentList.Arguments: [var nameofArg],
            })
        {
            return false;
        }

        if (nameofArg.Expression is not IdentifierNameSyntax paramId)
        {
            return false;
        }

        if (model.GetSymbolInfo(paramId).Symbol is not IParameterSymbol ps ||
            !method.Parameters.Contains(ps, SymbolEqualityComparer.Default))
        {
            return false;
        }

        if (!TestsParameterForNull(ifStmt.Condition, ps, model))
        {
            return false;
        }

        paramName = ps.Name;
        return true;
    }

    private static bool TestsParameterForNull(ExpressionSyntax expr, IParameterSymbol parameter, SemanticModel model) => expr switch
    {
        BinaryExpressionSyntax { RawKind: (int)SyntaxKind.EqualsExpression } bin =>
            (IsParam(bin.Left, parameter, model) && IsNullLiteral(bin.Right)) ||
            (IsParam(bin.Right, parameter, model) && IsNullLiteral(bin.Left)),
        IsPatternExpressionSyntax { Pattern: ConstantPatternSyntax { Expression: var pe } } ip when IsNullLiteral(pe) =>
            IsParam(ip.Expression, parameter, model),
        _ => false,
    };

    private static bool IsNullLiteral(ExpressionSyntax expr) => expr is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NullLiteralExpression };

    private static bool IsParam(ExpressionSyntax expr, IParameterSymbol parameter, SemanticModel model) =>
        expr is IdentifierNameSyntax id && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(id).Symbol, parameter);
}

// Shared by both rewriters above: resolves the type name to emit so the output
// reads like the stock CA1510 fixer's — a short name
// (`ArgumentNullException.ThrowIfNull`) when that binds to the real
// System.ArgumentNullException at the insertion point (honouring whatever
// `using`s — explicit, global, or implicit — are in scope there), qualified
// with the namespace when it doesn't (no `using System;`), and `global::`
// qualified only if `System` itself is shadowed at that point. Resolved via
// speculative binding — not a text check for `using System;` — because usings,
// aliases, and local-type shadowing all affect what a bare identifier means at
// a given position, and only the binder actually knows.
static class GuardNames
{
    public static string ResolveThrowIfNull(SemanticModel model, int position)
    {
        var type = model.Compilation.GetTypeByMetadataName("System.ArgumentNullException");
        if (type is null)
        {
            // Shouldn't happen (ArgumentNullException has existed forever),
            // but fail toward a name that's always unambiguous.
            return "global::System.ArgumentNullException.ThrowIfNull";
        }

        var shortInfo = SpeculativeBind(model, position, type.Name);
        if (SymbolEqualityComparer.Default.Equals(shortInfo.Symbol, type))
        {
            return $"{type.Name}.ThrowIfNull";
        }

        var ns = type.ContainingNamespace.ToDisplayString();
        var namespaceHeadInfo = SpeculativeBind(model, position, ns.Split('.')[0]);
        return namespaceHeadInfo.Symbol is INamespaceSymbol
            ? $"{ns}.{type.Name}.ThrowIfNull"
            : $"global::{ns}.{type.Name}.ThrowIfNull";
    }

    private static SymbolInfo SpeculativeBind(SemanticModel model, int position, string identifier)
    {
        try
        {
            return model.GetSpeculativeSymbolInfo(position, SyntaxFactory.IdentifierName(identifier), SpeculativeBindingOption.BindAsExpression);
        }
        catch (ArgumentException)
        {
            // Position isn't a valid speculative-binding location (shouldn't
            // happen for a position inside a member body) — fail safe to
            // "doesn't bind" rather than throw out of a rewrite pass.
            return default;
        }
    }
}
