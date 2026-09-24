#:package Microsoft.CodeAnalysis.CSharp@4.14.0

// Adds [MethodImpl(MethodImplOptions.AggressiveInlining)] to every method a build log reports under
// PSH1410, and the System.Runtime.CompilerServices using where the file lacks it. Locations are read
// from the log's "path(line,col): error PSH1410" lines, de-duplicated across Roslyn slots; the method is
// the declaration whose identifier sits at that position. Doc comments move onto the new attribute so
// they stay attached to the member. Dry run by default; --write applies.
// usage: dotnet run add-aggressive-inlining.cs -- <build.log> [--write]

using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

var write = Array.IndexOf(args, "--write") >= 0;
var locations = new SortedDictionary<string, SortedSet<(int Line, int Column)>>(StringComparer.Ordinal);
var pattern = new Regex(@"^(?<path>/[^(]+\.cs)\((?<line>\d+),(?<column>\d+)\): (error|warning) PSH1410:", RegexOptions.CultureInvariant);
foreach (var line in File.ReadLines(args[0]))
{
    var match = pattern.Match(line);
    if (!match.Success) continue;
    var path = match.Groups["path"].Value;
    if (!locations.TryGetValue(path, out var set)) locations[path] = set = [];
    set.Add((int.Parse(match.Groups["line"].Value), int.Parse(match.Groups["column"].Value)));
}

foreach (var (path, positions) in locations)
{
    var bytes = File.ReadAllBytes(path);
    var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
    var text = new UTF8Encoding(false).GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));
    var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
    var tree = CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.Preview), path);
    var root = tree.GetRoot();
    var lines = tree.GetText().Lines;

    var targets = new List<MethodDeclarationSyntax>();
    foreach (var (line, column) in positions)
    {
        var position = lines[line - 1].Start + column - 1;
        var method = root.FindToken(position).Parent?.FirstAncestorOrSelf<MethodDeclarationSyntax>();
        if (method is null || HasAggressiveInlining(method)) { Console.WriteLine($"skip {path}:{line}"); continue; }
        targets.Add(method);
        Console.WriteLine($"add {path}:{line} {method.Identifier.ValueText}");
    }

    if (targets.Count == 0) continue;

    var newRoot = (CompilationUnitSyntax)root.ReplaceNodes(targets, (original, _) => AddAttribute(original, newLine));
    newRoot = EnsureUsing(newRoot, newLine);
    if (write) File.WriteAllText(path, newRoot.ToFullString(), new UTF8Encoding(hasBom));
}

static bool HasAggressiveInlining(MethodDeclarationSyntax method)
{
    foreach (var list in method.AttributeLists)
    {
        foreach (var attribute in list.Attributes)
        {
            if (attribute.Name.ToString() is "MethodImpl" or "MethodImplAttribute" && attribute.ArgumentList?.ToString().Contains("AggressiveInlining", StringComparison.Ordinal) == true)
            {
                return true;
            }
        }
    }

    return false;
}

static MethodDeclarationSyntax AddAttribute(MethodDeclarationSyntax method, string newLine)
{
    var leading = method.GetLeadingTrivia();
    var indentation = leading.Count > 0 && leading[^1].IsKind(SyntaxKind.WhitespaceTrivia) ? SyntaxFactory.TriviaList(leading[^1]) : SyntaxFactory.TriviaList();
    var attribute = SyntaxFactory.AttributeList(
            SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.Attribute(
                    SyntaxFactory.IdentifierName("MethodImpl"),
                    SyntaxFactory.AttributeArgumentList(
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.AttributeArgument(SyntaxFactory.ParseExpression("MethodImplOptions.AggressiveInlining")))))))
        .WithLeadingTrivia(leading)
        .WithTrailingTrivia(SyntaxFactory.EndOfLine(newLine));
    var stripped = method.WithLeadingTrivia(indentation);
    return stripped.WithAttributeLists(stripped.AttributeLists.Insert(0, attribute));
}

static CompilationUnitSyntax EnsureUsing(CompilationUnitSyntax unit, string newLine)
{
    const string Name = "System.Runtime.CompilerServices";
    foreach (var existing in unit.Usings)
    {
        if (existing.Name?.ToString() == Name) return unit;
    }

    var directive = SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(Name))
        .WithUsingKeyword(SyntaxFactory.Token(SyntaxFactory.TriviaList(), SyntaxKind.UsingKeyword, SyntaxFactory.TriviaList(SyntaxFactory.Space)))
        .WithTrailingTrivia(SyntaxFactory.EndOfLine(newLine));

    if (unit.Usings.Count == 0)
    {
        // The header and the blank line after it are the first member's leading trivia: they move onto the
        // using, and the member keeps one blank line.
        var first = unit.Members[0];
        var withBlank = unit.ReplaceNode(first, first.WithLeadingTrivia(SyntaxFactory.EndOfLine(newLine)));
        return withBlank.WithUsings(SyntaxFactory.SingletonList(directive.WithLeadingTrivia(first.GetLeadingTrivia())));
    }

    var index = 0;
    while (index < unit.Usings.Count
        && unit.Usings[index].Name?.ToString() is { } name
        && name.StartsWith("System", StringComparison.Ordinal)
        && string.CompareOrdinal(name, Name) < 0)
    {
        index++;
    }

    var usings = unit.Usings;
    if (index == 0)
    {
        var header = usings[0].GetLeadingTrivia();
        usings = usings.Replace(usings[0], usings[0].WithLeadingTrivia(SyntaxFactory.TriviaList()));
        return unit.WithUsings(usings.Insert(0, directive.WithLeadingTrivia(header)));
    }

    return unit.WithUsings(usings.Insert(index, directive));
}
