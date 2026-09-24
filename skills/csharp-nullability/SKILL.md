---
name: csharp-nullability
description: Use when writing or reviewing C# code that touches nullable reference types — enabling and trusting the `#nullable` analysis, the `System.Diagnostics.CodeAnalysis` attributes (`NotNullWhen`, `MaybeNullWhen`, `MemberNotNull`, `NotNull`, `AllowNull`, `DisallowNull`, `NotNullIfNotNull`), why `!` is usually wrong, guarding public boundaries, `required` interplay, generic `T?`/`notnull`, and `Try*` signatures.
---

# Nullability — make the analysis tell the truth

Nullable reference types (NRT) are a compile-time contract, not runtime enforcement. The goal is to make the compiler's warnings **accurate** so a clean build means "no unhandled null" — that only works if you annotate honestly instead of silencing warnings.

## Turn it on and mean it

```xml
<PropertyGroup>
  <Nullable>enable</Nullable>
</PropertyGroup>
```

`enable` applies to the whole project by default; use `#nullable disable`/`#nullable restore` directive pairs only around genuinely unconvertible legacy code, scoped as tightly as possible — never at the top of a new file.

A type is either `string` (the analysis believes it's never null here) or `string?` (it might be). The compiler tracks flow — after an `if (x is null) return;`, `x` is treated as non-null for the rest of that path — but it can only do that with the information you give it, which is what the rest of this skill is about.

## The attributes — say what your method actually does

These live in `System.Diagnostics.CodeAnalysis` and describe null behavior the compiler can't infer from types alone.

**`[NotNullWhen(bool)]`** — an `out`/return value is non-null when the method returns the given bool. The standard shape for `TryGetX`:

```csharp
bool TryParseName(string? input, [NotNullWhen(true)] out string? name)
{
    if (string.IsNullOrWhiteSpace(input))
    {
        name = null;
        return false;
    }
    name = input.Trim();
    return true;
}

if (TryParseName(raw, out var name))
{
    Console.WriteLine(name.Length);   // no warning — NotNullWhen(true) proved it
}
```

**`[MaybeNullWhen(bool)]`** — the opposite shape: a value declared non-null (`T` rather than `T?`) that is only *possibly* null when the method returns the given bool (common on `TryGetValue`-style APIs generic over `T` where you can't write `T?` for a reference-or-value-type parameter).

**`[MemberNotNull(nameof(field))]`** — a method guarantees a field/property is non-null *after it returns*, even though the compiler can't see that from the field's declared type alone. Use it on `Init`/setup helpers called from every constructor path:

```csharp
class Widget
{
    private string? _name;

    [MemberNotNull(nameof(_name))]
    void Init() => _name = "default";

    public Widget()
    {
        Init();
        Console.WriteLine(_name.Length);   // no warning — MemberNotNull proved it
    }
}
```

**`[NotNull]`** (no `When`) — an `out`/ref parameter or return is always non-null when the method returns normally, regardless of what was passed in — typically paired with a parameter that arrives as `T?` and leaves guaranteed non-null (e.g., a validator that throws instead of returning on failure).

**`[AllowNull]` / `[DisallowNull]`** — decouple the *get* and *set* nullability of a property or parameter from its declared type. `[AllowNull] string Name { get; set; }` on a `string`-typed property means the setter accepts `null` (and normalizes it) while the getter still promises non-null:

```csharp
private string _name = "";

[AllowNull]
public string Name
{
    get => _name;
    set => _name = value ?? "";
}
```

**`[NotNullIfNotNull(nameof(param))]`** — the return is non-null whenever the named parameter was non-null (and may be null when it was null) — useful for pass-through/transform methods:

```csharp
[return: NotNullIfNotNull(nameof(input))]
string? Normalize(string? input) => input?.Trim();
```

## `!` — the null-forgiving operator

`!` tells the compiler "trust me," permanently, with no runtime check. It's the right tool only when **you have information the compiler cannot**, and you're confident enough to accept an `NullReferenceException` if you're wrong:

```csharp
// ✅ legitimate: framework guarantees non-null here, analyzer can't see it
var value = dictionary[key]!;   // you just checked ContainsKey and nothing else can race

// ✅ legitimate: test/setup code establishing a known-good fixture
var service = _serviceProvider.GetService<IThing>()!;   // registered in test setup, must exist

// ❌ silencing a warning without understanding it
var user = GetUser(id)!;   // GetUser can genuinely return null — this just hides the bug
```

If you reach for `!` because a warning is "annoying," that's a signal the surrounding code's contract is wrong — fix the contract (attribute it, guard it, or make the type honestly nullable) instead of suppressing the message.

## Guard at public boundaries

Public API entry points should fail fast and loudly on invalid null, not rely on NRT warnings (which callers in other assemblies, or callers who ignore warnings, won't see):

```csharp
public void Configure(string connectionString, Options options)
{
    ArgumentNullException.ThrowIfNull(connectionString);
    ArgumentNullException.ThrowIfNull(options);
    ArgumentException.ThrowIfNullOrEmpty(connectionString);
    // ...
}
```

`ArgumentNullException.ThrowIfNull(x)` throws with the right parameter name (via `CallerArgumentExpression`) and is understood by the nullable analysis as a null check — code after it treats `x` as non-null. `ArgumentException.ThrowIfNullOrEmpty`/`ThrowIfNullOrWhiteSpace` do the same for strings that must also be non-empty.

## `required` and nullability

`required` forces a member to be set during initialization, which is a *different* guarantee from non-nullability — a `required string?` member is still nullable, just mandatory to set (even if only to `null`). Use `required` for "must be provided", and the type's nullability for "may be null":

```csharp
class Person
{
    public required string Name { get; init; }   // must be set, never null
    public string? MiddleName { get; init; }      // optional, may be null, not required
}
```

For types a serializer fills, `required` also changes the wire contract. `System.Text.Json` throws when a `required` property is missing from the JSON, so don't add it just to satisfy the compiler. When unsure, make the member nullable and handle a missing value where the object is used (see `csharp-nullable-migration` and `csharp-json-source-generation`).

## Generics: `T?` and `notnull`

- **Unconstrained `T?`** in a generic method/type means "if `T` is a reference type, this can be null; if `T` is a value type, `T?` is `Nullable<T>`." The compiler tracks both correctly, but it means you can't treat `T?` as uniformly "reference type made nullable" — check `default(T) is null` semantics don't apply until `T` is known.
- **`where T : notnull`** constrains `T` to non-nullable value types and non-nullable reference types — the standard constraint for dictionary keys and similar (`Dictionary<TKey, TValue>` itself constrains `TKey : notnull`).
- **`where T : class` / `where T : struct`** further narrow to reference/value types respectively, after which `T?` behaves unambiguously as "nullable reference"/`Nullable<T>`.

```csharp
static T Identity<T>(T value) where T : notnull => value;
```

## `Try*` pattern signatures

The idiomatic shape pairs a `bool` result with `[NotNullWhen(true)]` (or `[MaybeNullWhen(false)]` when the out type is `T` rather than `T?`) so callers get flow-sensitive non-null after checking the return:

```csharp
public bool TryGetUser(int id, [NotNullWhen(true)] out User? user)
{
    user = _cache.TryGetValue(id, out var found) ? found : null;
    return user is not null;
}
```

Don't return a nullable value *and* a bool without the attribute — callers get an unnecessary warning (or worse, silently `!`-suppress it) at every call site instead of getting flow analysis for free.

## Checklist

- [ ] `<Nullable>enable</Nullable>` project-wide; `#nullable disable` only in tight, justified legacy pockets
- [ ] `Try*` methods annotate their `out`/return with `NotNullWhen`/`MaybeNullWhen`
- [ ] Setup/init helpers that guarantee a field is non-null after returning use `[MemberNotNull]`
- [ ] Properties with asymmetric get/set nullability use `[AllowNull]`/`[DisallowNull]` instead of widening the declared type
- [ ] Pass-through/transform methods preserving null-in-null-out use `[NotNullIfNotNull]`
- [ ] Every `!` has a one-line justification (in review, if not in a comment) — not a reflex to clear a warning
- [ ] Public methods guard with `ArgumentNullException.ThrowIfNull`/`ArgumentException.ThrowIfNullOrEmpty`, not just NRT warnings
- [ ] `required` used for "must be set", nullability used for "may be null" — not conflated
- [ ] Generic code constrains `notnull`/`class`/`struct` where the algorithm actually depends on it
