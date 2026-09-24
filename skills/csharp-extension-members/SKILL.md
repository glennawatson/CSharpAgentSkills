---
name: csharp-extension-members
description: Use when writing or reviewing C# 14 `extension` blocks — extension properties, extension static members, extension operators, the `extension(Type receiver)` receiver syntax, generic extension blocks, how they interoperate with classic `this`-parameter extension methods, and where extension members belong vs. instance members.
---

# Extension members (C# 14)

C# 14 adds `extension` blocks: a way to add not just methods but **properties, static members, and operators** to an existing type, without editing its source. Classic `this`-parameter extension methods still work and still compile to the same kind of static method — the new syntax is a superset, not a replacement.

## Syntax: `extension(Type receiver)`

An `extension` block lives inside a `static class`, groups one or more members under a shared receiver declaration:

```csharp
public static class StringExtensions
{
    extension(string s)
    {
        public bool IsBlankish => string.IsNullOrWhiteSpace(s);   // extension property
        public string Shout() => s.ToUpperInvariant() + "!";       // extension method
    }
}

"  ".IsBlankish;     // true
"hi".Shout();        // "HI!"
```

The identifier in `extension(string s)` (`s` here) is the receiver — usable in every member's body exactly like `this` would be for an instance member. You can omit the name if a member doesn't need it (e.g. a receiver-type-only static member):

```csharp
extension(int)
{
    public static int Meaning => 42;   // int.Meaning
}
```

## Extension properties

The headline new capability — properties weren't possible with classic extension methods at all:

```csharp
extension(IEnumerable<int> source)
{
    public double AverageOrZero() => source.Any() ? source.Average() : 0;
}
```

works exactly like instance property access from the caller's perspective. Get/set extension properties are supported too:

```csharp
extension(Counter c)
{
    public int Count
    {
        get => c.Backing;
        set => c.Backing = value;
    }
}

counter.Count = 5;   // routes through the extension setter
```

## Extension static members

Static members inside an `extension(Type)` block become static members callable on the type itself (`int.Meaning` above), not on an instance. Combine with generics for a type-driven factory:

```csharp
public readonly struct Box<T>(T value)
{
    public T Value { get; } = value;
}

public static class BoxExtensions
{
    extension<T>(Box<T> box)
    {
        public string Describe() => $"Box<{typeof(T).Name}>({box.Value})";
    }

    extension<T>(T value)
    {
        public Box<T> WrapBox() => new(value);   // callable as 5.WrapBox(), "hi".WrapBox()
    }
}
```

Both blocks live in the same static class; the generic parameter `<T>` is declared on the `extension(...)` clause, not repeated per member.

## Extension operators

Verified working in C# 14: an `extension` block can declare operators, which participate in overload resolution for the receiver type exactly as if declared on the type itself:

```csharp
public static class OpExtensions
{
    extension(int)
    {
        public static int operator +(int a, Marker _) => a + 1;
    }
}

public struct Marker;

5 + new Marker();   // 6 — resolves through the extension operator
```

Use this sparingly — operator overloads added from outside a type's own source are easy to miss when reading call sites. Reserve it for well-established domain conventions (unit types, measurement libraries), not ad hoc convenience.

## Extension indexers (.NET 11 / C# 15)

Check `TargetFramework`/`LangVersion` first (see `csharp-language-versions`) — needs `net11.0`+/C# 15; don't raise `LangVersion` on an older-targeted project just for this. An `extension(Type receiver)` block can declare `this[...]` like any other member, giving an existing type indexer syntax it didn't have:

```csharp
public class Matrix
{
    public Dictionary<int, string> Data { get; } = new();
}

public static class MatrixExtensions
{
    extension(Matrix m)
    {
        public string this[int i]
        {
            get => m.Data.TryGetValue(i, out var v) ? v : "";
            set => m.Data[i] = value;
        }
    }
}

var m = new Matrix();
m[1] = "hello";
Console.WriteLine(m[1]);   // "hello" — routes through the extension indexer
```

Same constraints as other extension members: no backing field (an extension block can't declare state — `field` isn't available inside an extension indexer's accessors), and no access to the receiver's private members. Store any needed state on the receiver type itself (as above) or in an external map (e.g. `ConditionalWeakTable`) keyed by the receiver instance.

## Interoperability with classic extension methods

Both forms can live in the same static class and are called identically — callers can't tell which syntax defined a given extension method:

```csharp
public static class Mixed
{
    extension(string s)
    {
        public string Shout() => s.ToUpperInvariant() + "!";
    }

    public static string ClassicShout(this string s) => s.ToUpperInvariant() + "!!";
}

"abc".Shout();        // extension block
"abc".ClassicShout();  // classic this-parameter method — both compile and call the same way
```

There is no migration requirement — existing `this`-parameter extension methods keep working unchanged. Reach for the new `extension` syntax when you need a **property**, a **static member**, or an **operator** on an existing type (impossible with the classic form), or when grouping several members under one receiver reads better than repeating `this Type x` on every method.

## Where extension members belong vs. instance members

- If you own the type and the member is a core part of its behavior, make it a real instance/static member — extensions are for types you don't own, or for optional/layered behavior you don't want on the type's primary surface.
- Extension members can't add fields or override anything, and can't access private members of the receiver type — they're built entirely from the receiver's public surface. If the logic genuinely needs private state, it belongs inside the type.
- Group related extension members for one receiver type into one `extension(...)` block rather than scattering classic-style methods across the file — the receiver declaration then documents the contract once.

## Naming and containing class conventions

- The containing type must be a `static class`; name it `<Type>Extensions` (e.g. `StringExtensions`) as with classic extension methods — this is a convention, not a compiler requirement, but keep it for discoverability.
- One static class per logical receiver/domain is easier to navigate than one giant grab-bag class; multiple `extension(...)` blocks for different receiver types can still live in the same static class when they're thematically related (e.g. all string-and-span helpers for a parser).
- Keep extension members agent/tool-agnostic in intent: they should read as natural members of the receiver type, not as free functions that happen to take it as a first argument.

## Checklist

- [ ] `extension(Type x)` used when a property, static member, or operator is needed — not forced onto types that just need a plain method
- [ ] Extension indexers (`net11.0`+/C# 15 only) don't assume backing-field state — any needed storage lives on the receiver type or an external keyed map
- [ ] Receiver identifier omitted when unused (static-only members)
- [ ] Generic parameters declared once on the `extension<T>(...)` clause, not per member
- [ ] Extension operators used only for well-established domain conventions, not casual convenience
- [ ] Classic `this`-parameter extension methods left as-is where they already exist — no forced migration
- [ ] Containing class is `static`, named `<Type>Extensions`, grouped by receiver/domain
- [ ] No attempt to access receiver-type private members or add fields from an extension block
