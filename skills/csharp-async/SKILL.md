---
name: csharp-async
description: Use when writing or reviewing C#/.NET async/await code — correctness (deadlocks, sync context, TaskScheduler, async void) and performance (allocations, ValueTask, ConfigureAwait, elision). Blocking on async is banned in product code but allowed in test code. Avoid WhenAll/WhenAny on a single task.
---

# Async/await — correct *and* fast

Async is about freeing threads, not magic speed. Most async bugs are about **which context/scheduler the continuation resumes on**; most async waste is **needless allocations and state machines**. Both matter.

## The blocking rule

**Never block on async in product code** — no `.Result`, `.Wait()`, `.GetAwaiter().GetResult()`. It ties up a thread and, on any context that marshals continuations back (classic UI `SynchronizationContext`, older ASP.NET), it **deadlocks**: the blocked thread is the exact thread the continuation needs to resume on. Async all the way down.

**Exception — test code only.** In tests, blocking is acceptable when you have no async entry point and need a synchronous assertion path (e.g. a `[Test]` that can't be `async`, or asserting inside a non-async callback). `task.GetAwaiter().GetResult()` is preferred over `.Result`/`.Wait()` there because it unwraps the real exception instead of wrapping it in `AggregateException`. Keep it in test code; the moment it appears in `src/`, it's a bug. (TUnit tests should be `async Task` anyway — see `csharp-tunit`.)

## SynchronizationContext & ConfigureAwait

By default `await` captures the current `SynchronizationContext`/`TaskScheduler` and resumes the continuation on it. That's what you want in UI code (resume on the UI thread); it's overhead you don't want in libraries.

- **In library / non-UI code: `await foo.ConfigureAwait(false)`** on every await. It skips the context capture — faster, and immune to sync-over-async deadlocks if a caller misbehaves.
- **In UI / app code that touches UI afterward:** leave the default (capture) so you resume on the right thread.
- Modern ASP.NET Core has **no** `SynchronizationContext`, so `ConfigureAwait(false)` is a no-op for correctness there — still worth it in shared libraries that might run under a context.

## The TaskScheduler footgun (`StartNew` vs `Task.Run`)

`Task.Factory.StartNew(action)` schedules on **`TaskScheduler.Current`**, not `.Default`. Inside a continuation or a UI handler, `.Current` may be the UI scheduler or some nested scheduler — so your "background" work silently runs somewhere surprising (or serializes onto the UI). Two more `StartNew` traps:

- With an **`async` lambda**, `StartNew` returns `Task<Task>` — it completes when the *outer* delegate returns, not when your async work finishes. You must `.Unwrap()` (or just use `Task.Run`).
- `StartNew` does **not** default to `DenyChildAttach`, so child tasks can attach unexpectedly.

**Rule:** use **`Task.Run`** for offloading — it targets `TaskScheduler.Default`, unwraps async lambdas, and denies child attach. Only reach for `StartNew` when you deliberately pass an explicit `TaskScheduler` and `TaskCreationOptions`, and you know why.

## async void

Banned except for top-level event handlers. `async void` can't be awaited, and an exception inside it is raised on the `SynchronizationContext` and usually **crashes the process** instead of being catchable by the caller. Use `async Task`. (Event handlers that must be `async void` should wrap their body in try/catch.)

## WhenAll / WhenAny — only for *multiple* awaitables

`Task.WhenAll(singleTask)` and `Task.WhenAny(singleTask)` are pointless: they allocate an array/wrapper and an extra task to do what `await singleTask` already does. **Just await the task directly.** Reserve `WhenAll`/`WhenAny` for genuinely concurrent sets:

```csharp
// ❌ noise + allocation, single target
await Task.WhenAll(DoThingAsync());
var done = await Task.WhenAny(FetchAsync());

// ✅ direct
await DoThingAsync();

// ✅ WhenAll for real concurrency — start all, then await
var a = FetchAAsync();   // start
var b = FetchBAsync();   // start
await Task.WhenAll(a, b);
var (ra, rb) = (await a, await b);   // already complete — cheap
```

Note the pattern: **start** the tasks (don't `await` each in turn — that serializes them), then `WhenAll`. `WhenAny` is for "first to finish wins" / timeout races — and remember the losers keep running, so observe or cancel them.

## Performance

- **`ValueTask` / `ValueTask<T>`** for hot paths that *usually complete synchronously* (cache hits, buffered reads) — avoids allocating a `Task` per call. Rules: **await it exactly once, never block on it, never await it twice, don't store it.** If you need to await multiple times or fan out, call `.AsTask()` first. Don't reach for `ValueTask` by default — `Task` is simpler and pooled-ish; use `ValueTask` where a profiler shows the allocation hurts.
- **Elide pointlessly-async methods.** A method that only forwards can return the inner `Task` directly instead of `async`/`await` — no state machine, no extra allocation:
  ```csharp
  public Task<int> GetAsync() => _inner.GetAsync();   // no async/await needed
  ```
  But **don't elide** when you have a `using`/`try`/`finally`, a `ConfigureAwait`, or post-processing — those require the `await` to work correctly. Correctness beats the micro-saving.
- **Fast-path completed work:** `Task.CompletedTask`, `Task.FromResult(x)`, `ValueTask.FromResult` instead of `Task.Run(() => x)`.
- **`TaskCompletionSource`:** create with `TaskCreationOptions.RunContinuationsAsynchronously` — otherwise the thread that calls `SetResult` runs the awaiter's continuations inline, which can deadlock or cause surprising reentrancy.
- **`Task.Run` is for CPU-bound offload, not I/O.** Wrapping an already-async I/O call in `Task.Run` just burns a thread-pool thread to wait. Call the async API directly.
- **Cancellation is part of the contract.** Take a `CancellationToken`, thread it through every downstream async call, and honor it. Don't swallow `OperationCanceledException`.
- **Don't `async`-ify CPU-bound libraries** for no reason — a state machine around synchronous work is pure overhead. Offload at the call site with `Task.Run` if needed.

## .NET 11: Runtime Async (net11.0+)

With runtime async, `await` compiles to a **runtime-async** method that the runtime suspends and resumes itself, instead of a classic compiler-generated state machine. The .NET 11 BCL is compiled this way. Your own code is not by default: on the 11.0.100-rc.1 SDK a `net11.0` project still gets classic state machines unless it sets `<Features>runtime-async=on</Features>`, despite docs that describe it as on by default. Check your SDK before relying on either behaviour. Mono (Blazor WebAssembly) is not a supported runtime for it. See `dotnet-runtime-async` for enabling, detecting and library-author guidance. It doesn't change any of the rules above (still `ConfigureAwait(false)`, still no blocking, still `ValueTask` on measured hot paths) — it changes the cost model underneath them:

- **Stack traces through `await` are cleaner** — a runtime-async continuation resumes without the pile of `MoveNext`/`AsyncStateMachine` frames, so exception stacks read like synchronous call stacks. Don't rewrite exception-handling code for this; it's a debugging quality-of-life win, not a correctness change.
- **Lower per-await overhead**, and a continuation **skips capturing `ExecutionContext`** when there's no ambient state (no `AsyncLocal`, no flowed `SynchronizationContext`) to flow — one of the costs `ConfigureAwait(false)` and hand-rolled avoidance tricks existed to dodge. Keep writing `ConfigureAwait(false)` in libraries anyway: it's still correct, still needed pre-net11.0 and on any TFM that doesn't runtime-async, and costs nothing when it's already a no-op.
- **Don't treat this as a reason to stop measuring.** The state-machine-avoidance techniques here (eliding pointlessly-async wrappers, `ValueTask` on hot paths) are still valid; runtime async lowers the constant factor, it doesn't remove the case for them. Benchmark net11.0 separately from net10.0 before claiming a win — see `benchmarking`.

## Review checklist

- [ ] No `.Result` / `.Wait()` / `.GetAwaiter().GetResult()` in product code (test code only, prefer `GetAwaiter().GetResult()`)
- [ ] `ConfigureAwait(false)` on every await in library code
- [ ] No `async void` except a guarded event handler
- [ ] `Task.Run` (not `StartNew`) for offloading; explicit scheduler only when deliberate
- [ ] No `WhenAll`/`WhenAny` wrapping a single task
- [ ] Concurrent work is *started* then awaited together — not awaited one-by-one
- [ ] `ValueTask` awaited once and never blocked on; `.AsTask()` if reused
- [ ] `CancellationToken` accepted and threaded through
- [ ] `TaskCompletionSource` uses `RunContinuationsAsynchronously`
