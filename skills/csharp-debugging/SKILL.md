---
name: csharp-debugging
description: Use when a C#/.NET bug, exception, test failure, or wrong behavior needs diagnosing. Enforces a hypothesis-driven loop — reproduce, read the real exception, isolate, change one thing, verify the root cause — instead of guessing and trying random fixes.
---

# Systematic debugging (.NET)

Random fixes waste time and often paper over the real bug. Debugging is a disciplined loop: **reproduce → read → isolate → hypothesize → change one thing → verify**. Don't skip to "try a fix" — you can't fix what you haven't located.

## Phase 0 — resist the guess

If your instinct is "let me just try changing X" before you understand the failure, stop. That's how symptoms get masked and root causes survive. Form a hypothesis you can test *first*.

## 1. Reproduce reliably

You cannot fix what you can't trigger on demand.

- Get it failing **consistently**. An intermittent bug is usually a timing/ordering/state issue — pin it down before fixing (see the async + execution-context notes in `csharp-async` / `csharp-tunit`).
- Capture the smallest repro: a failing test is ideal. Write one if you can — it both reproduces *and* becomes the regression guard (the `tdd` skill's bug-fix flow).
- Note the exact inputs, environment, and runtime (`net8`/`net9`/AOT vs JIT) — behavior can differ.

## 2. Read the actual error — all of it

Most .NET bugs announce themselves precisely. Read the whole thing before theorizing.

- **Exception type + message** tell you the category. `NullReferenceException`, `InvalidOperationException` ("Collection was modified"), `ObjectDisposedException`, `TaskCanceledException` each point somewhere specific.
- **Read the full stack trace top-down** to the first frame in *your* code. That line is the crime scene.
- **Unwrap aggregates.** `AggregateException.Flatten().InnerExceptions` and the `InnerException` chain hide the real cause — sync-over-async (`.Result`/`.Wait()`) wraps the true exception. Look inside.
- **Async stack traces** can be shallow. Enable first-chance exception breaks in the debugger to catch the throw at its origin, not where it surfaced.
- Don't ignore compiler warnings and analyzer hints — a nullable warning (`CS86xx`) often *is* the bug.

## 3. Isolate — shrink the search space

- **Binary search the cause.** Bisect by code (comment out / git bisect across commits) or by data (which input row triggers it?). Halve the suspect space each step.
- **Minimal repro.** Strip away everything that still leaves the bug present. What remains is the bug.
- **Check the boundaries.** Bugs cluster at edges: null/empty, first/last element, zero, concurrency races, disposed objects, culture-sensitive parsing/formatting, time zones.

## 4. Hypothesize, then test one thing

- State a falsifiable hypothesis: "the list is mutated while enumerated in `Foo`." 
- Test it with the **smallest probe**: a breakpoint, one log line, a conditional breakpoint (`when (id == 42)`), `Debug.Assert`, or a focused unit test.
- **Change one variable at a time.** If you change three things and it works, you don't know which mattered — and may have added two new bugs.

## Debugging a CI failure specifically

- **Read the actual failing log before theorizing.** Open the failing job's log (e.g. `gh run view <run-id> --log-failed`) and find the real error line before proposing a cause — never blame config, infra, or "CI flakiness" without evidence from the log.
- **Check whether the run is even current.** Compare the failing run's commit against the branch head; if the head already contains a fix, the run is stale and the answer is a re-run, not a code change.
- **"Flaky" is not a finding.** A test that fails under load or only intermittently is telling you the code has a race, a swallowed exception, or a visibility gap — read the failure, find the mechanism, and fix that, rather than re-running until it passes.
- **Never harden a test to hide a real defect.** Widening a tolerance, adding a retry, or weakening an assertion to make an intermittent failure stop showing up papers over the bug instead of fixing it (see `csharp-verification`).

## .NET tools to reach for

- **Debugger:** conditional breakpoints, first-chance exception settings (Break When Thrown), watch/immediate window, "Just My Code" off to step into framework.
- **Logging:** structured logs at the suspected path; check log level *before* formatting to avoid noise.
- **`dotnet-trace`** for CPU/perf and event flow; **`dotnet-dump`** + `dotnet-dump analyze` for hangs/deadlocks (`clrstack`, `syncblk`); **`dotnet-counters`** for live GC/thread-pool/allocation pressure.
- **Memory/leak:** a dump + heap analysis; watch for event-handler and static-reference roots, undisposed `IDisposable`.
- **Concurrency:** if it only fails under load or parallel tests, suspect shared mutable state, a missing `await`, or a captured `SynchronizationContext` — reproduce deterministically rather than adding sleeps.

## 5. Fix the root cause, then verify

- Fix the **cause**, not the symptom. Catching and swallowing the exception, adding a null check at the crash site, or widening a timeout usually hides the bug — ask *why* the value was null / the collection changed / the task cancelled.
- **Verify properly:** the repro/test now passes, *and* it failed before your change (prove the test bites). Run the full suite — your fix shouldn't break neighbors.
- **Explain it.** If you can't say in one sentence why the bug happened and why the fix addresses it, you probably patched a symptom.

## Anti-patterns

- Trying fixes before reproducing or reading the exception.
- Adding `try/catch` to make an error "go away."
- Changing several things at once.
- "It works now" without understanding *why* it was broken.
- Blaming the framework/compiler first — it's almost always your code or your assumptions.
