---
name: csharp-discards
description: Use when writing or reviewing C# code that has an intentionally unused value — discarding return values, `out _`, deconstruction, pattern-matching discards, lambda discard parameters `(_, _) =>`, and when NOT to discard (a result that signals failure or must be observed).
---

# Discards — say "I don't want this" explicitly

A discard (`_`) marks a value as intentionally unused. Default to a discard whenever a value genuinely doesn't matter at that call site — it documents intent for the next reader and for analyzers, instead of leaving them to guess whether an unused result was an oversight.

## Discarding a return value

```csharp
// ✅ deliberate fire-and-forget, decided on purpose
_ = Task.Run(() => BackgroundWork());

// ✅ the bool result of TryAdd genuinely doesn't matter here — caller
// only cares the entry exists afterward, not who added it
_ = dictionary.TryAdd(key, value);
```

`_ = Task.Run(...)` is a real decision, not a shortcut: an unobserved faulted `Task` can crash the process (or silently swallow an exception, depending on context) — see `csharp-async` for when a task must be awaited, stored, or have its exceptions observed some other way instead of discarded. Only discard a `Task` when you've actually decided you don't need its completion or its exceptions.

## `out _`

Use it when a method only has a `TryX(out T)` shape but you only need the `bool`:

```csharp
if (int.TryParse(input, out _))
{
    // only care that it parsed, not the value
}
```

## Deconstruction

```csharp
var (x, _) = GetCoordinates();   // only need x
foreach (var (key, _) in dictionary) { UseKey(key); }
```

## Pattern discards

`_` inside a pattern matches anything without binding it:

```csharp
if (point is (_, 0)) { /* on the x-axis, x value not needed */ }

string Describe(object o) => o switch
{
    int n when n > 0 => "positive int",
    string => "string",
    _ => "something else",   // catch-all arm
};
```

**`_` pattern vs. a variable named `_`:** `case _:` / `_ =>` is the *discard pattern* — it matches anything and binds nothing. If a variable literally named `_` is already declared in the enclosing scope, `_` in a pattern position stops being a discard and refers to that variable instead (comparing against its value, or shadowing rules apply depending on position) — this only matters in the rare case where `_` was already used as a real identifier nearby; don't name a variable `_` if you also want discard patterns in the same scope.

## Lambda discard parameters (C# 9+)

Unused lambda parameters can be named `_` instead of inventing a throwaway name — with two or more, they're true discard parameters (no name enters scope, they can repeat):

```csharp
// event handlers — sender/args routinely unused
button.Click += (_, _) => Refresh();

// unused index in a Select/Where-style callback
items.Select((item, _) => Transform(item));
```

A single parameter named `_` is **not** a discard — for backward compatibility, one `_` parameter is treated as an ordinary parameter named `_`. Discard behavior (no scope entry, reusable name) only kicks in with two or more parameters named `_` in the same parameter list.

## Standalone `_ =` to silence an intentional non-use

```csharp
_ = someObject.SomePropertyWithSideEffectingGetter; // deliberately triggering the getter, not using the value
```

The built-in analyzers **IDE0058** (expression value is unused) and **IDE0059** (value assigned is unused) both nudge toward either discarding explicitly or removing dead code, depending on the shape; **CA1806** ("do not ignore method results") is the opposite-direction check — it flags ignoring a return value from certain BCL/pure methods where ignoring it is very likely a bug (e.g. calling `string.Trim()` and dropping the result). Don't blanket-suppress CA1806 by discarding — read what it's flagging first; see "When not to discard" below.

## The shadowing gotcha

If a variable named `_` is already declared in the enclosing scope, later uses of `_` refer to *that variable*, not a fresh discard — including `out _`, which then writes into the real variable instead of throwing the value away:

```csharp
int _ = 10;
_ = int.TryParse("42", out _);
Console.WriteLine(_); // prints 42 — `out _` reused the declared variable named `_`, it did not discard
```

Avoid declaring a variable literally named `_` in any scope where discard syntax is also used nearby — the two meanings silently diverge.

## When NOT to discard

Discard only what's genuinely unneeded. Don't discard:

- A `bool`/result from a `TryX` method when failure actually needs handling — `_ = dict.TryGetValue(key, out var v)` silently ignores a missing key that the caller then uses `v`'s default for, which is usually a bug, not an intentional discard.
- A value that must be disposed — discarding an `IDisposable`-returning result (`_ = File.OpenRead(path)`) leaks the handle; the return value being unused doesn't mean the object doesn't need cleanup.
- A `Task`/`ValueTask` you actually need to await or whose exceptions must propagate — see `csharp-async`.
- Anything CA1806 is flagging without having actually read what it's pointing at — the rule exists because ignoring specific return values (immutable-string methods, `Enumerable` methods, etc.) is a common real bug, not just noise.

## Caveat: expression trees

Discard parameters in a lambda are fine inside an `Expression<Func<...>>`, but discards elsewhere in an expression tree are not — and neither is any assignment expression, including `_ = ...`:

| Feature | Compiles in `Expression<TDelegate>`? |
|---|---|
| Lambda discard parameters `(_, _) =>` | Yes |
| `out _` (or any `out` variable declaration, named or discard) | No — `CS8198`/`CS8207` |
| `_ = value` (or any assignment operator) | No — `CS0832`, expression trees ban assignment entirely |

If code needs to run inside a `Expression<TDelegate>` consumed by an `IQueryable` provider, Moq-style matcher, or similar, keep discard usage to lambda parameter lists only — everything that reads as "assignment" (including a discard assignment) is rejected by the expression-tree conversion itself, not by the provider. See `csharp-static-lambdas` for the matching table covering `static`, patterns, and other newer syntax inside expression trees.

## Checklist

- [ ] Fire-and-forget `Task`s are discarded deliberately (`_ = Task.Run(...)`), with exceptions genuinely not needing observation — otherwise see `csharp-async`
- [ ] `TryX(out _)` used only when the out value truly isn't needed, not when a missing/failed result should be handled
- [ ] Deconstruction and lambda parameters use `_` for pieces that are genuinely unused, not just untyped quickly
- [ ] No variable literally named `_` declared in a scope that also uses discard syntax
- [ ] `_ =` used to intentionally silence CA1806/IDE0058/IDE0059 only after confirming the ignored value really doesn't matter
- [ ] Disposable or must-check results are never discarded
- [ ] Discards inside `Expression<TDelegate>` lambdas are limited to parameter lists — no `out _`, no `_ =` assignment
