#!/usr/bin/dotnet run
#:package Microsoft.Build.Locator@1.7.8
#:package Microsoft.CodeAnalysis.CSharp.Workspaces@5.0.0
#:package Microsoft.CodeAnalysis.Workspaces.MSBuild@5.0.0

// Discover, across a whole SOLUTION, every type that implements an interface or
// derives from a base class — by NAME, with file:line for each. This is the kind
// of question grep answers badly (misses indirect/transitive implementers, hits
// comments and strings) and stale LSP answers incompletely. The semantic model
// knows the real type graph, so this is the reliable way to build a work-list
// (e.g. "every IHandler — now go add a method to each" → feed to roslyn-rewriters).
//
// Run:  dotnet run 02-find-implementations.cs -- /path/to/Your.slnx IDisposable
//       dotnet run 02-find-implementations.cs -- /path/to/Your.slnx BlobCacheBase
//
// Matches by simple type name (not fully-qualified), so `IHandler` finds
// `My.App.IHandler`. Read-only — never writes.

using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: dotnet run 02-find-implementations.cs -- <solution.slnx> <TypeName>");
    return 1;
}

var (slnPath, targetName) = (args[0], args[1]);

// Must run before any MSBuild-backed Roslyn call.
MSBuildLocator.RegisterDefaults();

using var workspace = MSBuildWorkspace.Create();
workspace.RegisterWorkspaceFailedHandler(e => Console.Error.WriteLine($"[workspace] {e.Diagnostic}"));

Console.WriteLine($"opening {slnPath} ...");
var solution = await workspace.OpenSolutionAsync(slnPath);

var hits = new List<string>();
foreach (var project in solution.Projects)
{
    var compilation = await project.GetCompilationAsync();
    if (compilation is null)
    {
        continue;
    }

    // Walk every type DECLARED in this project's source (not referenced assemblies).
    foreach (var type in DeclaredTypes(compilation))
    {
        var relation = Relationship(type, targetName);
        if (relation is null)
        {
            continue;
        }

        var location = type.Locations.FirstOrDefault(l => l.IsInSource);
        var where = location is null ? "(no source location)" : Format(location);
        hits.Add($"{type.ToDisplayString()}  [{relation}]  {where}");
    }
}

if (hits.Count == 0)
{
    Console.WriteLine($"No source types implement or derive from '{targetName}'.");
    return 0;
}

hits.Sort(StringComparer.Ordinal);
Console.WriteLine($"\n{hits.Count} type(s) related to '{targetName}':");
foreach (var hit in hits)
{
    Console.WriteLine($"  {hit}");
}

return 0;

// "implements" if the interface is anywhere in AllInterfaces (covers transitive);
// "derives" if the name is anywhere up the base-type chain. Null = unrelated.
static string? Relationship(INamedTypeSymbol type, string targetName)
{
    if (type.AllInterfaces.Any(i => i.Name == targetName))
    {
        return "implements";
    }

    for (var baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
    {
        if (baseType.Name == targetName)
        {
            return "derives";
        }
    }

    return null;
}

// Recursively enumerate all named types declared in the compilation's source.
static IEnumerable<INamedTypeSymbol> DeclaredTypes(Compilation compilation)
{
    var stack = new Stack<INamespaceOrTypeSymbol>();
    stack.Push(compilation.Assembly.GlobalNamespace);

    while (stack.Count > 0)
    {
        var current = stack.Pop();
        foreach (var member in current.GetMembers())
        {
            switch (member)
            {
                case INamespaceSymbol ns:
                    stack.Push(ns);
                    break;
                case INamedTypeSymbol type:
                    yield return type;
                    foreach (var nested in type.GetTypeMembers())
                    {
                        stack.Push(nested);
                    }

                    break;
            }
        }
    }
}

static string Format(Location location)
{
    var span = location.GetLineSpan();
    return $"{span.Path}:{span.StartLinePosition.Line + 1}";
}
