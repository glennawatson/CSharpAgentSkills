#!/usr/bin/dotnet run
#:package Microsoft.CodeAnalysis.CSharp@5.0.0

// Discover the PUBLIC API SURFACE of a codebase — every public/protected type and
// member — and flag the ones missing XML documentation. Useful for API review,
// spotting accidental `public`, and finding undocumented surface before a release.
// Syntax-only: no build, no semantic model, nothing mutated. Fast.
//
// Run:  dotnet run 01-api-surface.cs -- /path/to/src
//       dotnet run 01-api-surface.cs -- /path/to/src --undocumented-only
//
// Sibling of the roslyn-rewriters examples; same single-file `dotnet run` shape.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: dotnet run 01-api-surface.cs -- <src-folder> [--undocumented-only]");
    return 1;
}

var undocumentedOnly = args.Contains("--undocumented-only");
int types = 0, members = 0, undocumented = 0;

foreach (var path in EnumerateSource(args[0]))
{
    var root = await CSharpSyntaxTree.ParseText(await File.ReadAllTextAsync(path)).GetRootAsync();

    foreach (var type in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
    {
        // Top-level types default to `internal`; only surface explicitly public/protected ones.
        if (!IsVisible(type.Modifiers))
        {
            continue;
        }

        types++;
        var typeUndocumented = !HasDocComment(type);
        if (typeUndocumented)
        {
            undocumented++;
        }

        if (!undocumentedOnly || typeUndocumented)
        {
            Console.WriteLine($"{Namespace(type)}.{type.Identifier.Text}  [{Kind(type)}]" +
                              (typeUndocumented ? "  ⚠ undocumented" : ""));
        }

        // Members default to `private`; again, only explicit public/protected count as surface.
        foreach (var member in (type as TypeDeclarationSyntax)?.Members ?? [])
        {
            if (member is not MemberDeclarationSyntax m || !IsVisible(m.Modifiers))
            {
                continue;
            }

            members++;
            var memberUndocumented = !HasDocComment(m);
            if (memberUndocumented)
            {
                undocumented++;
            }

            if (!undocumentedOnly || memberUndocumented)
            {
                Console.WriteLine($"    {Describe(m)}" + (memberUndocumented ? "  ⚠ undocumented" : ""));
            }
        }
    }
}

Console.WriteLine($"\n{types} public type(s), {members} public member(s), {undocumented} undocumented.");
return 0;

// `public` is surface; `protected`/`protected internal` is surface to subclasses.
// `private protected` and bare `internal`/`private` are not public API.
static bool IsVisible(SyntaxTokenList modifiers)
{
    var hasPublic = modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword));
    var hasProtected = modifiers.Any(m => m.IsKind(SyntaxKind.ProtectedKeyword));
    var hasPrivate = modifiers.Any(m => m.IsKind(SyntaxKind.PrivateKeyword));
    return hasPublic || (hasProtected && !hasPrivate);   // exclude `private protected`
}

// `Keyword` is on TypeDeclarationSyntax (class/struct/interface/record); enums have their own.
static string Kind(BaseTypeDeclarationSyntax type) => type switch
{
    TypeDeclarationSyntax t => t.Keyword.Text,
    EnumDeclarationSyntax => "enum",
    _ => type.Kind().ToString(),
};

static bool HasDocComment(SyntaxNode node) =>
    node.GetLeadingTrivia().Any(t =>
        t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
        t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia));

static string Namespace(SyntaxNode node)
{
    for (var n = node.Parent; n is not null; n = n.Parent)
    {
        if (n is BaseNamespaceDeclarationSyntax ns)   // covers file-scoped and block namespaces
        {
            return ns.Name.ToString();
        }
    }

    return "<global>";
}

static string Describe(MemberDeclarationSyntax m) => m switch
{
    MethodDeclarationSyntax method => $"{method.Identifier.Text}() : {method.ReturnType}",
    PropertyDeclarationSyntax prop => $"{prop.Identifier.Text} {{ get; set; }} : {prop.Type}",
    ConstructorDeclarationSyntax ctor => $".ctor({ctor.ParameterList.Parameters.Count} param)",
    FieldDeclarationSyntax field => $"field {field.Declaration.Variables.First().Identifier.Text} : {field.Declaration.Type}",
    EventDeclarationSyntax ev => $"event {ev.Identifier.Text}",
    EventFieldDeclarationSyntax ef => $"event {ef.Declaration.Variables.First().Identifier.Text}",
    _ => m.Kind().ToString(),
};

static IEnumerable<string> EnumerateSource(string root) =>
    Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
        .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                    !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
