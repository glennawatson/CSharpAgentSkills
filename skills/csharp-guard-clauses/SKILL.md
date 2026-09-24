---
name: csharp-guard-clauses
description: Use when writing or reviewing argument validation / guard clauses in C#/.NET 6-11 — null checks, empty/whitespace string checks, numeric range and index/bounds checks (ArgumentOutOfRangeException.ThrowIfZero/ThrowIfNegative/ThrowIfGreaterThan/ThrowIfLessThan/etc.), ObjectDisposedException.ThrowIf, replacing hand-written `if (x == null) throw new ...` with the BCL static throw helpers, guarding iterator/async methods eagerly, the CA1510-CA1513/CA2201 analyzer rules, and polyfilling the helpers on netstandard2.0/net4x.
---

# Guard clauses — use the BCL throw helpers, not hand-written `if`/`throw`

.NET 6+ ships static `ThrowIf*` helpers on the exception types themselves, replacing
hand-written `if (...) throw new ...;` guards with a single call. This skill is the in-depth
authority on guard clauses; `csharp-error-handling` only summarizes them.

## Why

- **`[CallerArgumentExpression]` fills `paramName` for free** — no `nameof(...)`, and it works
  for member-access expressions too: `ArgumentNullException.ThrowIfNull(obj.Name)` throws with
  `Message: "Value cannot be null. (Parameter 'obj.Name')"`.
- **Smaller, consistent call sites** — one line, same wording everywhere, instead of whatever each
  author typed.
- **Throw stays out of the hot path** — the branch and `throw` live in the helper; the calling
  method's body is just a call, which the JIT can inline far more readily than a method body that
  contains its own inline `throw new SomeException(...)`.
- **Nullable flow analysis understands them** — after
  `ArgumentNullException.ThrowIfNull(host)`, a following `host.Length` produces no `CS8602`
  warning, exactly as if you'd written `if (host is null) throw ...` yourself.

## Catalogue (.NET 8 / 9 / 10 / 11)

These are all the public static `ThrowIf*` helpers on exception types. The set is the same from
.NET 8 through .NET 11:

```
ArgumentNullException.ThrowIfNull(object? argument, [CallerArgumentExpression] string? paramName)
ArgumentNullException.ThrowIfNull(void* argument, [CallerArgumentExpression] string? paramName)

ArgumentException.ThrowIfNullOrEmpty(string? argument, [CallerArgumentExpression] string? paramName)
ArgumentException.ThrowIfNullOrWhiteSpace(string? argument, [CallerArgumentExpression] string? paramName)

ArgumentOutOfRangeException.ThrowIfZero<T>(T value, ...)                       where T : INumberBase<T>
ArgumentOutOfRangeException.ThrowIfNegative<T>(T value, ...)                   where T : INumberBase<T>
ArgumentOutOfRangeException.ThrowIfNegativeOrZero<T>(T value, ...)             where T : INumberBase<T>
ArgumentOutOfRangeException.ThrowIfEqual<T>(T value, T other, ...)             -- see version note below
ArgumentOutOfRangeException.ThrowIfNotEqual<T>(T value, T other, ...)          -- see version note below
ArgumentOutOfRangeException.ThrowIfGreaterThan<T>(T value, T other, ...)       where T : IComparable<T>
ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual<T>(T value, T other, ...) where T : IComparable<T>
ArgumentOutOfRangeException.ThrowIfLessThan<T>(T value, T other, ...)          where T : IComparable<T>
ArgumentOutOfRangeException.ThrowIfLessThanOrEqual<T>(T value, T other, ...)   where T : IComparable<T>

ObjectDisposedException.ThrowIf(bool condition, object instance)
ObjectDisposedException.ThrowIf(bool condition, Type type)
```

**Version note:** on .NET 8/9, `ThrowIfEqual`/`ThrowIfNotEqual` are constrained `where T :
IEquatable<T>`. On .NET 10/11 that constraint is **removed** — calling `ThrowIfEqual`
with a plain struct implementing no interfaces compiles and runs on net10.0/net11.0 (falls
back to `EqualityComparer<T>.Default`) but fails on net8.0/net9.0. Multi-targeting net8.0 and
net10.0+? Keep call sites on `IEquatable<T>` types so the code compiles on both.

**No `ThrowIf*` helpers exist anywhere else in CoreLib.** In particular `IndexOutOfRangeException`
has **zero** on any version — see the value-guards section below.

Exact shape of what gets thrown: `ArgumentOutOfRangeException.ThrowIfNegative(-1,
"index")` sets `ParamName="index"`, `ActualValue=-1`, message `"index ('-1') must be a
non-negative value. (Parameter 'index')\nActual value was -1."` — populated automatically, no
separate constructor argument needed.

## Before/after: the common hand-written shapes

```csharp
// ❌                                                          ✅
if (host == null) throw new ArgumentNullException(nameof(host));
ArgumentNullException.ThrowIfNull(host);

if (string.IsNullOrEmpty(host)) throw new ArgumentException("...", nameof(host));
ArgumentException.ThrowIfNullOrEmpty(host);

if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("...", nameof(name));
ArgumentException.ThrowIfNullOrWhiteSpace(name);

if (port < 0) throw new ArgumentOutOfRangeException(nameof(port));
ArgumentOutOfRangeException.ThrowIfNegative(port);

if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

if (value > max) throw new ArgumentOutOfRangeException(nameof(value));
ArgumentOutOfRangeException.ThrowIfGreaterThan(value, max);

if (_disposed) throw new ObjectDisposedException(GetType().Name);
ObjectDisposedException.ThrowIf(_disposed, this);
```

### The `?? throw` assignment form

```csharp
_host = host ?? throw new ArgumentNullException(nameof(host));
```

Still fine — arguably better than a preceding `ThrowIfNull` line — when the guard and the
assignment *are* the same expression: a constructor assigning straight into a `readonly` field in
one statement. Once the null check needs to guard more than that one assignment (multiple fields,
a method body that uses the parameter repeatedly), a leading `ThrowIfNull(host)` reads better and
lets nullable flow analysis narrow the parameter for every statement that follows.

## Value guards — the `ArgumentOutOfRangeException` family

Range and bounds checks are guard clauses too, and this family is as important as the null-check
helpers. People often reach for `IndexOutOfRangeException` for a bad index — **don't**: it's
thrown by the runtime itself for `array[i]` out-of-bounds access, has no `ThrowIf*` helpers on any
version (see above), and throwing it yourself is flagged by analysis (CA2201, below). For a
bad index/count/length *argument*, `ArgumentOutOfRangeException` is the correct exception — that's
exactly what "an argument's value was invalid" means.

```csharp
// index bounds: index < 0 || index >= count
ArgumentOutOfRangeException.ThrowIfNegative(index);
ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, count);

// count/length must be non-negative
ArgumentOutOfRangeException.ThrowIfNegative(length);

// capacity must be positive
ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

// timeout must be non-negative — TimeSpan is IComparable<TimeSpan>, not INumberBase
ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);

// two-sided range (percentage 0-100) — always two calls, no single "in range" helper
ArgumentOutOfRangeException.ThrowIfLessThan(percent, 0);
ArgumentOutOfRangeException.ThrowIfGreaterThan(percent, 100);

// min/max pair invariant
ArgumentOutOfRangeException.ThrowIfGreaterThan(min, max, nameof(min));
```

There's no single "in range" helper on purpose: two calls means each side independently reports
the right `ParamName`/`ActualValue`/message for whichever bound actually failed:
`ThrowIfGreaterThan(150, 100, "percent")` produces `"percent ('150') must be less than or equal to
'100'."`, naming the upper bound specifically.

Generic constraints: `ThrowIfZero`/`ThrowIfNegative`/
`ThrowIfNegativeOrZero` need `T : INumberBase<T>` (any built-in numeric type, or a custom type via
generic math). `ThrowIfGreaterThan`/`ThrowIfGreaterThanOrEqual`/`ThrowIfLessThan`/
`ThrowIfLessThanOrEqual` need `T : IComparable<T>` — this is how `TimeSpan`/`DateTime`/
`DateTimeOffset` guards work despite not being `INumberBase<T>`. `ThrowIfEqual`/`ThrowIfNotEqual`
need `T : IEquatable<T>` on .NET 8/9, unconstrained on .NET 10+ (see version note above).

## Where guards belong

- **Public/protected entry points of public types**: constructors, public methods, public
  property setters — the boundary where an untrusted caller (another assembly, a caller ignoring
  nullable warnings, reflection-driven construction) can hand you a value the compiler never
  checked.
- **Not every private method.** A `private` helper called only from an already-guarded public
  method doesn't need its own copy — that's redundant work per call and hides the real contract
  behind noise. Trust what the type already guarantees internally.
- **Regardless of nullable annotations.** A `string` (not `string?`) parameter looks non-null to
  the compiler, but NRT is compile-time only — guard public entry points anyway.
- **Iterators and `async` methods: guard eagerly, in a non-iterator/non-async wrapper.** A guard
  written inside an iterator's body compiles into `MoveNext()`, not the call — it doesn't run
  until enumeration starts. `async` methods behave the same way: the guard runs when the state
  machine first executes:

  ```csharp
  // ❌ deferred: returns normally, throws only when .MoveNext() is first called
  static IEnumerable<char> BadIterator(string s)
  {
      ArgumentNullException.ThrowIfNull(s);
      foreach (var c in s) yield return c;
  }

  // ✅ eager: guard in a plain method, iterator logic in a local function
  static IEnumerable<char> GoodIterator(string s)
  {
      ArgumentNullException.ThrowIfNull(s);
      return Impl(s);
      static IEnumerable<char> Impl(string s) { foreach (var c in s) yield return c; }
  }
  ```

  Same split applies to `async Task`: a guard inside the `async` method returns a `Task` with
  `IsFaulted == true` instead of throwing synchronously; a synchronous wrapper calling an `async`
  local function throws synchronously at the call site, matching ordinary (non-async) validation.

## Analyzer rules that push toward these helpers

Against the installed SDK, triggering each rule needs `<AnalysisMode>All</AnalysisMode>`:

- **CA1510** "Use ArgumentNullException throw helper" — fires on `if (x == null) throw new
  ArgumentNullException(...)`.
- **CA1511** "Use ArgumentException throw helper" — fires on `if (string.IsNullOrEmpty(x)) throw
  new ArgumentException(...)`, but **only when the message is empty** (`""`); a custom message
  suppresses it, since `ThrowIfNullOrEmpty` has no way to carry one. Enabled as a **suggestion**
  by default on .NET 10, not a warning — it won't show
  in a plain `dotnet build` until you raise its severity (`dotnet_diagnostic.CA1511.severity =
  warning` in `.editorconfig`) or view it in an IDE.
- **CA1512** "Use ArgumentOutOfRangeException throw helper" — the range-check equivalent.
- **CA1513** "Use ObjectDisposedException throw helper" — for hand-written `if (_disposed) throw
  new ObjectDisposedException(...)`.
- **CA1062** — validate arguments of public methods before use; flags a dereference that skips the
  guard, not the shape of the guard itself.
- **CA2208** — correct argument names: catches `throw new ArgumentException(msg, "wrongParam")`
  where the string doesn't match an actual parameter.
- **CA2201** "reserved exception type" — throwing `IndexOutOfRangeException` directly produces
  `warning CA2201: Exception type System.IndexOutOfRangeException is reserved by the runtime`.
  This is the concrete reason not to throw it yourself for a bad index — use
  `ArgumentOutOfRangeException` via the helpers above instead.

`dotnet format analyzers --diagnostics CA1510,CA1511,CA1512,CA1513 --severity info` applies the
built-in fixers unattended — rewriting `if (string.IsNullOrEmpty(arg)) throw new
ArgumentException("", nameof(arg));` into `ArgumentException.ThrowIfNullOrEmpty(arg);`.
`--severity info` is needed because CA1511's default severity is below what `dotnet format` acts
on otherwise.

## Older TFMs: netstandard2.0 / net4x

The helpers don't exist there. Use an alias polyfill (see `dotnet-msbuild-usings`,
`dotnet-multi-targeting` for the general mechanism):

```xml
<ItemGroup Condition="$([MSBuild]::IsTargetFrameworkCompatible('$(TargetFramework)', 'net8.0'))">
  <Using Include="System.ArgumentNullException" Alias="ArgumentExceptionHelper" />
</ItemGroup>
<ItemGroup Condition="!$([MSBuild]::IsTargetFrameworkCompatible('$(TargetFramework)', 'net8.0'))">
  <Using Include="MyLib.Helpers.ArgumentValidation" Alias="ArgumentExceptionHelper" />
</ItemGroup>
```

Call sites read `ArgumentExceptionHelper.ThrowIfNull(x)` everywhere. The polyfill compiles and runs
on `netstandard2.0` (also needs `CallerArgumentExpressionAttribute`
polyfilled, since the type itself is missing before net5.0/netstandard2.1):

```csharp
#if !NET6_0_OR_GREATER
namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Parameter)]
    internal sealed class CallerArgumentExpressionAttribute(string parameterName) : Attribute
    {
        public string ParameterName { get; } = parameterName;
    }
}
#endif

namespace MyLib.Helpers;

internal static class ArgumentValidation
{
    public static void ThrowIfNull(
        object? argument, [CallerArgumentExpression("argument")] string? paramName = null)
    {
        if (argument is null) throw new ArgumentNullException(paramName);
    }

    public static void ThrowIfNullOrEmpty(
        string? argument, [CallerArgumentExpression("argument")] string? paramName = null)
    {
        if (string.IsNullOrEmpty(argument))
            throw new ArgumentException("Value cannot be null or empty.", paramName);
    }
}
```

`ArgumentValidation.ThrowIfNull(bad)` on `netstandard2.0` throws with `ParamName ==
"bad"` — `[CallerArgumentExpression]` is a compile-time source feature, so only the attribute
*type* needs to exist; the polyfill is picked up exactly like the real one on newer TFMs. Range
and disposed polyfills follow the same shape (same method names/parameter order) so the alias swap
stays source-compatible.

## Custom guards: when and how

Write your own only for domain rules the BCL can't express ("this string must be a valid SKU",
"this collection must be non-empty") — don't reinvent `ThrowIfNull`/`ThrowIfNegative` under a
different name.

```csharp
internal static class OrderValidation
{
    public static void ThrowIfInvalidSku(
        string sku, [CallerArgumentExpression(nameof(sku))] string? paramName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sku, paramName);
        if (!SkuPattern.IsMatch(sku))
        {
            ThrowInvalid(sku, paramName);
        }
    }

    [DoesNotReturn]
    [StackTraceHidden]
    private static void ThrowInvalid(string sku, string? paramName) =>
        throw new ArgumentException($"'{sku}' is not a valid SKU.", paramName);
}
```

- **Static**, value plus `[CallerArgumentExpression]` for the parameter name — mirrors the BCL
  shape.
- **`[DoesNotReturn]`** informs flow/nullable analysis the same way the BCL's internal throw
  helpers do.
- **`[StackTraceHidden]`** keeps the helper's frame out of the exception's stack trace (see
  `csharp-error-handling`'s hot-path throw-helper pattern, which this reuses).
- **Generic constraints** where the check is type-shaped (`where T : IComparable<T>`) rather than
  hard-coding one numeric type.
- **Never swallow** — a guard validates and throws, or returns silently on success; it never
  catches, logs-and-continues, or coerces an invalid value into a valid one.

## Checklist

- [ ] Hand-written `if (...) throw new Argument*/ObjectDisposedException(...)` replaced with the
      matching `ThrowIf*` helper
- [ ] Two-sided ranges expressed as two `ArgumentOutOfRangeException.ThrowIf*` calls
- [ ] Bad index/count/length arguments throw `ArgumentOutOfRangeException`, never
      `IndexOutOfRangeException` (CA2201 — reserved for the runtime)
- [ ] Guards live at public/protected entry points, not duplicated into every private helper
- [ ] Iterator (`yield return`) and `async` methods guard eagerly via a non-iterator/non-async
      wrapper
- [ ] `x ?? throw new ArgumentNullException(nameof(x))` used only for single-expression
      guard-and-assign, not as a substitute for `ThrowIfNull` across a whole method
- [ ] CA1510/CA1511/CA1512/CA1513 enabled at a severity that surfaces them (CA1511 defaults to
      suggestion) and applied with `dotnet format analyzers --severity info` where useful
- [ ] `netstandard2.0`/`net4x` legs use an `ArgumentExceptionHelper`-style `Using Alias` polyfill,
      not `#if` scattered at every guard
- [ ] Custom guards only for domain rules, follow the BCL shape, never swallow

## See also

- `csharp-error-handling` — catch/throw correctness generally; the short guard-helper summary this
  skill expands on, plus the `[DoesNotReturn]`/`[StackTraceHidden]` hot-path pattern.
- `csharp-nullability` — the NRT attributes and boundary-guard rationale these guards enforce at
  runtime.
- `csharp-nullable-migration` — rolling NRT out across an existing codebase, where guard clauses
  are often added as part of the migration.
- `dotnet-analyzers` — running/configuring the analyzer set CA1510-CA1513/CA1062/CA2208/CA2201
  belong to.
- `roslyn-guard-rewriters` — standalone rewriters that convert existing hand-written guards to
  these helpers, and add missing null guards, at scale without enabling the analyzers.
- `dotnet-msbuild-usings` — the `<Using Alias>` mechanism behind the `ArgumentExceptionHelper`
  polyfill pattern.
- `csharp-api-design` — where argument validation fits into public surface design generally.
