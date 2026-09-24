---
name: csharp-error-handling
description: Use when writing or reviewing C#/.NET exception handling — where to catch, exception filters, throw vs throw ex, ExceptionDispatchInfo, guard/throw helpers (ArgumentNullException.ThrowIfNull etc.), custom exception types, OperationCanceledException, AggregateException, and using/finally correctness.
---

# Error handling — catch with intent, throw without losing information

Exceptions exist for the unexpected and the unrecoverable-at-this-level. The two ways to get this
wrong are catching too eagerly (swallowing real bugs) and throwing carelessly (losing the stack
trace that would have told you why).

## Catch at boundaries, never swallow

Catch where you can actually *do* something: recover, retry, translate into a domain error, or
log and surface to the user. That's usually a boundary — a controller action, a message-handler
entry point, a top-level `Main`/host — not deep in a call chain.

```csharp
// ❌ swallows the bug, and every caller upstream now sees "it worked"
try
{
    ProcessOrder(order);
}
catch
{
}

// ✅ catch where you can act, and only what you can act on
try
{
    ProcessOrder(order);
}
catch (InvalidOrderException ex)
{
    _logger.LogWarning(ex, "Order {OrderId} rejected", order.Id);
    return OrderResult.Rejected(ex.Reason);
}
```

An empty `catch` block, a `catch (Exception) { }`, or a `catch` that only logs and continues as
if nothing happened are all the same bug: they hide a failure from everything upstream that could
have reacted correctly. If you truly cannot handle it, don't catch it — let it propagate.

## Exception filters (`when`)

A `when` filter lets you match on exception *state*, not just type, and — critically — it runs
**before stack unwinding**, so a non-matching filter leaves the original throw context (and any
first-chance debugger break) intact.

```csharp
try
{
    await httpClient.SendAsync(request, cancellationToken);
}
catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
{
    await DelayAndRetryAsync(cancellationToken);
}
// other HttpRequestExceptions propagate untouched — no re-throw needed
```

Prefer a filter over catching broadly and re-throwing on the cases you didn't want:

```csharp
// ❌ catches everything, then has to figure out what it actually wanted
catch (HttpRequestException ex)
{
    if (ex.StatusCode != HttpStatusCode.TooManyRequests)
    {
        throw;
    }
    await DelayAndRetryAsync(cancellationToken);
}
```

Filters can also carry side effects (logging inside the filter expression) since C# does allow
arbitrary boolean expressions there — but keep filters to conditions, not hidden work; a filter
with a logging call is a surprise to the next reader.

## `throw;` vs `throw ex;`

```csharp
catch (Exception ex)
{
    Log(ex);
    throw;      // ✅ preserves the original stack trace
    throw ex;   // ❌ resets it to this line — you lose where it actually happened
}
```

`throw;` (no expression) re-throws the current exception with its original stack trace intact.
`throw ex;` throws it as a *new* throw point, overwriting `StackTrace` so it looks like the
exception originated at the `catch` block. Always use bare `throw;` to rethrow unchanged.

## Crossing a thread/async boundary: `ExceptionDispatchInfo`

When you must capture an exception on one thread/continuation and rethrow it later (in a queued
callback, after leaving a `catch` block, from inside a custom awaiter), `throw;` isn't available
— you're not inside the original `catch` anymore. `ExceptionDispatchInfo.Capture(ex).Throw()`
preserves the original stack trace the same way `throw;` does, from wherever you rethrow:

```csharp
ExceptionDispatchInfo? captured = null;
try
{
    DoWork();
}
catch (Exception ex)
{
    captured = ExceptionDispatchInfo.Capture(ex);
}

// ... later, possibly on a different thread ...
captured?.Throw();   // rethrows with the original stack trace appended, not replaced
```

This is what `Task`/`await` itself uses internally to preserve stack traces across the async
state machine — you rarely need it directly in ordinary `async`/`await` code, but reach for it
whenever you manually shuttle an exception across a boundary that isn't a plain `catch`.

## Guard helpers

Use the built-in `ThrowIf*` static helpers instead of hand-written `if (...) throw ...` — they're
shorter, consistent, and (via `[DoesNotReturn]`/attributes) understood by nullable and flow
analysis:

```csharp
public Connection(string host, int port, TimeSpan timeout)
{
    ArgumentNullException.ThrowIfNull(host);
    ArgumentException.ThrowIfNullOrEmpty(host);
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(port);
    ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
    _host = host;
    _port = port;
}

public void Send(byte[] data)
{
    ObjectDisposedException.ThrowIf(_disposed, this);
    ...
}
```

Common ones: `ArgumentNullException.ThrowIfNull`, `ArgumentException.ThrowIfNullOrEmpty` /
`ThrowIfNullOrWhiteSpace`, `ArgumentOutOfRangeException.ThrowIfNegative` /
`ThrowIfNegativeOrZero` / `ThrowIfZero` / `ThrowIfGreaterThan` / `ThrowIfLessThan` /
`ThrowIfEqual` / `ThrowIfNotEqual`, `ObjectDisposedException.ThrowIf(bool, object)`. They also
capture the argument expression text automatically via `[CallerArgumentExpression]`, so the
thrown message names the right parameter without you passing `nameof(...)`.

## Throw helpers for hot paths

A method with an inline `throw new SomeException(...)` is usually not inlined by the JIT, and the
constructed message/string-formatting work sits in the method body even on the path that never
throws. For methods called very frequently, move the throw out into a separate, marked helper:

```csharp
public int this[int index]
{
    get
    {
        if ((uint)index >= (uint)_length)
        {
            ThrowIndexOutOfRange();
        }
        return _items[index];
    }
}

[DoesNotReturn]
[StackTraceHidden]
private static void ThrowIndexOutOfRange() =>
    throw new ArgumentOutOfRangeException("index");
```

- `[DoesNotReturn]` tells flow analysis (and readers) the method never returns normally — useful
  for nullability inference after the call site.
- `[StackTraceHidden]` removes the helper's own frame from the exception's captured stack trace,
  so callers see the throw as if it happened at the call site, not inside your helper.
- This is a hot-path optimization, not a style mandate — apply it where a profiler or a genuinely
  hot indexer/loop body justifies it, not on every guard clause in the codebase.

## Custom exception types — when warranted

Define a custom exception type when callers need to catch *this specific failure* distinctly from
generic ones, or when it carries structured data a caller needs (not just a message string).

```csharp
public sealed class OrderRejectedException(string reason, Guid orderId)
    : Exception($"Order {orderId} rejected: {reason}")
{
    public string Reason { get; } = reason;
    public Guid OrderId { get; } = orderId;
}
```

- Derive from `Exception` (or the closest meaningful built-in base, e.g. `InvalidOperationException`)
  directly — don't build a deep custom exception hierarchy unless callers genuinely need to catch
  at an intermediate level.
- Don't create a custom exception type just to wrap a message with no extra data and no distinct
  catch scenario — an existing framework exception (`InvalidOperationException`,
  `ArgumentException`, `NotSupportedException`) is usually the right, boring choice.
- Custom exceptions do **not** need a full set of serialization constructors on modern .NET —
  the `SerializationInfo` constructor pattern is only relevant if you use binary serialization
  (rare); skip it for ordinary libraries.

## `OperationCanceledException`

`OperationCanceledException` (and its common subclass `TaskCanceledException`) signals
cooperative cancellation, not failure. Don't catch and log it as an error at every level — let it
propagate to the code that owns the `CancellationToken` and knows cancellation was requested.

```csharp
try
{
    await LongRunningAsync(cancellationToken);
}
catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
{
    // expected: our own token fired — exit quietly
}
// an OperationCanceledException from an *unrelated* token still propagates
```

The `when (cancellationToken.IsCancellationRequested)` filter matters: it distinguishes "the
token I passed in caused this" from "something downstream threw its own unrelated cancellation" —
don't swallow the latter.

## `AggregateException`

`Task.Wait()`, `.Result`, and `Task.WaitAll` wrap faults in `AggregateException` (see
`csharp-async` for why blocking on tasks is banned in product code in the first place). If you're
stuck with one (test code, or a legacy synchronous boundary), don't catch and report the wrapper
directly — unwrap it:

```csharp
catch (AggregateException ex)
{
    throw ex.Flatten().InnerException ?? ex;
}
```

`await`ing a task instead of blocking on it avoids the wrapping entirely — the awaiter unwraps
and throws the original exception directly. That's the actual fix in most cases; only reach for
`Flatten()`/`InnerException` handling where blocking is unavoidable.

## `finally` / `using` correctness

- `finally` always runs, even when the `try` returns, throws, or the `catch` rethrows — use it
  for cleanup that must happen regardless of outcome, not as a place to swallow exceptions.
- Never `throw` a *new* exception from inside `finally` (or a `catch` triggered by disposal)
  during normal cleanup — it replaces the original exception that was propagating, hiding the
  real cause. If cleanup can fail, log it there; don't let it mask the original fault.
- Prefer `using`/`using var` over manual `try/finally` + `Dispose()` — it's equivalent but can't
  be gotten wrong by forgetting the `finally`, and composes correctly with early returns.
- Multiple `using var` declarations dispose in **reverse declaration order** automatically —
  don't hand-roll ordering with nested `try/finally` unless disposal order needs to differ from
  that default.

```csharp
// ✅ disposed in reverse order automatically, even on early return/throw
using var connection = OpenConnection();
using var transaction = connection.BeginTransaction();
DoWork(connection, transaction);
```

## Review checklist

- [ ] No empty `catch` blocks; nothing caught without a reason it can act on
- [ ] Catches are at boundaries, not scattered through call chains
- [ ] Rethrow uses bare `throw;`, never `throw ex;`
- [ ] Cross-boundary exception capture uses `ExceptionDispatchInfo`, not a rethrow-by-message hack
- [ ] Guard clauses use the built-in `ThrowIf*` helpers, not hand-rolled `if`/`throw`
- [ ] Hot-path throws are isolated in `[DoesNotReturn][StackTraceHidden]` helpers where profiled
- [ ] Custom exception types only where a distinct catch or structured data is needed
- [ ] `OperationCanceledException` from your own token is treated as expected, not an error
- [ ] `AggregateException` is unwrapped (or avoided by awaiting instead of blocking)
- [ ] No new exception thrown from `finally`/disposal masking the original
- [ ] `using`/`using var` used instead of manual `try/finally` disposal
