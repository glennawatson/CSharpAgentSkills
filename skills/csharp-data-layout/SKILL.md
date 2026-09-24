---
name: csharp-data-layout
description: Use when designing or reviewing C#/.NET data structures for cache-coherent, allocation-light access — struct-of-arrays vs array-of-structs for loops, struct field ordering, flat data instead of inheritance, static helpers that carry only the memory they touch. The default design approach for production code in these programs (most non-test code here is performance-sensitive), not a hot-path-only exception.

# Cache-coherent data layout (C#)

Performance on data-heavy code is dominated by **cache misses, not instruction count**. The CPU reads memory in 64-byte cache lines and prefetches sequentially, so the win comes from laying data out the way the code *reads* it — contiguous, dense, only the bytes you actually touch. This is the data-oriented axis; `csharp-performance` is the allocation/concurrency axis. They compose.

**This is the default here, not a special case.** Most non-test code in these programs is performance-sensitive, so design data this way from the start rather than gating it behind "is this a proven hot path?" The cost is some OO niceness — parallel arrays, integer indices instead of object references, less encapsulation — which is a worthwhile, accepted trade in production code. (Tests are the exception: there, readability wins; see `csharp-tunit`.) Still keep benchmarks for changes you expect to matter, but you don't need a profiler's permission to lay data out well.

## 1. Inheritance scatters data — avoid it for data

Use inheritance **only when there's a real `is-a` relationship and the behaviour is 100% consistently shared**. Short of that bar it's an anti-pattern — and for data specifically it's a performance tax:

- Every class instance is a **heap object** with an object header + method-table pointer (~16 bytes on x64 *before* any field), reached through a reference. An `array of a base type` is an array of references to objects **scattered across the heap** — every element is a pointer-chase to a random address, the opposite of cache-friendly.
- `virtual`/`abstract` calls are an indirection that also blocks inlining.

Replace data polymorphism with flat, contiguous shapes:

- A `sealed` type with no hierarchy, or a small `struct` so an array of them is one contiguous block (no headers, no indirection, no per-element allocation).
- A **discriminator** — an `enum` tag (or a union-style struct) — and a `switch`, instead of a subclass per variant.
- **Separate arrays per kind** processed by per-kind loops, instead of one polymorphic array.
- Behaviour that "varies by type" becomes a **static helper that switches on the tag** (see §2), or distinct per-kind loops — not an overridden method on a heap object.

## 2. Static helpers carrying only the memory they need

A method on a fat object implicitly pulls `this` — the **whole object** — into cache even if it reads two fields. A `static` helper that takes *exactly* the data it touches keeps the working set tiny:

- Pass the specific data by `in` / `ref` (large structs) or as a `Span<T>` over the relevant array — not the whole aggregate.
- Any method that doesn't read instance state should be `static` anyway (no `this` load, easier to inline).
- **Group the data and the helpers that operate on it together**, and keep the helpers narrow — each touches one coherent slice of memory. This is how you get cache-friendly *without* a class hierarchy: data in flat arrays, logic in focused statics over `ref`/`Span`.

## 3. Tight loops: arrays of related data (SoA over AoS)

When a hot loop over many elements touches only *some* fields, an **array of structs** wastes most of every cache line:

```csharp
// Array of Structs (AoS): updating Position+Velocity still drags
// Name/Health/… through cache on every element — mostly wasted bytes.
struct Entity { public Vector3 Position; public Vector3 Velocity; public string Name; public int Health; }
Entity[] entities;
```

**Structure of Arrays (SoA)** — parallel arrays indexed by the same `i` — makes the hot loop walk pure, dense, relevant data:

```csharp
Vector3[] positions;   // hot
Vector3[] velocities;  // hot
string[]  names;       // cold — not in the movement loop
int[]     health;      // cold

for (var i = 0; i < count; i++)          // streams two arrays linearly
    positions[i] += velocities[i] * dt;  // every cache-line byte is used; vectorizes well
```

- **Use SoA when:** a loop touches a subset of fields across many elements. Every byte pulled is used and the prefetcher streams perfectly.
- **Use AoS when:** you touch most fields of an element together, or the collection is tiny. AoS keeps related data together and is *far* more maintainable — don't SoA by reflex.
- The cost: the arrays must stay the **same length and in lockstep**, indexed by the same `i`. That synchronization is the maintainability hit — keep them behind one type that owns all of them.

## 4. Order struct fields deliberately

Field ordering changes how many elements fit per cache line:

- The runtime **aligns** fields; bad ordering wastes bytes on padding. Order fields **largest→smallest** (8-byte, then 4, then 2, then 1, then `bool`s) and put fields read together adjacent. More elements per cache line = fewer misses.

```csharp
// ❌ interleaved sizes — each bool sits before a 4-byte int, forcing padding
//    to realign the next int. The struct bloats with dead bytes.
struct Bad  { int i1; bool b1; int i2; bool b2; int i3; bool b3; }

// ✅ grouped largest→smallest — the three ints pack together, then the three
//    bools pack together. No interleave padding; smaller struct, more per line.
struct Good { int i1; int i2; int i3; bool b1; bool b2; bool b3; }
```

- Managed structs default to `LayoutKind.Auto` (the runtime may reorder/pack for you — usually fine). When you need a **guaranteed** layout (interop, deliberate packing), use `[StructLayout(LayoutKind.Sequential)]` (with `Pack` if needed) and verify with `Unsafe.SizeOf<T>()`.
- **Hot/cold split:** keep frequently-read fields in a small dense struct/array and push rarely-touched fields to a parallel "cold" array keyed by the same index. Cold data then never evicts hot data from a cache line. (This is the field-level version of §3's SoA split.)
- **JIT "physical promotion" (.NET 11) is about register allocation of a struct *local*, not memory layout.** It increasingly turns a small struct held in a local/parameter into independent locals/registers instead of a stack-copied blob — fewer struct-copy stalls when you pass/read such a struct inside one method. It doesn't change how fields are laid out **in an array** or on the heap, so it's not a substitute for the field-ordering advice above — an `Entity[]` still has whatever layout you gave the struct.

## 5. Keep access sequential and arrays dense

- **Sequential beats random.** Pointer-chasing and random indexing defeat the prefetcher. Lay data out in the order the loop walks it; sort/partition so the loop is linear.
- **Keep arrays packed.** Removal by **swap-and-pop** (move the last live element into the hole, decrement count) keeps the live range contiguous instead of leaving holes the loop must skip and that waste cache lines.
- **Iterate with `for` over a `Span<T>`/array** on these loops — skip `IEnumerable`/`foreach`/LINQ indirection (enumerator allocation, virtual `MoveNext`, no bounds-check elision). On net10.0+ the JIT closes most of the *enumerator-allocation* gap for plain `foreach` over `List<T>`/`T[]` (see `csharp-performance` §0) — but that doesn't replace `for`/`Span<T>` here: bounds-check elimination and vectorization on a tight SoA loop still key off the explicit `i < arr.Length` index pattern, not off enumerator devirtualization.

## 6. Concurrency: avoid false sharing (niche)

Two threads writing *different* fields that happen to share one cache line ping-pong that line between cores and kill scaling. Pad hot per-thread counters onto their own cache lines (`[StructLayout(LayoutKind.Explicit)]` with `FieldOffset` spacing, or array slots spaced 64 bytes). Only when a profiler shows the contention — otherwise the padding just wastes memory.

## The honest tradeoff

- This is **uglier and more error-prone**: parallel arrays instead of objects, integer indices instead of references, manual lockstep between arrays, less encapsulation. Bugs hide in index mismatches — own each set of parallel arrays behind one type so the invariants live in one place.
- That's an **accepted cost** in this production code, not a reason to avoid it. Contain the ugliness with clear ownership and naming rather than by sprinkling it only where a profiler points.
- **Keep a benchmark** for changes you expect to move the needle — a number beats a hunch — but you don't need a profiler's blessing to choose a cache-friendly layout in the first place (see `csharp-performance` for the measurement tooling).

## Checklist

- [ ] Inheritance only for a real `is-a` with 100%-shared behaviour; else flat `sealed`/`struct` + tag + static helpers
- [ ] A loop touching a subset of fields → SoA parallel arrays (kept in lockstep behind one owner)
- [ ] AoS kept where most fields are used together — not SoA by reflex
- [ ] Struct fields ordered large→small; hot/cold fields split where it helps
- [ ] Arrays kept dense (swap-remove) and iterated sequentially with `for`/`Span<T>`
- [ ] Static helpers take only the data they touch (`in`/`ref`/`Span<T>`), grouped with that data
