---
name: performance-bounded-deduplication
description: Use when removing production-code duplication from a C#/.NET codebase (especially Roslyn analyzers and code fixes) under hard performance limits - e.g. "no more than 5% allocation or 10% time regression". Covers the full loop - Roslyn readers that find whole-body, fragment and forwarder duplication; the golden path for consolidating (static helpers, parameters, Func with state, no inheritance/interfaces/forwarders/ValueTuple); Roslyn rewriters that retarget callers; the build/test gate; a TUnit EventPipe baseline to find where cost lives; one-session A-B-B-A sweeps and BenchmarkDotNet to break it down; confirmation runs; and the evidence standard for duplication that stays.
---

# Performance-bounded deduplication

Deduplication that makes the product slower is not a win. The loop below removes duplication **only as far as
the measurements allow**, and records proof for everything that stays. Every commit is measured against the
limits.

Related skills: `roslyn-duplicate-detection` (the percentage view and the original whole-body detector),
`roslyn-rewriters`, `roslyn-analyzer-performance`, `benchmarking`, `csharp-tunit`, `csharp-verification`.

## The rules that decide every merge

Write them into the report before starting; they are what "proof" is judged against.

- **Limits are ceilings, not targets**: allocations may regress at most N% (typically 5), time at most M% (typically 10), in any measured subject and path. Breach -> revert and record the numbers.
- **Golden path**: static helper classes or extension blocks first; composition only when a static cannot hold the state; **never** inheritance, base classes, interfaces or virtual dispatch merely to share code (framework contracts - `DiagnosticAnalyzer`, `CodeFixProvider`, `FixAllProvider`, walkers - are exempt).
- **Delegates**: `Func<>`/`Action<>`. A custom delegate only where `in`/`ref`/`out` cannot be expressed (`ActionIn`, `FuncRef`, `TryFunc` with `[NotNullWhen]`), and each one is justified in the report.
- **No `ValueTuple`** in signatures: a named `record struct` in its own file.
- **No forwarding methods**: a method whose body only passes its own parameters to a helper is deleted and its callers call the helper. A wrapper that adds caller-specific arguments (kinds, keys, rules, a test) is allowed.
- **Proof for retained duplication** is one of: a measured breach; a framework contract; distinct framework types with no common member (no generic body can compile); a goal rule (e.g. merging needs an interface). *Readability, complexity, "a delegate might be slower" and "more code than it saves" are not proof.*
- **Never reject a faster or lower-allocation version** for readability.

## The loop

```
detect -> classify -> consolidate (rewriter) -> gate (build + all tests) -> commit
       -> A-B-B-A measure -> confirm flags -> revert breaches -> report -> detect again
```

Commit each verified wave separately (small, logical commits) so a breach can be reverted alone; squash at the
end only if asked.

### 1. Detect - four passes, all syntax-only single-file apps

| Pass | Example | Finds |
| --- | --- | --- |
| Whole-body type-2 | `examples/detect-clones-named.cs --min-tokens 40 --skip-members Initialize` | method/accessor/ctor/operator bodies equal after `$id`/`$lit` canonicalisation |
| Whole-body type-1 | same with `--exact` | text-identical copies. **Zero exact sets means every residual differs in some identifier - read the bodies before calling any of them a clone** |
| Bodies printed | same with `--print-bodies` | what actually differs in each set (a kind? a type? a callee?) |
| Statement windows | `examples/detect-fragment-clones.cs --statements 3 --min-tokens 60` | duplicated runs inside otherwise different members - the partial copies whole-body scans miss |
| Forwarders | `examples/find-forwarders.cs` | `PURE` methods that only forward parameters to another type's static member, with call counts |

Also grep-free structural audits worth writing when relevant: holders of per-compilation state
(`class X { Lazy<...> }` shapes), custom delegate declarations and their reference counts, interfaces/abstract
classes/virtual members declared by the codebase.

Run readers on a spare core (`taskset -c 7 nice -n 19`) so they never disturb a pinned benchmark.

### 2. Classify every set by what differs

Print the bodies and sort each set into exactly one bucket:

| What differs | Action |
| --- | --- |
| Nothing but names of locals | merge - plain helper |
| Constants (`SyntaxKind`s, descriptors, option keys, strings) | merge - pass them as parameters; no delegate needed |
| One node/type-specific member access on the **same** framework type | merge - parameter or small `Func` |
| A predicate/step/callee | merge with `Func<...>`; pass state as a `TArg` so the lambda is `static` (no closure) - then **measure** |
| Distinct framework types with no shared member (`ParameterListSyntax` vs `ArgumentListSyntax`, `SyntaxTokenList` vs `SyntaxList<T>`, each `Update(...)` overload) | retain - type-level proof |
| Private mutable scan structs with different state | retain - merging needs an interface over the struct (goal rule) |
| Framework override bodies (`Initialize`, `RegisterCodeFixesAsync` one-call configs) | retain - contract proof |
| Same shape, different computation (two switches mapping different node sets) | not a duplicate; say so with the difference |

Check existing helpers before adding one - the codebase often already has `StringArrays.ContainsOrdinal`,
`TypeRelations.IsOrImplements`, `SyntaxNames.GetMemberName`. Watch semantic traps: a name-first interface
lookup misses explicit implementations; `OriginalDefinition` vs exact symbol equality are different helpers.

### 3. Consolidate with rewriters, not hand edits

Many call sites across packages, tests and benchmarks: use a Roslyn rewriter so the change is uniform.

- `examples/inline-forwarders.cs` - spec `path|Method`; verifies arguments are the parameters in order, maps generic type arguments, skips overloads, retargets qualified references everywhere (including tests/benchmarks) and deletes the forwarder. Dry-run first.
- `examples/add-aggressive-inlining.cs` - reads a build log's "add AggressiveInlining" diagnostics and adds `[MethodImpl(AggressiveInlining)]` plus the using, keeping doc comments attached.
- The `roslyn-rewriters` skill covers the general pattern; a per-class spec rewriter (`path|Class|member-to-delete|call-to-retarget`) is the workhorse for code-fix consolidation.

Consolidation patterns that measured well:

```csharp
// Constants as parameters - no delegate, same inlined compare
internal static bool ContainsAny(in SyntaxTokenList modifiers, SyntaxKind first, SyntaxKind second, SyntaxKind third, SyntaxKind fourth, SyntaxKind fifth)

// State passed through, so the lambda is static and allocates nothing
internal static bool Any<TItem, TValue>(TItem[] items, TValue value, Func<TItem, TValue, bool> matches)
ListScan.Any(prefixes, value, static (prefix, candidate) => HasPrefix(candidate, prefix));

// Two alternatives: a named record struct, never a tuple
internal readonly record struct StyleChoice<TStyle>(string Text, TStyle Style);

// One loop, the cursor passed by ref
internal static string? NameOf(AttributeArgumentSyntax argument, IMethodSymbol constructor, ref int positional)
```

Overload trap: two generic overloads with the same arity and target-typed `new(...)` arguments bind to the wrong
one (`new("x", Enum.Value)` became `new string(char, int)`). Give the second overload its own name.

### 4. Gate

Build the whole solution with warnings as errors, then every test project. Bound parallelism on memory-tight
machines (`-m:4`, `--max-parallel-test-modules 2`) - an unbounded run can be killed and look like a failure.
New analyzer lint on the helper code (inline-forwarder rules, method-group rules, target-typed `new`) is part of
the work, not noise. Add a unit test for every new helper before committing.

### 5. Measure - from coarse to fine

**a. TUnit baseline (where does cost live at all?)** - `examples/tunit-baseline/`. Inject a test executor and an
EventPipe `GcVerbose` session into the existing test projects (no source changes), bracket each test body and each
analyzer run with `EventSource` markers, serialise tests, and slice the one trace afterwards with
`examples/tunit-baseline/slice-test-trace.cs`. The existing suite becomes a ranked list of subjects by sampled
bytes and duration. Use it to pick subjects and build corpora; do not use it as the A/B verdict (test bodies do
setup work you did not change).

**b. One-session sweep, A-B-B-A** - the verdict for many subjects. Snapshot the baseline and candidate *assemblies*
(copy the built `.dll`s into `ab-<name>/<package>/{analyzers,codefixes}`; never rebuild a baseline from a dirty
tree), then run every subject in one process per build with one EventPipe session and a marker per subject
window: baseline then candidate (r1), candidate then baseline (r2). Paths: analyzer `startup` (one empty class),
`clean`, `violating`; code fix `register`, `apply`, `fix-all`. Compare per order with
`examples/ab-verdict.cs`:

- bytes: resolved only if the runs differ after widening each by 2 GC ticks and both have >= 5 ticks;
- time: resolved only if the error intervals do not overlap;
- a **breach counts only if it reproduces in both orders**.

**c. Confirm flags** - a both-order flag at 30 iterations or 400 ms windows is a candidate, not a result. Re-run
just those subjects (plus two unchanged controls) at 1,000 iterations / 2 s windows. Short-window flags are often
just between-process variance and vanish on the longer run; a real regression repeats with the same sign and
similar size - e.g. a registration path allocating extra bytes because a shared overload fetched a syntax root
the provider never read.

**d. BenchmarkDotNet for one subject** - `examples/benchmarkdotnet/SnapshotAbBenchmarks.cs` when a single site
needs a precise answer or an allocation breakdown: `[MemoryDiagnoser]`, `[EventPipeProfiler(GcVerbose)]`,
in-process toolchain, the snapshot directory as a `[Params]`. Pin with `taskset -c 0-6 nice -n -20`.

Machine discipline: pin benchmarks to physical cores, run tools on the spare core, **never build or compile
anything while a benchmark runs - not even a file-based app pinned to the spare core, because an already-running
compiler server or build node is not pinned and lands on the benchmark cores** (queue the build behind the sweep
with a background wait); gate each run on a quiet machine, and pass explicit subject filter files - a stale
default filter silently measures the wrong subjects.

### 6. Report

Keep six sections current as work lands (not at the end):

1. Duplication removed - family, consolidation, commits.
2. Duplication remaining - scan numbers (`N sets, L lines, P% at T tokens, type-2, skipped members`) and each family's state.
3. Evidence - one row per retained family, stated as the proof type above.
4. Abstractions removed - holder classes, delegates, forwarders, interfaces.
5. Permitted exceptions - framework inheritance, justified custom delegates, polyfill tuples.
6. Benchmarks - baseline, candidate, scope, orders, summed bytes per path, every both-order flag and its confirmation result, verdict.

Write outcomes as they are ("accepted", "breach fixed in <commit>", "unresolved in one order"), with the numbers.

## Pitfalls seen in practice

- A shared registration overload that "helpfully" fetches the root or semantic model moves binding cost onto the lightbulb path for every provider that never needed it - measure register separately from apply.
- Replacing hand-written per-compilation holders: keep run-once semantics where the original locked, and do not replace holders that build with a cancellation token or publish two independently gated values.
- Forwarders you create while consolidating count too: after a wave, run `find-forwarders.cs` again.
- The detector's percentage can stay flat while real duplication drops (fragment clones are invisible to it) - report the fragment pass separately.
- Summaries and memory from earlier sessions go stale; re-read the tool's usage before re-running it with old arguments.
