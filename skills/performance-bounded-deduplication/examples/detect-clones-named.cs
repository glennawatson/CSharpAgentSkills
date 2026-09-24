#!/usr/bin/env dotnet run
#:package Microsoft.CodeAnalysis.CSharp@4.14.0

// Type-2 whole-body clone detector that names each body's containing type and member.
// Usage: dotnet run detect-clones-named.cs -- <root> [--min-tokens N] [--skip-members A,B] [--top K]

using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

string root = ".";
int minTokens = 40;
int top = 500;
bool exact = false;
bool printBodies = false;
var skipMembers = new HashSet<string>(StringComparer.Ordinal);
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--min-tokens": minTokens = int.Parse(args[++i]); break;
        case "--top": top = int.Parse(args[++i]); break;
        case "--exact": exact = true; break;
        case "--print-bodies": printBodies = true; break;
        case "--skip-members": foreach (var m in args[++i].Split(',')) skipMembers.Add(m); break;
        default: if (!args[i].StartsWith("--")) root = args[i]; break;
    }
}

string[] skip = { "/bin/", "/obj/", "/tests/", "/test/", "/benchmarks/", "/tools/" };
bool Skip(string p)
{
    p = p.Replace('\\', '/');
    foreach (var s in skip)
    {
        if (p.Contains(s)) return true;
    }

    return p.EndsWith(".Designer.cs") || p.EndsWith(".g.cs");
}

var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories).Where(f => !Skip(f)).ToList();
var groups = new Dictionary<string, List<Body>>();
long totalCodeLines = 0;

foreach (var file in files)
{
    var srcText = File.ReadAllText(file);
    totalCodeLines += srcText.Count(c => c == '\n') + 1;
    var rootNode = CSharpSyntaxTree.ParseText(srcText).GetRoot();

    foreach (var node in rootNode.DescendantNodes())
    {
        (SyntaxNode? body, string name) = node switch
        {
            MethodDeclarationSyntax m => ((SyntaxNode?)m.Body ?? m.ExpressionBody, m.Identifier.ValueText),
            LocalFunctionStatementSyntax lf => ((SyntaxNode?)lf.Body ?? lf.ExpressionBody, "local " + lf.Identifier.ValueText),
            ConstructorDeclarationSyntax c => ((SyntaxNode?)c.Body ?? c.ExpressionBody, ".ctor"),
            OperatorDeclarationSyntax o => ((SyntaxNode?)o.Body ?? o.ExpressionBody, "operator " + o.OperatorToken.ValueText),
            AccessorDeclarationSyntax a => ((SyntaxNode?)a.Body ?? a.ExpressionBody, a.Keyword.ValueText),
            _ => ((SyntaxNode?)null, ""),
        };
        if (body is null || skipMembers.Contains(name)) continue;

        var tokens = body.DescendantTokens().ToList();
        if (tokens.Count < minTokens) continue;

        var sb = new StringBuilder();
        foreach (var t in tokens)
        {
                if (exact) sb.Append(t.Text).Append(' ');
            else if (t.IsKind(SyntaxKind.IdentifierToken)) sb.Append("$id ");
            else if (t.IsKind(SyntaxKind.StringLiteralToken) || t.IsKind(SyntaxKind.NumericLiteralToken) || t.IsKind(SyntaxKind.CharacterLiteralToken)) sb.Append("$lit ");
            else sb.Append(t.Kind()).Append(' ');
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
        var span = body.GetLocation().GetLineSpan();
        var type = node.FirstAncestorOrSelf<BaseTypeDeclarationSyntax>()?.Identifier.ValueText ?? "?";
        var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
        if (!groups.TryGetValue(hash, out var list)) groups[hash] = list = new List<Body>();
        list.Add(new Body(rel, span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1, type + "." + name, tokens.Count, node.ToString()));
    }
}

var clones = groups.Values.Where(g => g.Count >= 2).ToList();
long dupLines = 0;
foreach (var g in clones)
{
    var ordered = g.OrderBy(x => x.File).ThenBy(x => x.Start).ToList();
    for (int i = 1; i < ordered.Count; i++) dupLines += ordered[i].End - ordered[i].Start + 1;
}

Console.WriteLine($"Clone sets: {clones.Count} (>= {minTokens} tokens, type-2, skipped members: {string.Join(',', skipMembers)})");
Console.WriteLine($"Duplicated lines: {dupLines} ({(totalCodeLines == 0 ? 0 : 100.0 * dupLines / totalCodeLines):F2}%)");
foreach (var g in clones.OrderByDescending(x => x.Count * (x[0].End - x[0].Start)).Take(top))
{
    var ordered = g.OrderBy(x => x.File).ThenBy(x => x.Start).ToList();
    Console.WriteLine($"x{ordered.Count} ~{ordered[0].End - ordered[0].Start + 1} lines, {ordered[0].Tokens} tokens:");
    foreach (var m in ordered)
    {
        Console.WriteLine($"    {m.Member}  {m.File}:{m.Start}-{m.End}");
        if (printBodies) Console.WriteLine(m.Text);
    }
}

internal sealed record Body(string File, int Start, int End, string Member, int Tokens, string Text);
