---
name: executing-plans
description: Use when working through an agreed implementation plan or task list in a C#/.NET project. Execute task-by-task with real verification, but batch any questions into one round and proceed on sensible defaults — do not stop and prompt after every micro-step.
---

# Executing plans (without the prompt storm)

You have a plan (a task list, an issue, an agreed approach). Your job is to *execute* it and report — not to re-litigate it or ask permission at every line. Keep momentum; surface decisions in batches.

## The cardinal rule: batch your questions

**Do not drip-feed prompts.** Stopping after every step to ask "shall I continue?" is the single most annoying thing you can do. Instead:

- **Read the whole plan first.** Collect *every* genuine ambiguity up front.
- **Ask them all at once** — one `AskUserQuestion` round with multiple questions, or a single numbered list. Then go.
- **Proceed on sensible defaults** for anything you can reasonably infer. State the assumption in passing ("assuming the existing `IClock` abstraction — say so if not") and keep moving. The user corrects what's wrong; they don't pre-approve what's obvious.
- **Only re-prompt mid-flight** when you hit something genuinely unforeseeable that blocks you *and* has no safe default. That's rare.

A good session has **one** question batch near the start and then steady progress, not twenty interruptions.

## The execution loop

For each task in the plan:

1. **Do the task** — write the code following existing conventions in the repo.
2. **Verify it for real** — build clean, run the relevant tests, and **honour the analyzers**. "It should compile" is not verification. `dotnet build` (with warnings-as-errors if the repo uses it) then the focused test (`dotnet run -- --treenode-filter ...` for TUnit, or `dotnet test --filter`).
3. **Analyzer warnings are failures, not noise.** Roslyn analyzers (the .NET SDK set, `Microsoft.CodeAnalysis.NetAnalyzers`, StyleCop, the repo's own analyzers, `.editorconfig` severities) encode the project's rules. A green-but-warning build is not done. Fix the cause; do not blanket-`#pragma warning disable` or scatter suppressions to silence them. See `csharp-verification` for the full gate.
4. **Keep the bar green** — if a task breaks an unrelated test or trips an analyzer, fix it or flag it; don't leave the suite red and move on.
4. **Move to the next task.** Don't stop to ask whether to proceed — proceed.

Work in TDD style where it fits (see `tdd`): failing test first, then the implementation. Independent tasks can be fanned out to subagents (see `dispatching-subagents`).

## Track progress, lightly

- Keep a running checklist (your agent's task list or the plan itself) so you and the user can see what's done. Update status as you go.
- Mark a task done **only after it's verified**, not after it's written.
- If you discover the plan is wrong or a task is unnecessary, say so briefly and adapt — don't silently skip or silently over-build.

## Don't create files nobody asked for

- No status-report markdown, no `PLAN-PROGRESS.md`, no design docs dropped into the repo unless the user asked for them. Report progress in chat.
- Don't commit unless asked. When you do, follow `commit-messages`.

## Report at the end (and only meaningfully mid-way)

- When the plan is done: a short summary of what changed, what was verified (with the actual test result), and anything you assumed or deferred.
- Mid-way updates only at meaningful milestones, not per task. Be honest — if something failed or you skipped it, say so plainly.

## Anti-patterns

- Asking "should I continue?" after every step.
- A second, third, fourth question round for things you could have asked together — or inferred.
- Marking tasks done without running anything.
- Leaving the build/test bar red and moving on.
- Writing progress/spec files into the repo unprompted.
