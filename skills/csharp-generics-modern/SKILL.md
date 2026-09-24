---
name: csharp-generics-modern
description: Use when writing or reviewing C#/.NET generic code — static abstract/virtual interface members and generic math (INumber<T>), the `allows ref struct` anti-constraint, constraint selection (notnull, unmanaged, struct, class?, new()), avoiding boxing with constrained generics instead of interface-typed parameters, per-T static caches, and variance.
---

# Modern generics (C# 13/14, .NET 10)

Generics done well avoid boxing, avoid interface dispatch, and let the JIT specialize per value type. Generics done poorly just move a runtime `object`/interface cast one level up.

## Static abstract/virtual interface members and generic math

An interface can declare `static abstract`/`static virtual` members — implementers supply them as ordinary statics, and generic code calls them through the type parameter:

```csharp
interface IShape<TSelf> where TSelf : IShape<TSelf>
{
    static abstract TSelf Unit { get; }
    static abstract TSelf operator +(TSelf a, TSelf b);
}

readonly struct Meters(double v) : IShape<Meters>
{
    public double Value { get; } = v;
    public static Meters Unit => new(1);
    public static Meters operator +(Meters a, Meters b) => new(a.Value + b.Value);
}

static T AddTwice<T>(T a, T b) where T : IShape<T> => a + b + T.Unit;
```

This is how `System.Numerics` generic math works — `INumber<T>`, `IAdditionOperators<TSelf,TOther,TResult>`, etc. let you write one algorithm over any numeric type without `dynamic`, reflection, or `double`-everything:

```csharp
static T SumAll<T>(ReadOnlySpan<T> values) where T : INumber<T>
{
    var total = T.Zero;
    foreach (var v in values) total += v;
    return total;
}

SumAll<int>([1, 2, 3]);
SumAll<double>([1.5, 2.5]);
```

Constrain to the narrowest numeric interface that covers what you actually use (`INumber<T>`, `IBinaryInteger<T>`, `IFloatingPoint<T>`) rather than `INumber<T>` by habit — it documents intent and widens what callers can pass.

## `allows ref struct` (C# 13)

A generic type parameter normally can't be instantiated with a `ref struct` (like `Span<T>`), because the compiler must assume the value might be boxed, stored in a field, or captured. `allows ref struct` is an *anti-constraint* — it tells the compiler "I promise not to do anything that would require boxing this `T`," which then permits `ref struct` arguments:

```csharp
void Process<T>(T value) where T : allows ref struct
{
    // no boxing, no storage in a field, no capture — span-safe body only
}

Span<int> s = stackalloc int[3];
Process(s);   // only compiles because of `allows ref struct`
```

Use it when writing generic utility code (parsers, buffer helpers) that should work equally over `Span<T>` and ordinary types. It's a relaxation, not a requirement — code without it still accepts non-ref-struct `T` as before.

## Constraint cheat sheet

| Constraint | Means | Use when |
|---|---|---|
| `struct` | value type, never `null` | want value semantics, no null checks |
| `class` | reference type | want reference semantics |
| `class?` | reference type, nullable-annotation aware | generic wrapper that must preserve nullability of a reference `T` |
| `notnull` | any type, but not nullable | API contract forbids `null` for both value and reference `T` |
| `unmanaged` | value type with no managed references anywhere in its layout (implies `struct`) | pointer/interop code, `stackalloc T`, pinning |
| `new()` | has an accessible parameterless constructor | factory-style generic code (`new T()`) |
| interface/base-class constraint | must implement/derive from it | need members on `T` without boxing |

`unmanaged` already implies `struct` — combining them (`where T : struct, unmanaged`) is a compile error; the `class`/`struct`/`unmanaged`/`notnull`/`new()` constraints must also come first in the list when combined with type constraints.

## Avoid boxing: constrain, don't accept an interface

Passing a value type through an interface-typed parameter boxes it — an allocation and an indirect call on every invocation:

```csharp
// ❌ boxes every struct TComparer passed in
int BinarySearch<T>(T[] data, T target, IComparer<T> comparer) { ... }

// ✅ constrained generic — struct comparers stay unboxed, JIT can inline/devirtualize
int BinarySearch<T, TComparer>(T[] data, T target, TComparer comparer)
    where TComparer : IComparer<T>
{
    ...
}
```

The constrained-generic form lets the JIT generate a specialized version per concrete `TComparer` and *always* inlines/devirtualizes the comparison — guaranteed at JIT time, not dependent on profiling. This is the same trick `Span<T>.Sort` and `Dictionary<K,V>`'s comparer slot use internally. Reserve the plain interface-typed parameter for cases where callers genuinely need to pass different implementations at runtime and the extra dispatch is not on a hot path.

Dynamic PGO's guarded devirtualization (default since .NET 8) can now speculatively devirtualize the interface-typed call too, *if* the site is hot enough to get profiled and one concrete type dominates — but that's a runtime bet with a monomorphic fallback path, not a compile-time guarantee, and **it does not stop the boxing**. A struct `TComparer` passed as `IComparer<T>` is boxed at the call site regardless of whether the virtual call inside gets devirtualized afterward. The constrained-generic form is still the only way to avoid the allocation.

## Specialization: `typeof(T) == typeof(int)`

For a closed set of blittable types where you want a fast path per type, compare `typeof(T)` against a specific closed type — the JIT constant-folds this away entirely for each instantiation, so the branch and the dead arm cost nothing in the compiled code for that `T`:

```csharp
static int SizeOf<T>() where T : unmanaged
{
    if (typeof(T) == typeof(byte)) return 1;
    if (typeof(T) == typeof(int)) return 4;
    if (typeof(T) == typeof(long)) return 8;
    return Unsafe.SizeOf<T>();
}
```

This only works because generics in .NET are specialized per value-type instantiation (each closed generic over a distinct value type gets its own compiled code) — reference-type instantiations share one compiled body, so this trick doesn't help them; use ordinary polymorphism there instead.

## Static generic caches (`Cache<T>.Value`)

A `static` field on a generic type is **per closed generic type**, not shared across `T` — this gives you a free per-type cache with no dictionary lookup:

```csharp
static class TypeCache<T>
{
    public static readonly Type Value = typeof(T);
}

static class DefaultOf<T>
{
    public static readonly T? Value = default;
}
```

`TypeCache<int>.Value` and `TypeCache<string>.Value` are backed by different static fields entirely — reading one never contends with or invalidates the other, and there's no runtime dictionary to hash into. Use this pattern for anything expensive to compute once per `T` (compiled accessors, reflection metadata, formatting strategies) instead of a `ConcurrentDictionary<Type, ...>`.

## .NET 11: generic math additions

A few `System.Numerics`/`System.Random` APIs went generic in .NET 11 — reach for them instead of hand-rolling the same thing per numeric type:

```csharp
// Random.NextInteger<T>/NextBinaryFloat<T> — generic over any INumber<T>-shaped type,
// instead of a per-type NextInt64/NextDouble plus a manual cast/range clamp
int i = Random.Shared.NextInteger<int>(0, 100);
float f = Random.Shared.NextBinaryFloat<float>();

// INumberBase<T>.TryParsePartial — parse a prefix of a span and report how much was consumed,
// instead of TryParse (all-or-nothing) plus manual scanning to find where the number ends
bool ok = int.TryParsePartial("123abc".AsSpan(), NumberStyles.Integer, provider: null,
    out int value, out int charsConsumed);   // ok=true, value=123, charsConsumed=3

// Complex<T> — generic complex number over any floating-point T (float, double, Half, …),
// where System.Numerics.Complex was hardcoded to double
var c = new Complex<double>(1.0, 2.0);
```

- `TryParsePartial` is genuinely useful for tokenizers/parsers walking a larger span (a number embedded in a longer token stream) where plain `TryParse` forces you to pre-slice the exact number substring first.
- `NextInteger<T>`/`NextBinaryFloat<T>` are for writing one generic-math routine that needs random values across `T` instead of a `switch` over concrete numeric types.
- `Complex<T>` slots into the same `INumber<T>`-constrained generic algorithms as any other numeric type now that it isn't hardcoded to `double` — use it when a generic numeric routine needs to also work over complex values.

## Covariance and contravariance

Only applies to **interfaces and delegates**, and only through `out`/`in` on the type parameter:

```csharp
interface IProducer<out T> { T Produce(); }      // out = covariant: IProducer<Cat> is an IProducer<Animal>
interface IConsumer<in T> { void Consume(T x); } // in  = contravariant: IConsumer<Animal> is an IConsumer<Cat>
```

- `out T` — `T` may appear only in output positions (return values). Read-only producers.
- `in T` — `T` may appear only in input positions (parameters). Write-only consumers.
- Variance is compile-time and reference-type only — value types are never variant (`IProducer<int>` is not related to `IProducer<object>`).
- `IEnumerable<out T>` and `IComparer<in T>` in the BCL are the everyday examples; you rarely need to declare your own variant interface unless building a producer/consumer abstraction.

## Checklist

- [ ] Generic math uses the narrowest numeric interface (`IBinaryInteger<T>`, not `INumber<T>` by default)
- [ ] `allows ref struct` only added when the method body genuinely never boxes/stores `T`
- [ ] Constraints combined in valid order; `unmanaged` not paired with `struct`
- [ ] Struct-typed strategy/comparer parameters constrained generically, not accepted as an interface, on hot paths
- [ ] `typeof(T) ==` specialization used only for a small closed set of value types
- [ ] Per-`T` expensive state cached via a generic static holder, not a runtime `Type`-keyed dictionary
- [ ] Variance (`in`/`out`) only on interfaces/delegates where it's actually needed
