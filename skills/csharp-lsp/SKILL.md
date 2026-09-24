---
name: csharp-lsp
description: Use when relying on LSP/IDE language-server data in a C#/.NET repo — go-to-definition, find-references, hover, "errors" lists. Treat LSP as a fast index that goes stale, not as ground truth. `dotnet build` is the authority on whether code compiles. Lean on LSP for navigation, renames, and trivia; verify everything else against a real build.
---

# Using the C# language server without trusting it too much

The LSP/IDE language server is a *cached index*. It's fast and great for moving around code, but it lags reality — it serves stale results after edits, reports phantom errors from a half-loaded workspace, and misses errors in code it hasn't re-analyzed yet. In this repo it throws a lot of **false positives**. So use it for what it's good at, and never let it be the thing that decides whether the code is correct.

## `dotnet build` is the king of truth

- **Compilation status comes from the build, not the language server.** If LSP shows red squiggles / "errors" but `dotnet build` is clean, the build wins — the index is stale. If LSP is green but `dotnet build` fails, the build wins. Always reconcile against an actual build before acting on a reported error.
- **Don't "fix" an LSP-reported error you haven't reproduced in a build.** Chasing a phantom diagnostic wastes effort and can introduce real breakage. Run the build, see if it's real, then fix the cause.
- **Analyzer warnings come from the build too** (see `csharp-verification`). The IDE may surface a subset or a stale set; `dotnet build` / `dotnet format --verify-no-changes` is the gate.

## What LSP *is* good for

Lean on it for navigation and mechanical edits where a stale answer is cheap to notice:

- **Go-to-definition / find-references** to navigate and understand structure. Treat the reference list as "very likely complete," not "provably complete" — confirm with a text search (`grep`/`Grep`) for anything load-bearing, like before deleting a member.
- **Rename** of a symbol — the language server's rename is syntax-aware and usually correct across the solution. Still build afterward.
- **Trivia / formatting / signature help** — whitespace, usings organization, hover for a type's shape. Low-stakes, fast.
- **Quick "where is this" orientation** before you dig in with real tools.

## What to verify against a build instead

- Whether the project actually compiles.
- Whether an "unused" symbol is truly unused (LSP can miss reflection, DI, source-generated, or `InternalsVisibleTo` usage — cross-check with a text search).
- Whether an error is real before you change code to satisfy it.
- Anything you're about to delete or change broadly.

## When LSP looks wrong

Stale index is the common cause. In order:

1. **Build it.** `dotnet build` tells you the truth in seconds.
2. If LSP and build disagree, **trust the build** and keep going — don't reload-dance unless you're actively using LSP navigation and it's clearly desynced.
3. If you *are* relying on LSP and it's stale, a reload/restart of the server (or reopening the workspace) re-indexes — but only bother when navigation is the task. For a verdict on correctness, just build.

## Anti-patterns

- Treating the IDE's error list as authoritative and "fixing" diagnostics you never reproduced in a build.
- Declaring a symbol unused on find-references alone, then deleting it — missing reflection/DI/codegen callers.
- Reload-dancing the language server to chase a red squiggle instead of just running `dotnet build`.
- Reporting "no errors" because LSP is green, without building.
- Using LSP find-references as proof of completeness for a risky refactor — back it with a text search.
