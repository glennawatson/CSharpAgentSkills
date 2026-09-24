---
name: roslyn-discovery
description: Use to answer structural questions about a C#/.NET codebase that grep and LSP answer badly — public API surface, who implements an interface or derives from a base, what's undocumented, call/usage inventories. Write a throwaway read-only Roslyn program (a single-file `dotnet run query.cs` app) that parses the real syntax/semantic graph instead of text-matching. Roslyn knows the code; grep guesses.
---

# Roslyn discovery — querying a codebase, reliably

Some questions about a codebase can't be answered by text search: *which types implement `IHandler` (including transitively)? what's our public API surface? which public members lack docs? where is this symbol actually used, as opposed to where its name appears in a comment?* Grep hits strings and comments and misses indirect relationships; LSP find-references serves a stale, sometimes-incomplete index (see `csharp-lsp`). **A small read-only Roslyn program parses the actual syntax tree — or resolves the real semantic type graph — and gives you a complete, correct answer.**

This is the read-only sibling of `roslyn-rewriters`: same single-file `dotnet run` harness, same "parse, don't guess" principle, but it *reports* instead of *editing*. A common pairing is **discover here → feed the work-list to a rewriter**: find every `IHandler`, then drive an edit across exactly those files.

If the question is about the compiled output rather than the source tree, stop and switch skills: manifest resources, assembly references, shipped members, package payloads, and WinUI/PRI questions belong to `csharp-assembly-inspection`, not Roslyn.

**Fighting the shell is the signal to switch.** The moment you're escaping quotes, threading a file list through `xargs`, or debugging why a loop printed nothing, stop — that friction means the question is structural, and a Roslyn query will be shorter *and* correct. Shell quirks make it worse than it looks: `fish` does not word-split a space-joined string (only arrays/newlines split), so a `$files` variable arrives as one giant argument; `rg -h` is `--help`, not "no headers" (use `--no-filename`); `\K` needs `-P`. Don't burn three attempts wrestling these — the second failed `grep`/`rg` pipeline is the cue to write `query.cs`.

## Two modes, same as rewriting

- **Syntax-only** (parse text, no build) — enough for anything you can see in the tree: API surface, attribute usage, missing doc comments, `async void`, method/type inventories. Fast, no project context, just `CSharpSyntaxTree.ParseText`.
- **Semantic** (load the solution via `MSBuildWorkspace`) — needed the moment a question depends on *what a name binds to*: implementations of an interface, derived types, real call sites, overload resolution, "is this `our` `Cache` or the BCL one." You get an `INamedTypeSymbol`/`SemanticModel` graph that knows the truth across projects and referenced assemblies.

Default to syntax-only; step up only when the question genuinely needs binding. (Same `MSBuildLocator.RegisterDefaults()` rule and latest-stable Roslyn guidance as `roslyn-rewriters` — pin all `Microsoft.CodeAnalysis.*` to the same current `5.0.0`-line version; the newer line keeps the MSBuild workspace restore advisory-clean.)

## Runnable examples

`./examples/` has two complete single-file apps:

- **`01-api-surface.cs`** — syntax-only. Enumerates every public/protected type and member and flags the undocumented ones (`--undocumented-only` to list just those). For API review, catching accidental `public`, and finding doc gaps before a release.
- **`02-find-implementations.cs`** — semantic. Across a whole solution, lists every type that implements an interface or derives from a base *by name*, with `file:line` and whether it's `implements` vs `derives`. Catches transitive/indirect relationships grep can't, and builds a reliable work-list.

Copy one and adapt the query. They're disposable — don't commit them into the product repo unless the team wants to keep a query around.

## Writing a good discovery query

- **Walk the right nodes.** `root.DescendantNodes().OfType<MethodDeclarationSyntax>()` etc. for syntax; recurse `INamespaceSymbol`/`INamedTypeSymbol` (as `02` does) for semantic. `SymbolFinder.FindImplementationsAsync` / `FindDerivedClassesAsync` / `FindReferencesAsync` are the idiomatic semantic shortcuts when you already hold the target symbol.
- **Reference scans: compare identifier-token sets, not regexes.** To ask "does this file use type `Foo`," collect `DescendantTokens().Where(t => t.IsKind(SyntaxKind.IdentifierToken)).Select(t => t.ValueText)` into a `HashSet` and test membership. This ignores comments and string literals for free — the thing regex gets wrong — and stays syntax-only (fast, no build). Collect declared names from `BaseTypeDeclarationSyntax`/`DelegateDeclarationSyntax` identifiers.
- **Partition + dependency closure (fixpoint).** A common split decision — "which files are type-agnostic enough for the `.Core` project vs. must stay behind the shim?" — is a discovery query: seed the "tainted" set with files referencing the forbidden types, then **iterate**: any file that references a type *declared only in* the tainted set joins it, repeat until no change. The fixpoint catches transitive entanglement a one-pass scan misses, and proves the partition is closed (printing the count converged-to and which files were promoted, and why). Don't eyeball transitivity — loop it.
- **Skip `obj/`/`bin/`** so you don't survey generated or build output.
- **Match visibility from the right default.** Top-level types are `internal` by default, members are `private` — only explicit `public`/`protected` is API surface (see `IsVisible` in `01`).
- **Emit `file:line`** for anything actionable (`location.GetLineSpan()`), and print a count summary at the end — discovery output is something you act on.
- **Report what you skipped.** If you sampled, capped, or ignored a project, say so — a silent omission reads as "nothing there."
- **It's read-only.** A discovery tool should never write source files. If you find yourself wanting to edit, that's `roslyn-rewriters`.

## When NOT to reach for this

- A genuinely simple, literal-text question (`grep` for a constant string) — just grep.
- A one-off "where's this method" navigation — LSP go-to-definition is fine for that (and `csharp-lsp` covers when to trust it).
- Reach for Roslyn when the question is **structural or semantic** and a wrong/incomplete answer would matter — completeness-critical sweeps, refactor work-lists, API audits.

## Anti-patterns

- Grepping for an interface name to find implementers — misses indirect implementers and base-class chains, hits comments/strings.
- Trusting LSP find-references as a *complete* list for a risky sweep — cross-check with a semantic query.
- Loading a full `MSBuildWorkspace` for a question syntax could answer — slower, more setup, no benefit.
- A "discovery" tool that mutates files — keep read and write separate; rewriting is `roslyn-rewriters`.
- Reporting a partial sweep as if it were complete — print the count and anything skipped.
- Wrestling shell quoting / `fish` word-splitting / `xargs` to do structural analysis — the friction is the signal; write the query in Roslyn instead of fighting the shell a third time.
- Regex over raw file text for "uses type X" — matches comments and strings; compare identifier-token sets instead.
- Eyeballing a one-pass scan for a split/partition decision and assuming it's closed — iterate to a fixpoint so transitive dependencies can't leak across the boundary.
