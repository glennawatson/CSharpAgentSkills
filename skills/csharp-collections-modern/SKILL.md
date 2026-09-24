---
name: csharp-collections-modern
description: Use when writing or reviewing C#/.NET code that builds, exposes, or iterates collections — collection expressions and target typing, params collections/spans, choosing between Dictionary/HashSet (the default), FrozenDictionary/FrozenSet (rarely, only for a long-lived build-once/read-many table), ImmutableArray/ImmutableDictionary, arrays, and List<T>, using concrete collection types instead of IList/ICollection/IDictionary/ISet interfaces, Span<T>/stackalloc for local work, CollectionsMarshal, and when LINQ is fine vs when it isn't.
---

# Modern collections (C# 13/14, .NET 10)

Collection choice is an API and performance decision, not a style preference. Pick the narrowest type that fits how the data is built, read, and exposed.

## Collection expressions

`[...]` is target-typed — the compiler picks the concrete type from context, not from the syntax:

```csharp
int[] arr = [1, 2, 3];              // T[]
List<int> list = [1, 2, 3];         // List<T>
Span<int> span = [1, 2, 3];         // stack-allocated for a ref struct target
int[] a = [1, 2];
int[] combined = [.. a, 5, 6];      // spread — concatenates/copies
```

For interface targets, the compiler synthesizes a **read-only wrapper over an array**, not a `List<T>`:

```csharp
IEnumerable<int> ie = [1, 2, 3];      // compiler-generated read-only array wrapper
IReadOnlyList<int> irl = [1, 2, 3];   // same — not a List<T>, don't downcast to one
```

Don't cast a collection-expression result to `List<T>` expecting it to succeed — the concrete type is an implementation detail. If you need `List<T>` specifically, target-type to it explicitly.

The spread element `..` works on any type enumerable and lets the compiler pick a single-allocation, correctly-sized result — prefer it over `Concat().ToArray()` chains.

## .NET 11 / C# 15 — collection expression arguments

Check `TargetFramework`/`LangVersion` first (see `csharp-language-versions`) — needs `net11.0`+/C# 15; don't raise `LangVersion` on an older-targeted project just for this. A collection expression can now take a leading `with(...)` argument list to configure the collection being built — capacity, comparer, or both — instead of falling back to imperative construction:

```csharp
List<string> names = [with(capacity: 100), .. values];              // pre-sized List<T>
HashSet<string> set = [with(StringComparer.OrdinalIgnoreCase), "a", "A"]; // custom comparer, still target-typed
```

`with(...)` must be the first element and only applies when the target type has a matching constructor/factory shape (e.g. `List<T>(int capacity)`, `HashSet<T>(IEqualityComparer<T>)`) — it's resolved the same way collection-expression construction already is, just with arguments threaded through instead of a bare `new()`. Prefer this over `new List<T>(capacity) { ... }`/`new HashSet<T>(comparer) { ... }` boilerplate once the project targets `net11.0`+.

## .NET 11 library additions worth knowing

- **`EqualityComparer<T>.Create<TKey>(Func<T,TKey> keySelector, IEqualityComparer<TKey>? keyComparer = null)`** — build a comparer from a key projection without hand-writing `IEqualityComparer<T>`:
  ```csharp
  var byLength = EqualityComparer<string>.Create(s => s.Length);
  var set = new HashSet<string>(byLength);
  ```
  (There's also a `Create(Func<T?,T?,bool> equals, Func<T,int>? getHashCode = null)` overload for a fully custom comparison.)
- **LINQ `FullJoin`** — outer join that keeps unmatched rows from *both* sides (filling the missing side with `default`/a supplied value), where `Join` only keeps matches and `GroupJoin` groups matches per outer element.
- **Tuple-returning `Join`/`GroupJoin` overloads** — skip the result-selector lambda when you just want the pairing: `left.Join(right, l => l.Key, r => r.Key)` returns `(TLeft, TRight)` tuples directly; `left.GroupJoin(right, l => l.Key, r => r.Key)` returns `IEnumerable<IGrouping<TKey, TRight>>` directly, no selector needed.
- **`BitArray` span constructors** — `new BitArray(ReadOnlySpan<bool> values)` alongside the existing `bool[]`/`int[]`/`byte[]` constructors, so a `stackalloc`/slice can seed a `BitArray` without an intermediate array.

## `params` collections and spans (C# 13)

`params` is no longer array-only. Prefer `params ReadOnlySpan<T>` for hot-path APIs — the compiler stack-allocates the backing storage for small, fixed call sites instead of heap-allocating an array:

```csharp
static int Sum(params ReadOnlySpan<int> values)
{
    var total = 0;
    foreach (var v in values) total += v;
    return total;
}

Sum(1, 2, 3);          // no array allocation
Sum([4, 5, 6]);         // collection expression also binds
```

Rules:
- `params ReadOnlySpan<T>` / `params Span<T>` cannot be used in `async` methods or iterators (spans can't cross an `await` or be captured in state machines) — keep those `params T[]`.
- A type only needs a `GetEnumerator()`, indexer, and `Count`/`Length` (a "collection-like" shape) to support `params` now — you can author your own `params`-friendly collection types.
- Don't change an existing public `params T[]` to a span overload casually — it's a binary-breaking, source-compatible-only change; add an overload if both call shapes matter.

## Choosing a collection type

| Need | Use |
|---|---|
| Default lookup/set — mutable or not, most processes | `Dictionary<K,V>` / `HashSet<T>` |
| Tiny fixed set of cases known at compile time | array, or a `switch` expression — no collection at all |
| Local, mutable, size known or grows during a method | `List<T>` / `T[]` |
| Fixed-size, no growth, tight loops, best cache locality | `T[]` |
| Small, fixed-size, value-semantics collection you compare/hash | `ImmutableArray<T>` |
| Need structural immutability with cheap incremental "with" edits | `ImmutableDictionary<K,V>` / `ImmutableList<T>` |
| Long-running process, one lookup table built once at startup, read millions of times after — and you've *measured* construction is amortized away | `FrozenDictionary<K,V>` / `FrozenSet<T>` |

- **`FrozenDictionary`/`FrozenSet` are rarely the right choice — treat them as an exception, not a default.** Construction analyses the actual key set to build a specialized layout (perfect-hash-style branching tuned for that data), which is meaningfully *more* expensive to build than a `Dictionary`/`HashSet`. That cost is only worth paying when the collection is built exactly once in a long-running process and then queried a very large number of times afterward — e.g. a server's static routing table or a compiler's fixed keyword set, built at startup and read for the life of the process.
- **Do not reach for `Frozen*` for:** short-lived processes, CLI tools, tests, analyzers/generators, request/response-scoped data, anything rebuilt per request or per compilation, or "it's read-only so it should be Frozen" reasoning. In all of these the construction cost is paid on (or near) every use, which makes it slower than `Dictionary`/`HashSet`, not faster.
- **Default to `Dictionary<K,V>`/`HashSet<T>`** (or a plain array/`switch` for a handful of fixed cases) for lookup tables. Only move to `Frozen*` after profiling shows construction is amortized over enough reads to matter — measure with BenchmarkDotNet **including construction cost**, not just steady-state lookup time, before switching.
- **`ImmutableArray<T>`/`ImmutableDictionary<K,V>`** give you real structural immutability and safe sharing across threads without copy-on-write bookkeeping in your own code — `ImmutableDictionary` edits are `O(log n)` persistent-tree operations, not full copies, but that means it is slower to *read* than a plain `Dictionary`. Don't reach for `ImmutableDictionary` as a "safe default" for something only ever read — that's still just `Dictionary` territory unless you need the structural-sharing/edit semantics.
- Plain `Dictionary<K,V>`/`List<T>`/arrays remain correct and fastest for anything mutable or short-lived. Don't add `Frozen`/`Immutable` ceremony to a collection nobody shares or freezes.
- `ImmutableArray<T>` is a thin struct wrapper over `T[]` — cheap to pass around, but a `default` instance is *not* the same as `Empty` (it's null-backed and throws on iteration). Always initialize it.

```csharp
// ✅ default choice — cheap to build, fast enough for almost everything
static readonly Dictionary<string, Command> Commands =
    new() { ["run"] = Run, ["stop"] = Stop };

// ✅ Frozen only once measurement justifies it: built once at process startup,
// read millions of times over a long-running server's lifetime
static readonly FrozenDictionary<string, Route> Routes =
    LoadRoutesFromConfig().ToFrozenDictionary();

// ❌ rebuilding per call pays the (larger) construction cost on every use —
// this is slower than a plain Dictionary, not faster
FrozenDictionary<string, Command> Lookup() => _raw.ToFrozenDictionary(); // called per request
```

## Use concrete collection types, not collection interfaces

Declare collections with their concrete types: `List<T>`, `Dictionary<TKey, TValue>`, `HashSet<T>`, `T[]`, `ImmutableArray<T>`. Don't use the mutable collection interfaces (`IList<T>`, `ICollection<T>`, `IDictionary<TKey, TValue>`, `ISet<T>`) for fields, locals, parameters or return types. This matches the .NET analyzer guidance (CA1859, "use concrete types when possible for improved performance"):

- **Speed:** calls through a concrete type are direct and can be inlined; through an interface they're virtual dispatch. `foreach` over a `List<T>` uses its struct enumerator; over `IList<T>` the enumerator is boxed on older runtimes.
- **Clarity:** the concrete type tells the reader exactly what they have: its complexity, ordering, whether it's a copy.
- **Honesty:** the mutable interfaces promise operations that often throw (`IList<T>.Add` on an array, a read-only wrapper), so the type guarantees nothing.

```csharp
// ✅ concrete types throughout
private readonly Dictionary<string, Order> _byId = [];
public List<Order> FindOpen(HashSet<string> ids) { ... }

// ❌ interface types that hide what the collection is and cost dispatch
private readonly IDictionary<string, Order> _byId = new Dictionary<string, Order>();
public IList<Order> FindOpen(ICollection<string> ids) { ... }
```

Narrow exceptions:

- **`IEnumerable<T>`** for a genuinely lazy or streaming sequence (an iterator, a query you don't want to materialise). Anything already materialised goes out as its concrete type.
- **`IReadOnlyList<T>` / `IReadOnlyDictionary<TKey, TValue>`** only when a public API deliberately exposes a read-only view of internal state without copying it. The view is a contract, not protection: a caller can cast back to `List<T>`. Return `ImmutableArray<T>` or a copy when the guarantee matters. Reach for `FrozenDictionary<TKey, TValue>` only for the long-lived, build-once table discussed above, not by default.

```csharp
private readonly List<Order> _orders = [];
public IReadOnlyList<Order> Orders => _orders;   // read-only view of internal state, no copy
```

## `Span<T>` / `ReadOnlySpan<T>` / `stackalloc` for local work

Use spans for slicing and short-lived buffers that never escape the method:

```csharp
ReadOnlySpan<char> csv = "a,bb,ccc";
var comma = csv.IndexOf(',');
ReadOnlySpan<char> first = csv[..comma];      // slice, no allocation

Span<int> scratch = stackalloc int[16];        // small, fixed, stack-only
```

- Spans are `ref struct` — cannot be a field of a non-`ref struct` class, cannot be boxed, cannot cross `await`/`yield`. That's a feature: it keeps them from escaping.
- `stackalloc` is for small, bounded sizes known not to blow the stack (tens to low hundreds of elements) — never with an unbounded/user-controlled length.
- Slice instead of `Substring`/`ToArray`/`Skip().Take()` when you're only reading a subrange.

## `CollectionsMarshal` — controlled escape hatches

For hot paths where the collection API forces an extra lookup or copy:

```csharp
// mutate a List<T>'s backing storage in place, no copy
Span<int> span = CollectionsMarshal.AsSpan(list);
span[0] = 42;

// one lookup instead of ContainsKey + indexer/Add
ref int slot = ref CollectionsMarshal.GetValueRefOrAddDefault(dict, key, out var existed);
if (!existed) slot = ComputeInitial();
slot++;
```

Caveats: `AsSpan` is invalidated by any operation that resizes the list (`Add` past capacity, `RemoveAt`, etc.) — don't mutate the list's shape while holding the span. `GetValueRefOrAddDefault` returns a ref into the dictionary's internal storage — it's invalidated by any subsequent resize of the dictionary, so use it immediately, not stashed across other mutations.

## LINQ in hot paths

LINQ is fine on cold/startup paths and for readability — the iterator and delegate overhead is real but usually irrelevant off the hot path. On measured hot loops (per-frame, per-request, per-notification), prefer a manual `for`/`foreach` — see `csharp-performance` for the allocation reasoning.

Newer LINQ methods worth knowing (avoid hand-rolled equivalents):

```csharp
words.CountBy(w => w[0]);                          // group + count in one pass
words.AggregateBy(w => w[0], 0, (acc, w) => acc + w.Length); // group + fold, no intermediate groupings
words.Index();                                      // (int Index, T Item) pairs, replaces .Select((w, i) => (i, w))
```

**Avoid multiple enumeration.** An `IEnumerable<T>` from a LINQ query or a `yield return` iterator re-runs its whole pipeline (including side effects and I/O) on every enumeration:

```csharp
// ❌ query runs twice
IEnumerable<int> Query() => data.Where(x => x > 0);
var q = Query();
if (q.Any()) Process(q.ToList());     // enumerated once for Any, again for ToList

// ✅ materialize once, reuse
var results = Query().ToList();
if (results.Count > 0) Process(results);
```

If a method returns `IEnumerable<T>` and the caller might enumerate more than once, materialize (`ToList`/`ToArray`) at the boundary rather than letting each call site guess.

## Checklist

- [ ] Collection-expression target type is the one you actually want (`IReadOnlyList<T>`/`IEnumerable<T>` are *not* `List<T>`)
- [ ] `params ReadOnlySpan<T>` on hot-path variadic APIs, not `async`/iterator methods
- [ ] On `net11.0`+/C# 15: `[with(capacity: n), ...]`/`[with(comparer), ...]` used instead of `new List<T>(n) { ... }` boilerplate where the TFM allows it
- [ ] `Dictionary`/`HashSet` by default; `Frozen*` only in a long-running process, with a benchmark that includes construction cost
- [ ] `Immutable*` only where structural sharing/immutability is actually needed
- [ ] Collections declared as concrete types (`List<T>`, `Dictionary<TKey, TValue>`, `HashSet<T>`, `T[]`), never `IList<T>`/`ICollection<T>`/`IDictionary<TKey, TValue>`/`ISet<T>`
- [ ] `IEnumerable<T>` only for lazy sequences; `IReadOnlyList<T>`/`IReadOnlyDictionary<TKey, TValue>` only for a deliberate read-only view of internal state
- [ ] Spans/`stackalloc` stay local, bounded, and never cross `await`
- [ ] `CollectionsMarshal` refs/spans used immediately, not held across a resize
- [ ] LINQ avoided on measured hot loops; no query re-enumerated by accident
