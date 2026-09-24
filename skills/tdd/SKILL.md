---
name: tdd
description: Use when writing or changing behavior in code and you want test-driven discipline — red/green/refactor on the real failing test. NO spec docs, no design essays, just the loop. Pairs with dispatching-subagents and csharp-tunit.
---

# TDD (no ceremony)

Write the test first, watch it fail for the right reason, make it pass, clean up. That's it. No spec document, no design doc, no "requirements gathering" phase. The test *is* the spec.

## The loop

1. **RED** — Write one small failing test for the next behavior you want. Run it. Confirm it fails, and that it fails for the reason you expect (assertion mismatch, not a compile error or a typo'd setup). A test that fails for the wrong reason is lying to you.
2. **GREEN** — Write the least code that makes it pass. Hardcoding a return value is allowed if it's honestly the smallest step — the next test will force you to generalize.
3. **REFACTOR** — With the bar green, improve names, remove duplication, tighten structure. Re-run after every change. Never refactor on red.

Then repeat. Each lap is minutes, not hours.

## Rules that keep it honest

- **One behavior per test.** If you can't name the test in one clear sentence, it's testing too much.
- **See it fail before you make it pass.** Skipping the red step means you never proved the test can fail — it might pass against broken code.
- **Don't write production code with no failing test demanding it.** If you're tempted, write the test that demands it first.
- **Don't test implementation details.** Test observable behavior and contracts, so refactoring doesn't break the suite.
- **Name and place the test for the behavior, not the chore.** A file, type, or test named after the task that produced it (`Touched`, `Fix`, `Coverage`, `WIP`) tells the next reader nothing; name it for what it verifies, and put it with the feature it exercises (see `csharp-tunit`).
- **Keep the suite fast.** Slow tests don't get run; tests that don't get run rot.
- **Commit on green.** A passing bar is a safe checkpoint.

## What this skill deliberately does NOT do

- No spec/design/requirements markdown. Don't generate one. If the user genuinely needs a design discussion, that's a separate, explicit ask — not a default step.
- No "let me plan the whole feature first" essays. Plan the *next test*, not the next ten.
- No asking a pile of clarifying questions. Pick the most obvious next behavior, write the test, and let failures tell you what's wrong. Ask only when truly blocked on intent.

## Bug fixes

A bug means a behavior you care about has no test, or a wrong one. Reproduce it as a failing test *first* — that proves you understand the bug and locks it shut forever. Then fix to green.

## Working with subagents

When tests span independent areas, hand each area to a subagent (see `dispatching-subagents`). Give each agent the same TDD contract: *write the failing test, show it red, make it green, do not touch unrelated code, return what you changed and the final test output.* Then run the full suite yourself to integrate.

For .NET / TUnit specifics (new-instance-per-test, parallelism, controlling time instead of sleeping), see `csharp-tunit`.
