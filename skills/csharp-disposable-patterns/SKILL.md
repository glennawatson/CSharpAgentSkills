---
name: csharp-disposable-patterns
description: Use when implementing or reviewing IDisposable/IAsyncDisposable on a type that can be disposed from more than one thread — idempotent Dispose, disposed-state checks (ObjectDisposedException), assignable/replaceable disposable slots, composite disposal (bags/groups), the full Dispose(bool)/finalizer pattern, IAsyncDisposable, and who owns disposal (CA2000/CA2213/CA1001). Includes a Roslyn rewriter that converts the classic non-thread-safe `if (_disposed) return; _disposed = true;` shape to the Interlocked form.
---

# Thread-safe disposable patterns

Most `Dispose()` bugs are races: two threads dispose concurrently and run cleanup twice, or
one thread disposes while another is mid-use. A `bool _disposed` field with `if (_disposed)
return; _disposed = true;` is **not** atomic — two threads can both read `false` before either
writes `true`, and both run the cleanup body. Fix it with `Interlocked`, not a lock, unless the
type has other reasons to hold a lock anyway.

The patterns below follow ReactiveUI.Primitives' `Disposables` namespace (house style
for lock-free disposal) — file names cited throughout are real files under
`src/ReactiveUI.Disposables/Disposables/` in that repo.

## The idempotent Dispose: `Interlocked`, not a flag

```csharp
// ❌ not thread-safe — two threads can both pass the check before either sets the flag
public void Dispose()
{
    if (_disposed) return;
    _disposed = true;
    _resource.Dispose();
}

// ✅ thread-safe — exactly one caller ever sees `!= 0` as false
private int _disposed; // 0 = live, 1 = disposed
public void Dispose()
{
    if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
    _resource.Dispose();
}
```

`Interlocked.Exchange` is a full memory barrier that atomically sets the flag and returns the
*previous* value in one indivisible step — there's no window where two threads can both observe
"not yet disposed". Losing racers return immediately without touching the resource.

When the thing you're disposing lives in a single field, skip the separate flag entirely and
make the field itself the latch — this is `ActionDisposable.cs`'s and `BooleanDisposable.cs`'s
shape:

```csharp
// ActionDisposable.cs pattern: the field null-vs-not-null *is* the disposed state
private Action? _action;
public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
```

```csharp
// BooleanDisposable.cs pattern: no payload at all, just a latch
private int _isDisposed;
public bool IsDisposed => Volatile.Read(ref _isDisposed) != 0;
public void Dispose() => Interlocked.Exchange(ref _isDisposed, 1);
```

One `Interlocked.Exchange`, no allocation, no lock. This is the default — reach for it any time
`Dispose()` can plausibly be called from more than one thread, which in practice means: the type
is `public`, is shared across an async boundary, or is handed to unrelated subsystems (DI
containers, callback registrations, anything you don't fully control the lifetime of).

### When the classic flag is fine

A `private set`-only type used strictly within one method's call stack (never escapes, no async
handoff, no DI registration) can use the plain `if (_disposed) return; _disposed = true;` form —
it's simpler to read and there's no race to guard against because there's only ever one caller.
Don't add `Interlocked` machinery to a type that provably never sees concurrent `Dispose()`
calls; that's ceremony with no payoff. The moment the type's own doc comment says "thread-safe"
or it's registered with a container/event that can dispose it from a callback thread, switch to
the atomic form.

## Disposed-state checks

Read the same field with `Volatile.Read` (or an equivalent atomic read) and throw through the
BCL helper — see `csharp-guard-clauses` for the throw-helper catalogue:

```csharp
private int _disposed;

public void DoWork()
{
    ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    // ... use the resource
}
```

- **`ObjectDisposedException`** — the only correct exception for "this instance was already
  disposed, and you tried to use it anyway." Don't invent a custom exception type for this.
- **`InvalidOperationException`** — for a *different* wrong-state problem: not disposed, but
  called in the wrong lifecycle order (e.g. `Start()` called twice, `Dispose()` called before
  `Initialize()` — states orthogonal to disposal).
- **Never throw from `Dispose()` or `DisposeAsync()` itself.** A disposer that throws breaks
  every caller relying on `using`/`await using` for cleanup — if `Dispose()` throws mid-loop
  over a composite (see below), later entries never get disposed, and a `using` block's own
  exception (if any) is masked by the one from `Dispose()`. When you truly must call multiple
  independent cleanup steps and any of them can throw, swallow/log rather than propagate for
  best-effort cleanup, or use the composite-disposal aggregation pattern below where every child
  runs before any exception propagates. A constructor argument validation guard (throwing from
  inside `Dispose()` because a precondition was violated) is a design smell, not a guard clause
  — validate before you're in the cleanup path.
- **Never throw from a finalizer.** An exception escaping a finalizer used to terminate the
  process outright (pre-.NET Core it was configurable and still catastrophic); today it still
  tears down the finalizer thread for that AppDomain-equivalent context. Finalizers must be
  defensive: no `ObjectDisposedException.ThrowIf`, no rethrow, no unguarded calls into things
  that might already be finalized (field order at finalization time is undefined across
  objects — a field's own finalizer may have already run).

## Assignable / replaceable disposable slots

Primitives' pattern for "a field that holds *one* disposable, atomically, with correct
behaviour once the owner is disposed." Two shapes, both built on a `CompareExchange`/`Exchange`
loop against the field itself — no lock:

**Set-once, throw on reassignment** (`SingleDisposable.cs` / `AssignmentState.cs` /
`OnceDisposable.cs`): assigning twice is a bug (`InvalidOperationException`); assigning *after*
Dispose immediately disposes the incoming value instead of storing it.

```csharp
// Minimal version of AssignmentState.cs's Create/Dispose pair
public sealed class SetOnceDisposable : IDisposable
{
    private static readonly IDisposable Disposed = new NoopDisposable();
    private IDisposable? _value;

    public void Set(IDisposable value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var current = Interlocked.CompareExchange(ref _value, value, null);
        if (current is null) return;                 // won — stored
        if (ReferenceEquals(current, Disposed)) { value.Dispose(); return; } // too late — dispose it now
        throw new InvalidOperationException("Already assigned.");
    }

    public void Dispose()
    {
        var previous = Interlocked.Exchange(ref _value, Disposed);
        if (!ReferenceEquals(previous, Disposed)) previous?.Dispose();
    }

    private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
}
```

**Replaceable, disposes the displaced value** (`SingleReplaceableDisposable.cs` /
`ReplaceableState.cs` / `MutableDisposable.cs` / `Slot.cs`): `Set`/`Create` can be called
repeatedly; each call disposes whatever was there before. `MutableDisposable.cs` is the variant
that does *not* dispose the displaced value (pure swap) — use it when the caller wants to
observe/reclaim the old value itself, driven by `DisposableSlotHelper.cs`'s
`AssignWithoutDisposingPrevious`/`SwapAndDisposePrevious`/`TryDispose` primitives, which are
worth reading directly as the canonical lock-free "slot + separate disposed-flag" building
blocks.

The shared invariant across every one of these types: **a disposed sentinel is a real
`IDisposable` instance stored in the same field the live value lives in**, not a separate bool
next to it, whenever the field alone needs to answer "am I disposed" without a second atomic
read. That's what makes `Set` after `Dispose` a single `CompareExchange`/`Exchange` instead of a
check-then-act race between the disposed flag and the value field. Where a separate `int
_disposed` field is used instead (`MutableDisposable`, `DisposableBag`), the assign path must
re-check the flag *after* the exchange (`DisposeIfRaced`) to close the race where `Dispose()`
runs between the assignment's flag-check and its `Exchange` — see `DisposableSlotHelper.cs`.

## Composite disposal (bags/groups)

`DisposableBag.cs` (class, `Lock`-based) and `DisposableSet.cs` (struct, embeddable field, lazy
`Lock`) hold N disposables and dispose them together, in registration order, exactly once.
Pattern:

- **`Add`**: under the gate, if already disposed, dispose the incoming item *immediately*
  outside the gate (never dispose while holding it — a badly-behaved disposable that reenters
  the bag would deadlock); otherwise store it (two inline slots + a growable overflow array to
  avoid a `List<T>` allocation for the common 0-3 item case).
- **`Dispose`**: under the gate, swap `_disposed = true` and snapshot+clear every slot in one
  step; release the gate; then dispose every snapshotted entry *outside* the gate, in
  registration order. Repeated `Dispose()` calls see `_disposed` already true and return
  instantly.
- **Primitives' bag does not aggregate exceptions** — each child's `Dispose()` is called in
  turn and an exception from one would abort the loop, leaving later children un-disposed. If
  your composite must guarantee every child gets a chance to dispose even when one throws,
  collect exceptions and rethrow as `AggregateException` after the loop finishes:

```csharp
List<Exception>? errors = null;
foreach (var d in snapshot)
{
    try { d.Dispose(); }
    catch (Exception ex) { (errors ??= []).Add(ex); }
}
if (errors is { Count: > 0 }) throw new AggregateException(errors);
```

Use this aggregating form for user-facing composite disposables where "silently drop the second
exception" would hide real bugs; use Primitives' simpler form (let the first exception
propagate, accept that later entries in that call may not get disposed) for internal glue where
every constituent's `Dispose()` is known not to throw.

`MultipleDisposable.cs`'s static `Create(params IDisposable[])` factory (`DisposableArray`
nested type) is the leanest version: an array field nulled via a single `Interlocked.Exchange`,
no lock at all, appropriate when the set is fixed at construction (no `Add`/`Remove` needed).

## The full `Dispose(bool)` pattern — only when you need it

Reach for the classic virtual `Dispose(bool disposing)` + finalizer shape **only** when the type
directly owns an unmanaged resource (a native handle, not wrapped in `SafeHandle`) or is
`unsealed` and derived types might need to add their own cleanup. Otherwise it's ceremony:

```csharp
public class OwnsUnmanaged : IDisposable
{
    private int _disposed;
    private IntPtr _handle;
    private Stream? _managedResource;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Dispose(true);
        GC.SuppressFinalize(this);           // CA1816 — nothing left for the finalizer to do
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _managedResource?.Dispose();     // only touch managed objects when `disposing`
        }

        ReleaseHandle(ref _handle);          // unmanaged cleanup runs either way
    }

    ~OwnsUnmanaged() => Dispose(false);      // must NOT touch managed fields — they may already be finalized
}
```

- **`Dispose(bool)` must be `protected`, `virtual` (or `abstract`/`override`), never `sealed`,
  never `public`** — CA1063's `DisposeBoolSignatureRule`.
- **`Dispose()` itself must be `public` and effectively sealed** (not virtual/abstract; if it's
  an override, it must be sealed) — derived types override `Dispose(bool)`, never `Dispose()`.
  A derived class that declares its own `public new void Dispose()` *hides* rather than
  overrides the base method — callers holding the base type never see the derived cleanup run.
- **`GC.SuppressFinalize(this)` only where a finalizer exists** — calling it on a sealed class
  or struct with no finalizer anywhere in the hierarchy is dead weight (flagged as
  `PSH1008`/`ImplementIDisposableCorrectly`'s implicit skip-when-`!type.HasFinalizer()` check).
- **Sealed types skip all of this.** A `sealed` class needs only a plain `Dispose()` — no
  `Dispose(bool)` overload, no finalizer unless it directly holds a native resource, no
  `SuppressFinalize` call unless it does. Most types should be sealed for exactly this reason:
  simpler, faster, no virtual dispatch on the hot path.
- **Prefer `SafeHandle` over a raw handle + finalizer.** `SafeHandle` already implements the
  atomic release-once, finalizer-safe pattern correctly (`ReleaseHandle` runs under a CER-like
  guarantee); wrapping a raw `IntPtr` field yourself and writing your own finalizer reimplements
  a solved, easy-to-get-subtly-wrong problem (see CA2216 below).

## `IAsyncDisposable`

Same idempotency requirement, `ValueTask`-shaped:

```csharp
private int _disposed;

public ValueTask DisposeAsync()
{
    if (Interlocked.Exchange(ref _disposed, 1) != 0) return default; // idempotent, no allocation
    return DisposeAsyncCore();
}

private async ValueTask DisposeAsyncCore()
{
    await _channel.DisposeAsync().ConfigureAwait(false);   // ConfigureAwait(false) — library code
    _cts.Dispose();
}
```

- **Idempotent the same way**: `Interlocked.Exchange` on the guard field, first winner runs the
  real cleanup, everyone else gets a completed `default` `ValueTask` back immediately — this is
  `DisposableAsync.cs`'s `DelegateAsyncDisposable` shape and `DisposableAsyncSlot.cs`'s
  `DisposeAsync(ref slot)` slot-swap shape (`Interlocked.Exchange(ref slot, DisposedSentinel)`).
- **Implementing both `IDisposable` and `IAsyncDisposable`**: give `Dispose()` a synchronous
  fallback path (dispose what can be disposed synchronously) and `DisposeAsync()` the
  asynchronous one; both must share the same idempotency guard field so calling one blocks the
  other from re-running the same cleanup. Never implement `Dispose()` as `DisposeAsync().Wait()`
  or `.GetAwaiter().GetResult()` — that's the deadlock-prone sync-over-async anti-pattern (see
  `csharp-async`); if a type only has meaningful async cleanup, make `Dispose()` a best-effort
  synchronous subset, not a blocking wrapper.
- **`await using`** is the disposal-site analogue of `using` — prefer it over a manual
  `try/finally` calling `DisposeAsync()`. In an `async` method, `using` on an
  `IAsyncDisposable`-only resource doesn't compile; on a resource implementing *both*
  interfaces, plain `using` compiles but silently takes the synchronous path — `PSH1310` flags
  this, since it can block the async method's thread on cleanup (flushing a stream, closing a
  connection) instead of yielding it.
- **`ConfigureAwait(false)`** inside `DisposeAsync()` itself for library code, same rationale as
  any other awaited library call — see `csharp-async`.

## Ownership: who disposes it

- **CA2000** (Dispose objects before losing scope): a `new SomeDisposable()` created as a local
  must be disposed (directly, or via ownership transfer — assigned to a field that itself gets
  disposed, or returned to a caller who's clearly taking ownership) before the method returns;
  if it "escapes" without either, that's the bug. Read this as: every `new` of an `IDisposable`
  needs a traceable owner before the creating scope ends.
- **CA2213** (disposable fields should be disposed): once a type is itself `IDisposable` and
  owns a field whose value came from `new`'d-or-transferred-ownership assignment (not merely an
  externally-supplied/injected reference passed straight through), that field must actually be
  released somewhere inside the type's own `Dispose()` path — a disposable type with a Dispose
  method that forgets one of its owned fields is the CA2213 shape.
- **CA1001** (types that own disposable fields should be disposable): the cheaper, non-dataflow
  precondition — if a field is directly assigned `new SomeDisposable()` (right there in an
  initializer or assignment, not traced through method calls) and the containing type doesn't
  implement `IDisposable` at all, that's a bug regardless of whether any `Dispose()` exists yet.
- **The distinguishing question for a field**: did this type call `new` on it (or otherwise
  take explicit ownership), or was it handed a reference by a caller/DI container that owns the
  lifetime itself? Only the former needs disposing by this type. Document a `leaveOpen`-style
  parameter (mirroring `StreamReader`/`StreamWriter`'s constructors) whenever a type wraps a
  caller-supplied disposable it does *not* want to own — default `leaveOpen: false` for streams
  wrapping streams is the BCL convention; pick the safer default for your own APIs deliberately,
  and document it either way.
- **Returning a disposable**: the caller becomes the owner and must dispose it — don't dispose
  it yourself before returning (that's `SST2423`'s "don't return a disposable owned by a
  `using`" shape: assigning the return value inside a `using` block whose scope ends before the
  caller ever touches it).
- **DI containers**: a container-managed singleton/scoped disposable is disposed by the
  container at scope/container teardown — don't call `Dispose()` on it yourself from consuming
  code, and don't register a type whose lifetime the container doesn't actually own (e.g. a
  shared static instance) as disposable-managed, or the container will dispose something still
  in use elsewhere.

## Rewriter: mechanical conversion to the Interlocked form

`examples/thread-safe-dispose.cs` is a single-file `dotnet run` tool (see `roslyn-rewriters`
skill for the conventions it follows: syntax-only `CSharpSyntaxRewriter`, dry-run by default,
`--write` to apply, skips `obj`/`bin`). It finds the classic non-thread-safe shape — a `private
bool _disposed;` field plus a `Dispose()` method whose body starts `if (_disposed) return; ...
_disposed = true; ...` (or the analogous ordering), plus any `if (_disposed) throw new
ObjectDisposedException(...)` guard elsewhere in the type — and rewrites it to:

- `private bool _disposed;` → `private int _disposed;`
- The `if (_disposed) return; _disposed = true;` pair at the top of `Dispose()` →
  `if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0) return;`
- Each `if (_disposed) throw new ObjectDisposedException(...)` →
  `ObjectDisposedException.ThrowIf(System.Threading.Volatile.Read(ref _disposed) != 0, this);`

It **only** rewrites when: the field is `private`, it is written to (`= true`/`= false`)
nowhere except inside `Dispose()`, and the type is not already using the virtual `Dispose(bool)`
pattern in a way the conversion would change the meaning of (an unsealed type with a `protected
virtual void Dispose(bool)` override is reported, not rewritten — the flag there often gates
more than the two statements the rewriter recognizes). Anything it can't prove safe is left
alone and reported.

It also has a `--report-owned-fields` mode: a lightweight, syntax-plus-symbol pass (no full
dataflow — deliberately not attempting to reproduce CA2213/CA2000's points-to analysis) that
lists types implementing `IDisposable` with a field directly assigned `new SomeDisposable()`
that is never referenced inside that type's `Dispose()`/`Dispose(bool)` body — the CA2213/CA1001
shape in miniature. **Report-only**: it never rewrites these, since the fix (call
`field.Dispose()` in the right place, in the right order relative to other cleanup) is a
judgment call the tool can't safely automate.

Run it:

```bash
dotnet run examples/thread-safe-dispose.cs -- /path/to/src                 # dry run
dotnet run examples/thread-safe-dispose.cs -- /path/to/src --write         # apply
dotnet run examples/thread-safe-dispose.cs -- /path/to/src --report-owned-fields
```

## Checklist

- [ ] `Dispose()`/`DisposeAsync()` idempotent via `Interlocked.Exchange` on the guard field (or
      the resource field itself) — not a plain `bool` check-then-set, unless the type provably
      never sees concurrent disposal
- [ ] Disposed-state checks use `Volatile.Read` + `ObjectDisposedException.ThrowIf`, not a
      hand-written `if (_disposed) throw`
- [ ] `Dispose()`/`DisposeAsync()` never throws; finalizers never throw and never touch other
      managed objects
- [ ] Assignable slots dispose a late-arriving value immediately once closed; replaceable slots
      dispose the value they displace
- [ ] Composite disposables snapshot-and-clear under a lock/atomic op, then dispose every entry
      outside it, in order; aggregate exceptions only where "second exception is silently
      dropped" would be unacceptable
- [ ] `Dispose(bool)`/finalizer pattern used only for unmanaged resources or unsealed types that
      need it — everything else is `sealed` with a plain `Dispose()`
- [ ] `GC.SuppressFinalize(this)` called exactly when a finalizer exists, never otherwise
      (CA1816/PSH1008)
- [ ] `IAsyncDisposable.DisposeAsync()` shares its idempotency guard with `Dispose()` when both
      are implemented; `Dispose()` never blocks on `DisposeAsync()`
- [ ] `await using` for `IAsyncDisposable` resources inside `async` methods, not a blocking
      `using` (PSH1310)
- [ ] Every field assigned `new SomeDisposable()` either gets disposed by this type or is
      provably not owned by it (`leaveOpen`-style parameter, documented) — CA1001/CA2213
- [ ] A returned disposable is never disposed before returning; a disposable's scope never ends
      before its last use

## See also

- `csharp-guard-clauses` — `ObjectDisposedException.ThrowIf` and the rest of the BCL throw-helper
  catalogue this skill builds on.
- `csharp-concurrency-primitives` — `Interlocked`/`Volatile`/`Lock` fundamentals underlying every
  pattern here.
- `csharp-async` — `ConfigureAwait`, sync-over-async pitfalls relevant to `DisposeAsync()`.
- `csharp-performance` — `helpers/AtomicDisposable.cs`, a smaller two-method version of the
  assign-once/dispose-once slot pattern for simple cases.
- `roslyn-rewriters` — the single-file `dotnet run` rewriter conventions this skill's
  `examples/thread-safe-dispose.cs` follows.
- `dotnet-analyzers` — configuring CA1001/CA2000/CA2213/CA1063 severities in a real project.
