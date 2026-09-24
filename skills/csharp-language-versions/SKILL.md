---
name: csharp-language-versions
description: Use before reaching for any C# 12–15 language feature — how to find the effective LangVersion for a project (TargetFramework default, explicit LangVersion, Directory.Build.props, file-based apps), a feature-by-version reference table, multi-targeting/polyfill caveats for older TFMs, and treating LangVersion/TargetFramework changes as deliberate repo-level decisions rather than side effects of one change.
---

# Language versions — know what the project actually compiles with

C# version is a **property of the project**, not of the SDK installed on the machine. Having a newer SDK on the machine does not mean a given project compiles with a newer C# version — check before using a feature, every time you're not sure.

**`LangVersion` (a compiler setting) and `TargetFramework` (a runtime/BCL setting) are independent knobs.** The SDK picks a default `LangVersion` from the TFM, but nothing stops you from setting `<LangVersion>latest</LangVersion>` on `netstandard2.0` or `net4x` — the compiler will emit C# 15 syntax into an old-runtime assembly. Whether it *runs* depends entirely on whether the feature needs a runtime-looked-up type (polyfillable) or real runtime support (not). See `csharp-polyfills` for the full three-tier breakdown, how polyfills work, and hand-written-vs-PolySharp guidance — this file only covers the polyfill caveat briefly below.

## Finding the effective LangVersion

In order of precedence (later overrides earlier):

1. **`TargetFramework` default.** Each TFM implies a default `LangVersion` if none is set explicitly:

   | `TargetFramework` | Default `LangVersion` |
   | --- | --- |
   | `net8.0` | 12 |
   | `net9.0` | 13 |
   | `net10.0` | 14 |
   | `net11.0` | 15 |
   | `netstandard2.x`, `net48`, etc. | 7.3 (oldest "no TFM-based default" behavior — see polyfills below) |

2. **Explicit `<LangVersion>`** in the `.csproj` — overrides the TFM default in either direction (can pin *down* to keep an older surface on a new TFM, or, rarely, force a value the TFM wouldn't otherwise imply).
3. **`Directory.Build.props`** anywhere up the folder tree — a repo-wide `<LangVersion>` here silently applies to every project under it; check for one before assuming a project's own `.csproj` is the whole story.
4. **File-based apps** (`dotnet run file.cs`, see `dotnet-file-based-apps`) — no TFM until you set one; without `#:property TargetFramework=...` they build against the SDK's default TFM (whatever `dotnet new console` would pick on that SDK), so LangVersion follows that default TFM's row above unless overridden with `#:property LangVersion=...`.

To check from the command line rather than reading files by hand:

```bash
dotnet build -getProperty:LangVersion
dotnet build -getProperty:TargetFramework
```

**Change `LangVersion` or `TargetFramework` deliberately, never as a side effect.** Raising `LangVersion` above the TFM default (commonly `latest` on `netstandard2.0`/`net4x` with polyfills) is a valid repo-wide choice; see `csharp-polyfills` for which features then work. Retargeting changes the supported runtime matrix and is a bigger decision. Either way, make it its own change in `Directory.Build.props`, not a bump buried inside an unrelated edit. Within an existing change, use what the project's current LangVersion and TFM support.

## Feature-by-version reference (C# 12–15)

| Feature | Version | Notes |
| --- | --- | --- |
| Primary constructors (`class`/`struct`) | 12 | See `csharp-modern-types` for the capture pitfall |
| Collection expressions (`[...]`, spread `..`) | 12 | Target-typed; see `csharp-collections-modern` |
| Default lambda parameters | 12 | |
| `ref readonly` parameters | 12 | |
| Alias any type (`using X = (int, string);`) | 12 | |
| `params` collections (`params ReadOnlySpan<T>`, `params IEnumerable<T>`, etc.) | 13 | Not array-only anymore; see `csharp-collections-modern` |
| `ref`/`unsafe` in async/iterator methods (locals only) | 13 | |
| Natural type for method groups improvements | 13 | |
| Partial properties/indexers | 13 | |
| `field` contextual keyword in property accessors | 14 | See `csharp-modern-types` |
| Extension members (`extension(T receiver)` blocks — properties, static members, operators) | 14 | See `csharp-extension-members` |
| `nameof` unbound generic type support | 14 | |
| Implicit `Span<T>`/`ReadOnlySpan<T>` conversions widened | 14 | |
| `Lock` type get first-class `lock` statement support | 13 (type in .NET 9)/14 (compiler pattern-matches it) | Compiler uses the efficient `Lock` API automatically when `System.Threading.Lock` is present and locked with a plain `lock` statement |
| Collection expression arguments (`[with(capacity: n), ...]`) | 15 | See `csharp-collections-modern` |
| Union types (`union Pet(Cat, Dog, Bird);`) | 15 | See `csharp-modern-types`, `csharp-pattern-matching` |
| Closed hierarchies (`closed record class`) | 15 | See `csharp-modern-types`, `csharp-pattern-matching` |
| Extension indexers | 15 | See `csharp-extension-members` |
| Labeled `break`/`continue` | 15 | See below |
| Memory-safety pointer relaxations, `unsafe(expr)`, `safe` keyword | 15, **preview only** | Do not adopt in product code yet — see below |

## Labeled `break`/`continue` (C# 15)

Label an outer loop and jump to it directly from a nested loop, instead of a flag variable or `goto`:

```csharp
outer: for (var i = 0; i < rows; i++)
{
    for (var j = 0; j < cols; j++)
    {
        if (grid[i, j] == sentinel) continue outer;   // next row
        if (grid[i, j] == target) break outer;         // stop entirely
        Process(grid[i, j]);
    }
}
```

Needs `net11.0`+/C# 15. An analyzer (IDE0410) flags the older flag-variable/`goto` workaround once the project is on a version that supports this — don't leave the workaround in place after upgrading.

## Polyfill caveats when multi-targeting

See `csharp-polyfills` for the full mechanism (how a polyfill type is looked up, hand-written shim
patterns, PolySharp). Short version: targeting `netstandard2.0` or another older TFM alongside a modern one splits features into two buckets:

- **Compiler-only features** (no runtime type required) work purely from `LangVersion` regardless of TFM — e.g. pattern matching syntax, `switch` expressions, most of collection expressions when the target is a concrete type the old TFM already has (`T[]`, `List<T>`).
- **Features needing a runtime-provided type or attribute** fail on older TFMs unless you supply a polyfill:
  - `required` members need `RequiredMemberAttribute`/`SetsRequiredMembersAttribute` (and `CompilerFeatureRequiredAttribute`) — missing on `netstandard2.0`; add the well-known polyfill shim (a same-named type in your own assembly satisfies the compiler) or reference a polyfill package.
  - `init` accessors need `System.Runtime.CompilerServices.IsExternalInit` — same story, trivially polyfilled with an empty marker type in the target namespace.
  - Records need `IsExternalInit` for `init` members and cooperate best on `netstandard2.1`+/`net5.0`+ — heavier records features (positional records, `with`) also need runtime support that gets thinner the older the TFM.
  - Unions (C# 15) need `System.Runtime.CompilerServices.UnionAttribute`/`IUnion` — not present before .NET 11's runtime; a union type is effectively `net11.0`+ only even with a polyfilled attribute, since consumers (`System.Text.Json`, etc.) key off the real runtime contract.
  - `Index`/`Range` (`^`, `..`) need `System.Index`/`System.Range` — polyfillable, ships in `Microsoft.Bcl.Numerics`-style shim packages for old TFMs.
  - `params ReadOnlySpan<T>`/`Span<T>` need the `System.Memory`-family types present — fine on `netstandard2.1`+, needs the `System.Memory` package on `netstandard2.0`.

If a multi-targeted project needs one of these, verify it against the *lowest* TFM you multi-target, not just the highest — `dotnet build -f netstandard2.0 -getProperty:LangVersion` to check what that leg actually compiles with.

## Preview features — never in product code

Anything requiring `<LangVersion>preview</LangVersion>` (currently: the C# 15 memory-safety pointer relaxations, `unsafe(expr)`, `safe`) is explicitly unstable — behavior, syntax, or existence can change before the feature ships. Preview features are for spikes/experiments in a throwaway file-based app (`dotnet-file-based-apps`) only. Never ship `LangVersion=preview` in a product `.csproj`, and never let a preview-only construct leak into code reviewed as "done."

## Checklist

- [ ] Checked the project's actual `TargetFramework`/`LangVersion` (including `Directory.Build.props`) before using a C# 12–15 feature, not assumed from the installed SDK
- [ ] Any `LangVersion`/TFM change made deliberately as its own repo-level change, with polyfills for the features it relies on
- [ ] Multi-targeted projects verified against their lowest TFM, with polyfills identified for any runtime-type-dependent feature
- [ ] No `LangVersion=preview` construct present outside a throwaway file-based spike
- [ ] Labeled `break`/`continue` used instead of a flag variable or `goto` once the project is on `net11.0`+/C# 15
