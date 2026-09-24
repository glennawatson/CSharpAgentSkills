#!/usr/bin/dotnet run
#:package Microsoft.Build.Locator@1.7.8
#:package Microsoft.CodeAnalysis.CSharp.Workspaces@5.0.0
#:package Microsoft.CodeAnalysis.Workspaces.MSBuild@5.0.0

// Semantic rewrite — the case where syntax alone ISN'T enough. Removing a
// redundant `.AsQueryable()` is only safe when the receiver is ALREADY an
// IQueryable<T>; you can't know that from the syntax, you have to ask the type
// system. So this loads the real solution through MSBuildWorkspace to get a
// semantic model, and only strips the call when the receiver's type checks out.
//
// Run:  dotnet run 03-remove-redundant-call.cs -- /path/to/Your.slnx
// Dry run by default; add --write to apply.
//   dotnet run 03-remove-redundant-call.cs -- /path/to/Your.slnx --write
//

using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: dotnet run 03-remove-redundant-call.cs -- <solution.slnx> [--write]");
    return 1;
}

var slnPath = args[0];
var write = args.Contains("--write");

// Must run before any MSBuild-backed Roslyn call, or you get cryptic load errors.
MSBuildLocator.RegisterDefaults();

using var workspace = MSBuildWorkspace.Create();
workspace.RegisterWorkspaceFailedHandler(e => Console.Error.WriteLine($"[workspace] {e.Diagnostic}"));

Console.WriteLine($"opening {slnPath} ...");
var solution = await workspace.OpenSolutionAsync(slnPath);

var changed = 0;
foreach (var project in solution.Projects)
{
    foreach (var document in project.Documents)
    {
        if (document.FilePath is not { } filePath ||
            !filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
            filePath.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
            filePath.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
        {
            continue;
        }

        var root = await document.GetSyntaxRootAsync();
        var model = await document.GetSemanticModelAsync();
        if (root is null || model is null)
        {
            continue;
        }

        // Find `<receiver>.AsQueryable()` calls whose receiver is already IQueryable.
        var redundant = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(inv => IsRedundantAsQueryable(inv, model))
            .ToList();

        if (redundant.Count == 0)
        {
            continue;
        }

        // Replace each `x.AsQueryable()` with just `x`, keeping the call's leading trivia.
        var newRoot = root.ReplaceNodes(redundant, (original, _) =>
        {
            var receiver = ((MemberAccessExpressionSyntax)original.Expression).Expression;
            return receiver.WithLeadingTrivia(original.GetLeadingTrivia());
        });

        changed++;
        if (write)
        {
            await File.WriteAllTextAsync(filePath, newRoot.ToFullString().ReplaceLineEndings("\n"));
            Console.WriteLine($"rewrote {filePath} ({redundant.Count} call(s))");
        }
        else
        {
            Console.WriteLine($"would rewrite {filePath} ({redundant.Count} call(s))");
        }
    }
}

Console.WriteLine($"\n{changed} file(s) {(write ? "rewritten" : "would change")}.");
if (!write && changed > 0)
{
    Console.WriteLine("re-run with --write to apply, then `dotnet build` + tests.");
}

return 0;

// Semantic check: the method is named AsQueryable AND the thing it's called on
// already implements IQueryable<T>, so the call adds nothing.
static bool IsRedundantAsQueryable(InvocationExpressionSyntax invocation, SemanticModel model)
{
    if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess ||
        memberAccess.Name.Identifier.Text != "AsQueryable")
    {
        return false;
    }

    var receiverType = model.GetTypeInfo(memberAccess.Expression).Type;
    return receiverType is INamedTypeSymbol named &&
           named.AllInterfaces.Any(i => i.Name == "IQueryable");
}
