---
name: subagent-driven-development
description: Use when building a C#/.NET feature by driving subagents to do the work — especially lots of repetitive, mechanical, or templated files. You orchestrate; agents implement. Pass the exact changes in each agent's prompt; never stage instructions through temporary files in the repo.
---

# Subagent-driven development (C#)

You are the orchestrator. You hold the design and the standards; subagents do the per-file implementation. This shines when the work is **repetitive or templated** — many similar handlers, DTOs, interface implementations, test fixtures, mappers, migrations, or the same refactor applied across dozens of files. One context can't comfortably hold 40 near-identical edits; 40 focused agents can.

This is the *development* counterpart to `dispatching-subagents` (which is for diagnosing independent failures). Here you're constructing, often in bulk.

## Core rule: instructions go in the prompt, not in a temp file

**Do not write a scratch/plan/spec file into the repo for agents to read.** That litters the working tree, risks getting committed, and adds a file-IO round-trip. Instead, **put the exact changes the agent must make directly in its prompt** — the target path, the template/pattern, the concrete values, the constraints. The agent receives everything it needs inline and has no reason to invent or to hunt.

If the shared context is large (a base template, an interface, a conventions block), **paste it into each agent's prompt verbatim**. Repetition in prompts is cheap; a stray `TASKS.md` in the repo is not.

## The loop

1. **Design once, in your head / the conversation.** Decide the pattern: the shape of each file, the naming, the conventions, what varies per item and what's constant. Don't write it to disk.
2. **Enumerate the work-list.** Scan the repo to find exactly which files to create or change (e.g. "one mapper per entity in `Domain/Entities/`"). This is *your* job — discover the list, then drive it.
3. **Dispatch one agent per file (or small batch).** Each prompt is fully self-contained (see structure below). For independent files, fan out in parallel (one message, multiple Agent calls). For files that must agree on a shared contract, do the contract first, then fan out the rest.
4. **Integrate and verify yourself.** Collect the diffs, then run the real gate — build, **analyzers**, tests (see `csharp-verification`). Agents can drift from the pattern; you are the consistency check.

## Agent prompt structure

Each agent gets, inline:

- **Exact target** — the full path(s) to create or edit. One file, or a named small set.
- **The concrete change** — the template/pattern *with the per-item values already filled in*, or the precise edit to apply. Spell it out; don't make the agent guess the shape.
- **Shared context pasted in** — the base class/interface, the namespace, the conventions, an example of an existing file that already follows the pattern ("match `OrderMapper.cs` exactly, but for `Customer`").
- **Constraints** — "Only touch this file. Follow the repo's analyzer/.editorconfig rules. Use file-scoped namespaces and the existing using style. Do NOT add packages."
- **Verification the agent must do** — "Make sure it compiles in context." 
- **Return contract** — "Return the final file content (or the diff) and confirm it builds. Nothing else."

Example:

```
Create src/Mapping/CustomerMapper.cs.

Match the existing pattern in src/Mapping/OrderMapper.cs EXACTLY (pasted below),
but map the Customer entity → CustomerDto.

Fields to map: Id→Id, FullName→Name, Email→Email, CreatedUtc→CreatedAt.

[paste OrderMapper.cs here]
[paste Customer.cs and CustomerDto.cs here]

Rules:
- Only create CustomerMapper.cs. Touch nothing else.
- File-scoped namespace `Acme.Mapping`, nullable enabled, follow the repo .editorconfig.
- No new packages, no reflection.

Return: the full file content and confirm it compiles against the pasted types.
```

## When the work is templated, template the prompts

For 30 near-identical files, build the 30 prompts programmatically from your work-list (same skeleton, swap the per-item values) and dispatch them. Each agent still gets a complete, standalone instruction. If you have an orchestration harness available, a fan-out over the work-list is ideal — otherwise dispatch in parallel batches.

## Whether agents may skip the build depends on the kind of change

Letting each agent build is the safe default but serializes the slow step. Skipping it is much
faster and is safe **only for local, mechanical substitution** — one call shape becoming another, a
`Substring` becoming a span slice. Those land clean at high parallelism.

A **structural** change must build per batch. Moving control flow — extracting a helper, removing a
registration, restructuring a callback — trips the repo's own analyzers in ways the agent cannot
predict, and blind batches accumulate errors that nobody owns.

- Serialize the builds with a lock (`flock <lockfile> dotnet build ...`) so several agents can edit
  concurrently without their builds colliding on `obj/`.
- Tell each agent to fix only errors naming **its own** files and ignore the rest, or it fights the
  other agents' in-progress edits. Know that this leaves aggregate breakage nobody owns, so still
  run a repair pass at the end.
- Under heavy lock contention an agent may only get one or two build iterations inside its window,
  so the end-of-run repair is not optional.
- **Give the repair pass every constraint, not just the obvious one.** A pass told "fix the build,
  don't undo the optimisation" will happily satisfy a style rule in a way that reintroduces the cost
  somewhere else. Name the specific wrong fixes.

## You own consistency and correctness

- **Spot-check for drift.** Agents working in isolation can each make a slightly different choice. Diff a sample against the pattern; if they diverged, tighten the prompt and re-run, don't hand-patch 30 files.
- **One contract, then the rest.** If files share an interface or base type, create/confirm that first and paste it into every downstream agent so they all bind to the same thing.
- **Verify the whole, not the parts.** Each agent confirms its file; *you* confirm the suite builds clean, analyzers pass, and tests are green before calling it done.
- **TDD still applies** (see `tdd`) — for behavioral code, have agents write the failing test first or drive a test-per-file.

## Anti-patterns

- Writing a `TASKS.md` / `plan.txt` / scratch file into the repo for agents to read — put it in the prompt.
- Vague prompts ("make a mapper for Customer") that force the agent to reverse-engineer the pattern — paste the pattern.
- Letting each agent pick its own conventions — pin them in every prompt.
- Trusting agent self-reports as final verification — run the build/analyzers/tests yourself.
- Hand-fixing widespread drift instead of fixing the prompt and re-dispatching.
