---
name: csharp-concurrency-primitives
description: Use when writing or reviewing C#/.NET code that coordinates access across threads or async work — picking between lock/System.Threading.Lock, SemaphoreSlim, Interlocked/Volatile, Channel<T>, ConcurrentDictionary, ReaderWriterLockSlim, Parallel.ForEachAsync, and lazy initialization. Wrong primitive choice causes deadlocks, torn state, or silent double-work.
---

# Concurrency primitives — picking the right tool

Every primitive here trades off differently on "blocks a thread vs. frees it", "works with `await`", and "how many writers/readers". Pick based on those axes, not habit.

## `lock` / `System.Threading.Lock` — simple mutual exclusion, no `await` inside

.NET 9+ has a dedicated `System.Threading.Lock` type. The `lock` statement recognizes it and uses its `ref struct` enumerator (`EnterScope`) instead of `Monitor`, which is faster and avoids some `Monitor`-specific footguns:

```csharp
private readonly Lock _gate = new();
private int _counter;

public void Increment()
{
    lock (_gate)
    {
        _counter++;
    }
}
```

- Prefer `Lock` over a bare `object` for new code targeting .NET 9+; it's a drop-in `lock (_gate)` target with better performance and clearer intent than "lock on some object".
- **Never `await` inside a `lock` block.** The lock is thread-affine (`Monitor`-based, including `Lock`) — the continuation after an `await` can resume on a *different* thread, which either throws (`SynchronizationLockException`) or silently corrupts the lock's owner tracking depending on the primitive. If you need to hold exclusion across an `await`, use `SemaphoreSlim` instead.
- Keep the critical section short: no I/O, no calling into code you don't control, nothing that can throw and leave shared state half-mutated without a `try`/`finally`.

## `SemaphoreSlim` — mutual exclusion or throttling *across* `await`

```csharp
private readonly SemaphoreSlim _gate = new(1, 1);

public async Task UpdateAsync(CancellationToken ct)
{
    await _gate.WaitAsync(ct).ConfigureAwait(false);
    try
    {
        await DoWorkAsync().ConfigureAwait(false);
    }
    finally
    {
        _gate.Release();
    }
}
```

- `new SemaphoreSlim(1, 1)` used as a mutex is the standard way to serialize async work — `WaitAsync` doesn't block a thread while waiting, unlike `lock`.
- Also use it (with initial count > 1) to throttle concurrency, e.g. `new SemaphoreSlim(maxConcurrent)` around calls you fan out with `Task.WhenAll`.
- Always `Release()` in a `finally`. Always pass a `CancellationToken` to `WaitAsync` — see `csharp-async` for why cancellation is part of the contract.
- Don't reach for `SemaphoreSlim` when a plain `lock`/`Lock` would do (no `await` in the critical section) — it's heavier and the async overhead is wasted.

## `Interlocked` / `Volatile` — lock-free single-value updates

```csharp
private long _requestCount;
public void RecordRequest() => Interlocked.Increment(ref _requestCount);

private volatile bool _shuttingDown;
public void Shutdown() => Volatile.Write(ref _shuttingDown, true);
public bool IsShuttingDown => Volatile.Read(ref _shuttingDown);
```

- `Interlocked.Increment`/`Decrement`/`Add`/`Exchange`/`CompareExchange` are for single scalar fields where a full lock would be overkill — counters, flags, simple state machines via `CompareExchange` loops.
- `Volatile.Read`/`Write` (or the `volatile` keyword on a field) prevent instruction/memory reordering around a single field's read/write — use for simple published flags, not for anything needing atomic read-modify-write (that's `Interlocked`).
- Don't hand-roll a multi-field invariant out of several `Interlocked` calls — if more than one value must change together consistently, that's a `lock`, not a cleverness contest.

## `Channel<T>` — async producer/consumer

```csharp
var channel = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(capacity: 100)
{
    FullMode = BoundedChannelFullMode.Wait,
    SingleReader = true,
    SingleWriter = false,
});

// producer(s)
await channel.Writer.WriteAsync(item, ct).ConfigureAwait(false);
// ...
channel.Writer.Complete();

// consumer
await foreach (var item in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
{
    Process(item);
}
```

- **Bounded vs. unbounded:** `CreateBounded<T>` applies backpressure — producers slow down (or fail/drop, depending on `FullMode`) when the channel is full, which is almost always what you want for anything talking to an external system. `CreateUnbounded<T>` has no capacity limit and can grow without bound if the consumer falls behind — only use it when you've already reasoned about worst-case queue growth.
- **`BoundedChannelFullMode`:** `Wait` (default — producer awaits until space frees, backpressure), `DropOldest`/`DropNewest` (lossy, for "latest value wins" telemetry-style feeds), `DropWrite` (drop the item being written, keep everything already queued). Pick deliberately; `Wait` is the safe default.
- **`SingleReader`/`SingleWriter`:** set `true` when you can guarantee only one reader or writer thread — it lets the channel skip synchronization it would otherwise need, which is a real throughput win. Get it wrong (set `true` but actually use multiple writers) and behavior is undefined, not just slow.
- Channels replace hand-rolled `BlockingCollection` + `Task.Run` producer loops for the async case — prefer them over building your own queue+`SemaphoreSlim` signaling.

## `ConcurrentDictionary` — mind `GetOrAdd`'s factory

```csharp
// ❌ factory can run more than once under contention — the return values race,
// only one wins the dictionary slot, but all of them ran (side effects included)
var value = dict.GetOrAdd(key, k => ExpensiveCompute(k));

// ✅ Lazy<T> trick: the *Lazy* object may be constructed more than once, but
// its factory delegate (.Value) runs at most once because Lazy<T> itself
// synchronizes
var lazy = dict.GetOrAdd(key, k => new Lazy<T>(() => ExpensiveCompute(k)));
var value = lazy.Value;
```

- `ConcurrentDictionary<TKey,TValue>.GetOrAdd(key, factory)` does **not** guarantee the factory runs only once per key. Under concurrent first-access, multiple threads can all call the factory; only one result is stored, the rest are discarded — but if the factory has side effects (I/O, counters, allocation you care about), that's a real bug, not a theoretical one.
- The `Lazy<T>` wrapper fixes this: `Lazy<T>`'s own synchronization (default `LazyThreadSafetyMode.ExecutionAndPublication`) ensures the *inner* factory runs once, even though the outer `GetOrAdd` may construct (but not invoke) several `Lazy<T>` instances that are cheap, unevaluated wrappers.
- If the value is cheap/pure (no side effects, idempotent), plain `GetOrAdd` is fine and simpler — don't add the `Lazy<T>` layer speculatively.
- `AddOrUpdate`'s update factory has the same multiple-invocation caveat.

## `ReaderWriterLockSlim` — rarely

```csharp
private readonly ReaderWriterLockSlim _rw = new();

public T Read()
{
    _rw.EnterReadLock();
    try { return _state; }
    finally { _rw.ExitReadLock(); }
}
```

- Only worth it when reads vastly outnumber writes *and* the protected work under the read lock is non-trivial (a cheap `lock`/`Lock` is faster than `ReaderWriterLockSlim`'s overhead for small critical sections, even single-threaded, because of the extra bookkeeping).
- No native async support — same "don't `await` inside" rule as `lock`.
- Measure before reaching for it; `ConcurrentDictionary` or a plain `lock` around a small object usually wins on both simplicity and speed for typical workloads.

## `Parallel.ForEachAsync` — bounded CPU/IO-parallel iteration

```csharp
await Parallel.ForEachAsync(
    items,
    new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
    async (item, token) =>
    {
        await ProcessAsync(item, token).ConfigureAwait(false);
    }).ConfigureAwait(false);
```

- The async-native replacement for `Parallel.ForEach` + blocking, or a hand-rolled `SemaphoreSlim` + `Task.WhenAll` loop. Set `MaxDegreeOfParallelism` explicitly — the default is `Environment.ProcessorCount`, which is too high for I/O-bound work hitting an external service.
- Honors `CancellationToken` passed via `ParallelOptions` and via the per-item delegate parameter — thread it through to inner async calls.

## Lazy initialization — `Lazy<T>` and `LazyInitializer`

```csharp
private readonly Lazy<ExpensiveService> _service = new(() => new ExpensiveService());
public ExpensiveService Service => _service.Value;
```

- `Lazy<T>` defaults to thread-safe (`ExecutionAndPublication`): concurrent first access blocks all callers until one factory run completes, and all callers see the same instance. That's almost always what you want.
- `LazyThreadSafetyMode.None` removes the synchronization for single-threaded-only scenarios — don't use it unless you've proven there's no concurrent access.
- `LazyInitializer.EnsureInitialized(ref field, ...)` is a lighter-weight alternative when you don't need the `Lazy<T>` wrapper object itself (e.g. a mutable field rather than a property) — check its overload docs for whether it guarantees single-invocation vs. "may run more than once but only one wins," similar to the `ConcurrentDictionary` caveat above.

## `CancellationToken` in waits

Every blocking-in-spirit wait that has an async or token-accepting overload should take one: `SemaphoreSlim.WaitAsync(ct)`, `Channel.Reader.ReadAsync(ct)`, `Task.Delay(ms, ct)`. A wait with no way to cancel is a wait that can hang the shutdown path. See `csharp-async` for the broader cancellation contract.

## Checklist

- [ ] No `await` inside a `lock`/`Lock` block — use `SemaphoreSlim` instead
- [ ] New code targeting .NET 9+ uses `System.Threading.Lock` over a bare `object` for `lock`
- [ ] `SemaphoreSlim.Release()` in `finally`; `WaitAsync` takes a `CancellationToken`
- [ ] Single-field lock-free updates use `Interlocked`/`Volatile`, not a full lock
- [ ] `Channel<T>` capacity and `FullMode` chosen deliberately; `SingleReader`/`SingleWriter` only when actually true
- [ ] `ConcurrentDictionary.GetOrAdd` factories with side effects wrapped in `Lazy<T>`, or confirmed idempotent
- [ ] `ReaderWriterLockSlim` only after measuring, not by default
- [ ] `Parallel.ForEachAsync` has an explicit, workload-appropriate `MaxDegreeOfParallelism`
- [ ] Lazy init uses `Lazy<T>`'s default thread-safe mode unless single-threaded access is proven
- [ ] Every wait accepts and honors a `CancellationToken`

See `csharp-async` for the broader async correctness/perf rules these primitives sit inside, and `csharp-performance` for when the allocation/contention cost of a primitive actually matters.
