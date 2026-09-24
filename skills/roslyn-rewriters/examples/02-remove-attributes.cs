#!/usr/bin/dotnet run
#:package Microsoft.CodeAnalysis.CSharp@5.0.0

// Syntax-only rewrite. Strips a configured set of attributes off every method,
// dropping now-empty attribute lists while carrying their comments/whitespace
// across so you don't leave blank-line craters or eat a comment. No semantic
// model needed — attribute names are right there in the syntax.
//
// Run:  dotnet run 02-remove-attributes.cs -- /path/to/src
// Dry run (default): prints what it WOULD change. Add --write to edit files.
//   dotnet run 02-remove-attributes.cs -- /path/to/src --write
//

using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: dotnet run 02-remove-attributes.cs -- <src-folder> [--write]");
    return 1;
}

var srcFolder = args[0];
var write = args.Contains("--write");

// Match by simple-name prefix so `Http`, `HttpGet`, `[Authorize(...)]` all hit.
string[] attributesToRemove =
[
    "Http", "Authorize", "AllowAnonymous", "Route", "Consumes", "EnableCors",
];

var changed = 0;
foreach (var path in EnumerateSource(srcFolder))
{
    var original = await File.ReadAllTextAsync(path);
    var root = await CSharpSyntaxTree.ParseText(original).GetRootAsync();

    var newRoot = new AttributeStripper(attributesToRemove).Visit(root);
    if (newRoot == root)
    {
        continue;   // transform didn't touch this file — leave it completely alone
    }

    changed++;
    if (write)
    {
        // Normalize to LF + no trailing whitespace + no BOM, like the original tool.
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

Console.WriteLine($"\n{changed} file(s) {(write ? "rewritten" : "would change")}.");
if (!write && changed > 0)
{
    Console.WriteLine("re-run with --write to apply, then `dotnet build` + tests.");
}

return 0;

static IEnumerable<string> EnumerateSource(string root) =>
    Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
        .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                    !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

// A rewriter visits each node and returns a replacement; everything it doesn't
// touch (and all trivia) is preserved exactly.
sealed class AttributeStripper(string[] namesToRemove) : CSharpSyntaxRewriter
{
    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        if (node.AttributeLists.Count == 0)
        {
            return node;
        }

        var keptLists = new List<AttributeListSyntax>();
        var rescuedTrivia = new List<SyntaxTrivia>();

        foreach (var list in node.AttributeLists)
        {
            var kept = list.Attributes.Where(a => !ShouldRemove(a)).ToList();
            if (kept.Count == list.Attributes.Count)
            {
                keptLists.Add(list);                                   // untouched list
            }
            else if (kept.Count > 0)
            {
                keptLists.Add(list.WithAttributes(SyntaxFactory.SeparatedList(kept)));
            }
            else
            {
                // Whole list is going away — rescue any comments on it (drop the
                // pure-whitespace trivia so we don't accumulate blank lines).
                rescuedTrivia.AddRange(list.GetLeadingTrivia()
                    .Where(t => !t.IsKind(SyntaxKind.WhitespaceTrivia) &&
                                !t.IsKind(SyntaxKind.EndOfLineTrivia)));
            }
        }

        var newNode = node.WithAttributeLists(SyntaxFactory.List(keptLists));
        if (rescuedTrivia.Count > 0)
        {
            newNode = newNode.WithLeadingTrivia(
                newNode.GetLeadingTrivia().InsertRange(0, rescuedTrivia));
        }

        return newNode;
    }

    private bool ShouldRemove(AttributeSyntax attr)
    {
        var name = attr.Name.ToString();
        return namesToRemove.Any(name.Contains);
    }
}
