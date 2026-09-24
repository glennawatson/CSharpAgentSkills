#!/usr/bin/env dotnet run
#:package Microsoft.CodeAnalysis.CSharp@4.14.0

// Structural clone detector. Finds duplicated method / accessor / constructor /
// operator / local-function bodies by normalizing each to a token-KIND stream
// (by default canonicalizing identifiers and literals, so a renamed copy still
// matches - a "type-2" clone), hashing it, and grouping equal hashes. Reports
// the clone sets, the duplicated line count, and a duplication percentage.
//
// This is deliberately METHOD-granular: it finds whole bodies that are clones,
// which are exactly the units you extract into a shared helper. It will not find
// a duplicated 6-line run buried inside two otherwise-different methods - reach
// for a token-window tool (jscpd) when you need that finer granularity.
//
// Usage (from the repo root, or pass a root):
//   dotnet run detect-clones.cs -- <root> [--min-tokens N] [--exact] [--top K]
//     <root>        directory to scan (default: current directory)
//     --min-tokens  ignore bodies smaller than N tokens (default 75)
//     --exact       type-1 only: keep identifiers/literals verbatim
//                   (default is type-2: canonicalize them, which also catches type-1)
//     --top K       print the K largest clone sets (default 25)

using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

string root = ".";
int minTokens = 75;
bool exact = false;
int top = 25;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--min-tokens": minTokens = int.Parse(args[++i]); break;
        case "--exact": exact = true; break;
        case "--top": top = int.Parse(args[++i]); break;
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

var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
    .Where(f => !Skip(f)).ToList();

// fingerprint -> the bodies that share it (file, first line, last line)
var groups = new Dictionary<string, List<(string File, int Start, int End)>>();
long totalCodeLines = 0;

foreach (var file in files)
{
    var srcText = File.ReadAllText(file);
    totalCodeLines += srcText.Count(c => c == '\n') + 1;
    var rootNode = CSharpSyntaxTree.ParseText(srcText).GetRoot();

    foreach (var node in rootNode.DescendantNodes())
    {
        SyntaxNode? body = node switch
        {
            MethodDeclarationSyntax m => (SyntaxNode?)m.Body ?? m.ExpressionBody,
            LocalFunctionStatementSyntax lf => (SyntaxNode?)lf.Body ?? lf.ExpressionBody,
            ConstructorDeclarationSyntax c => (SyntaxNode?)c.Body ?? c.ExpressionBody,
            OperatorDeclarationSyntax o => (SyntaxNode?)o.Body ?? o.ExpressionBody,
            AccessorDeclarationSyntax a => (SyntaxNode?)a.Body ?? a.ExpressionBody,
            _ => null,
        };
        if (body is null) continue;

        var tokens = body.DescendantTokens().ToList();
        if (tokens.Count < minTokens) continue;

        var sb = new StringBuilder();
        foreach (var t in tokens)
        {
            if (!exact && t.IsKind(SyntaxKind.IdentifierToken))
            {
                sb.Append("$id ");
            }
            else if (!exact && (t.IsKind(SyntaxKind.StringLiteralToken)
                || t.IsKind(SyntaxKind.NumericLiteralToken)
                || t.IsKind(SyntaxKind.CharacterLiteralToken)))
            {
                sb.Append("$lit ");
            }
            else
            {
                sb.Append(t.Kind()).Append(' ');
            }
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
        var span = body.GetLocation().GetLineSpan();
        var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
        if (!groups.TryGetValue(hash, out var list))
        {
            groups[hash] = list = new List<(string, int, int)>();
        }

        list.Add((rel, span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1));
    }
}

var clones = groups.Values.Where(g => g.Count >= 2).ToList();
long dupLines = 0;
foreach (var g in clones)
{
    var ordered = g.OrderBy(x => x.File).ThenBy(x => x.Start).ToList();
    for (int i = 1; i < ordered.Count; i++)
    {
        dupLines += ordered[i].End - ordered[i].Start + 1;   // every instance past the first
    }
}

double pct = totalCodeLines == 0 ? 0 : 100.0 * dupLines / totalCodeLines;
Console.WriteLine($"Files scanned:    {files.Count}");
Console.WriteLine($"Code lines:       {totalCodeLines}");
Console.WriteLine($"Clone sets:       {clones.Count}   (>= {minTokens} tokens, {(exact ? "exact/type-1" : "structural/type-2")})");
Console.WriteLine($"Duplicated lines: {dupLines}  ({pct:F2}%)");
Console.WriteLine();

foreach (var g in clones.OrderByDescending(x => x.Count * (x[0].End - x[0].Start)).Take(top))
{
    var ordered = g.OrderBy(x => x.File).ThenBy(x => x.Start).ToList();
    Console.WriteLine($"  x{ordered.Count}  ~{ordered[0].End - ordered[0].Start + 1} lines each:");
    foreach (var m in ordered)
    {
        Console.WriteLine($"      {m.File}:{m.Start}-{m.End}");
    }
}
