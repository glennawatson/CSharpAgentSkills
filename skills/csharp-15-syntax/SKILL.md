---
name: csharp-15-syntax
description: Use when a project targets net11.0/C# 15 (or is deciding whether to) and you need the full depth on union types, closed hierarchies, collection expression arguments, extension indexers, labeled break/continue, or the memory-safety preview — implicit conversions, exhaustiveness, what the compiler actually generates, the boxing cost of unions with value-type cases, and how to choose between union/closed/enum/hand-written discriminated union. `csharp-language-versions`, `csharp-pattern-matching`, `csharp-modern-types`, `csharp-extension-members` and `csharp-collections-modern` cover these features briefly; this is the deep-dive.
---

# C# 15 / .NET 11 — in-depth reference

Check the project's actual `TargetFramework`/`LangVersion` first — see `csharp-language-versions`. Everything here needs C# 15 (the default for `net11.0`) unless noted; some features also need runtime types that only exist on `net11.0` (unions), while `closed` can be polyfilled onto older targets (see `csharp-polyfills`). Don't change `LangVersion` or the target framework as a side effect of using one of these. RC1-specific gaps are called out explicitly.

## Union types — how they actually work

`union Pet(Cat, Dog, Bird);` is sugar. The compiler lowers it to a plain `struct`:

```csharp
[System.Runtime.CompilerServices.Union]
public struct Pet : System.Runtime.CompilerServices.IUnion
{
    public Pet(Cat value) => Value = value;
    public Pet(Dog value) => Value = value;
    public Pet(Bird value) => Value = value;
    public object? Value { get; }
}
```

Reflecting on a compiled `union Pet(Cat, Dog, Bird);` (case types are `record`s) shows the generated type has exactly one field, `<Value>k__BackingField : System.Object`, implements `System.Runtime.CompilerServices.IUnion`, and carries `System.Runtime.CompilerServices.UnionAttribute`. `typeof(Pet).IsValueType` is `true` — a union declaration is always a `struct`, never a `record struct` (an earlier proposal considered and rejected — see "Resolved: Is union declaration a record?" in the csharplang speclet).

**Any class or struct** can opt into union behavior just by carrying `[Union]`, implementing `IUnion`, and exposing matching creation members (constructors, or static `Create` factories via a nested `IUnionMembers` provider interface) plus a `Value` property — the `union` keyword is just the compiler-generated shorthand for the common case. Hand-authoring lets you add the *non-boxing access pattern* (`HasValue` + a `TryGetValue(out T)` per case type), which the compiler prefers for pattern matching when present. **The compiler-generated `union` declaration never emits `HasValue`/`TryGetValue`** — reflecting on a generated union's public members shows only the constructors and `Value` are present. So every `union X(...)` you declare with the shorthand syntax always goes through the boxing `object? Value` path; there is no way to opt a shorthand `union` declaration into the non-boxing pattern short of hand-writing the whole type yourself.

### Implicit conversions and exhaustive matching

```csharp
Pet p = new Cat("Tom");   // union conversion — calls Pet(Cat value)

string Describe(Pet pet) => pet switch
{
    Cat c => $"cat {c.Name}",
    Dog d => $"dog {d.Name}",
    Bird b => $"bird {b.Name}",   // omitting this produces CS8509
};
```

Omitting the `Bird` arm produces `warning CS8509: ... the pattern 'Bird' is not covered.` — real, compiler-checked exhaustiveness, not convention.

Union conversions rank as another form of user-defined conversion: an explicit user-defined `implicit`/`explicit operator` you declare on the union type takes priority over the union conversion for the corresponding case type. There are no *explicit* union conversions beyond the implicit ones — an explicit conversion existing to a case type doesn't imply one to the union.

### Pattern matching semantics (not just `switch`)

A type pattern against a union unwraps to `Value` and narrows: `p is Cat` succeeds when `p.Value is Cat`, with output value `(Cat)p.Value`. `p is Pet` (the union type itself) is a compile error unless `Pet` is itself a possible case's ancestor — you match case types, not the union type. `null` patterns are special-cased: for a *struct* union, `is null` checks whether `Value` is null; for a *class* union it also has to account for the instance itself being null (see the speclet's "Checking instance itself for null" discussion — still an open corner in the design). `var`, list, and discard patterns do **not** unwrap the union — they apply to the union instance itself, not `Value`.

Property and positional patterns only unwrap when they carry an explicit case type — a bare `{ ... }`/`(...)` pattern (no type before the braces/parens) matches against the union instance itself, not its contents:

```csharp
record Cat(string Name);
union Pet(Cat, Dog);
Pet p = new Cat("Fido");

p is { Name: "Fido" }        // error — Pet has no 'Name' member; applied to p itself
p is Cat { Name: "Fido" }    // true — type portion present, applied to p.Value
p is { Value: Cat }          // true — matches the union's own Value member directly, no unwrapping
```

The same rule applies to positional patterns: `p is (Cat)` needs the leading type to trigger unwrapping to `p.Value`; a bare `p is (...)` without it calls `Pet`'s own `Deconstruct` if it has one, not the case type's.

### Nullability

`Value`'s default null-state is "maybe null" if any case type's default state is maybe-null, otherwise "not null" — tracked like any other property, and a `switch` that's otherwise exhaustive over case types still warns if `Value`'s null state is "maybe null" and no arm handles `null`.

### Generics

Unions can be generic (`union Result<T>(T, System.Exception) where T : notnull;`) and work normally when matched against a **closed** (instantiated) generic type at the use site — `Result<string>` matching `string v => ...` compiles and runs correctly. Matching directly against an **open, unconstrained type parameter as a case type inside the generic method itself** (`T v => ...` inside a method with `<T>` still generic) hits `error CS8780: A variable may not be declared within a ... union matching` in RC1 — treat generic-parameter-as-case-type patterns as not fully baked yet and verify before relying on them.

### Hand-writing a non-boxing union (the escape hatch from the boxing caveat)

Since the shorthand `union` syntax never emits `HasValue`/`TryGetValue`, the only way to get a non-boxing union today is to author the type yourself, storing each case's value in its own strongly-typed field and implementing the *non-boxing access pattern* by hand:

```csharp
[System.Runtime.CompilerServices.Union]
public readonly struct IntOrBool : System.Runtime.CompilerServices.IUnion
{
    private readonly bool _isBool;
    private readonly int _intValue;

    public IntOrBool(int value) { _isBool = false; _intValue = value; }
    public IntOrBool(bool value) { _isBool = true; _intValue = value ? 1 : 0; }

    // Mandatory: still needs a boxing Value property for the basic pattern...
    public object Value => _isBool ? _intValue is 1 : _intValue;

    // ...but the compiler prefers these for pattern matching when present,
    // avoiding the box on the hot path.
    public bool HasValue => true;
    public bool TryGetValue(out int value) { value = _intValue; return !_isBool; }
    public bool TryGetValue(out bool value) { value = _isBool && _intValue is 1; return _isBool; }
}
```

This is real work — it's the trade-off for zero boxing on the matching path. `Value` itself is still `object`-typed (mandatory for the basic pattern the language recognizes), so anything that reads `Value` directly still boxes; only pattern matching through `TryGetValue` avoids it. If you need to avoid boxing on both the write path (construction) *and* every read, a plain hand-rolled struct with a discriminator field and no union-attribute machinery at all (not opting into `IUnion`/`[Union]`) is simpler and gives you full control — you lose the compiler's `switch` exhaustiveness checking in exchange.

### System.Text.Json (.NET 11)

Serialization of a union writes the *underlying case value's* JSON with no wrapper or discriminator — `JsonSerializer.Serialize((Pet)new Cat("Tom"))` produces `{"Name":"Tom"}`. **Deserialization back into the union type fails when case types are structurally ambiguous** — deserializing `{"Name":"Tom"}` into `Pet` (cases `Cat`, `Dog`, `Bird`, all `record(string Name)`) throws `JsonException: JSON value type 'Object' is ambiguous for union type 'Pet' because multiple case types can use this value type. Specify a custom type classifier to support deserialization.` Round-tripping works fine when the case types are structurally distinguishable (e.g., `union NumOrText(int, string)` round-trips `42` and `"hello"` correctly). If you need to serialize a union whose cases could collide in shape, give the case types distinct JSON shapes (a discriminator property) or don't rely on default `System.Text.Json` union support yet.

### What's not implemented / worth re-checking in RC1

- Non-boxing access pattern (`HasValue`/`TryGetValue`) is never generated for shorthand `union` declarations (see above) — only relevant if you hand-write a union type.
- Generic case-type patterns against an open type parameter inside the same generic method (`CS8780` above).
- `System.Text.Json` deserialization of structurally-ambiguous unions needs "a custom type classifier" per the runtime's own error message — no built-in attribute for this was found in RC1; treat it as not yet available and verify again on a newer preview/RC before depending on it.
- The docs note plainly: "Some features from the proposal specification aren't yet implemented. Those features are coming in future previews" — re-verify anything non-trivial (especially around `IUnionMembers` provider interfaces and the non-boxing pattern) against whatever SDK you're actually building with.

## The boxing caveat — read this before using unions on a hot path

**The shorthand `union` declaration stores its value in a single `object? Value` field. Any value-type case (`int`, an `enum`, a plain `struct`) is boxed the moment it's converted into the union, and every subsequent read unboxes.** This is not a hypothetical — it's the only storage strategy the current compiler emits (reflecting on the generated field shows `System.Object`, not `System.Int32`):

```csharp
public union NumOrText(int, string);
```

```csharp
NumOrText nt = default;
for (int i = 0; i < 100_000; i++)
{
    nt = i;   // implicit conversion int -> union: boxes 'i' on every iteration, a real heap allocation
}
```

A parallel loop doing plain `total += i` over the same iterations allocates nothing. Each conversion costs the same as any other `object`-boxed `int` on a 64-bit runtime (an 8-byte header, an 8-byte method table pointer, and the payload, rounded up to 8-byte alignment) — not virtual/free. This is a straight-line consequence of the lowering shown above; a `BenchmarkDotNet [MemoryDiagnoser]` run shows a non-zero `Gen0`/`Allocated` column for any hot loop that repeatedly assigns a struct/int/enum case into a `union`-declared type.

Implications:

- **Avoid `union` types with value-type cases on hot paths** — a tight loop, a parser's per-token result, a per-frame game-loop value, anything allocated per element of a large collection. Every conversion into the union is a GC allocation; every read back out is an unbox.
- If all case types are reference types (records, classes), there's no boxing concern — the `object? Value` field just holds a reference, same cost as any other reference assignment.
- For a hot path with value-type cases, prefer either:
  - A **closed hierarchy of reference types** (below) if the cases genuinely are (or can be) reference types with shared behavior, or
  - A **hand-written struct discriminated union** — a tag field (`enum`/`byte`) plus explicit per-case storage (unioned via `[StructLayout(LayoutKind.Explicit)]`/multiple fields, or a case-specific field per variant if cases are few), giving you full control over layout and zero boxing. This is more code, but it's the only zero-allocation option today.
- This is the current (RC1) lowering strategy, not a language guarantee. The layout may change in future .NET releases (e.g., a non-boxing storage strategy for unions with few, small value-type cases) — don't hard-code assumptions about the field layout into production code (e.g., via unsafe field access), and re-verify allocation behavior if you upgrade the SDK and boxing matters to you.

## Closed hierarchies

```csharp
public closed record class GateState;
public sealed record Open : GateState;
public sealed record Closed : GateState;

string Describe(GateState g) => g switch
{
    Open => "open",
    Closed => "closed",
    // no default arm needed — compiles clean, no CS8509
};
```

Rules, several non-obvious:

- `closed` is a contextual modifier on `class` (including `record class`). It implies `abstract` (`typeof(GateState).IsAbstract == true`) and cannot combine with `sealed`, `static`, or an explicit `abstract` modifier.
- **Same-assembly, same-module restriction**: a type outside the declaring assembly (or module) cannot directly derive from a `closed` type — this is a compiler error, not a convention, unlike a plain `sealed`-base pattern which only stops derivation by discipline.
- **Not transitive**: a class deriving from a `closed` class is not itself closed unless you mark it `closed` too. If you want exhaustiveness through a multi-level hierarchy, every intermediate level needs its own `closed`.
- **Generic closed types** require every type parameter of a closed base to be used in a derived type's base-class specification (`class D1<U> : C<U>` is fine; `class D3<W> : C<int>` is an error) — this keeps a single derived generic instantiation mapped to each closed base instantiation, which is what makes exhaustiveness checking sound for generics.
- **Interface convertibility restriction**: if every subtype in a closed class's hierarchy is `sealed` or `closed` (a "sealed hierarchy"), the compiler blocks an explicit cast to an interface type none of them implement — the same rule that already applies to casting a `sealed` class to an unrelated interface.
- **Empty closed types are not exhaustive**: an empty `switch` over a closed type with zero derived types still warns — the compiler treats "no cases yet" as a transitional state, not evidence of true exhaustiveness (`C => 1` with the base type itself as an arm silences it).
- **A type parameter constrained to a closed type gets the same exhaustiveness treatment** as the closed type itself:

  ```csharp
  closed class C;
  class D1 : C;
  class D2 : C;

  int M<X>(X x) where X : C => x switch
  {
      D1 => 1,
      D2 => 2,
      // exhaustive — no default needed, same as switching on C directly
  };
  ```

- **Subtype constraints don't refine exhaustiveness**: if a derived type adds a `where` constraint the closed base didn't have (e.g. `class D2<U> : C<U> where U : struct`), the compiler doesn't attempt to prove that constraint is unsatisfiable for a given call site — it still asks you to handle that subtype's case (or the base type) even when generic substitution would make it impossible in practice.
- Lowering emits `[System.Runtime.CompilerServices.IsClosedType]` on the class.

### Closed hierarchies are more portable than unions

Unlike `union` (which needs `UnionAttribute`/`IUnion` from the .NET 11 runtime and hard-fails on `net10.0` even with `LangVersion=15` forced — see Availability below), **`closed` only needs a marker attribute the compiler can be handed via a local polyfill**. `closed record class` on a `net10.0` project with `LangVersion=15` fails with `error CS0656: Missing compiler required member 'System.Runtime.CompilerServices.IsClosedTypeAttribute..ctor'` — but defining that attribute yourself (a plain empty `[AttributeUsage(AttributeTargets.Class)] sealed class IsClosedTypeAttribute : Attribute` in `System.Runtime.CompilerServices`, same trick as the classic `IsExternalInit` polyfill for `init`) makes it compile and run correctly on `net10.0`. This still needs an explicit `LangVersion=15` override on a non-`net11.0` TFM (against the general "don't bump LangVersion" guidance in `csharp-language-versions` — treat this as a deliberate, rare exception only if you actually need exhaustiveness on an older TFM and are willing to own the polyfill) — decide deliberately, it's not free.

## Collection expression arguments — deeper notes

Covered at a syntax level in `csharp-collections-modern`. Two points worth the extra depth:

- `with(...)` binds to whichever construction path the target type would already use for a collection expression — a matching constructor for `new CollectionType(...)`, a matching parameter set on a `Create` factory method, or (for an interface target like `IDictionary<,>`) a single argument that must implement a well-known BCL comparer interface. It is resolved the same way plain `[...]` construction already picks a strategy; `with(...)` just threads extra arguments through that existing resolution instead of introducing a new one.
- `List<string> names = [with(capacity: 10), "a", "b"];` produces a list with `Capacity == 10, Count == 2`; `HashSet<string> set = [with(StringComparer.OrdinalIgnoreCase), "a", "A"];` produces `Count == 1` (case-insensitive dedupe applied at construction, not after) — the constructor overload actually gets called with the argument, not just accepted syntactically.
- `with(...)` must be the *first* element; there is exactly one per collection expression, and no comma-vs-semicolon ambiguity was chosen deliberately (see the "Design Philosophy" section of the csharplang proposal) precisely to avoid `[1, 2]` (two elements) being confused with an arguments-then-elements list.

## Extension indexers, labeled break/continue

Both are covered with working examples in `csharp-extension-members` and `csharp-language-versions` respectively — nothing to add beyond what's there except confirming both compile and run as documented in RC1: a `this[int]` inside an `extension(T receiver)` block routes indexing syntax through the extension, and `continue outer:`/`break outer:`-labeled jumps out of/past a nested loop compile and execute as expected, including through a 2D grid scan pattern. The IDE0410 style rule (flagging the flag-variable/`goto` workarounds these replace) is a Roslyn analyzer, not something to verify via `dotnet run` — trust the docs' description of it.

## Memory-safety pointer relaxations — preview only, don't ship it

Requires `<LangVersion>preview</LangVersion>` **and** `AllowUnsafeBlocks=true`; this is a multi-release effort still in its first step in C# 15/RC1. The split is precise: declaring a pointer and taking an address compiles *without* an `unsafe` context —

```csharp
int number = 42;
int* pointer = &number;      // compiles fine under LangVersion=preview, no `unsafe` needed
```

— but dereferencing still requires one: `*pointer` under the same settings, outside `unsafe`, fails with `error CS9360: This operation may only be used in an unsafe context`. So the relaxation is narrowly about *declaring/taking addresses/`fixed`/`sizeof`*, not about unmanaged memory access — indirection (`*p`, `p->m`, `p[i]`), function pointer invocation, and anything that actually reads/writes through a pointer is still gated. There's also `unsafe(expr)` (an unsafe *expression*, useful in field initializers/constructor initializers/catch filters where an `unsafe` block can't syntactically appear) and a `safe` contextual keyword for `extern` members/fields in explicit/extended layout types, tied to a separate `updated-memory-safety-rules` compiler feature flag that adds caller-side "requires-unsafe" propagation.

Per the general preview-features rule in `csharp-language-versions`: **never use `LangVersion=preview` constructs in product code.** These are for a throwaway file-based spike only, and the feature itself is explicitly unstable pending future C# releases.

## Decision guide — union vs. closed hierarchy vs. enum + switch vs. hand-written struct union

| Need | Use |
| --- | --- |
| A small fixed set of well-known, non-overlapping **states** with no payload | `enum` + `switch` with a `_`/`default` fallback — cheapest, but remember enum exhaustiveness is not compiler-enforced (see `csharp-pattern-matching`) |
| A fixed set of **unrelated reference types**, no shared base makes sense, need real compiler exhaustiveness | `union` (e.g. `union Pet(Cat, Dog, Bird);`) — accept the boxing caveat doesn't apply since there's no value-type case |
| A fixed set of cases that **share real behavior/members** via inheritance, all reference types, compiler-enforced exhaustiveness | `closed record class` hierarchy — no boxing concern either way since it's plain reference-type polymorphism, and it's the more portable choice (polyfillable to older TFMs, unlike `union`) |
| A fixed set of cases where **one or more cases are value types** (`int`, `enum`, small `struct`) **and this is a hot path** | Hand-written struct discriminated union (tag + explicit per-case storage) — the only option with no boxing today; do **not** reach for `union` here |
| A fixed set of cases where value-type cases exist but it's **not** a hot path (config parsing, one-off API responses, rare branches) | `union` is fine — the boxing is real but immaterial off the hot path; simpler to declare and maintain than a hand-rolled struct union |
| Need extensibility by consumers outside your assembly/package | Neither `union` nor `closed` — use a classic `abstract`/`sealed` (or `interface`) hierarchy; both C# 15 features are specifically about closing the set |
| Already have an "OneOf"-style third-party struct union type in the codebase | Don't migrate it wholesale just because `union` exists — compare actual allocation behavior (many hand-rolled OneOf-style types already avoid boxing for small case counts) before switching to the boxing-by-default `union` declaration |

## Availability

`net11.0` defaults to `LangVersion` 15: a file-based app with no explicit TFM/LangVersion on the RC1 SDK reports `TargetFramework: net11.0, LangVersion: 15.0` via `dotnet build -getProperty:`.

- **`union` cannot be polyfilled onto an older TFM.** `net10.0` + explicit `LangVersion=15` fails a `union` declaration with `error CS0518: Predefined type 'System.Runtime.CompilerServices.IUnion' is not defined or imported` and `error CS0656: Missing compiler required member 'System.Runtime.CompilerServices.UnionAttribute..ctor'` — these are real runtime types shipped starting .NET 11 Preview 5, not synthesizable by the compiler the way `IsExternalInit` is for `init`. A union type is effectively `net11.0`+ only, full stop, even if you're willing to hand-write the attribute/interface yourself (the compiler explicitly declines to synthesize them per the speclet's resolved open question — and even if it compiled, consumers like `System.Text.Json`'s union support key off the real runtime contract, not a look-alike).
- **`closed` hierarchies *can* be polyfilled** onto `net10.0`/earlier with an explicit `LangVersion=15` and a hand-written `IsClosedTypeAttribute` marker, as shown above. Still requires bumping `LangVersion` past the TFM's implied default, which is an exception to the general "don't do that" rule in `csharp-language-versions` — only do it as a deliberate call, not casually.
- Collection expression arguments, extension indexers, and labeled `break`/`continue` are pure compiler/IL features with no runtime-type dependency — they need `LangVersion` 15 but not specifically `net11.0`'s runtime, the same category as most C# 12–14 features per the polyfill table in `csharp-language-versions`.

## Checklist

- [ ] Confirmed the project's actual `TargetFramework`/`LangVersion` before using any C# 15 construct (see `csharp-language-versions`) — never bumped either just to unlock one
- [ ] No `union` type with a value-type case (`int`, `enum`, `struct`) used on a hot/allocating path — hand-written struct union or `closed` reference-type hierarchy used instead
- [ ] `union` used only where case types are genuinely unrelated (no sensible shared base) or the boxing cost is provably immaterial
- [ ] `closed` hierarchy used (not `union`) when cases share real inherited behavior and are reference types
- [ ] Verified exhaustiveness warnings actually fire (CS8509) for both `union`/`closed` switches before relying on "the compiler will catch missing cases"
- [ ] Any `union`/`closed` type intended for cross-assembly extensibility replaced with a classic `abstract`/`sealed` hierarchy — both features are specifically about closing the set
- [ ] `System.Text.Json` round-tripping of a `union` verified empirically if case types could be structurally ambiguous — don't assume deserialization "just works"
- [ ] `with(...)` in a collection expression is the first element, matches an actual constructor/factory shape on the target type
- [ ] No `LangVersion=preview` memory-safety construct (`unsafe(expr)`, pointer relaxations, `safe`) present outside a throwaway spike
- [ ] Labeled `break`/`continue` used instead of a flag variable or `goto` once the project is on `net11.0`+/C# 15 (IDE0410)
- [ ] Anything non-trivial about union member providers (`IUnionMembers`), the non-boxing access pattern, or generic case-type patterns re-verified against the actual installed SDK before depending on it — RC1 has documented gaps
