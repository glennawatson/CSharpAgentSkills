---
name: csharp-polyfills
description: Use when a project's LangVersion is higher than its TargetFramework's default (especially netstandard2.0/net4x/older net*.0), or when a feature like record/init/required/pattern matching won't compile on an old TFM — how LangVersion and TargetFramework are independent knobs, which C# 12-15 features need a polyfilled runtime type versus which are impossible on an old runtime, and how to write/wire the polyfill (hand-written internal shim vs PolySharp).
---

# Polyfills — making modern C# compile on old runtimes

`LangVersion` is a **compiler** setting. `TargetFramework` is a **runtime/BCL** setting. The SDK
picks a default `LangVersion` from the TFM, but nothing stops you from overriding it — the compiler
will happily emit IL for a `netstandard2.0` assembly using C# 15 syntax. Whether that IL *runs* is a
separate question, decided per feature. This skill is the detailed authority on that question;
`csharp-language-versions` covers it briefly and points here.

## The three tiers

| Tier | What it needs | Examples | Works on `netstandard2.0`/`net4x` with `LangVersion latest`? |
| --- | --- | --- | --- |
| **Pure compiler lowering** | Nothing beyond IL the old runtime already understands | pattern matching, switch expressions, file-scoped namespaces, target-typed `new`, raw string literals, collection expressions targeting `T[]`/`List<T>`, primary constructors, `field` keyword, `using` alias for tuples | Yes, unconditionally |
| **Needs a polyfilled type** | A specific type/attribute the compiler binds to *by full name* — doesn't have to be the real BCL one | `init` (`IsExternalInit`), `required` (`RequiredMemberAttribute`/`CompilerFeatureRequiredAttribute`/`SetsRequiredMembersAttribute`), `[CallerArgumentExpression]`, nullable flow attributes (`NotNullWhen`, `MemberNotNull`, `DoesNotReturn`, ...), `Index`/`Range` (`^`, `..`), `ModuleInitializerAttribute`, `SkipLocalsInitAttribute`, `InterpolatedStringHandlerAttribute`, `UnscopedRefAttribute`, `OverloadResolutionPriorityAttribute` | Yes, if you supply the type |
| **Needs runtime support** | A real capability the CLR/metadata format of the old runtime doesn't have — no type can fake it | default interface members, static abstract interface members, `ref struct` generics/`allows ref struct`, runtime async, union types (`IUnion`/`UnionAttribute`) | No, not ever, regardless of `LangVersion` |

With `LangVersion latest` on `netstandard2.0`/`net472` and those polyfills in place, `record`,
`required` members, `[CallerArgumentExpression]` guards, `[NotNullWhen]` and `^1` indexing all
compile. Tier 3 features fail on those targets whatever the `LangVersion`: a default interface
member gives `CS8701` ("Target runtime doesn't support default interface implementation") and a
`static abstract` interface member gives `CS8919` ("Target runtime doesn't support static abstract
members in interfaces").

Check the *default* `LangVersion` the SDK picks per TFM directly:

```bash
dotnet build -f netstandard2.0 -getProperty:LangVersion   # 7.3
dotnet build -f net472        -getProperty:LangVersion   # 7.3
dotnet build -f net8.0        -getProperty:LangVersion   # 12.0
dotnet build -f net9.0        -getProperty:LangVersion   # 13.0
dotnet build -f net10.0       -getProperty:LangVersion   # 14.0
```

See `csharp-language-versions` for the full version-to-TFM default table.

## Two truths, stated plainly

Microsoft's own docs (`configure-language-version.md`) say: don't set `LangVersion` to `latest`,
because "it enables language features that might require runtime or library features not included
in the current SDK" — raising `LangVersion` above the TFM default is explicitly framed as
unsupported/risky.

The practical reality in real multi-targeted libraries (ReactiveUI, Splat, Refit, punchclock,
Primitives, Akavache, Fusillade, ReactiveUI.Validation) is that every one of them sets
`<LangVersion>latest</LangVersion>` repo-wide and ships hand-written polyfills for the tier-2 types
on their oldest TFMs. Both things are true at once: it is unsupported in the sense that nothing
guarantees a future language feature stays polyfillable, and it is done deliberately, with the
polyfill surface audited and owned, because the payoff (one C# dialect across every TFM, not a
downgraded one on the old legs) is worth it. Do the same only if you're willing to own that surface —
check every new language feature you adopt against the three-tier table above before assuming it
polyfills.

## How a polyfill actually works

The compiler doesn't check that `System.Runtime.CompilerServices.IsExternalInit` came from the BCL —
it checks that a type with that exact namespace and name exists *somewhere* in the compilation's
reference closure, including your own assembly. Declare an `internal` type with the matching
signature and the feature lights up exactly as if the real BCL type were there. This is true for
every tier-2 feature: the compiler's job is done once the marker type/attribute resolves; what the
type actually contains (often nothing at all, for pure markers like `IsExternalInit`) is irrelevant
to the compiler, though some polyfills (throw-helper style APIs) do need a real body because callers
invoke members on them.

### The shape used across ReactiveUI/Splat/Refit/punchclock/Primitives/Akavache

Every one of these repos keeps a `Polyfills/` folder of small, single-purpose files, one type per
file, each guarded so it's a no-op on TFMs that already ship the real type:

```csharp
// src/Polyfills/IsExternalInit.cs
using System.Diagnostics;
using System.Runtime.CompilerServices;

#if !NET
using System.Diagnostics.CodeAnalysis;

namespace System.Runtime.CompilerServices;

[ExcludeFromCodeCoverage]
[DebuggerNonUserCode]
internal static class IsExternalInit;

#else
[assembly: TypeForwardedTo(typeof(IsExternalInit))]
#endif
```

Points worth copying:

- **`#if !NET`** (or `#if !NET5_0_OR_GREATER`/`#if !NET7_0_OR_GREATER` for a type added later than
  net5.0) — the type only needs to exist where the real one is missing; compiling it unconditionally
  on a TFM that already has the BCL type is a duplicate-type error.
- **Same namespace, same name as the real type.** This is the entire mechanism — the compiler does
  a name lookup, nothing more.
- **Always `internal`.** A `public` polyfill can collide with the real BCL type the moment a
  *consumer's* TFM supplies it, or with another assembly's identically-named public polyfill. Keeping
  it internal means each assembly's polyfill is invisible outside itself.
- **`[ExcludeFromCodeCoverage]`/`[DebuggerNonUserCode]`** keep these marker types out of coverage
  reports and the debugger's stepping — they're plumbing, not code under test.
- **No `InternalsVisibleTo`-shared single copy.** ReactiveUI's Core/leaf split deliberately has no
  IVT between shipping assemblies specifically so each assembly can compile its *own* internal copy
  of the same polyfill without ambiguity — sharing one copy across IVT-linked assemblies is exactly
  how you get `CS0436` ("type conflicts with imported type") once both the shared copy and a
  referenced assembly's copy are visible in the same compilation. If two IVT-linked assemblies both
  need the same polyfill, either put it in the one assembly the other doesn't need `InternalsVisibleTo`
  for, or accept `CS0436` isn't fatal (the compiler picks the type declared in the current
  compilation) but keep it in mind before wiring IVT for something else.

### Wiring it into the TFMs that need it: TFM-condition in the props file, not `#if` at every call site

Reference `Directory.Build.props` for how you'd generally condition anything on `TargetFramework`
(`dotnet-msbuild-usings`, `dotnet-multi-targeting`). For polyfills specifically, ReactiveUI's
`src/Directory.Build.props` gates the whole folder on the TFMs that lack the types:

```xml
<!-- Polyfills: the Core/leaf split has no IVT between shipping assemblies, so each ReactiveUI
     assembly compiles its own internal copy of the BCL polyfills it needs. The sources are guarded
     with #if !NET, so the include is gated to the net4 TFMs (net5+ ship these types). -->
<ItemGroup Condition="$(MSBuildProjectName.StartsWith('ReactiveUI')) and $(TargetFramework.StartsWith('net4'))">
  <Compile Include="$(MSBuildThisFileDirectory)Polyfills\*.cs" Link="Polyfills\%(Filename)%(Extension)"/>
</ItemGroup>
```

Refit's `src/Directory.Build.props` goes further and conditions *per file*, because its TFM matrix
needs different subsets on different legs — `net4x` needs the whole folder, `netstandard2.0` needs
only four of the files (it already has `IsExternalInit`/`Index`/`Range` via `netstandard2.1`-adjacent
shims elsewhere, but lacks the throw-helper/attribute set), and `net8.0` needs exactly one file
(`OverloadResolutionPriorityAttribute`, added to the BCL only in net9.0) for exactly one project:

```xml
<ItemGroup Condition="$(TargetFramework.StartsWith('net4'))">
  <Compile Include="$(MSBuildThisFileDirectory)Polyfills\*.cs" Link="Polyfills\%(Filename)%(Extension)"/>
</ItemGroup>

<ItemGroup Condition="'$(TargetFramework)' == 'netstandard2.0'">
  <Compile Include="$(MSBuildThisFileDirectory)Polyfills\ArgumentExceptionHelper.cs" Link="Polyfills\ArgumentExceptionHelper.cs"/>
  <Compile Include="$(MSBuildThisFileDirectory)Polyfills\ArgumentOutOfRangeExceptionHelper.cs" Link="Polyfills\ArgumentOutOfRangeExceptionHelper.cs"/>
  <Compile Include="$(MSBuildThisFileDirectory)Polyfills\CallerArgumentExpressionAttribute.cs" Link="Polyfills\CallerArgumentExpressionAttribute.cs"/>
  <Compile Include="$(MSBuildThisFileDirectory)Polyfills\NotNullAttribute.cs" Link="Polyfills\NotNullAttribute.cs"/>
</ItemGroup>

<!-- net8.0 has every other shim natively but lacks OverloadResolutionPriority (added in net9.0).
     Only Refit.HttpClientFactory uses the attribute; compiling the shim into consumers that already
     reference Refit would create a duplicate compiler-recognized type. -->
<ItemGroup Condition="'$(TargetFramework)' == 'net8.0' and '$(MSBuildProjectName)' == 'Refit.HttpClientFactory'">
  <Compile Include="$(MSBuildThisFileDirectory)Polyfills\OverloadResolutionPriorityAttribute.cs" Link="Polyfills\OverloadResolutionPriorityAttribute.cs"/>
</ItemGroup>
```

**Detect the TFM to condition on with the same tools `dotnet-multi-targeting` recommends for
anything else** — `$(TargetFramework.StartsWith('net4'))` for the whole net4x family, exact string
equality for a single TFM, `$([MSBuild]::IsTargetFrameworkCompatible(...))` when the real question is
a version floor rather than an exact TFM. Condition at the granularity the gap actually has: a whole
folder for "everything missing pre-net5", a single file for "one attribute missing until net9.0".
Roll your own file-by-file like this rather than reaching for an all-or-nothing package — it's the
only way to get Refit's single-file, single-project net8.0 precision above.

### API polyfills vs attribute polyfills

Two different shapes solve two different gaps:

- **Attribute/marker-type polyfills** (`IsExternalInit`, `RequiredMemberAttribute`,
  `CallerArgumentExpressionAttribute`) are what the compiler looks up by name to unlock *syntax* —
  usually empty or near-empty types, as above.
- **API polyfills** fill a missing *method*, using ordinary extension methods, C# 14 `extension`
  blocks, or a static helper class with the same name/signature as the modern API. punchclock's
  `Polyfills/TaskPolyfillExtensions.cs` adds `Task.WaitAsync(TimeSpan, CancellationToken)` on
  pre-net6.0 targets as an `extension(Task task) { ... }` block, guarded the same way:

  ```csharp
  #if !NET6_0_OR_GREATER
  namespace System.Threading.Tasks;

  internal static class TaskPolyfillExtensions
  {
      extension(Task task)
      {
          internal async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) { /* ... */ }
      }
  }
  #endif
  ```

  Splat's/Refit's/Primitives'/punchclock's `ArgumentExceptionHelper`/`ArgumentOutOfRangeExceptionHelper`
  (the `ThrowIf*` guard-clause polyfill — see `csharp-guard-clauses`) is the same idea as a plain
  static class instead of an extension block: a hand-written `ThrowIfNull`/`ThrowIfNullOrWhiteSpace`
  with the same name and parameter shape as the .NET 6+ BCL helper, so callers don't know or care
  which one they got.

### The `Using Alias` seam: same call site, different binding per TFM

An attribute/marker polyfill only needs to *exist*; an API polyfill's call sites need to *resolve*
to either the polyfill or the real BCL member depending on TFM, without `#if` at every call. Alias
one neutral name to the real type on modern TFMs and to the polyfill type on old ones — full
mechanism and pitfalls (props-vs-targets timing, cross-targeting-only guarantee) are in
`dotnet-msbuild-usings`; the shape used for guard clauses across these repos:

```xml
<ItemGroup Condition="$([MSBuild]::IsTargetFrameworkCompatible('$(TargetFramework)', 'net8.0'))">
  <Using Include="System.ArgumentNullException" Alias="ArgumentExceptionHelper" />
</ItemGroup>
<ItemGroup Condition="!$([MSBuild]::IsTargetFrameworkCompatible('$(TargetFramework)', 'net8.0'))">
  <Using Include="ReactiveUI.Helpers.ArgumentValidation" Alias="ArgumentExceptionHelper" />
</ItemGroup>
```

`ArgumentExceptionHelper.ThrowIfNull(x)` reads identically everywhere; on `net8.0`+ it's the
runtime's `ArgumentNullException.ThrowIfNull`, on older TFMs it's the hand-written polyfill. Refit
does the same for `Lock`-style aliasing across `net4x`/`netstandard2.0` vs modern TFMs. See
`dotnet-multi-targeting`'s "Polyfills for older TFMs" section for how this generalizes past guard
clauses to any API gap.

## Hand-written vs PolySharp (or another source-generated package)

Both approaches produce the same marker types; the difference is who owns the file.

| | Hand-written `Polyfills/` folder | PolySharp / similar source-generated package |
| --- | --- | --- |
| What ships | Files you write, review, and own in your own repo, under your own namespace conventions | A source generator that emits the same marker types at compile time, into `obj/`/`Generated/` |
| Precision | File-by-file, TFM-by-TFM, project-by-project — exactly the gap you have, nothing more (Refit's single-file net8.0 case) | Whatever the package's generator decides to emit for the detected TFM; less control over exactly which types land where |
| Auditability | Every type is a `.cs` file in your diff history, reviewed like any other change | Generated output isn't part of your source tree unless you deliberately check it in (as ReactiveUI.SourceGenerators does, committing the `.g.cs` files under `Generated/PolySharp.SourceGenerators/...`) |
| Cost | You maintain it; a new language feature needing a new marker type means writing one more file | `PrivateAssets="all"` PackageReference, zero maintenance until the package stops supporting a feature you need |
| Used by | ReactiveUI, Splat, Refit, punchclock, Primitives, Akavache, Fusillade, ReactiveUI.Validation (all hand-written) | ReactiveUI.SourceGenerators (`Directory.Packages.props`: `PackageVersion Include="PolySharp"`) |

The libraries in this set default to **hand-written** — most of the shipping ReactiveUI-family
libraries copy small, audited polyfill files (several credit SimonCropp's
[Polyfill](https://github.com/SimonCropp/Polyfill) source-only package as the origin of the
implementation, without taking it as a dependency) rather than pull in a generator dependency. Prefer
this when: you already know exactly which marker types you need (the tier-2 list above is short and
stable), you want the polyfill surface visible in code review and `git blame`, or — as with
ReactiveUI's Core/leaf IVT-free split — you need per-assembly control that a one-size package can't
give you.

Reach for a **source-generated package** (PolySharp, or similar — Meziantou.Polyfill is another) when
a project's own team doesn't want to own polyfill maintenance, or when the TFM matrix is large enough
that hand-auditing every marker type per leg is real ongoing cost. Reference it
`PrivateAssets="all"` so it never flows to consumers:

```xml
<ItemGroup>
  <PackageReference Include="PolySharp" Version="1.16.0" PrivateAssets="all" />
</ItemGroup>
```

If you want the generated output visible and reviewable rather than a black box in `obj/`,
ReactiveUI.SourceGenerators checks the emitted files into `Generated/PolySharp.SourceGenerators/...`
and includes them explicitly via `<Compile Include>` — treat that as a middle ground, not the
default; most projects just reference the package and let it run silently. Either way, don't mix a
hand-written polyfill and PolySharp's generated version of the *same* type in one project — that's
the duplicate-type conflict again, this time between your own file and generated output.

## Source generators/analyzers: the most common place this bites

A Roslyn analyzer/source generator project is forced to `netstandard2.0` regardless of what TFM its
consumers target, because it runs inside the compiler host (`csc`/the IDE's Roslyn process), whose
TFM you don't control. That means every generator project needing `record`/`init`/`required` for its
pipeline models (see `csharp-source-generators`'s value-equatable-models section) needs this exact
polyfill set — `IsExternalInit` alone for positional records, plus `RequiredMemberAttribute`/
`CompilerFeatureRequiredAttribute`/`SetsRequiredMembersAttribute` for `required` members. Refit's
`InterfaceStubGenerator.Shared/Polyfills` (an `Index.cs`/`Range.cs` pair) is the same story for a
generator that only needs `^`/`..` indexing, not full record support. See `csharp-source-generators`
for the packaging requirements (`netstandard2.0`, `IsRoslynComponent`, `EnforceExtendedAnalyzerRules`)
that go alongside the polyfill.

## Checklist

- [ ] `LangVersion` set deliberately (`latest`, or a pinned number) — not left to drift with the SDK
      installed on a given machine (`csharp-language-versions`)
- [ ] Every C# 12-15 feature actually used is classified into a tier: compiles as-is, needs a named
      polyfill type, or is impossible on the project's oldest TFM
- [ ] Polyfills exist only for tier-2 (compiler-lookup) types — never attempted for tier-3
      (default interface members, static abstract members, `ref struct` generics, unions)
- [ ] Every polyfill type is `internal`, guarded with `#if !NET`/`#if !NETx_y_OR_GREATER` so it
      compiles only where the real type is missing
- [ ] `Compile Include` for the `Polyfills/` folder (or individual files) is conditioned on the
      exact TFM(s)/project(s) that lack the type — not included unconditionally
- [ ] No IVT-shared single copy of a polyfill across assemblies that don't need to share one —
      each assembly compiles its own internal copy to avoid `CS0436`
- [ ] API-gap polyfills (not just markers) use the same name/signature as the modern BCL method, so
      an alias seam (`dotnet-msbuild-usings`) can swap bindings without touching call sites
- [ ] Hand-written vs PolySharp/generated chosen deliberately, not mixed for the same type in one
      project
- [ ] Multi-targeted project builds every TFM, lowest first (`dotnet build -f netstandard2.0`
      before assuming the modern leg's success means anything about the old one) — see
      `dotnet-multi-targeting`'s "Building and testing every TFM"
