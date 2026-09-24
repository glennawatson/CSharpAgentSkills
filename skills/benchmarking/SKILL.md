---
name: benchmarking
description: Use when measuring .NET performance with BenchmarkDotNet, or when about to claim something is faster, slower, or "already optimal". Covers making the numbers trustworthy on a noisy machine, reading which differences are real, capturing and analysing EventPipe traces, and running a suite in CI. Reach for this before any A/B, not after.
---

# Benchmarking

A benchmark you cannot resolve is not evidence. Most wrong performance calls come from comparing two
numbers whose error bars overlap, then reporting the difference as a result.

## The order of operations

1. **Build the benchmark before forming the theory.** Reading code never tells you where the time
   goes. Benchmark broadly — overall and micro — and let the numbers pick the target.
2. **Make the measurement resolvable** (below) before trusting any comparison.
3. **A/B one change at a time**, re-running the baseline in the same session. Numbers from different
   sessions on a shared machine are not comparable.
4. **Keep only what the benchmark proves.** If the bars overlap, the answer is "unresolved", not
   "no difference" and not "a win". Revert the change or make the measurement sharper — don't ship
   complexity on a hunch.

## Making the numbers trustworthy

Default BenchmarkDotNet runs on a desktop are dominated by scheduling noise. On Linux:

```bash
taskset -c 0-6 nice -n -20 dotnet run -c Release --project <benchmarks> -- \
  --filter "*Foo*" --warmupCount 5 --iterationCount 15
```

- **`nice -n -20`** — check `ulimit -e` first. A value of 40 means RLIMIT_NICE permits -20 *without
  root*. BenchmarkDotNet tries to raise its own priority and often fails silently on Linux, so set it
  on the command line rather than assuming.
- **`taskset`** — pin to one thread per physical core. Read the topology instead of guessing:
  `cat /sys/devices/system/cpu/cpuN/topology/core_id`. Two cpus sharing a `core_id` are SMT siblings;
  putting the benchmark on both halves of one core measures contention. Leave a core for the OS.
- **`--warmupCount` / `--iterationCount`** — `ShortRunJob` is 3+3 and exists for a fast smoke test,
  not for a verdict. Raise both when the answer matters.
- **Governor** — `cat /sys/devices/system/cpu/cpu0/cpufreq/scaling_governor` should say `performance`.
  Disabling boost tightens things further but needs root.

Measured effect of doing this: error on a ~570 ms benchmark fell from ±241 ms to ±18 ms, and on a
~390 ms one from ±558 ms to ±5.9 ms. Before, nothing under about 5% was visible at all.

## Reading the result

- **Compare error bars, not means.** `573.5 ± 18.1` against `589.6 ± 14.8` overlap: that is not a
  regression, it is an unresolved measurement.
- **`Allocated` is a counter, not a timer.** `MemoryDiagnoser`'s allocation column stays trustworthy
  even on an unpinned, noisy run. When timing is inconclusive, allocation often still decides — and a
  change that allocates less on the common path and more on the rare one is a trade to argue about,
  not an automatic win.
- **A result that appears at one input size and vanishes at another is suspect.** Reproduce across
  the `[Params]` range before believing it.
- **Watch the Gen2 column.** A single extra gen-2 collection can move a mean more than the change
  under test.
- **Server GC + DATAS (default since .NET 9) resizes the heap to observed usage**, which is one more
  source of a stray Gen2/heap-growth collection landing mid-run on a benchmark process that starts
  small and ramps up. If a run shows one outlier iteration with a live-heap jump, don't chase it as a
  real regression before checking `Server=true`/DATAS is the cause — pin `[GcServer(true)]` explicitly
  (or force Workstation GC) so every A/B run starts from the same GC mode instead of an ambient default.

## Allocation profiling

**A perf change is not done until a GcVerbose trace says what the change itself allocated.**
`MemoryDiagnoser`'s `Allocated` is a per-op total: it cannot separate your code from the harness or
from the framework, so "Allocated barely moved" does not answer "did my code allocate". Capture the
trace, attribute it, and name the number — including when the answer is "nothing".

Use BenchmarkDotNet's EventPipe diagnoser rather than an ad-hoc byte counter
(`GC.GetAllocatedBytesForCurrentThread` probes are a tangent, not the measurement):

```csharp
[ShortRunJob]
[EventPipeProfiler(EventPipeProfile.GcVerbose)]
public class FooProfiledAllocBenchmarks { ... }
```

That writes `.nettrace` and `.speedscope.json` beside the other artifacts. Analyse them with the
tools in `~/source/rxui/tools`:

```bash
# Ranked allocation types, sites and inclusive frames
dotnet run ~/source/rxui/tools/nettrace-analyzer.cs -- --top 15 <trace>.nettrace

# Just your own frames, with the framework noise filtered out
dotnet run --project ~/source/rxui/tools/TraceFocus -- --file <trace>.speedscope.json --analyzer-only
```

`TraceFocus` reports the focused share — the fraction of sampled time inside your code. That number
tells you whether optimising your code can matter at all before you spend time on it. A leaf-time of
near zero under a frame with large inclusive time means the cost is in what it *calls*, not in it.

Three things that otherwise waste a run:

- **Attribute by type name, not by namespace.** The benchmark project usually sits under the same
  namespace prefix as the code under test (`Foo.Benchmarks` under `Foo`), and its corpus-building and
  parsing dominate the trace. Filtering on the namespace reports the harness; filtering on the type
  under test reports the change.
- **Narrow the filter.** `--filter "*ProfiledAlloc*"` matches every profiled class in the project —
  hours of EventPipe runs. Name the classes you actually changed.
- **Walk the stack outward to the first frame of yours.** Allocations happen inside the framework; the
  question is which of your frames asked for them. Read each `GCAllocationTickTraceData`'s
  `CallStack()` and attribute the sample to the nearest enclosing frame you own.

Expect a well-written hot path to attribute at or near 0%. When a frame of yours does hold a real
share, the allocated *types* say why — framework symbol/binding types under your frame mean you asked
the framework a question, not that you allocated.

### The sampling floor — one tick is not a measurement

`GCAllocationTick` fires roughly every 100 KB allocated, so a short window is sampled once or not at
all. A subject that allocates ~500 KB in total yields five ticks, and a single 106,600-byte tick
against it reads as "20% of sampled bytes" — a sampling artifact, not a result.

- Before quoting a share, look at the **tick count** behind it. Fewer than about five ticks resolves
  nothing; a one-tick difference between two runs is noise in both directions.
- The fix is more iterations inside the same window, not a bigger corpus: repeating the measured
  region 30 times lifts a 1-tick window to 15–90 ticks and makes both the share and the top frame
  trustworthy.
- Report a small mover as *unresolved* and re-measure. Never call it a regression or a win.

### Separate the fixed cost from the per-item cost

A per-invocation setup cost and a per-item cost hide each other unless you measure them apart. Add a
third benchmark case with an empty/trivial input alongside the real ones: whatever it attributes is
the fixed cost every call pays regardless of size. Subtract it before judging a per-item number —
otherwise a fixed cost dressed up as "20% of the time" reads as a defect in the loop, not in the setup.

### Sweeping many subjects: one session, many windows

Starting an EventPipe session per subject is what makes BenchmarkDotNet cost 70–250 s per family —
the cost is the generated build, the session handshake and the rundown, not the code under test.
Sweeping hundreds of subjects that way is a day's work.

Instead: start **one** process-wide session, bracket each measurement with a marker `EventSource`
event carrying the subject's key, run every subject in that one process, then slice the trace by
timestamp afterwards. A 600-subject sweep becomes minutes, and you can take warmup-plus-iteration
timing in the same run.

- Serialize the subjects (or slice carefully) so windows do not interleave.
- Record each owned site as `frame <= AllocatedType`; the pairing is what lets you classify a
  residual instead of merely asserting it is irreducible.
- Validate as you sweep — if a subject's "should report" input reports nothing, its numbers are
  mislabelled, usually because a type it needs is missing from the reference set. Flag those rows
  rather than averaging them in.

This does not replace a pinned BenchmarkDotNet family for a specific A/B; it is how you find the
handful of subjects that deserve one.

## In CI

Keep it `workflow_dispatch` with a suite choice and a BenchmarkDotNet filter — a suite on every push
is wasted runner time. Two things worth copying into any benchmark workflow:

- **Raise the process priority** for the run, and say in a comment that the runner is shared so the
  numbers are for trend and regression detection, not absolute figures.
- **Fail when nothing was measured.** BenchmarkDotNet reports validation errors and still exits zero,
  and a job whose build failed exports rows reading `NA`. Both look identical to a clean run unless
  the workflow greps the exported reports for `NA` rows and for the reports existing at all.

Collect `*-report-github.md` into the step summary (capped — the summary is dropped whole past 1 MiB)
and upload the full JSON/HTML/CSV as an artifact.

## .NET 11: benchmark both runtimes before claiming a win

.NET 11's JIT (better bounds-check elimination, devirtualization, switch-expression and `SequenceEqual` constant-folding, faster NativeAOT interface dispatch) and Runtime Async (see `csharp-async`) change the baseline cost of code that hasn't changed at all. A change measured as a win on net10.0 alone may already be free on net11.0, or vice versa — the runtime moved, not just your code.

- **Multi-target the benchmark project** (`<TargetFrameworks>net10.0;net11.0</TargetFrameworks>`) and let BenchmarkDotNet's `[SimpleJob]`/multi-runtime support run both, or run the suite twice pinned to each SDK via `global.json` — either way, report both numbers, not just the newer runtime.
- **A "no change, still allocates the same" result on net10.0 can shrink or vanish on net11.0** purely from JIT/runtime-async improvements with zero code change — don't attribute that delta to your change if the baseline (unmodified code) shows the same shift.
- Once .NET 11 GA lands and net10.0 support windows close for a given repo, drop the net10.0 leg — until then, a claim that only holds on one runtime isn't a general performance claim.

## Don't

- Don't struct-ify a type, pool mutable state, or hoist work into `[GlobalSetup]` to move a number.
  That games the harness, not the program.
- Don't triage candidates by gut before measuring. The biggest wins hide in the pile you would have
  skipped as low-reward or too risky.
- Don't report a mean without its error.
