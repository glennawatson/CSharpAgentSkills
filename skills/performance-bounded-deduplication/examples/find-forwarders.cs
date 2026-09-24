#:package Microsoft.CodeAnalysis.CSharp@4.14.0

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Lists methods whose whole body is one call to a static member of another type, passing the method's own
// parameters (in any order) plus at most constants, lambdas or member accesses of other types. Reports the
// number of call sites of each forwarder by simple name within the file set, and whether every argument is a
// bare parameter (a pure forwarder) or some are extra per-caller arguments.
// Usage: find-forwarders <root> [--exclude-tests]
var root = Path.GetFullPath(args[0]);
var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
    .Where(p => !p.Contains("/obj/") && !p.Contains("/bin/") && !p.Contains("/tests/") && !p.Contains("/benchmarks/"))
    .ToList();
var trees = files.Select(f => (Path: f, Root: CSharpSyntaxTree.ParseText(File.ReadAllText(f)).GetRoot())).ToList();

var nameCounts = new Dictionary<string, int>(StringComparer.Ordinal);
foreach (var (_, r) in trees)
{
    foreach (var name in r.DescendantNodes().OfType<SimpleNameSyntax>())
    {
        if (name.Parent is MethodDeclarationSyntax) continue;
        nameCounts[name.Identifier.ValueText] = nameCounts.GetValueOrDefault(name.Identifier.ValueText) + 1;
    }
}

foreach (var (path, r) in trees)
{
    foreach (var method in r.DescendantNodes().OfType<MethodDeclarationSyntax>())
    {
        if (method.Modifiers.Any(SyntaxKind.OverrideKeyword) || method.ExplicitInterfaceSpecifier is not null) continue;
        var expression = method.ExpressionBody?.Expression
            ?? (method.Body is { Statements: [ReturnStatementSyntax { Expression: { } returned }] } ? returned
                : method.Body is { Statements: [ExpressionStatementSyntax { Expression: { } statement }] } ? statement : null);
        if (expression is not InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax or MemberAccessExpressionSyntax } target } invocation) continue;
        var owner = method.FirstAncestorOrSelf<TypeDeclarationSyntax>()?.Identifier.ValueText;
        var receiver = target.Expression.ToString();
        if (receiver == owner || receiver.StartsWith("context", StringComparison.Ordinal) || char.IsLower(receiver[0])) continue;
        var parameters = method.ParameterList.Parameters.Select(p => p.Identifier.ValueText).ToHashSet(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var extras = 0;
        var reject = false;
        foreach (var argument in invocation.ArgumentList.Arguments)
        {
            if (argument.Expression is IdentifierNameSyntax id && parameters.Contains(id.Identifier.ValueText)) { used.Add(id.Identifier.ValueText); continue; }
            if (argument.Expression.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Any(i => parameters.Contains(i.Identifier.ValueText))) { reject = true; break; }
            extras++;
        }

        if (reject || used.Count != parameters.Count) continue;
        var calls = nameCounts.GetValueOrDefault(method.Identifier.ValueText);
        var line = method.SyntaxTree.GetLineSpan(method.Span).StartLinePosition.Line + 1;
        Console.WriteLine($"{(extras == 0 ? "PURE " : $"EXTRA{extras}")}\tcalls={calls}\t{Path.GetRelativePath(root, path)}:{line}\t{owner}.{method.Identifier.ValueText} -> {target}");
    }
}
