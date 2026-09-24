---
name: csharp-performance
description: Use when writing or reviewing C#/.NET code on a hot path where allocations, GC pressure, or throughput matter. Distils the techniques used across ReactiveUI, Splat, Akavache, Fusillade, Punchclock and Roslyn-style parsers. Ships with companion helper classes in ./helpers. Measure first — these are for proven hot paths, not everywhere.
---

# C# performance toolkit

These are the patterns the high-throughput .NET libraries in `~/source` actually use to stay fast. They trade a little complexity for fewer allocations, less GC pressure, and lock-free reads. **Apply them on measured hot paths, not by default** — on cold paths they just cost readability. Profile first (BenchmarkDotNet for micro, `dotnet-counters` for live GC/alloc/thread-pool, `dotnet-trace` for flow).

Companion helpers in `./helpers/` are drop-in implementations of the most reusable patterns — change the `PerfHelpers` namespace to suit your project.

This skill is the **allocation/caching/concurrency** axis. For the **data-layout / cache-coherence** axis — struct-of-arrays for tight loops, struct field ordering, flat data instead of inheritance, static helpers that carry only the memory they touch — see `csharp-data-layout`.

## 0. What the runtime now does for you (by version)

Know what's already free before hand-applying a trick below — otherwise you're paying readability for something the JIT already gives you.

- **.NET 8 — Dynamic PGO on by default.** The JIT instruments hot call sites at runtime and inserts a guarded-devirtualization (GDV) fast path per observed concrete type at an interface/virtual call. This is the mechanism behind most of what follows.
- **.NET 9/10 — escape analysis stack-allocates small non-escaping objects/arrays/boxes.** An object, small array, or box that's allocated, used, and provably dead within one method — never returned, stored to a field/collection, or captured by something that outlives the call — can live on the JIT's own frame instead of the GC heap: a small array built and used locally allocates nothing, while the same array returned from the method is heap-allocated. Micro-tricks that exist purely to dodge a small, obviously-local allocation (e.g. hand-rolling a tuple-of-locals instead of a tiny local array/object) aren't worth the readability cost *if the value genuinely never escapes the method* — check with `[MemoryDiagnoser]` before deleting a pooling/caching trick on the strength of this, since "never escapes" is easy to get wrong by adding one more line.
- **.NET 10 — `foreach` over `List<T>`/`T[]` through an `IEnumerable<T>`-typed reference gets much cheaper.** Conditional escape analysis lets the JIT stack-allocate and field-promote the enumerator even when it's only proven non-escaping behind a not-yet-resolved GDV type-test guard. On net11.0 a `foreach` over a `List<T>` through an `IEnumerable<T>` reference performs the same as through the concrete type, with no enumerator allocation; on net10.0 the gap is usually but not always closed. Casting/storing as the concrete collection type purely to avoid enumerator-allocation/dispatch overhead in a hot `foreach` isn't necessary on net10.0+ for plain `List<T>`/array enumeration — keep the concrete type where it's already natural (better in every other way), but don't add it as a perf-only change without measuring.
- **.NET 11 — broader bounds-check elimination, more constant folding, more devirtualization.** `arr[i+1]`, index-from-end (`span[^1]`), and `span[0]` after an `!span.IsEmpty` guard no longer need manual restructuring to drop the redundant check. Switch expressions over small constant sets, and `string`/`ReadOnlySpan<T>.SequenceEqual` calls where both sides are compile-time constants, fold away entirely. Generic virtual methods, default interface methods on generic interfaces, and `Activator.CreateInstance<T>()` results devirtualize where they didn't before. None of this requires a code change — it's automatic on net11.0.

**What's still on you, no version changes this:**
- Anything that actually escapes — returned, stored in a field/collection, captured by a closure/state machine that outlives the call, or boxed into something retained.
- **LINQ and hand-written iterators (`yield return`) still allocate in hot loops.** The compiler-generated `MoveNext` for these contains a `try`/`finally`, and the JIT still cannot inline a method containing exception handling — so escape analysis can't see through the state machine to prove the enumerator/closures are dead. The "manual `for` over `foreach`/LINQ in hot loops" rule below is unchanged for these; it's specifically plain `List<T>`/array `foreach` that got cheaper.
- Allocations inside a loop are only stack-allocated when the JIT can prove the value is dead across iterations — harder to prove than a straight-line method, so don't assume a loop-local allocation is free without measuring.
- Large objects/arrays — stack allocation is JIT-size-bounded; it isn't a substitute for pooling something big.
- Dictionary/HashSet enumeration and methods with multiple overlapping enumerators are only partially covered — measure rather than assume.

**GC: DATAS (Server GC, default since .NET 9).** Server GC now sizes its heap/heap-count dynamically to the process's actual working set (Dynamic Adaptation To Application Sizes) instead of assuming "use every core" — a real win for container/cloud memory footprint. The tradeoff: a cache sized for peak load no longer sits at a GC-provisioned peak-size heap for free — the heap grows and shrinks around actual usage, so an oversized long-lived cache causes more heap resize churn than it used to. Bound caches (capacity/TTL eviction) rather than relying on Server GC to just absorb a large working set. `DOTNET_GCgen0size`, `DOTNET_GCHeapCount`, and `DOTNET_GCDynamicAdaptationMode=0` opt back toward the old fixed-sizing behaviour if you've measured DATAS hurting a specific latency-sensitive workload — don't flip it defensively without a benchmark.

## 1. Kill allocations on hot paths

Allocation is the usual enemy: every temporary is future GC work. Observed across the repos:

- **`ref struct` for stack-only state** — parsers/scanners that hold a `ReadOnlySpan<char>` cursor can't be boxed or escape to the heap. Zero allocation by construction. *(SourceDocParser `DocXmlScanner`)*
- **`readonly struct` + `in` params** — small value-shaped types (change records, work items, time intervals) stack-allocate; pass large ones `in` to avoid the copy. *(ReactiveUI `CollectionChanged`, Primitives `TimedWorkItem`)*
- **`Span<T>`/`ReadOnlySpan<T>` slicing** — `span[start..end]` is a view, not a substring. Slice instead of `Substring`/`ToArray`. Use `stackalloc` for small scratch buffers.
- **Cached `SearchValues<char>`** — `static readonly SearchValues.Create(" \t\r\n")` then `IndexOfAnyExcept(...)`; far faster than `IndexOfAny(new[]{...})` which allocates per call. *(SourceDocParser)*
- **`string.Create(len, state, span-fill)`** — build a string in one exact-sized allocation, no intermediate concatenations; `static` lambda + `TState` only — a capturing lambda still allocates a closure even under .NET 10's delegate escape analysis (the closure display class isn't covered, only the `Func`/`Action` wrapping it). .NET 9+ also lets `TState` be a `ref struct`, so a span can be passed as state directly instead of being captured. *(PublicSurfaceProbe)* → full version-by-version breakdown in `csharp-string-handling`.
- **`StringBuilder.GetChunks()` + `ArrayPool<byte>`** — encode/stream a builder without ever materializing the full string. *(PageWriter)* → see `PooledStringBuilder` helper for the rent/return side.
- **Manual `for` over `foreach`/LINQ in hot loops** — avoids iterator/closure allocation and virtual dispatch. LINQ is fine on cold paths; not in per-frame/per-notification code. *(ReactiveUI `ViewModelActivator`, SourceDocParser)* — **exception:** plain `foreach` over `List<T>`/`T[]`, including through an `IEnumerable<T>`-typed reference, is close to free on net10.0+ (see §0) — this rule is really about LINQ operators and `yield return` iterators, which still allocate because their generated `MoveNext` blocks inlining.
- **Concrete collection types** — declare `List<T>`, `Dictionary<TKey, TValue>`, `HashSet<T>`, `T[]`, never `IList<T>`/`IDictionary<TKey, TValue>`/`ICollection<T>`/`ISet<T>`, so calls are direct and inlinable instead of interface dispatch (CA1859). See `csharp-collections-modern`.
- **Non-capturing callbacks** — `GetOrAdd(key, static k => ...)` and `Do(onNext: static _ => {})` don't capture `this` or locals, so no closure is allocated. The saving comes from not capturing; `static` guarantees it stays that way (see `csharp-static-lambdas`). Pass state via the `TArg` overload instead of capturing.
- **Pre-size collections** — `new Dictionary(32)` / `new List(capacity)` when the size is known, to skip reallocation/rehash churn. *(Splat resolver)*
- **Reuse singletons** — cache common immutable values (`EventArgs`, `Unit`, `true`/`false` observables, empty arrays via `[]`) instead of re-allocating. *(ReactiveUI `SingletonPropertyChangedEventArgs`, `SingleValueObservable`)*
- **`[MethodImpl(MethodImplOptions.AggressiveInlining)]`** on tiny hot helpers/getters to remove call overhead. Don't sprinkle it — only where a profile shows the call cost.
- **Pool transient buffers/builders** — `ArrayPool<T>.Shared.Rent(...)` in `try/finally`, or a builder pool wrapped in a `readonly struct` rental that returns on `Dispose`. → `PooledStringBuilder` helper.

## 2. Cache the expensive stuff

- **`static readonly Type Type = typeof(T)`** per closed generic — the JIT specializes one instance per `T`; hot paths skip repeated `typeof`/metadata cost. → `TypeCache<T>` helper.
- **`FrozenDictionary<K,V>` rarely.** Reads are faster than `Dictionary`, but construction is expensive and hurts startup. Only use it for a table read a very large number of times in a long-running process, and only after a benchmark that includes construction cost shows a win. Default to `Dictionary`/`HashSet`. *(CatalogIndexes)*
- **Compile expressions to delegates once** — turn a property-access `Expression` into a cached getter delegate at setup; reuse it per notification instead of reflecting each time. *(ReactiveUI `CompiledPropertyChain`)*
- **Bounded memoization (MRU)** for expensive reflection/factory resolution — cache hits dwarf the lookup cost; bound the size so it can't grow unbounded. *(ReactiveUI `Reflection` caches)*
- **Inline state slot to skip `ConditionalWeakTable`** — when an object can store its own auxiliary state in a field (behind an interface), you avoid a process-wide table lookup per access. Fall back to `ConditionalWeakTable` only for types you can't modify. *(ReactiveUI `IReactiveObjectStateSlot`)*

## 3. Lock-free / minimal-lock concurrency

Favour atomic reads over locks when reads vastly outnumber writes.

- **Copy-on-write snapshot** — writers clone + publish a new immutable array under a lock; readers `Volatile.Read` the reference and enumerate with **no lock at all**. The workhorse pattern in Splat's resolver and ReactiveUI's registries. → `CopyOnWriteList<T>` helper.
- **`Volatile.Read/Write`** for flags/counters/snapshot refs — memory-barrier visibility without mutual exclusion. *(Splat, Punchclock)*
- **`Interlocked`** for counters and assign/dispose-once — `Increment/Decrement`, `Exchange`, and `CompareExchange(ref x, new, null)` to claim a one-time slot. → `AtomicDisposable` helper. *(Primitives `SinkSubscription`, Splat)*
- **`ConcurrentDictionary.GetOrAdd` with a factory that captures nothing** — atomic check-and-insert. The `static` is not a style nicety: a capturing lambda allocates a display class per call and measured **1.60x slower than the `lock` it replaced** at one thread. Pass state through the `TArg` overload. Wrap the value in `Lazy<T>` if the factory must run *exactly* once under contention.

  | Read-heavy memo cache, 256 keys | 1 thread | 8 threads |
  | --- | --- | --- |
  | `lock` + `Dictionary` | 8.93 us | 266.08 us |
  | `GetOrAdd`, **static** factory + state | **4.04 us (2.21x)** | **34.81 us (7.64x)** |
  | `GetOrAdd`, capturing lambda | 14.25 us (**1.60x slower**) | 81.98 us (3.25x) |
  | `Interlocked.CompareExchange` over an immutable snapshot | 8.41 us (unresolved) | 52.19 us (5.10x) |

- **One-time initialisation: keep the gate, move it off the fast path.** Deleting the lock from a lazily built cache is the tempting "lock-free" rewrite and it is usually wrong — every concurrent first caller then runs the whole build and discards all but one result. But leaving the `lock` *in* the queried method blocks that method from inlining, so the warm path pays for a slow path it never takes. Split them: an inlinable `Volatile.Read` fast path, and the gate in its own `NoInlining` method.

  ```csharp
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public bool Contains(TKey key) => (Volatile.Read(ref _map) ?? Resolve()).Contains(key);

  [MethodImpl(MethodImplOptions.NoInlining)]
  private HashSet<TKey> Resolve()
  {
      lock (_gate)
      {
          var map = _map;
          if (map is null)
          {
              map = Build();
              Volatile.Write(ref _map, map);
          }

          return map;
      }
  }
  ```

  | Expensive builder | Cold, 8 threads | Warm, 1 thread | Builder runs at 8 threads |
  | --- | --- | --- | --- |
  | No synchronisation | 25,392 us | 3,842 us | **8** |
  | `lock` inside the queried method | 3,951 us | 3,435 us | 1 |
  | **Split slow path** | **4,067 us** | **1,266 us (3.03x)** | **1** |

  Where the cached value is *cheap* (a couple of metadata lookups), a race is harmless — use `Interlocked.CompareExchange` over one immutable record holding every resolved value. Never publish the fields one at a time.

- **Inline observer set (none/one/many)** — store `null` / single `IObserver` / array, copy-on-write per subscribe; avoids a list allocation for the common 0–1 observer case. *(ReactiveUI `Broadcaster`)*
- **Right data structure** — a 4-ary (quaternary) heap beats a binary heap for priority queues (shallower, better cache locality); key-based serialization runs different keys concurrently while serializing same-key work. *(Punchclock)*

## 4. Async throughput

(See `csharp-async` for the full async rules — this is the perf-specific subset.)

- **`ValueTask`/`ValueTask<T>`** for paths that usually complete synchronously (cache hits, buffered reads) — no `Task` allocation on the sync path. Await once, never block, never reuse.
- **`ConfigureAwait(false)`** everywhere in library code — skips context capture, cheaper continuations, deadlock-immune.
- **Coalesce duplicate in-flight work** — when N callers request the same key at once, run the operation once and share the result (`Replay(1).RefCount()` for observables, or a shared `Task`). Kills the thundering herd. → `AsyncRequestCoalescer<TKey,TValue>` helper. *(Akavache `RequestCache`, Fusillade `InflightRequest`)*
- **Guard before you format** — `if (!IsDebugEnabled) return;` before `string.Format(...)` avoids allocating and boxing args for logs that won't be written. *(Splat `AllocationFreeLoggerBase`)*
- **`TaskCompletionSource` with `RunContinuationsAsynchronously`** — prevents inline continuation execution on the completing thread (reentrancy/deadlock risk).

## Discipline

- **Measure before and after.** A "faster" change with no benchmark is a guess. Keep the benchmark.
- **Hot paths only.** These patterns cost readability — spend that budget where a profiler says it matters.
- **AOT-friendly.** Prefer source-gen/cached delegates over per-call reflection; annotate any retained reflection with `[DynamicallyAccessedMembers]`.
- **Don't break correctness for nanoseconds.** Lock-free is subtle — only hand-roll it where the contention is real, and test it deterministically (see `csharp-tunit` on controlling execution context).

## Companion helpers (`./helpers/`)

| File | Pattern |
|------|---------|
| `PooledStringBuilder.cs` | Rent/return `StringBuilder` pool with a `readonly struct` rental |
| `AsyncRequestCoalescer.cs` | Coalesce concurrent identical async requests into one |
| `AtomicDisposable.cs` | Lock-free assign-once / dispose-once via `Interlocked` |
| `TypeCache.cs` | `static readonly typeof(T)` per-generic cache |
| `CopyOnWriteList.cs` | Lock-free reads via published immutable snapshot |
