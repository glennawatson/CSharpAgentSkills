---
name: csharp-static-lambdas
description: Use when writing or reviewing C# lambdas, anonymous methods, or local functions — when to add the `static` modifier (C# 9+), the `TState` state-passing pattern for APIs like `ConcurrentDictionary.GetOrAdd`/`string.Create`/`ThreadPool.QueueUserWorkItem`, avoiding accidental `this`/local capture, and the IDE0320 analyzer suggestion.
---

# `static` lambdas — a capture guarantee, not a speed switch

`static` on a lambda, anonymous method, or local function does not, by itself, change how the method is compiled or how fast it runs. What it does is make the compiler **reject any capture** of locals, parameters, `this`, or `base` from the enclosing scope as a compile error. The lambda can still reference `static` members, constants, and its own parameters. It's a guarantee, enforced at every future edit, that this piece of code has no closure — not an optimization you apply to existing code and expect to measure.

The reason people reach for it for performance is real, but indirect: a lambda that captures nothing already compiles to a cached, zero-allocation delegate, `static` or not. Adding `static` doesn't make a capturing lambda stop capturing — it makes a *would-be* capture a compile error instead of a silent allocation.

## What actually changes: nothing, if there's no capture

A non-capturing lambda is lowered to a method on a compiler-generated cache class and the delegate instance is created once, then reused — this has been true since C# 1's anonymous-method caching, long before `static` existed. Comparing a non-capturing lambda with and without `static`:

```csharp
Func<int, int> MakeA() => x => x + 1;          // non-static, non-capturing
Func<int, int> MakeB() => static x => x + 1;   // static, non-capturing

var a1 = MakeA(); var a2 = MakeA();
var b1 = MakeB(); var b2 = MakeB();

ReferenceEquals(a1, a2); // true — same cached delegate every call
ReferenceEquals(b1, b2); // true — same cached delegate every call
```

Both delegates target an instance method on the compiler's cache singleton (`Program+<>c`), not a `static` method in metadata — the C# 9 spec explicitly leaves that choice to the compiler. `GC.GetAllocatedBytesForCurrentThread()` around thousands of calls to either factory reports 0 bytes/call for both. A lambda that captures one outer local, by contrast, allocates a fresh closure (a display-class instance plus a new delegate) on every call — around 56 bytes/call in a minimal repro — and `static` on *that* lambda wouldn't compile at all until the capture is removed.

So: `static` doesn't speed up a lambda. Removing a capture speeds it up (fewer allocations, no retained references). `static` just makes "no capture" a checked fact instead of a hope.

## The `TState` pattern — how to fix a capture instead of hiding it

When a lambda needs outside data, pass it as an explicit parameter instead of capturing it. Many BCL and library APIs offer a `TState` overload for exactly this:

```csharp
// ❌ captures `prefix` — a closure allocation on every call that creates this lambda
string prefix = GetPrefix();
cache.GetOrAdd(key, k => prefix + k);

// ✅ static lambda, prefix passed as explicit state — no capture possible
string prefix = GetPrefix();
cache.GetOrAdd(key, static (k, s) => s + k, prefix);
```

The same shape shows up across the BCL:

```csharp
// string.Create — state is the second argument, not a captured local
string s = string.Create(len, (Prefix: prefix, Id: id), static (span, state) =>
{
    state.Prefix.AsSpan().CopyTo(span);
});

// ThreadPool — state flows through QueueUserWorkItem, not a closure
ThreadPool.QueueUserWorkItem(static state => Process(state), workItem);

// Rx / event-style APIs that accept a state parameter alongside the callback
source.Subscribe(static (value, state) => state.Handle(value), subscriber);
```

Reach for the `TState` overload whenever one exists and the lambda would otherwise need to close over something — see `csharp-string-handling` for `string.Create`'s state-based overloads and `csharp-concurrency-primitives` for thread-pool/queue APIs that take state this way.

## `this` capture — the most common hidden allocation

Any lambda written inside an instance method or property that references an instance member (a field, a non-static method, `this` itself) captures `this`, even if no other locals are involved:

```csharp
class Worker
{
    private int _count;

    // ❌ captures `this` to reach `_count` — not obviously a closure at a glance
    public Action Handler() => () => _count++;

    // ✅ static rejects the implicit `this` capture at compile time; `this`
    // has to be passed explicitly as state instead
    public Action HandlerFixed() => () => Increment(this);
    private static void Increment(Worker w) => w._count++;
}
```

Marking `Handler`'s lambda `static` (`static () => _count++;`) fails to compile (`CS8821: A static anonymous function cannot contain a reference to 'this' or 'base'`) — that failure is the tool doing its job, pointing at exactly the line with the hidden capture. `HandlerFixed`'s lambda still captures `this` too (it calls `Increment(this)`), but now `this` flows through an explicit `static` method parameter instead of an implicit field access, so the intent is visible at the call site — pass `this` through a `TState`-style parameter (as in `GetOrAdd`/`string.Create` above) if the lambda itself also needs to be capture-free.

Marking a lambda `static` inside an instance method is the fastest way to find out whether it's quietly capturing `this` — if it compiles, it wasn't.

## Static local functions

The same rule applies to local functions (C# 8+): `static void Local() { ... }` cannot reference the enclosing method's locals, parameters, or `this`. A non-`static` local function captures exactly like a lambda when it references outer state, with the same hidden-allocation risk; mark local functions `static` by default and let the compiler force capture to be explicit.

## What a `static` lambda can still reference

- Its own parameters
- `static` fields, properties, and methods (instance members of the *outer* type only via an explicit `static` accessible member, not via `this`)
- `const` values
- `nameof(...)` may still reference outer locals, parameters, `this`, or `base` — `nameof` is evaluated at compile time and never actually captures anything

## When not to bother

Adding `static` to every lambda is not a rule to enforce mechanically. Skip it when:

- The lambda genuinely needs to close over local context and a capture is the clearest way to express that (e.g. a one-off callback in cold startup/config code where allocation is irrelevant).
- Forcing state through a `TState` parameter would make the call site harder to read than the allocation it saves — this is a judgment call, not automatic.

Reach for it by default on hot-path callbacks (loop bodies, per-request handlers, anything called per-item) where accidental capture would be a real cost — see `csharp-performance` for the broader allocation-avoidance picture.

## Analyzer support

The built-in Roslyn analyzer **IDE0320 ("Make anonymous function static")** flags a lambda or anonymous method that doesn't capture anything and suggests adding `static`. It's a `Recommended`-severity suggestion, not an error — enabled by default (`csharp_prefer_static_anonymous_function`), tune it per project via `.editorconfig`; it won't fire on lambdas that do capture, since those can't take `static` at all. The equivalent option for local functions is `csharp_prefer_static_local_function`.

## Caveat: expression trees

`Expression<Func<...>>` lambdas (LINQ providers, `IQueryable`, Moq-style expression matchers, some DI/mapping libraries) only support a restricted set of syntax — the conversion to an expression tree, and often the provider translating it, rejects many newer constructs outright. `static` itself is unaffected:

| Feature | Compiles in `Expression<TDelegate>`? |
|---|---|
| `static` modifier on the lambda | Yes |
| `async`/`await` | No — `CS1989`, async lambdas cannot convert to expression trees |
| local function reference | No — `CS8110` |

Don't assume every construct that's fine in a normal `Func<>`/`Action<>` lambda works inside an expression tree — see `csharp-discards` for the matching table covering discard-related syntax, and check the provider's documentation for what it actually translates at runtime versus what merely compiles.

## Checklist

- [ ] Hot-path lambdas/local functions default to `static`; a capture is a deliberate choice, not an accident
- [ ] Where a `static` lambda needs outside data, it's passed via an explicit `TState` parameter, not captured
- [ ] Lambdas inside instance methods are checked for an implicit `this` capture (mark `static` to force the compiler to catch it)
- [ ] Local functions default to `static` unless they intentionally use outer state
- [ ] `static` isn't forced onto genuinely cold, one-off callbacks where a capture reads more clearly
- [ ] IDE0320 suggestions are reviewed, not blanket-suppressed
- [ ] Lambdas destined for `Expression<TDelegate>` are checked against the expression-tree feature set, not assumed to behave like a delegate lambda
