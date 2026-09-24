#:package Microsoft.CodeAnalysis.CSharp@4.14.0

using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Finds duplicated runs of consecutive statements across production code. Each block's statement list is
// scanned with a window of --statements statements; a window is kept when it spans at least --min-tokens tokens.
// Tokens are canonicalised (identifiers -> $id, literals -> $lit) unless --exact. Windows are grouped by hash,
// overlapping windows from the same block are collapsed to the widest, and groups whose members all sit in the
// same member are dropped. Prints each group with file:line spans and the first member's text.
// Usage: detect-fragment-clones <root> [--statements 3] [--min-tokens 60] [--exact] [--top 60]
var root = Path.GetFullPath(args[0]);
var statements = IntArg("--statements", 3);
var minTokens = IntArg("--min-tokens", 60);
var exact = Array.IndexOf(args, "--exact") >= 0;
var top = IntArg("--top", 60);

var groups = new Dictionary<string, List<Window>>(StringComparer.Ordinal);
foreach (var path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
{
    if (path.Contains("/obj/") || path.Contains("/bin/") || path.Contains("/tests/") || path.Contains("/benchmarks/")) continue;
    var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path));
    foreach (var block in tree.GetRoot().DescendantNodes().OfType<BlockSyntax>())
    {
        var list = block.Statements;
        for (var start = 0; start + statements <= list.Count; start++)
        {
            var builder = new StringBuilder();
            var tokens = 0;
            for (var i = start; i < start + statements; i++)
            {
                foreach (var token in list[i].DescendantTokens())
                {
                    tokens++;
                    builder.Append(exact ? token.Text : token.Kind() switch
                    {
                        SyntaxKind.IdentifierToken => "$id",
                        SyntaxKind.NumericLiteralToken or SyntaxKind.StringLiteralToken or SyntaxKind.CharacterLiteralToken => "$lit",
                        _ => token.Text,
                    }).Append(' ');
                }
            }

            if (tokens < minTokens) continue;
            var member = block.FirstAncestorOrSelf<MemberDeclarationSyntax>();
            var memberKey = member is null ? path : $"{path}:{member.SpanStart}";
            var first = tree.GetLineSpan(list[start].Span).StartLinePosition.Line + 1;
            var last = tree.GetLineSpan(list[start + statements - 1].Span).EndLinePosition.Line + 1;
            var key = builder.ToString();
            if (!groups.TryGetValue(key, out var windows)) groups[key] = windows = [];
            windows.Add(new Window(Path.GetRelativePath(root, path), memberKey, first, last, tokens, list[start].ToString()));
        }
    }
}

var reported = groups.Values
    .Select(w => w.GroupBy(x => x.MemberKey).Select(g => g.First()).ToList())
    .Where(w => w.Count >= 2)
    .OrderByDescending(w => w[0].Tokens * (w.Count - 1))
    .ToList();

// Collapse windows nested in a larger reported window of the same member pair.
var seen = new HashSet<string>(StringComparer.Ordinal);
var printed = 0;
Console.WriteLine($"Fragment clone groups: {reported.Count} (window {statements} statements, >= {minTokens} tokens, {(exact ? "exact" : "type-2")})");
foreach (var windows in reported)
{
    var signature = string.Join(";", windows.Select(w => $"{w.Path}:{w.First / 8}"));
    if (!seen.Add(signature)) continue;
    if (printed++ >= top) break;
    Console.WriteLine($"x{windows.Count} {windows[0].Tokens} tokens:");
    foreach (var w in windows)
    {
        Console.WriteLine($"    {w.Path}:{w.First}-{w.Last}");
    }

    Console.WriteLine("      | " + windows[0].FirstStatement.ReplaceLineEndings(" ").Trim());
}

int IntArg(string name, int fallback)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 ? int.Parse(args[index + 1]) : fallback;
}

internal sealed record Window(string Path, string MemberKey, int First, int Last, int Tokens, string FirstStatement);
