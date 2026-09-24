---
name: roslyn-rewriters
description: Use for predictable, structural, repo-wide C# code changes — removing/renaming attributes, rewriting call patterns, converting one API shape to another across many files. Write a throwaway Roslyn program (ideally a single-file `dotnet run rewrite.cs` app) that parses the real syntax tree and rewrites it, instead of regex/sed/Python guessing at C# structure. Roslyn understands the code; text tools don't.
---

# Throwaway Roslyn rewriters for structural C# changes

For a mechanical change that's the *same shape* across many files — strip a set of attributes, swap one API for another, rename a pattern, reshape method signatures — a regex or a Python script is guessing at C# syntax and will mangle edge cases (nested generics, trivia, string literals, conditional compilation). **A small Roslyn program parses the actual syntax tree and edits it structurally**, so the change is correct and the formatting/trivia survives. It's the reliable way to do predictable bulk edits.

Reach for this when: the change is **structural and repeatable**, affects **many files**, and a wrong text-match would silently corrupt code. For one-off edits or genuinely judgment-heavy changes, just edit directly (or drive subagents — see `subagent-driven-development`).

## The lightweight path: a single-file `dotnet run` app

Since .NET 10 you don't need a project — a single `.cs` file with directives runs directly. This is the ideal throwaway harness: one file, no scaffolding, delete it when done.

```csharp
#:package Microsoft.CodeAnalysis.CSharp@5.0.0

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

foreach (var path in Directory.EnumerateFiles(args[0], "*.cs", SearchOption.AllDirectories))
{
    if (path.Contains("/obj/") || path.Contains("/bin/")) continue;

    var text = await File.ReadAllTextAsync(path);
    var tree = CSharpSyntaxTree.ParseText(text);
    var root = await tree.GetRootAsync();

    var newRoot = new MyRewriter().Visit(root);
    if (newRoot == root) continue;                    // nothing changed — skip

    await File.WriteAllTextAsync(path, newRoot.ToFullString());
    Console.WriteLine($"rewrote {path}");
}

// A CSharpSyntaxRewriter visits nodes and returns replacements; unvisited nodes
// (and all their trivia) are preserved exactly.
sealed class MyRewriter : CSharpSyntaxRewriter
{
    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        // e.g. strip specific attributes, returning the edited node.
        return base.VisitMethodDeclaration(node);
    }
}
```

Run it: `dotnet run rewrite.cs -- /path/to/src`. Add more `#:package` lines as needed. Convert to a real project only if it outgrows one file (`dotnet project convert rewrite.cs`).

## Runnable examples

`./examples/` has three complete single-file apps you can copy and adapt — a progression from safest to most powerful:

- **`01-survey-attributes.cs`** — read-only reconnaissance. Walks every `.cs` file and prints the distinct attribute names it finds. *Do this first*: understand what's actually in the code before you rewrite it. Syntax-only.
- **`02-remove-attributes.cs`** — a syntax-only `CSharpSyntaxRewriter` that strips a configured set of attributes off methods and rescues their comments. Defaults to a **dry run**; `--write` applies. Covers the common case.
- **`03-remove-redundant-call.cs`** — the semantic case: removes a redundant `.AsQueryable()` *only* when the receiver is already `IQueryable<T>`, which you can only know from the semantic model. Loads the solution via `MSBuildWorkspace`. Also dry-run by default.

Two habits baked into the examples worth keeping: **dry-run by default** (print what *would* change, require `--write` to touch disk), and **skip `obj/`/`bin/`**.

## When you need semantics: load the workspace

Syntax-only parsing (above) is enough for most attribute/signature/call-shape edits and is far simpler. Step up to a full workspace **only when the change needs the semantic model** — resolving what a symbol actually binds to, types, overloads, "is this the `System.Text.Json` one or ours." Load the solution through `MSBuildWorkspace`:

```csharp
MSBuildLocator.RegisterDefaults();                    // before any Roslyn MSBuild call
using var workspace = MSBuildWorkspace.Create();
workspace.RegisterWorkspaceFailedHandler(e => Console.Error.WriteLine(e.Diagnostic));
var solution = await workspace.OpenSolutionAsync(slnPath);

foreach (var project in solution.Projects.Where(p => p.Name == "Target"))
foreach (var doc in project.Documents.Where(d =>
             d.FilePath?.EndsWith(".cs") == true &&
             !d.FilePath.Contains("/obj/") && !d.FilePath.Contains("/bin/")))
{
    var model = await doc.GetSemanticModelAsync();     // semantic info lives here
    var root = await doc.GetSyntaxRootAsync();
    // ...rewrite, then File.WriteAllText(doc.FilePath, newRoot.ToFullString());
}
```

Needs `Microsoft.Build.Locator` + `Microsoft.CodeAnalysis.Workspaces.MSBuild` (+ `.CSharp.Features` for code-fix-style edits). `RegisterDefaults()` must run before any MSBuild-backed Roslyn call.

## Use the latest stable Roslyn

**Default to the newest non-preview `Microsoft.CodeAnalysis.*` (the `5.0.0` line) for these throwaway tools.** There's no downside: the harness is disposable, doesn't ship, and never has to match the target repo's compiler version — it just reads/parses `.cs` text. Newer Roslyn also pulls a patched MSBuild, so the workspace example restores with **no vulnerability advisories**, whereas the older `4.x` line drags in a flagged `Microsoft.Build` transitively. Pin all the `Microsoft.CodeAnalysis.*` packages to the **same** version. Don't reach for preview builds unless you specifically need an unreleased language feature.

## Doing the edit right

- **Edit the tree, not the text.** Use `ReplaceNode`/`ReplaceNodes`, `WithAttributeLists`, `SyntaxFactory`, or a `CSharpSyntaxRewriter`. Nodes are immutable — every "change" returns a new node; reassign.
- **Preserve trivia.** Whitespace, comments, and newlines are *trivia* attached to tokens. When you remove or replace a node, carry the leading/trailing trivia across (as `AttributeRemover` does) so you don't leave dangling blank lines or eat comments.
- **Bail when nothing changed.** `if (newRoot == root) continue;` — don't rewrite (and reformat/touch) files the transform didn't actually affect.
- **Normalize output deliberately.** Decide on line endings and trailing whitespace and apply consistently (for example normalize to `\n`, trim line ends, and write UTF-8 without a BOM). Or run `dotnet format` afterward instead of hand-normalizing.

## After running it

- **Build is the verdict.** `dotnet build` + analyzers + tests (see `csharp-verification`). The rewriter is only correct if the result compiles clean and tests pass — confirm, don't assume.
- **Diff before trusting.** Skim `git diff` for a few representative files and any odd cases, and check that the exact spans changed are the ones you intended — a boundary-less text sweep (regex/sed) can splice a comment off its method or rename an identifier inside a string literal, and nothing says so until the build breaks. A structural rewrite is predictable, which makes a wrong assumption show up uniformly — easy to catch in a sample. Rebuild after.
- **Delete the harness.** It's throwaway tooling — don't commit `rewrite.cs` into the product repo unless the team wants to keep it. (Keep it in a scratch/tools location if it's worth re-running.)

## Anti-patterns

- Regex/sed/Python rewriting C# structure — mangles generics, trivia, strings, and `#if` blocks; use the parser.
- Mutating `.ToFullString()` text after parsing instead of editing the tree — throws away the whole point.
- Dropping trivia, leaving blank-line craters or deleting comments.
- Loading a full `MSBuildWorkspace` when syntax-only parsing would do — slower and more setup for no benefit unless you need the semantic model.
- Forgetting `MSBuildLocator.RegisterDefaults()` before MSBuild-backed calls (cryptic load failures).
- Declaring success on "it ran" — only `dotnet build` + tests confirm the rewrite was correct.
