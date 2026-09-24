---
name: csharp-modern-types
description: Use when declaring or reviewing C# types — choosing between record, record struct, readonly record struct, readonly struct, and class; primary constructor capture pitfalls; required/init members; `with` expressions and shallow copy; record value-equality traps (collections, inheritance, mutable members); sealed defaults; file-scoped types; and the C# 14 `field` keyword.
---

# Modern types — pick the right shape, avoid the equality traps

C# now has five common shapes for a type. Pick by **mutability** and **value vs. reference semantics**, not by habit.

## Choosing a shape

| Shape | Semantics | Mutable | Use for |
| --- | --- | --- | --- |
| `class` | reference, identity equality | yes | services, entities with identity, anything with behavior/lifetime |
| `record` (class) | reference, **value** equality | yes (unless you make it `init`-only) | immutable-by-convention data with structural equality; DTOs, messages |
| `record struct` | value type, value equality | yes | small, copyable, immutable-by-convention value data (already gets value equality free) |
| `readonly record struct` | value type, value equality | no | the default choice for small immutable value data — no defensive copies needed |
| `readonly struct` | value type, identity via members you define | no | small value types where you don't want generated value equality/`ToString`/`with` |

Rule of thumb: **need identity or a lifetime → `class`. Need structural equality → `record`. Small (≤ a few words) and copied by value → prefer a struct variant, `readonly` unless you have a real reason to mutate in place.**

```csharp
// entity with identity — plain class
class Customer
{
    public int Id { get; init; }
    public string Name { get; set; } = "";
}

// immutable value data compared by contents — readonly record struct
readonly record struct Money(decimal Amount, string Currency);

// immutable reference data compared by contents — record
record OrderPlaced(int OrderId, DateTimeOffset When);
```

## Primary constructors — the capture pitfall

A primary constructor parameter on a `class`/`struct` (not `record`, which turns them into properties automatically) is **not just constructor plumbing** — if you use it anywhere outside the parameter list's own initializers, the compiler captures it as a private field, and that field is **mutable**, even if you never intended to write to it:

```csharp
class Counter(int start)
{
    // `start` is captured into a hidden field because it's read here
    public int Next() => start++;   // compiles — the "parameter" is silently mutable
}
```

There's no way to mark a captured primary-constructor parameter `readonly` — if you need immutability, assign it to a real `readonly` field or an `init` property explicitly:

```csharp
class Counter(int start)
{
    private readonly int _start = start;   // now genuinely immutable
    public int Value => _start;
}
```

**Double-capture warning (CS9124):** if a parameter is both used to initialize a member *and* captured elsewhere in the body, the compiler warns because you likely meant to reference the member, not silently re-capture the parameter as a second piece of state:

```csharp
class Dual(int value)
{
    public int Stored { get; } = value;   // initializes a member from the parameter
    public int Get() => value;            // CS9124: also captured directly — probably meant `Stored`
}
```

Fix by referencing the member (`Stored`) instead of the parameter once it's been promoted to a field/property.

## `required` and `init`

`init` makes a member settable only during object-initializer syntax (or the constructor); `required` forces callers to set it there. Together they replace constructor-parameter boilerplate for DTOs while keeping immutability after construction:

```csharp
class Person
{
    public required string Name { get; init; }
    public int Age { get; init; }
}

var p = new Person { Name = "Ann" };   // Age defaults to 0; Name is mandatory — CS9035 if omitted
```

`required` members can still be set via a constructor if you mark it with `[SetsRequiredMembers]` — use that when you have a real constructor that guarantees the invariant, so callers aren't forced into object-initializer syntax.

## `with` expressions — shallow copy only

`with` on a `record`/`record struct` produces a **shallow copy**: value-type members are copied by value, reference-type members copy the *reference*, not the referenced object.

```csharp
record Basket(List<string> Items);

var a = new Basket(["apple"]);
var b = a with { };
b.Items.Add("banana");
// a.Items now also contains "banana" — same List<string> instance
```

If a member is a mutable collection (or any mutable reference type), `with` does **not** give you an independent copy of it. Either make the member itself immutable (`ImmutableList<T>`, `IReadOnlyList<T>` backed by a fresh array on each `with`) or deep-copy it explicitly in a custom constructor.

## Record value equality — where it traps you

Records generate member-wise `Equals`/`GetHashCode`/`==` automatically, which is exactly what you want for plain immutable data — and exactly what bites you in three situations:

**1. Collections inside records compare by reference, not contents**, because `List<T>`/arrays/`Dictionary<,>` don't override `Equals`. Two records holding "the same" list contents are **not equal**:

```csharp
record Basket { public List<string> Items { get; init; } = []; }

var b1 = new Basket { Items = ["apple"] };
var b2 = new Basket { Items = ["apple"] };
b1 == b2;   // false — reference equality on the List<string>
```

Use an immutable, value-comparable collection type (`ImmutableArray<T>`, or compare `SequenceEqual` explicitly) if you need this to be true.

**2. Inheritance changes what "equal" means.** Record equality checks the *runtime type* first, so a `Base` and a `Derived : Base` with identical inherited members are never equal — but two `Derived` instances compare **all** members, including ones added down the hierarchy, and a virtual `Equals` override that isn't updated when you add a derived record can silently stop comparing new members. Prefer a `sealed` leaf record for anything that must have simple value equality; keep base records `abstract`.

**3. Mutable members break the hash-as-key assumption.** A record's `GetHashCode` reads its current field values — if you mutate a record (via a settable, non-`init` property) after putting it in a `HashSet<T>`/using it as a `Dictionary<TKey,>` key, its hash changes and the collection can no longer find it. Keep records that participate in hashing fully `init`-only.

## `sealed` defaults

Records are **not** sealed by default — `record Base { }` can be inherited unless you say `sealed record Base { }`. This is different from many expectations and matters both for equality (see above) and for API design: seal a record the moment nothing should derive from it, which is most of the time for data-only records.

Classes are also unsealed by default. If a class isn't designed for inheritance, `sealed` it — it documents intent and lets the JIT devirtualize calls.

## File-scoped types

`file class`/`file record`/`file struct` restrict a type to the file that declares it — useful for helper types (source-generator output, a private implementation detail shared by a couple of top-level functions) that shouldn't leak into the assembly's public surface or collide by name with another file's helper:

```csharp
file class ParseState
{
    public int Position;
}
```

## The `field` keyword (C# 14)

Inside a property accessor, `field` refers to the compiler-synthesized backing field without you having to declare one — useful for adding validation/transformation to an auto-property without the ceremony of a full manual backing field:

```csharp
class Widget
{
    public string Name
    {
        get;
        set => field = value?.Trim() ?? throw new ArgumentNullException(nameof(value));
    } = "";
}
```

Only usable inside the accessors of the property that owns the implicit field; if you need to read/write the backing storage from elsewhere in the type, you still need an explicit field.

## .NET 11 / C# 15 — union vs. closed hierarchy vs. classic abstract base

Check `TargetFramework`/`LangVersion` before reaching for either (see `csharp-language-versions`) — both need `net11.0`+/C# 15. Never raise `LangVersion` or retarget just to use them; on `net10.0` and earlier, use the classic `abstract`/`sealed` base pattern below instead.

| Shape | Case types share a base? | Cases addable outside your assembly? | Use for |
| --- | --- | --- | --- |
| `union Pet(Cat, Dog, Bird);` | No — unrelated types, no inheritance needed | No — fixed set declared inline | "exactly one of these otherwise-unrelated shapes" — parse results, API response variants |
| `closed record class GateState;` + derived records | Yes — real shared members/behavior via inheritance | No — compiler-enforced to the declaring assembly | A small, fixed state/variant set that shares behavior and should never grow from outside |
| `abstract record Shape;` + `sealed` derived records (classic, works on any TFM) | Yes | Yes, in principle (only convention stops it) | Same as `closed`, but on `net10.0`/older, or when you *do* want extensibility |

```csharp
// union — no natural common base, just "one of these"
public union Pet(Cat, Dog, Bird);
public record Cat(string Name);
public record Dog(string Name);
public record Bird(string Name);

// closed hierarchy — shared behavior via inheritance, assembly-sealed case set
public closed record class GateState
{
    public abstract string Label { get; }
}
public sealed record Open : GateState { public override string Label => "open"; }
public sealed record Closed : GateState { public override string Label => "closed"; }
```

`closed` rules: implies `abstract` (can't instantiate the closed type directly); can't combine with `sealed`/`static`/`abstract` on the same declaration; derived types are restricted to the declaring assembly (stronger than `sealed`'s per-type convention — no other assembly can ever add a case); not transitive — an intermediate base in a multi-level hierarchy needs its own `closed` if you want that subtree closed too. `union` case types get implicit conversions to the union type, and `System.Text.Json` can serialize/deserialize a `union` directly.

Default to the classic `abstract`/`sealed` pattern from the table above when the project doesn't target `net11.0`+ yet, or when the type genuinely needs to be extensible by other assemblies — `closed`/`union` are for when a fixed, compiler-enforced case set is exactly what you want. See `csharp-pattern-matching` for how these interact with `switch` exhaustiveness.

## Checklist

- [ ] Reference/identity type → `class`; structural-equality data → `record`; small immutable value data → `readonly record struct`
- [ ] Primary constructor parameters that must stay immutable are promoted to a `readonly` field/`init` property, not left as a captured mutable parameter
- [ ] No CS9124 warnings (parameter both initializing a member and separately captured) left unaddressed
- [ ] `with` isn't relied on to deep-copy mutable reference members (collections especially)
- [ ] Records holding mutable collections don't rely on `==`/hashing over those collections unless using a value-comparable collection type
- [ ] Base records intended as leaves are `sealed`; hierarchies used for equality are deliberate, not accidental
- [ ] Records/objects used as dictionary keys or in hash sets are `init`-only, never mutated after insertion
- [ ] File-local helper types use `file` instead of `internal`/`private` sprawl
