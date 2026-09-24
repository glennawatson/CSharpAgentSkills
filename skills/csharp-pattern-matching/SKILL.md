---
name: csharp-pattern-matching
description: Use when writing or reviewing C# conditionals, type checks, or branching logic — switch expressions vs if/else chains, property/positional/relational/logical/type/list patterns, `is not null`, `when` guards, exhaustiveness (and why it doesn't apply to enums), deconstruction, and when a pattern helps readability vs when it hurts it.
---

# Pattern matching — decide with patterns, not chained ifs

Patterns replace nests of `if`/`else if`/casts with declarative shape checks. The compiler lowers them into an efficient decision structure — use them for clarity first, but they're rarely a performance regression over hand-written checks.

## Switch expressions over if/else chains

Prefer a `switch` expression when you're producing a value from a set of mutually exclusive conditions:

```csharp
// ❌ chain of ifs, easy to miss a case, no exhaustiveness help
string Describe(Shape s)
{
    if (s is Circle c) return $"circle r={c.Radius}";
    if (s is Square sq) return $"square side={sq.Side}";
    else return "unknown";
}

// ✅ switch expression
string Describe(Shape s) => s switch
{
    Circle { Radius: var r } => $"circle r={r}",
    Square { Side: var side } => $"square side={side}",
    _ => "unknown",
};
```

Keep `switch` statements (not expressions) when arms need multiple statements or side effects — don't force a `switch` expression with `{ ... }` block bodies just to avoid `if`.

## Pattern kinds

**Property patterns** — match on member values without a temp variable:

```csharp
if (order is { Status: OrderStatus.Shipped, Total: > 0 })
{
    // ...
}
```

**Positional patterns** — for types with `Deconstruct` (records get this free):

```csharp
record Point(int X, int Y);

string Quadrant(Point p) => p switch
{
    (0, 0) => "origin",
    ( > 0, > 0) => "Q1",
    ( < 0, > 0) => "Q2",
    ( < 0, < 0) => "Q3",
    ( > 0, < 0) => "Q4",
    _ => "axis",
};
```

**Relational patterns** — `>`, `<`, `>=`, `<=` against a constant, combined with `and`/`or`:

```csharp
string Grade(int score) => score switch
{
    >= 90 and <= 100 => "A",
    >= 80 and < 90 => "B",
    >= 70 and < 80 => "C",
    _ => "F",
};
```

**Logical patterns** — `and`, `or`, `not` compose any patterns, including types:

```csharp
if (value is not null and not "") { /* non-empty */ }
if (shape is Circle or Square) { /* ... */ }
```

**Type patterns** — `x is Foo f` tests and casts in one step; prefer this over `as` + null-check (see Performance below).

**List patterns** (C# 11+) — match array/list shape and slice the rest:

```csharp
int[] arr = [1, 2, 3, 4, 5];

if (arr is [var first, .., var last])
{
    Console.WriteLine($"{first}..{last}"); // 1..5
}

if (arr is [var head, .. var rest])
{
    Console.WriteLine(rest.Length); // 4
}

if (arr is [1, 2, ..]) { /* starts with 1, 2 */ }
if (arr is []) { /* empty */ }
```

Works on any type with an indexer and `Length`/`Count`, or that's countable/indexable per the pattern spec (arrays, `List<T>`, `Span<T>`, strings).

## `is not null`

Prefer `x is not null` / `x is null` over `!= null` / `== null` — it reads better next to other patterns and can't be redirected by an overloaded `==`/`!=` on the type (patterns always use the real identity/type check).

```csharp
if (result is not null)
{
    Use(result);
}
```

## `when` guards

Add extra conditions a pattern can't express directly. Keep the pattern doing the shape-matching and the guard doing the logic:

```csharp
string Classify(Exception ex) => ex switch
{
    HttpRequestException { StatusCode: var code } when code is >= System.Net.HttpStatusCode.InternalServerError
        => "server error",
    HttpRequestException => "client error",
    TimeoutException => "timeout",
    _ => "unknown",
};
```

## Exhaustiveness — real for types, not for enums

The compiler warns (CS8509/CS8846) when a `switch` expression doesn't cover every case it can statically prove, and that's genuinely useful for **sealed hierarchies** and **closed type sets** — add a case, get a compile warning everywhere it's missing.

**Enums are not exhaustive in any meaningful sense.** An `enum` is backed by its underlying integer type — nothing stops a caller passing `(DayKind)99`. The compiler will treat a `switch` over every named enum member as "exhaustive" and let you skip `_`, but that's a false sense of safety: an out-of-range value falls through to `default` (statement) or throws `SwitchExpressionException` (expression) at runtime, not compile time.

```csharp
// Always include a fallback arm for enums, even if you've listed every member
string Describe(DayKind k) => k switch
{
    DayKind.Weekday => "weekday",
    DayKind.Weekend => "weekend",
    _ => throw new ArgumentOutOfRangeException(nameof(k)),
};
```

For real exhaustiveness, use a `sealed` base class / `interface` with a closed set of implementations (or a discriminated union library) — the compiler's exhaustiveness check is meaningful there, so let it do the work instead of adding `default: throw`:

```csharp
// Base is sealed's contract enforced via permitted derived set — compiler flags missing arms
abstract record Shape;
sealed record Circle(double Radius) : Shape;
sealed record Square(double Side) : Shape;

double Area(Shape s) => s switch
{
    Circle c => Math.PI * c.Radius * c.Radius,
    Square sq => sq.Side * sq.Side,
    // no `_` needed — compiler proves these two cover every non-null Shape;
    // omitting one produces a warning here, not a runtime surprise
};
```

## .NET 11 / C# 15 — closed hierarchies and unions give *real* exhaustiveness

Check the project's `TargetFramework`/`LangVersion` first (see `csharp-language-versions`) — these need `net11.0`+ (or explicit `LangVersion=15`) and are unavailable on `net10.0` and earlier. Don't retarget or raise `LangVersion` just to use them.

Unlike the `sealed`-hierarchy pattern above (which is exhaustive only because *you* enforced the closed set by convention), `closed` and `union` make the compiler enforce it:

```csharp
// closed record hierarchy — derived types restricted to the declaring assembly
public closed record class GateState;
public sealed record Open : GateState;
public sealed record Closed : GateState;

// no `_` arm — and unlike a sealed base, an external assembly literally cannot add a case
string Describe(GateState g) => g switch
{
    Open => "open",
    Closed => "closed",
};
```

A `switch` over a `closed` type or a `union` warns (CS8509) on a missing case exactly like the sealed-hierarchy example — the difference is that a `sealed` base only stops *external* derivation by convention within your own codebase's discipline, while `closed` is a compiler-enforced assembly boundary: nothing outside the declaring assembly can add a case, ever. `closed` implies `abstract` — you can't instantiate `GateState` directly, and `closed` can't combine with `sealed`/`static`/`abstract`. It's also not transitive: an intermediate type in the hierarchy needs its own `closed` if you want its subtree closed too.

**Union types** (`union`) declare the exact case set inline instead of via inheritance, with implicit conversions from each case type:

```csharp
public union Pet(Cat, Dog, Bird);
public record Cat(string Name);
public record Dog(string Name);
public record Bird(string Name);

Pet p = new Cat("Tom");   // implicit conversion from case type

string Describe(Pet pet) => pet switch
{
    Cat c => $"cat {c.Name}",
    Dog d => $"dog {d.Name}",
    Bird b => $"bird {b.Name}",   // omit any arm here and the compiler warns (CS8509)
};
```

Prefer `union` when the case types are otherwise unrelated (no shared base makes sense) and you just need "exactly one of these shapes." Prefer a `closed` hierarchy when the cases share real inherited members/behavior. See `csharp-modern-types` for the full decision between `union`, `closed`, and a classic `abstract` base.

Some parts of the union spec aren't implemented yet in the RC1 compiler — verify a given union pattern compiles before relying on it in a codebase still tracking .NET 11 previews/RC.



Any type with a `Deconstruct` method (or a record, which gets one automatically) can be pattern-matched positionally or deconstructed into locals:

```csharp
var (x, y) = point;             // deconstruction assignment
if (point is (0, 0)) { }        // positional pattern
```

## When a pattern helps vs when it hurts

Use a pattern when it **replaces a cast, a null check, and a conditional in one readable expression** — that's the sweet spot. Stop when the pattern grows past what a glance can parse:

```csharp
// ✅ clear — one shape, one meaning
if (response is { StatusCode: 200, Body: not null } r) { ... }

// ❌ unreadable — deeply nested property/positional/logical patterns
if (response is { Headers: { ["X-Trace"]: [var traceId, ..] }, Body: (var a, (var b, not null and > 0)) }) { ... }
```

If a pattern needs a comment to explain what it matches, extract a method or a few `if`s instead. A named helper (`IsRetryable(ex)`) often communicates intent better than a `when` guard buried in a `switch` arm.

## Performance notes

- The compiler lowers pattern matching (especially multi-arm `switch` on type/shape) into an efficient **decision structure** — it doesn't re-test earlier conditions redundantly the way a naive `if`/`else if` chain sometimes does when hand-written carelessly. You don't need to hand-optimize arm order for performance; order for readability and to put more specific patterns before general ones (required for correctness anyway).
- `x is Foo f` (type pattern) and `x as Foo` do the same runtime type check, but the type pattern also handles the null check and scoping in one operation and cannot be forgotten — prefer it. `as` is instead appropriate when you need a nullable result that isn't tested immediately.
- Pattern matching doesn't allocate beyond what the equivalent hand-written check would; list patterns on `Span<T>`/arrays slice without copying when the sub-pattern doesn't bind the remainder, and bind a genuine `T[]`/`Span<T>` slice (which may allocate for arrays) when it does (`.. var rest`).

## Checklist

- [ ] Value-producing branches use a `switch` expression, not an `if`/`else if` chain
- [ ] `is not null` / `is null` instead of `!= null` / `== null`
- [ ] Type patterns (`is Foo f`) instead of `as` + null check when you're about to use the cast
- [ ] Closed hierarchies are `sealed`/exhaustive by design so the compiler's exhaustiveness check is meaningful
- [ ] On `net11.0`+/C# 15: `closed`/`union` used (not retrofitted onto older TFMs) where the compiler should enforce exhaustiveness across assembly boundaries
- [ ] `switch` over an `enum` still has a `_`/`default` fallback — enum exhaustiveness is not real
- [ ] `when` guards carry logic, not shape-matching that a pattern could express
- [ ] No pattern so deep it needs a comment to explain — extract a method instead
