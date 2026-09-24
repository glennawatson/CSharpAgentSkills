#!/usr/bin/dotnet run
#:package Microsoft.CodeAnalysis.CSharp@5.0.0

// Read-only reconnaissance. Before writing a rewriter, find out what's actually
// there. This walks every .cs file under a folder and prints the distinct class-,
// method-, and parameter-level attribute names it sees. Syntax-only — no build,
// no semantic model, nothing mutated.
//
// Run:  dotnet run 01-survey-attributes.cs -- /path/to/src
//
// Adapted (as a template) from ControllerConverter/GetAttributeNames.cs.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: dotnet run 01-survey-attributes.cs -- <src-folder>");
    return 1;
}

var classAttrs = new SortedSet<string>(StringComparer.Ordinal);
var methodAttrs = new SortedSet<string>(StringComparer.Ordinal);
var parameterAttrs = new SortedSet<string>(StringComparer.Ordinal);

foreach (var path in EnumerateSource(args[0]))
{
    var root = await CSharpSyntaxTree.ParseText(await File.ReadAllTextAsync(path)).GetRootAsync();

    foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
    {
        foreach (var attr in type.AttributeLists.SelectMany(l => l.Attributes))
        {
            classAttrs.Add(attr.Name.ToString());
        }

        foreach (var method in type.Members.OfType<MethodDeclarationSyntax>())
        {
            foreach (var attr in method.AttributeLists.SelectMany(l => l.Attributes))
            {
                methodAttrs.Add(attr.Name.ToString());
            }

            foreach (var parameter in method.ParameterList.Parameters)
            {
                foreach (var attr in parameter.AttributeLists.SelectMany(l => l.Attributes))
                {
                    parameterAttrs.Add(attr.Name.ToString());
                }
            }
        }
    }
}

Print("Class attributes", classAttrs);
Print("Method attributes", methodAttrs);
Print("Parameter attributes", parameterAttrs);
return 0;

static void Print(string heading, IEnumerable<string> names)
{
    Console.WriteLine(heading + ":");
    Console.WriteLine(string.Join('\n', names));
    Console.WriteLine();
}

// Skip generated output folders — they aren't yours to rewrite and they skew a survey.
static IEnumerable<string> EnumerateSource(string root) =>
    Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
        .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                    !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
