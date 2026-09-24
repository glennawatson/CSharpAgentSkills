#:package Microsoft.CodeAnalysis.CSharp@4.14.0

using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Removes static forwarding methods whose whole body calls a static member of another type with the method's
// own parameters in order, retargeting every reference to that member across the tree (production, tests and
// benchmarks) and deleting the forwarder. Generic forwarders whose target takes exactly their type parameters map
// each caller's type arguments onto the target. Overloaded names are skipped. Dry run unless --write.
// Usage: inline-forwarders <src root> <spec file: relative/path.cs|Method per line> [--write]
var root = Path.GetFullPath(args[0]);
var write = Array.IndexOf(args, "--write") >= 0;
var specs = File.ReadAllLines(args[1]).Where(l => l.Contains('|')).Select(l => l.Split('|')).ToList();
var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories).Where(p => !p.Contains("/obj/") && !p.Contains("/bin/")).ToList();
var texts = files.ToDictionary(f => f, File.ReadAllText, StringComparer.Ordinal);

foreach (var spec in specs)
{
    var ownerPath = Path.Combine(root, spec[0]);
    var methodName = spec[1];
    var ownerRoot = CSharpSyntaxTree.ParseText(texts[ownerPath]).GetRoot();
    var candidates = ownerRoot.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(m => m.Identifier.ValueText == methodName).ToList();
    if (candidates.Count != 1) { Console.WriteLine($"SKIP {spec[0]}|{methodName}: {candidates.Count} declarations"); continue; }
    var method = candidates[0];
    var ownerType = method.FirstAncestorOrSelf<TypeDeclarationSyntax>()!;
    if (ownerType.Members.OfType<MethodDeclarationSyntax>().Count(m => m.Identifier.ValueText == methodName) != 1 || !method.Modifiers.Any(SyntaxKind.StaticKeyword))
    {
        Console.WriteLine($"SKIP {spec[0]}|{methodName}: overloaded or instance");
        continue;
    }

    var body = method.ExpressionBody?.Expression
        ?? (method.Body is { Statements: [ReturnStatementSyntax { Expression: { } r }] } ? r
            : method.Body is { Statements: [ExpressionStatementSyntax { Expression: { } s }] } ? s : null);
    if (body is not InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax target } invocation)
    {
        Console.WriteLine($"SKIP {spec[0]}|{methodName}: body is not a member call");
        continue;
    }

    var parameters = method.ParameterList.Parameters;
    var arguments = invocation.ArgumentList.Arguments;
    var ordered = parameters.Count == arguments.Count;
    for (var i = 0; ordered && i < arguments.Count; i++)
    {
        ordered = arguments[i].Expression is IdentifierNameSyntax id
            && id.Identifier.ValueText == parameters[i].Identifier.ValueText
            && arguments[i].RefKindKeyword.Kind() == parameters[i].Modifiers.FirstOrDefault(m => m.IsKind(SyntaxKind.OutKeyword) || m.IsKind(SyntaxKind.RefKeyword)).Kind();
    }

    if (!ordered) { Console.WriteLine($"SKIP {spec[0]}|{methodName}: arguments are not the parameters in order"); continue; }

    // The target's type arguments must be exactly the forwarder's type parameters (or absent / closed).
    var methodTypeParameters = method.TypeParameterList?.Parameters.Select(p => p.Identifier.ValueText).ToList() ?? [];
    TypeArgumentListSyntax? closedTypeArguments = null;
    var mapTypeArguments = false;
    if (target.Name is GenericNameSyntax genericTarget)
    {
        var names = genericTarget.TypeArgumentList.Arguments.Select(a => a.ToString()).ToList();
        if (methodTypeParameters.Count > 0)
        {
            if (!names.SequenceEqual(methodTypeParameters)) { Console.WriteLine($"SKIP {spec[0]}|{methodName}: type arguments differ"); continue; }
            mapTypeArguments = true;
        }
        else
        {
            closedTypeArguments = genericTarget.TypeArgumentList;
        }
    }
    else if (methodTypeParameters.Count > 0)
    {
        Console.WriteLine($"SKIP {spec[0]}|{methodName}: generic forwarder over a non-generic target");
        continue;
    }

    var receiverText = target.Expression.ToString();
    var targetName = target.Name.Identifier.ValueText;
    var ownerName = ownerType.Identifier.ValueText;
    var total = 0;

    foreach (var path in files)
    {
        var text = texts[path];
        if (!text.Contains(methodName, StringComparison.Ordinal)) continue;
        var tree = CSharpSyntaxTree.ParseText(text);
        var fileRoot = tree.GetRoot();
        var isOwnerFile = path == ownerPath;
        var replacements = new Dictionary<SyntaxNode, SyntaxNode>();

        foreach (var name in fileRoot.DescendantNodes().OfType<SimpleNameSyntax>())
        {
            if (name.Identifier.ValueText != methodName || name.Parent is MethodDeclarationSyntax) continue;
            SyntaxNode replaced;
            if (name.Parent is MemberAccessExpressionSyntax access && access.Name == name)
            {
                var receiver = access.Expression.ToString();
                if (receiver != ownerName && !receiver.EndsWith("." + ownerName, StringComparison.Ordinal)) continue;
                replaced = access;
            }
            else if (name.Parent is MemberAccessExpressionSyntax || name.Parent is MemberBindingExpressionSyntax || name.Parent is QualifiedNameSyntax)
            {
                continue;
            }
            else
            {
                if (!isOwnerFile || name.FirstAncestorOrSelf<TypeDeclarationSyntax>(t => t.Identifier.ValueText == ownerName) is null) continue;
                replaced = name;
            }

            TypeArgumentListSyntax? typeArguments = closedTypeArguments;
            if (mapTypeArguments)
            {
                typeArguments = name is GenericNameSyntax callerGeneric ? callerGeneric.TypeArgumentList : null;
            }

            var newName = typeArguments is null ? targetName : targetName + typeArguments.ToString();
            var expression = SyntaxFactory.ParseExpression(receiverText + "." + newName).WithTriviaFrom(replaced);
            replacements[replaced] = expression;
        }

        SyntaxNode updated = fileRoot;
        if (replacements.Count > 0)
        {
            updated = updated.ReplaceNodes(replacements.Keys, (original, _) => replacements[original]);
            total += replacements.Count;
        }

        if (isOwnerFile)
        {
            var declaration = updated.DescendantNodes().OfType<MethodDeclarationSyntax>().First(m => m.Identifier.ValueText == methodName && m.FirstAncestorOrSelf<TypeDeclarationSyntax>()!.Identifier.ValueText == ownerName);
            updated = updated.RemoveNode(declaration, SyntaxRemoveOptions.KeepNoTrivia)!;
        }

        if (updated != fileRoot)
        {
            texts[path] = updated.ToFullString();
            Console.WriteLine($"  {Path.GetRelativePath(root, path)}: {replacements.Count} reference(s){(isOwnerFile ? " + declaration removed" : string.Empty)}");
        }
    }

    Console.WriteLine($"OK {spec[0]}|{methodName} -> {receiverText}.{targetName}: {total} reference(s)");
}

if (write)
{
    foreach (var path in files)
    {
        var original = File.ReadAllText(path);
        if (!string.Equals(original, texts[path], StringComparison.Ordinal))
        {
            File.WriteAllText(path, texts[path], new UTF8Encoding(false));
        }
    }
}
