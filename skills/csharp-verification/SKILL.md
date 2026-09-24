---
name: csharp-verification
description: Use before claiming a C#/.NET task is done. "Done" means it builds clean, the analyzers are satisfied, and the tests pass — verified by actually running them, not assumed. Roslyn analyzer warnings are failures, not noise.
---

# Verification before "done" (.NET)

Never say a change is complete on the strength of "it should work." Completion is something you *prove* by running the gate below. If you didn't run it, it isn't done — and saying it is erodes trust fast.

## The gate (all must pass)

1. **It builds clean.**
   - `dotnet build` (or the solution's build) with **no errors and no new warnings**.
   - If the repo sets `TreatWarningsAsErrors` / `-warnaserror`, respect it — a warning *is* a failure there. If it doesn't, treat new warnings as failures anyway.
   - **Build and test the whole solution in one command, never loop over projects.** `dotnet build <solution>` and `dotnet test <solution>` already cover every project and target framework; a `for` loop over `.csproj` paths burns time, can skip a project you forgot to list, and hides which one actually failed. Drop to a single project only while iterating on that project's own compile errors.
   - **Build every target framework before calling it done**, not just the one you're iterating on — a multi-targeted project routinely passes on one TFM and fails on another (an API or analyzer that only exists, or only fires, there).
   - Formatting: scope `dotnet format` to what changed. A bare `dotnet format <project|sln>` reformats the whole tree and buries the real diff in noise — use `--include <changed-file>`, and prefer one fixer at a time (`dotnet format analyzers --include <file> --diagnostics <ID>`) over applying every rule at once.

2. **The analyzers are satisfied — this is the part people skip.**
   - Roslyn analyzers encode the project's actual rules: the .NET SDK analyzers, `Microsoft.CodeAnalysis.NetAnalyzers`, StyleCop, `Roslynator`, any first-party analyzers in the repo, and the severities set in `.editorconfig` / `Directory.Build.props`.
   - A build that is green **but emits analyzer warnings is not done.** Read each warning and fix the underlying cause.
   - **Do not silence to pass.** No reflexive `#pragma warning disable`, no `[SuppressMessage]`, no dropping a rule's severity in `.editorconfig` just to get a clean run. A suppression is a deliberate, justified decision (with a comment saying *why*) — not a way to make the gate shut up.
   - Run analysis the way CI does: `dotnet build` surfaces them; `dotnet format analyzers --verify-no-changes` and `dotnet format style --verify-no-changes` catch formatting/style drift the build may not fail on.
   - If you genuinely believe an analyzer rule is wrong for this codebase, raise it with the user — don't unilaterally suppress.

3. **The tests pass — because you ran them.**
   - Run the actual suite (`dotnet test`, or TUnit via `dotnet run -- ...`), not "the tests should still pass."
   - Run the tests relevant to your change *and* a broad enough set to catch collateral damage. If you touched shared code, run the whole suite.
   - New behavior has a new test; a bug fix has a test that fails before the fix and passes after (see `tdd`).

4. **It actually does the thing.** For user-visible behavior, confirm the real behavior — run the app / endpoint / command, don't just trust green unit tests.

5. **Inspect the artifact when the failure is in packaging/resources/build output.**
   - If the bug is "this DLL/package still carries X" or "MakePri/resources are wrong", verify against the built artifact, not your mental model of the source.
   - Use `csharp-assembly-inspection` to check manifest resources, assembly references, and runtime-visible surface, plus the actual package/output file layout.

6. **Read coverage as a union, and judge the change by patch coverage.**
   - A repo that links shared source into several projects (`<Compile Include="..\Shared\Foo.cs"/>`) reports that file at 0% in every package whose tests do not touch it. A single project's report is not the number; merge every report and count a line covered if **any** run covered it.
   - Whole-file coverage of the files a change touched charges pre-existing gaps against the change. The honest number is **patch coverage** — of the executable lines this diff *adds*, how many are covered. Parse the unified diff for added line numbers and intersect with the merged report.
   - Compare patch coverage against the repo's existing union rate. Equal means the change held the line; below means it lowered the bar. Say which.
   - If the stated gate is 100% and the measured union is not, the gap is pre-existing — name it, don't imply the change caused it, and don't claim the gate passed.

## Coverage

- **Whole-file target is 100%.** Cover with real tests, not by weakening the bar.
- **Prefer `internal` + `InternalsVisibleTo` over `public` to make code reachable from tests.** Widen visibility only for a test assembly, never as a general-purpose way to expose internals to other shipping code (see `csharp-api-design`).
- **`[ExcludeFromCodeCoverage]` is a last resort, and only on a minimal extracted helper**, not a whole method or class of real logic — reserve it for genuinely untestable code: cross-thread races, sync-over-async bridges, unreachable defensive branches, async-iterator dispose epilogues. Extract just that fragment into its own small helper and exclude the helper, so the exclusion's blast radius is visible and minimal.
- **Reproduce a coverage gap as the union across every test project and every TFM the CI report covers**, not one project or one framework run locally — see the patch-coverage bullet above for why a single-project number misreports which lines are actually covered.

## Broken build

- **A red build, analyzer failure, or failing test is yours to fix, whatever its origin.** Whether the defect predates your change doesn't change what makes the gate green — find the cause and fix it rather than characterizing it as pre-existing.
- **"Flaky" is not a diagnosis.** A test that fails under load or only sometimes is reporting a real race, a swallowed exception, or a visibility gap — find the mechanism, don't re-run until it's green.
- **Never harden a test to hide a real defect** (widening a tolerance, adding a retry, weakening an assertion) instead of fixing the code it's failing on.

## .NET 11: faster verification loops with `dotnet test`

On the Microsoft.Testing.Platform-based `dotnet test` (opt in via `global.json`'s `"test": {"runner": "Microsoft.Testing.Platform"}`; SDK 11 flags), a few flags make the gate faster to run repeatedly without weakening it:

- `--no-dependencies` — skip rebuilding referenced projects when you know they haven't changed. Don't use it after touching shared code; that's exactly the "did I break something upstream" case this gate exists to catch.
- `--maximum-failed-tests <N>` — stop the run once N failures are hit, so a broken change fails fast instead of grinding through the whole suite. Fine for an inner-loop rerun; run without it before declaring the gate clean, so you see the *actual* full result, not just "at least N things are broken."
- `--timeout <duration>` — bound total run time; catches a hung test instead of a stuck terminal.
- `--use-current-runtime` (`--ucr`) — target the SDK's own runtime instead of the project's pinned TFM runtime, useful when probing behavior on the installed prerelease SDK.
- `--list-tests json` — machine-readable test discovery, useful for scripting "did my new test actually get discovered."
- Still true: **don't use `--no-build`** to skip a fresh build before asserting the gate passed — see `csharp-tunit` for the same rule. These flags make the loop *faster*, not a substitute for the full run stated in the final report.

## Report honestly

- State **what you ran** and the **actual result**: "`dotnet build` clean, 0 warnings; `dotnet test` 214 passed; analyzers clean via `dotnet format --verify-no-changes`." Specifics, not "everything passes."
- If something failed, is flaky, or you skipped it — **say so**. A surfaced problem is worth more than a false "done."
- Don't claim a category you didn't check. If you didn't run the app, don't imply you did.

## Anti-patterns

- "It should compile / the tests should pass" — assumption stated as fact.
- Green build with analyzer warnings left unread.
- `#pragma warning disable` / `[SuppressMessage]` / lowering `.editorconfig` severity to force a clean run.
- Running one narrow test and declaring the whole change verified.
- Reporting success in categories you never executed.

## Quick checklist

- [ ] `dotnet build` — 0 errors, 0 new warnings (warnings-as-errors respected)
- [ ] Analyzers clean — no new warnings, no suppressions added to silence them
- [ ] `dotnet format --verify-no-changes` (style + analyzers) clean
- [ ] Tests actually run and pass; new/changed behavior is covered
- [ ] User-visible behavior confirmed by running the real thing
- [ ] Report states exactly what was run and the real results
