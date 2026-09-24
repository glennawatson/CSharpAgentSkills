---
name: csharp-string-handling
description: Use when writing or reviewing C#/.NET string handling — raw string literals and interpolated raw strings, custom interpolated string handlers, UTF-8 literals, string.Create (including the .NET 9+ ref-struct state and initial-buffer interpolation overloads), CompositeFormat for repeated formats, SearchValues, correct StringComparison choice, AsSpan slicing vs Substring, and StringBuilder vs interpolation.
---

# String handling (C# 13/14/15, .NET 9–11)

String code is either correctness-sensitive (comparisons, protocol text) or allocation-sensitive (hot-path formatting). Pick the right tool for which one you're in.

## Raw string literals

`"""..."""` needs no escaping for quotes or backslashes, and strips the leading indentation of the closing delimiter:

```csharp
string raw = """
    Hello "world"
    Line two
    """;
```

Interpolated raw strings (`$$"""..."""`) let you write literal `{` `}` by using more `$` signs than braces — the hole itself needs the matching number of braces:

```csharp
int x = 42;
string s = $$"""
    Value is {{x}} and literal {{{"braces"}}}
    """;
// => Value is 42 and literal {braces}
```

Reach for raw strings for JSON/regex/SQL/code snippets embedded in source — anything currently fighting `\"` escaping or verbatim-string oddities.

## Interpolated string handlers

An ordinary `$"..."` compiles to a `string.Format`/concatenation call — the interpolated arguments are always evaluated and formatted. An **interpolated string handler** lets an API intercept construction and skip formatting work entirely when it isn't needed:

```csharp
[InterpolatedStringHandler]
public ref struct LogHandler
{
    private DefaultInterpolatedStringHandler _inner;
    public bool Enabled { get; }

    public LogHandler(int literalLength, int formattedCount, bool enabled)
    {
        Enabled = enabled;
        _inner = new DefaultInterpolatedStringHandler(literalLength, enabled ? formattedCount : 0);
    }

    public void AppendLiteral(string s) { if (Enabled) _inner.AppendLiteral(s); }
    public void AppendFormatted<T>(T value) { if (Enabled) _inner.AppendFormatted(value); }
    public string GetFormatted() => _inner.ToStringAndClear();
}

static void LogIfEnabled(bool enabled,
    [InterpolatedStringHandlerArgument(nameof(enabled))] LogHandler message)
{
    if (enabled) Console.WriteLine(message.GetFormatted());
}
```

This is exactly why logging APIs (and `Debug.Assert`-style APIs) accept a handler instead of a plain `string`: when the log level is disabled, `AppendFormatted` is never called, so no boxing, no `ToString()`, no intermediate string is built for that call.

**Caveat that trips people up:** the handler only skips *formatting*. The interpolation holes themselves (`{Compute()}`) are ordinary method arguments — they're evaluated by the caller before the handler ever sees them, whether or not `Enabled` is true. If an argument is expensive to *compute* (not just to format), guard the call site (`if (enabled) Log($"...")`) or pass a lazily-evaluated `Func<T>`/structured parameters instead — the handler pattern alone doesn't defer computation, only string building. `DefaultInterpolatedStringHandler` itself (used implicitly by every plain `$"..."` today) is what makes ordinary interpolation allocate minimally already; write a custom handler only when you need to *conditionally skip* the work, not just to format efficiently.

## UTF-8 string literals

`"..."u8` produces a `ReadOnlySpan<byte>` (UTF-8 encoded), computed at compile time — no runtime `Encoding.UTF8.GetBytes` call:

```csharp
ReadOnlySpan<byte> header = "HTTP/1.1 200 OK\r\n"u8;
```

Use it for protocol constants, magic bytes, and comparisons against byte-oriented APIs (`Utf8JsonWriter`, sockets, `SearchValues<byte>`) instead of encoding a `string` literal at runtime.

## `string.Create` for single-allocation building

Builds directly into the final buffer instead of allocating intermediates — the string itself is the only allocation:

```csharp
string created = string.Create(2, 42, static (span, state) =>
{
    state.TryFormat(span, out _);
});
```

Use it when you know the exact output length up front and would otherwise concatenate several pieces. **Always pass a `static` lambda plus a state argument, never a capturing closure.** Measured (BenchmarkDotNet, net10.0/net11.0, building a short `"prefix-12345"`-shaped string): a `static` lambda over a tuple state allocates only the 48-byte result string at ~13 ns; the same body written as a capturing lambda allocates an extra ~24–32 bytes for the closure and costs roughly 2–3x as long. This holds on both .NET 10 and .NET 11 — see the escape-analysis note below for why the closure still allocates even with .NET 10's stack-allocation improvements.

### .NET 9+: the state can be a `ref struct` (spans included)

Before .NET 9, `string.Create<TState>`'s `TState` was constrained to ordinary types — you couldn't pass a `Span<char>`/`ReadOnlySpan<char>` (or any other `ref struct`) as the state without boxing it into a wrapper or falling back to a capturing closure. .NET 9 added `TState : allows ref struct` (part of the C# 13 ref-struct-generics feature), so a span can now be the state directly:

```csharp
// ✅ .NET 9+: ReadOnlySpan<char> as TState, no closure needed
ReadOnlySpan<char> prefix = GetPrefix(); // e.g. a slice of another string
string s = string.Create(prefix.Length + 5, (Prefix: prefix, Id: 42), static (span, state) =>
{
    state.Prefix.CopyTo(span);
    state.Id.TryFormat(span[state.Prefix.Length..], out _);
});
```

Verified by compiling the same call against `net8.0` (fails: `CS9244 — TState may not be a ref struct ... to use it as parameter 'TState'`) and `net9.0`/`net10.0`/`net11.0` (compiles and runs) with the current SDKs. Reach for this when the piece you'd otherwise capture is itself a span/slice — it lets `string.Create` consume it as state instead of forcing a `.ToString()` (extra allocation) or a capturing lambda (closure allocation) to get it into scope.

### The interpolation overload: `string.Create(IFormatProvider?, Span<char> initialBuffer, ref DefaultInterpolatedStringHandler)`

A second, unrelated overload lets you culture-format an interpolated string into a caller-supplied buffer instead of the handler's own growable internal buffer:

```csharp
Span<char> buffer = stackalloc char[64];
string s = string.Create(CultureInfo.InvariantCulture, buffer, $"{name}: {amount:C}");
```

The interpolation holes are formatted straight into `buffer` (stackalloc, so no extra heap traffic for the intermediate), then copied once into the final `string`. Use it when you need explicit `IFormatProvider` control (avoiding the ambient-culture pitfalls in `StringComparison`/culture-sensitive formatting) *and* want to avoid the handler falling back to a pooled/heap array for a larger interpolation. This overload has been available since .NET 8 — it isn't new, but it's easy to miss next to the more famous span-fill overload above.

### When `string.Create` is pointless

- **A single `$"..."` interpolation already lowers to `DefaultInterpolatedStringHandler`** and allocates only the result string — wrapping it in `string.Create` (or a one-shot `StringBuilder`) buys nothing. Measured: plain interpolation and the `string.Create` span-fill form allocate the same 48 bytes for an equivalent short string.
- **`string.Concat(ReadOnlySpan<char>, ...)`** (the `ReadOnlySpan<char>` overloads, not the older `string[]` ones) is just as allocation-free for straight concatenation of already-available spans — reach for it over `string.Create` when there's no custom formatting logic, just pieces to lay end to end.
- **`string.Join`** with the .NET 9+ `params ReadOnlySpan<T>` overloads (see below) is likewise already optimal for joining a handful of values — don't hand-roll a `string.Create` call to replace it.
- Don't reach for `string.Create` just because a hot path "does string work" — profile first; for anything under a few concatenated pieces, the ordinary tools above already make one allocation.

### `.NET 9`'s `params ReadOnlySpan<T>` overloads — a free win, no code change required

Starting with C# 13/.NET 9, `params` can bind to a `ReadOnlySpan<T>` parameter, and over 60 BCL methods — including `string.Join(string, params ReadOnlySpan<string>)` and `string.Concat` — got such overloads. The compiler prefers the span overload over the array one, so:

```csharp
string.Join(", ", "a", "b", "c");   // .NET 9+: stack-allocates the span the compiler builds, no string[] allocation
```

recompiling existing call sites against .NET 9+ removes the implicit `string[]` allocation automatically — nothing to rewrite, just upgrade the target framework.

### .NET 10 JIT escape analysis — why the closure still costs you

.NET 10 added escape analysis for delegates: when a lambda's `Func`/`Action` object provably doesn't escape its calling method, the JIT stack-allocates *that* object instead of heap-allocating it. This does **not** eliminate the closure. For a capturing lambda, the compiler still emits a display class to hold the captured locals, and .NET 10's escape analysis does not (yet) extend to that display class — only the delegate object wrapping it. Measured directly (`static` no-capture lambda vs. a lambda capturing one local, both passed straight into a method and never stored, on net10.0): the static lambda allocates nothing; the capturing lambda still allocates 24 bytes for its closure every call, even though the surrounding `Func<>` itself is now stack-allocated. **Conclusion: a `static` lambda + explicit `TState` is still the only way to make `string.Create` (or any callback) fully allocation-free — .NET 10's escape analysis narrows the gap but doesn't close it for captured state.**

## `CompositeFormat` for repeated format strings

`string.Format("...", args)` reparses the format string every call. If the *same* format string is used repeatedly (a report template, a recurring log line), pre-parse it once:

```csharp
private static readonly CompositeFormat Template =
    CompositeFormat.Parse("Name: {0}, Age: {1}");

string.Format(null, Template, "Alice", 30);   // no re-parsing of the format string
```

Not worth it for a format string used once — the parse cost only matters when amortized over many calls.

## `SearchValues<T>` for repeated membership checks

`SearchValues<char>`/`SearchValues<byte>`/`SearchValues<string>` precompute an optimized search structure — far faster than `IndexOfAny(char[])`, which also allocates the array fresh each call:

```csharp
private static readonly SearchValues<char> Vowels = SearchValues.Create("aeiou");
"hello world".AsSpan().IndexOfAny(Vowels);

private static readonly SearchValues<string> Keywords =
    SearchValues.Create(["cat", "dog"], StringComparison.OrdinalIgnoreCase);
"I have a Dog".AsSpan().IndexOfAny(Keywords);
```

Build the `SearchValues` instance once (`static readonly`), reuse it for every search — building it is the expensive part; searching with it is cheap.

## .NET 11: `Rune`-aware `String` methods and UTF validation without exceptions

.NET 11 adds `Contains`/`StartsWith`/`EndsWith`/`IndexOf`/`LastIndexOf`/`Replace`/`Split`/`Trim`(`Start`/`End`) overloads on `string` that take a `System.Text.Rune` (with `StringComparison` overloads too). Use these instead of hand-rolling surrogate-pair-aware char comparisons when the text may contain characters outside the BMP (emoji, many non-Latin scripts) — `"😀".Contains('😀')`-style code using `char` silently breaks on surrogate pairs; the `Rune` overloads don't.

`System.Text.Unicode.Utf8.IsValid`/`Utf16.IsValid` (`ReadOnlySpan<byte|char> -> bool`) and the new `IndexOfInvalidSubsequence` methods let a parser/validator check or locate encoding errors directly on a span — no need to wrap an `Encoding.GetString`/`GetBytes` call in a try/catch just to validate input.

## `StringComparison` — get this right

- **Default to `Ordinal`/`OrdinalIgnoreCase`** for anything that isn't natural-language text meant for display: identifiers, keys, file paths (platform-dependent case-sensitivity aside), protocol strings, enum-like tokens, config keys. Ordinal is a byte/char comparison — fast and has no locale surprises.
- **Never use culture-sensitive comparison (`CurrentCulture`, or `==`/`string.Compare` with no `StringComparison` at all, which defaults to culture-sensitive) for protocol or identifier text.** The classic bug: Turkish-locale `"file".ToUpper()` produces `"FİLE"` (dotted İ), silently breaking a case-insensitive match that assumed ASCII behavior everywhere.
- Reserve culture-sensitive comparison (`StringComparison.CurrentCulture`/`InvariantCulture` variants) for text you're actually displaying/sorting for a human in a specific locale.
- Always pass `StringComparison` explicitly to `Contains`/`StartsWith`/`EndsWith`/`Equals`/`IndexOf` — the overloads without it silently pick culture-sensitive comparison.

```csharp
// ❌ culture-sensitive by default, locale-dependent behavior
if (name.StartsWith("prefix")) { ... }

// ✅ explicit, locale-independent
if (name.StartsWith("prefix", StringComparison.Ordinal)) { ... }
```

## `AsSpan` slicing instead of `Substring`

`Substring` allocates a new string. `AsSpan` slices the existing one — a view, not a copy:

```csharp
ReadOnlySpan<char> csv = "prefix-value".AsSpan();
var dash = csv.IndexOf('-');
ReadOnlySpan<char> value = csv[(dash + 1)..];   // no allocation
```

Only materialize a `string` (`.ToString()` on the slice) at the point you actually need a heap `string` — e.g. to store it, return it from a public API, or pass it to something that doesn't accept spans.

## .NET 11: wrap in-memory data as a `Stream` without copying

`new MemoryStream(Encoding.UTF8.GetBytes(s))` allocates twice: once for the encoded byte array, once for the `MemoryStream`'s internal buffer copy. .NET 11 adds `Stream` types (`System.IO`, confirmed by compiling against SDK `11.0.100-rc.1`) that wrap existing memory directly:

```csharp
// ❌ encodes into a new byte[], MemoryStream copies it again internally
Stream s = new MemoryStream(Encoding.UTF8.GetBytes(text));

// ✅ StringStream encodes on read, no intermediate byte[] materialized up front
Stream s = new StringStream(text, Encoding.UTF8);

// ✅ wrap an existing buffer/memory with no copy at all
Stream s = new ReadOnlyMemoryStream(existingBytes.AsMemory());
Stream s = new WritableMemoryStream(existingBuffer);          // write into a caller-owned buffer
Stream s = new ReadOnlySequenceStream(existingSequence);       // wrap a ReadOnlySequence<byte> (e.g. from a pipe)
```

- **`StringStream`** wraps a `string` + `Encoding` and streams the encoded bytes out lazily — use it for "I have a string, something wants a `Stream`" (an HTTP request body, a JSON reader) instead of pre-encoding into a byte array.
- **`ReadOnlyMemoryStream`**/**`WritableMemoryStream`** wrap an existing `ReadOnlyMemory<byte>`/`Memory<byte>` with no copy — use these instead of `MemoryStream` when you already have the buffer (from `ArrayPool`, a `Span`-backed source) and just need `Stream`-shaped access to it.
- **`ReadOnlySequenceStream`** wraps a `ReadOnlySequence<byte>` (the type `System.IO.Pipelines`/`PipeReader` hands you) as a `Stream` without flattening the segments into one buffer first.
- All are read/write-capable where it makes sense but don't own or grow the backing memory the way `MemoryStream` does — reach for `MemoryStream` only when you actually need an owned, growable buffer.

## `StringBuilder` vs. interpolation

- A single `$"..."` (even with several holes) compiles to an efficient `DefaultInterpolatedStringHandler`-based build already — don't manually replace it with a one-shot `StringBuilder` for "performance"; that's more code for no gain.
- Reach for `StringBuilder` when building a string **incrementally across multiple statements/loop iterations** — repeated `+=` concatenation in a loop reallocates and copies the whole string each time (`O(n²)`); `StringBuilder.Append` amortizes to `O(n)`.
- For very large or streamed output, see `StringBuilder.GetChunks()` with pooled buffers (`csharp-performance`) to avoid materializing the whole string at all.
- **.NET 11: `StringBuilder.MoveChunks(source)`** transfers a builder's content to a new `StringBuilder` without copying characters (the source is left with length 0) — use it instead of `new StringBuilder(source.ToString())` when handing off a builder's contents (e.g. pooling, ownership transfer) rather than reading them.

```csharp
// ❌ O(n²) — reallocates the whole string every iteration
string result = "";
foreach (var item in items) result += item + ",";

// ✅ O(n)
var sb = new StringBuilder();
foreach (var item in items) sb.Append(item).Append(',');
string result = sb.ToString();
```

## Checklist

- [ ] Raw string literals used for embedded JSON/regex/SQL instead of escaped verbatim strings
- [ ] Interpolated string handler used only where *conditionally skipping* formatting matters (logging/assert-style APIs); expensive arguments still guarded separately
- [ ] `"..."u8` used for byte-oriented protocol constants instead of runtime UTF-8 encoding
- [ ] `string.Create` used when the exact output length is known and pieces would otherwise be concatenated; callback is a `static` lambda + `TState`, never a capturing closure
- [ ] .NET 9+: a span/other `ref struct` piece passed to `string.Create` as `TState` directly, not `.ToString()`'d or captured
- [ ] Not reaching for `string.Create`/`StringBuilder` where plain interpolation or `string.Concat`/`string.Join`'s span overloads already allocate only the result
- [ ] `CompositeFormat` for format strings used repeatedly, not one-off `string.Format`
- [ ] `SearchValues<T>` built once (`static readonly`) for repeated membership/search checks
- [ ] `StringComparison.Ordinal`/`OrdinalIgnoreCase` for identifiers/protocol text; never implicit culture-sensitive comparison there
- [ ] `AsSpan` slicing instead of `Substring` when a copy isn't actually needed
- [ ] `StringBuilder` only for multi-step incremental building, not single interpolations
