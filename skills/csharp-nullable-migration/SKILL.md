---
name: csharp-nullable-migration
description: Use when turning on nullable reference types (`<Nullable>enable</Nullable>`) for a C#/.NET codebase that currently has it off — planning the rollout order, running a Roslyn rewriter for the mechanical fixes (provable `?` annotations, `ArgumentNullException.ThrowIfNull` guards, redundant `!` removal), and triaging the remaining CS86xx warnings that need a human to read intent. Not for writing new nullable-enabled code — see `csharp-nullability` for the target-state rules this migration is converging on.
---

# Nullable migration — mechanical first, judgement second

Turning on `<Nullable>enable</Nullable>` on an existing codebase produces hundreds or thousands of warnings at once. Most of that count is mechanical (a field that's obviously `null` by default, a method that obviously `return`s `null`) and a Roslyn rewriter can fix it safely and provably. What's left needs someone to read the code and decide whether `null` there is a valid state or a bug — that part cannot be automated and rushing it produces exactly the silently-wrong annotations the feature exists to prevent.

This skill is the rollout playbook. For what correct nullable-enabled code *looks like* once you're there, defer to `csharp-nullability` — this skill does not repeat those rules, it gets you to the point where they apply cleanly.

## Strategy: incremental, never "flip the solution"

Enabling nullable solution-wide in one commit produces a warning wall nobody reads carefully, which either stalls the migration or gets "fixed" by mass-suppressing — the worst outcome, because it looks done and isn't. Roll out incrementally instead, and keep the build green between every step (see `csharp-verification`).

### The three settings, and what they actually do

| Setting | Effect | When to use it |
|---|---|---|
| `<Nullable>enable</Nullable>` | Full analysis + `?` annotations respected, warnings on | The end state for a project |
| `<Nullable>annotations</Nullable>` | `?` is legal syntax and is *emitted* in metadata for callers, but this project gets **no warnings of its own** | Mid-migration on a project whose own code isn't clean yet, when a leaf project it depends on needs its public surface annotated so callers benefit — lets you annotate the API without fixing every internal warning first |
| `<Nullable>warnings</Nullable>` | Warnings fire only where the compiler has *real* nullability info to reason from — i.e. when this project's code touches an already-annotated dependency (the BCL, or another project already on `enable`); `?` is **not** legal syntax in this project's own code | Rare: surfacing how badly a project's *use of already-annotated dependencies* is broken, before committing to annotate the project itself — not a general scouting tool for its own logic, and not a resting state |

Verify these yourself before relying on the distinction, because the third one is counter-intuitive — set each in a scratch project and check with two cases: (a) a local `return null;` on a non-nullable return type, using only this project's own types, and (b) dereferencing an already-nullable BCL member (e.g. `Environment.GetEnvironmentVariable(...).Length` without a check) with no `?` used anywhere. Result: `enable` warns on both. `annotations` warns on neither (and compiles a `?` silently). `warnings` warns on **(b) only** — it stays silent on (a), because without annotations turned on, this project's own declared types are all "oblivious" (no tracked nullable state), so a bare `return null;` on a plain `string` has nothing to compare against; it only sees a real nullable/non-nullable distinction where one already exists in an annotated dependency it calls into. Writing `string? Foo()` in `warnings` mode is rejected as CS8632 ("nullable annotation... only in `#nullable` annotations context") since annotations aren't turned on. In practice this makes `warnings` far less useful for gauging a project's own migration effort than it sounds — `annotations` (to ship the API) or just running `enable` on a throwaway branch to get the real count (below) are the tools actually worth reaching for.

Per-file `#nullable enable` (a directive, not an MSBuild property) is the finer-grained tool inside a project that isn't fully converted yet — useful for converting one file at a time within a large project, or for keeping a handful of legacy files opted out (`#nullable disable`) after the project flips to `enable`. `csharp-nullability` covers directive placement; this skill covers when to reach for file-level vs project-level rollout.

### Order of work

1. **Leaf/dependency-free projects first.** A project nothing else depends on can be flipped in isolation with no ripple. Find the dependency order with a discovery query (`roslyn-discovery`) over project references, or just read the `.slnx`/`.sln` graph — don't guess.
2. **Public API surface before internals.** Annotations on public members flow to every caller; an unannotated public method that's actually nullable silently defeats the analysis for everyone who calls it, forever, until someone happens to re-look at that specific method. Annotate `public`/`protected` signatures in a project deliberately and early, even if `internal` cleanup lags behind — `<Nullable>annotations</Nullable>` (above) is exactly the tool for shipping that surface before the whole project is warning-clean.
3. **Then work inward**, project by project in dependency order, from leaves toward the root/entry-point project.
4. **Keep the build green between steps.** Each project's flip is its own commit; a broken interim state blocks everyone else's work and makes `git bisect` useless for anything else happening in the repo concurrently.
5. **Don't do all of #2 through #4 by hand file-by-file with no visibility into progress** — track remaining work per project as you go (below), and lean on rewriters plus fanned-out triage (last two sections) once the mechanical pass is done, rather than reading every file serially yourself.

### Track remaining work

Count `CS86xx` (and the `CS8765`/`CS8767` override-mismatch family) per project from a `dotnet build` log rather than eyeballing the IDE's warning count, which only reflects whatever's currently open:

```sh
dotnet build MySolution.slnx /p:Nullable=enable -warnaserror- 2>&1 \
  | grep -oE 'warning CS8[0-9]{3}' \
  | sort | uniq -c | sort -rn
```

For a per-project or per-file breakdown (which is what you actually need to plan the next slice of work), parse the build log structurally instead of scraping text — the `roslyn-discovery` conventions apply: a throwaway `dotnet build -bl` binlog read with the `Microsoft.Build.Logging.StructuredLogger`, or simpler, `dotnet build /flp:logfile=build.log;warningsonly` and group the `warning CSxxxx in <file>` lines by file/project. Re-run after each pass and watch the count trend to zero for the project in flight — a stalled or rising count on a project you already "finished" means something regressed.

## What a rewriter can do safely

These are provable from the syntax tree plus, where noted, the semantic model — not guesses. Build one throwaway single-file `dotnet run` app per transform, following `roslyn-rewriters` conventions exactly (dry-run by default, `--write` to apply, `CSharpSyntaxRewriter` to preserve trivia, skip `obj`/`bin`). A worked, verified example is in `./examples/annotate-null-returns.cs`.

- **Add `#nullable enable` file headers** as the file-level rollout mechanism — syntax-only, trivial, but still diff it: a header above a file with a copyright banner needs to land in the right place, not before it.
- **Annotate fields/params/returns as `?` where the code provably assigns or returns `null`.** "Provably" means: a field initialized `= null` or never assigned in every constructor path; a method whose only `return`s are `null` or a literally-nullable expression; a parameter the method body checks `if (x == null)` before using. This needs the **semantic model** for anything beyond a literal — data-flow analysis (`SemanticModel.AnalyzeDataFlow`) to confirm a field really is unassigned on every constructor path, not just the one you happened to read.

  ```csharp
  // syntax proves it: the ONLY return is a null literal
  public string Find(string key) { return null; }   // -> public string? Find(string key)

  // syntax alone is NOT enough here — needs data-flow to confirm _cache is
  // never assigned in ANY constructor before treating it as "always null-able"
  private Dictionary<string, string> _cache;
  ```

- **Convert `if (x == null) throw ...`/early-return null-guards at public entry points to `ArgumentNullException.ThrowIfNull(x)`.** Syntax match on the `if` shape plus semantic confirmation that `x` is a parameter of a public method with reference type — see `csharp-nullability`'s guidance on why `ThrowIfNull` over a hand-rolled guard.

  ```csharp
  // before
  public void Configure(string connectionString)
  {
      if (connectionString == null) throw new ArgumentNullException(nameof(connectionString));
      // ...
  }

  // after — same contract, understood by flow analysis, one line
  public void Configure(string connectionString)
  {
      ArgumentNullException.ThrowIfNull(connectionString);
      // ...
  }
  ```

- **Add `[NotNullWhen(false)]`/`[NotNullWhen(true)]` to `bool TryX(out T value)` patterns** where the semantic shape matches (a `bool`-returning method with a single `out` parameter, and data-flow shows the `out` is only ever null on one of the two return paths). Cross-check the *shape* against `csharp-nullability`'s `Try*` section — this skill only adds the code, that skill says what "right" looks like.
- **Remove redundant `!`** — semantically, a null-forgiving operator on an expression the flow analysis already proves non-null is provably a no-op; safe to strip. (The opposite direction — adding `!` — is never a rewriter's job; see below.)

  ```csharp
  if (x is null) return;
  Use(x!);   // redundant: flow analysis already knows x is non-null here -> Use(x);
  ```

- **Mark a field non-nullable when it's provably initialized on every constructor path** (data-flow again: every constructor assigns it, or it has a field initializer, before any method can observe it null).

Every one of these needs `SemanticModel`, not text matching — "the field is never assigned" and "the parameter is checked before use" are data-flow facts, not syntax patterns. And every one is **dry-run by default with a diff**, same as any `roslyn-rewriters` tool: print what would change, require `--write`, read the diff before trusting it.

## Rewriter safety rules for this migration specifically

On top of the general `roslyn-rewriters` rules (edit the tree not the text, preserve trivia, bail when nothing changed):

- **One transform per pass, scoped to one project.** Don't build a mega-rewriter that does null-return annotation *and* `ThrowIfNull` conversion *and* `!` removal in one visitor — you can't diff-review a mixed change, and a bad interaction between transforms (one's output invalidating another's precondition) is hard to spot when they're tangled.
- **Build between passes**, not just at the end — a pass that compiles clean in isolation but breaks combined with the next one is a debugging session you don't need.
- **Commit per pass.** Each rewriter run is one revertible commit: "apply annotate-null-returns to Project.Core" — if pass 3 turns out wrong, you revert pass 3, not re-derive which of five tangled changes to undo.
- **Never auto-apply `!` as a mass fix.** A rewriter that inserts `!` wherever a warning fires is indistinguishable, in its effect on the codebase's honesty, from disabling the warning — it makes the *build* quiet while leaving the *bug* (if it was one) exactly where it was, now invisible. `!` insertion, when genuinely justified, is a per-site human decision (see below), never a bulk transform.
- **Never mass-add `?` just to silence a warning.** Adding `?` to a member/return type that was never actually meant to hold null "fixes" the compiler and breaks every real invariant that depended on it being non-null — the null just travels further before it NREs, now with no compiler warning to catch it. Every `?` a rewriter adds must trace to a specific `return null;`/`= null`/null-check line it can point at, not "there was a warning here."
- **Run tests after each pass, not just the build.** A behaviorally-inert transform (adding `?`) can't break runtime behavior by itself, but a `ThrowIfNull` conversion changes what exceptions get thrown and when — that's runtime-observable and needs the actual suite (`csharp-verification`).

## What must be decided per site — the triage procedure

The core question at every remaining warning: **is `null` here a valid state the code needs to handle, or a bug the type was hiding?** A rewriter cannot answer that; only reading the surrounding code's intent can.

The most common judgement call is CS8618 on a field that isn't set in the constructor. Read *why* before picking a fix — the four shapes look similar in the warning but need different code:

```csharp
// (a) set in a lifecycle/Initialize method called from every real entry point:
//     tell the compiler the guarantee instead of hiding the warning.
private string _name;

[MemberNotNull(nameof(_name))]
public void Initialize(string name) => _name = name;

// (b) must be supplied by the caller at construction, never legitimately null
//     (not for types a JSON deserializer fills; see "Deserialized/DTO types" below):
public required string Name { get; init; }

// (c) computed on first use, absent before that — genuinely late, not a bug:
private string? _cachedName;
public string Name => _cachedName ??= Compute();

// (d) actually optional, no value is a real state the rest of the code handles:
public string? MiddleName { get; init; }
```

Picking (d) — "just make it `?`" — for a field that's actually (a) or (b) is the single most common wrong-fix pattern in a large migration: it compiles, but every caller now has to null-check a value that was never really allowed to be null, and the real invariant (the field is always set after `Initialize()`/construction) stops being checked by anything.

Below is triage by warning code — what it usually means, and the fix vs. the "just make it compile" non-fix to avoid.

| Code | Usually means | Right fix | Wrong "fix" |
|---|---|---|---|
| **CS8600** (converting null literal/possible-null to non-nullable) | You're assigning something the compiler can't prove is non-null into a non-nullable local/field | Trace where the value came from; if it's genuinely always non-null here, add a guard or assert that lets the compiler see it; if it can be null, widen the local's type | Slap `!` on the RHS without checking whether it actually can be null |
| **CS8601** (possible null assigned to parameter/ref) | Same as CS8600 but at a call site — you're passing a possibly-null value where the callee's parameter says non-null | Check the callee's real contract; if it truly requires non-null, guard before the call; if the callee should accept null, that's the callee's signature to fix, not this call site | `!` at the call site papering over a callee that's about to NRE |
| **CS8602** (dereference of a possibly-null reference) | The immediately preceding logic doesn't prove non-null to the compiler, even if you "know" it does | Add the check the compiler needs (`is null` guard, `??`, pattern match) so the *analysis* proves it, not just you | `!` right before the dereference |
| **CS8603** (possible null returned from non-null-returning method) | The method's own body can produce null on some path not reflected in its signature | Decide: is that path a real bug (fix the logic so it never returns null) or a legitimate case (change the return type to `?` and push the decision to callers) | Returning `default!` to shut the compiler up |
| **CS8604** (possible null argument) | Same shape as CS8601 at the call boundary | Guard at the boundary (`ArgumentNullException.ThrowIfNull` if it's a public API) or trace why the value could be null upstream | `!` at the call site |
| **CS8618** (non-nullable field/property not initialized in constructor) | The classic one — field is set in `Initialize()`, a lifecycle method, DI property injection, or lazily, not the constructor | Pick the shape that matches reality: `required` (must be supplied at construction), late-init with `[MemberNotNull]` on the initializer (see `csharp-nullability`), a lazy-init pattern (`field ??= Compute()`), or genuinely nullable if it's optional | `= null!` to silence it without picking one of the above — that's a lie the type now tells forever |
| **CS8625** (null literal to non-nullable type) | Usually a default parameter value or explicit `null` passed somewhere non-nullable expects | Same triage as CS8600/8604 — is the parameter actually optional-nullable, or is this call site wrong | Changing the parameter to `?` reflexively without checking every other caller's assumptions |
| **CS8619/CS8620** (nullability mismatch in generic type argument, e.g. `IEnumerable<string?>` where `IEnumerable<string>` expected) | A generic collection/delegate's element nullability doesn't match what's being assigned/passed | Fix at the source: either the producing collection shouldn't contain nulls (filter/guard upstream) or the consuming signature should accept nullable elements (`where T : notnull` decisions from `csharp-nullability`) | Casting through `object` or `!`-ing individual elements to dodge the generic check |
| **CS8765/CS8767** (override/interface parameter or return nullability mismatch with the base/interface) | The override's annotation disagrees with what it's overriding — often because the base was annotated by a rewriter (or by hand) without updating every override, or vice versa | Decide the *contract* at the base/interface level first, then make every override match it — this is exactly why rewriters skip overrides/interface implementations (see above) | Adding `#pragma warning disable CS8765` per override instead of reconciling the contract |

Other per-site judgement calls that show up as these codes but need extra context:

- **`Dictionary.TryGetValue` / `FirstOrDefault()` results** — `TryGetValue`'s `out` is correctly `[MaybeNullWhen(false)]` already in the BCL; the warning tells you the code isn't checking the bool/null before use. `FirstOrDefault()` on a reference-typed sequence returns `T?` honestly (empty sequence → null) — don't `!` it, either check or use `First()` if empty should throw.
- **Deserialized/DTO types: default to nullable, not `required`.** For types that `System.Text.Json` fills, `required` is not a nullability tool. It is a wire-format contract: deserialization throws `JsonException` ("missing required properties") whenever the property is absent from the JSON, even when the property type is nullable. Adding it during a migration turns every payload that omits the field (older clients, partial updates, other producers, stored documents) into a runtime failure the migration never exercised. The reverse gap also exists: by default a non-nullable `string` property accepts a JSON `null` without complaint, unless the context or options set `RespectNullableAnnotations = true`. So for any reference-typed DTO member you can't prove is always present and non-null in every document:
  - Make it nullable (`string?`, `List<T>?`) and leave off `required`. This matches what the wire can actually send.
  - Deal with the consequences per type afterwards. Where each object is consumed, decide whether a missing value gets a default, gets validated at the boundary with a clear error, or is a real optional state. That is a per-object analysis done by reading how each type is used, not a rewriter pass.
  - Use `required` on a JSON type only when the contract genuinely rejects documents without the field, and you've confirmed every producer sends it.

  This applies to other serializers and ORMs that populate objects through setters too: check what each one does with missing and null values before annotating.
- **Events/delegates** — a nullable event field (`public event EventHandler? Changed;`) is usually correct (no subscribers yet); invoking it needs `Changed?.Invoke(...)`, not a non-null assertion.
- **Interop/reflection** — `Type.GetProperty`, `Activator.CreateInstance`, P/Invoke return types are often legitimately nullable and the compiler is right to warn; don't blanket-`!` reflection call sites, check what the specific API's real contract is (some do promise non-null given valid input; most don't).
- **API contract changes that break callers** — annotating a public member's nullability is a source (and sometimes binary-compatible-but-behaviorally-different) change for every external caller. For a published/shipped API, that's a deliberate versioning decision, not something a migration pass should make silently — flag it for review rather than auto-annotating public surface you don't control the consumers of.
- **Tests that pass `null` deliberately** (boundary/negative tests) — those are often *correct* as-is; the fix is usually `!` at the call site *in the test*, with a comment, because the test's whole point is exercising the null path against a signature that's honestly non-nullable. This is one of the few places `!` is the right answer, precisely because it's a deliberate, understood violation, not an accident.

## Dividing the remaining work

Once the rewriter pass has taken the mechanical bulk off the count, what's left is warnings that need someone to read the code. Group them — by project, then by file or type — from the tracked build-log counts above, and work through them with the judgement calls above.

For a large remaining count, fan this out rather than reading every file serially yourself: this is exactly the shape `subagent-driven-development` covers — repetitive, per-file work with a shared decision framework (this skill's triage table) applied independently per site. Give each worker, inline in its prompt (not a scratch file in the repo):

- The exact file(s) and warning list to work through (from the grouped build-log output).
- This skill's triage table, pasted in, so every worker applies the same CS8618-means-X, CS8602-means-Y reasoning instead of inventing its own.
- The constraint that a bulk "add `?` everywhere" or "add `!` everywhere" pass is a failure, not a shortcut — each site needs its own one-line justification for the fix chosen.
- The verification bar: the file must build with zero new warnings and the project's tests must still pass (`csharp-verification`) before the worker reports done.

For example, one worker's prompt for a single file might read:

```
Fix the 6 nullable warnings in Payments/InvoiceRepository.cs (listed below,
with line numbers and codes from the build log). Apply this triage table
[paste the CS8600-CS8767 table from csharp-nullable-migration] — read each
site's surrounding code to decide whether null is a valid state or a bug,
don't pattern-match a generic fix. Do not touch any other file. When done,
`dotnet build Payments.csproj` must show zero warnings from this file and
`dotnet test Payments.Tests.csproj` must still pass. Report the fix chosen
per warning with a one-line reason.
```

You still own consistency: spot-check a sample of what came back, watch for a worker that silently reached for `!`/`?` as a reflex fix rather than tracing intent, and run the real project-wide build + test gate yourself before calling a project's migration finished — a worker's self-report that "it's done" is not the same as a clean `dotnet build` + `dotnet test`.

## Worked example: `annotate-null-returns.cs`

`./examples/annotate-null-returns.cs` implements the first mechanical bullet above end to end: it walks a folder, finds methods whose body provably `return`s a null literal, and adds `?` to the return type — skipping return types that are already `?`, value types, bare generic `T`, `override`s, and interface implementations (each for the reason given in the source comments: those four need a contract-level decision, not a per-method one).

It was run against a small sample library (`Nullable` disabled) with cases for every skip rule — a plain method, an expression-bodied method, an already-`?` method, an `override`, an interface implementation, a generic `T` method, and a method whose *local function* (not the method itself) returns null:

```
$ dotnet run annotate-null-returns.cs -- SampleLib
  Describe: return null; -> annotate return type ?      # BaseWidget's virtual method
  TryFind: return null; -> annotate return type ?
  AlwaysNull: return null; -> annotate return type ?
would rewrite SampleLib/Repo.cs (3 method(s))
```

`--write` applied exactly those three, and left `AlreadyNullable` (already `?`), `Count` (value type, no null return), `Describe`'s `override`, `Find` (interface implementation), `GetDefault<T>` (bare `T`), and `NotAffectedByLocalFunction` (the null return is in a nested local function, not the method itself) untouched — matches the skip list by design, confirmed by reading the diff.

Then `<Nullable>enable</Nullable>` was turned on and the project built:

- **Before the rewrite**, with nullable enabled: **7** `CS8603` ("possible null reference return") warnings, one per method that returns a null literal.
- **After `--write`**, same build: **4** `CS8603` warnings remain — exactly the `override`, the interface implementation, the generic method, and the local-function case, i.e. the ones that need a human to decide the contract (per the triage table above).
- Build succeeded both times, 0 errors.

This is the shape to expect from a real rewriter pass: it closes the mechanical majority and leaves a small, correctly-flagged remainder for triage — it doesn't (and shouldn't) drive the count to zero by itself.

## Anti-patterns

- Setting `<Nullable>enable</Nullable>` solution-wide in one commit and letting the warning count sit in the thousands — nobody reads a wall that size, and it invites bulk suppression instead of real fixes.
- Treating `<Nullable>warnings</Nullable>` as a general scouting tool for a project's own code — verified above, it stays silent on the project's own oblivious-typed null returns and only flags contact with already-annotated dependencies.
- A rewriter that inserts `!` or adds `?` to make a warning disappear without tracing back to a provable null site — indistinguishable in effect from suppressing the warning outright.
- A single rewriter pass mixing multiple transforms (annotation + `ThrowIfNull` + `!` removal) so a bad interaction can't be isolated or reverted independently.
- Annotating an `override` or interface-implementing method's signature in isolation without reconciling the base/interface contract — produces the exact CS8765/CS8767 mismatch this skill's triage table exists to catch.
- Auto-annotating public API surface on a shipped library without flagging it as a deliberate, versioned contract change for consumers.
- Trusting a fanned-out triage worker's "done" without running the real build + test gate yourself — see `subagent-driven-development`'s same rule.
- Reflexively picking the nullable-field fix (CS8618 → "just add `?`") without checking whether the field is actually late-initialized, lifecycle-managed, or `required` — see the CS8618 walkthrough above.

## Checklist

- [ ] Rollout is incremental — one project (or file, via `#nullable enable`) at a time, never the whole solution in one step
- [ ] Verified the actual behavior of `enable`/`annotations`/`warnings` in a scratch project before relying on the distinction
- [ ] Leaf/dependency-free projects and public API surfaces converted first — annotations flow downstream to callers
- [ ] Build stays green between every step; each project's flip (and each rewriter pass within it) is its own commit
- [ ] Remaining work tracked by `CS86xx`-per-project count from a real build log, not eyeballed
- [ ] Mechanical rewriter passes are dry-run reviewed, one transform per pass, scoped to one project, semantic-model-backed, trivia-preserving
- [ ] No mass `!` insertion, no mass `?` addition to silence warnings — every mechanical change traces to a provable null site
- [ ] Every remaining warning triaged by reading intent against the table above, not pattern-matched to a generic fix
- [ ] Overrides/interface implementations reconciled at the contract level, not per-override
- [ ] Public API nullability changes flagged for deliberate review, not auto-annotated
- [ ] Deserialized (System.Text.Json) types use nullable reference members, not `required`, unless every producer is confirmed to send the field; missing values handled per type where consumed
- [ ] Tests run (not just build) after every pass; fanned-out triage workers verified against the real gate, not self-reports
