---
name: dotnet-msbuild-usings
description: Use when adding or auditing global usings, using aliases, or static usings in a .NET repo — declaring them as MSBuild `<Using>` items in a `.csproj` or `Directory.Build.props`/`.targets` instead of scattered `GlobalUsings.cs` files, conditioning them per `TargetFramework`, and using an alias as a per-TFM polyfill so one source spelling compiles everywhere.
---

# MSBuild `<Using>` items

A `global using X;` line in a hand-written `GlobalUsings.cs` file and a `<Using Include="X" />` MSBuild item do the same thing, but the MSBuild item is declared once in build plumbing instead of copy-pasted into every project, and it can be conditioned on `TargetFramework`, `Configuration`, or any other MSBuild property. Prefer the MSBuild item for anything shared across projects; keep a hand-written `global using` only for a using that is genuinely local to one project and not worth a repo-wide rule.

## The basics

```xml
<ItemGroup>
  <Using Include="System.Text" />
  <Using Include="System.Console" Static="true" />
  <Using Include="System.Text.RegularExpressions.Regex" Alias="Rx" />
  <Using Remove="System.Threading.Tasks" />
</ItemGroup>
```

- `Include` adds a `global using Namespace.Or.Type;`.
- `Static="true"` adds `global using static Namespace.Type;` (members become callable unqualified — `WriteLine(...)` instead of `Console.WriteLine(...)`).
- `Alias="X"` adds `global using X = Namespace.Or.Type;` — works for a namespace or a type.
- `Remove` drops a using the SDK would otherwise add implicitly (see below) — for example, a library that deliberately doesn't use `System.Threading.Tasks` unqualified.

The SDK writes these into a generated `obj/**/<Project>.GlobalUsings.g.cs` (verified with SDK `11.0.100-rc.1`, .NET 10 SDK `10.0.401`) — read that file when you need to see exactly what a project's implicit usings resolved to, rather than guessing from the `.csproj`.

## `ImplicitUsings`

`<ImplicitUsings>enable</ImplicitUsings>` adds a fixed, SDK-specific set of `global using` lines on top of anything you declare yourself. The set differs by SDK:

- **`Microsoft.NET.Sdk`** (console/library): `System`, `System.Collections.Generic`, `System.IO`, `System.Linq`, `System.Net.Http`, `System.Threading`, `System.Threading.Tasks`.
- **`Microsoft.NET.Sdk.Web`**: the above plus `Microsoft.AspNetCore.Builder`, `Microsoft.AspNetCore.Hosting`, `Microsoft.AspNetCore.Http`, `Microsoft.AspNetCore.Routing`, `Microsoft.Extensions.Configuration`, `Microsoft.Extensions.DependencyInjection`, `Microsoft.Extensions.Hosting`, `Microsoft.Extensions.Logging`, and `System.Net.Http.Json`.
- Other SDKs (Worker, Blazor WebAssembly, etc.) add their own set on the same mechanism.

`ImplicitUsings` is one property, on or off — you can't cherry-pick individual implicit usings, only `Remove` the ones you don't want after the fact. See `dotnet-project-hygiene` for turning it on as part of new-project setup.

## Verifying what actually got declared

Don't guess from the XML — check the generated file and the effective property:

```bash
dotnet build -getProperty:ImplicitUsings -getProperty:TargetFramework
find obj -iname "*.GlobalUsings.g.cs"
cat obj/Debug/<tfm>/<Project>.GlobalUsings.g.cs
```

For a multi-targeted project each inner build writes its own copy under `obj/Debug/<tfm>/`, so a TFM-conditioned alias needs checking per TFM — a `Lock` alias that looks right in one folder can still be wrong in another.

## Where to declare shared usings

- **Truly per-project** (a using only one project needs): a `<Using>` item directly in that project's `.csproj`, or a plain `global using` in a `.cs` file if it's a single line nobody else needs to see.
- **Shared across every project in the repo**: `Directory.Build.props` — projects inherit it automatically (see `dotnet-project-hygiene` for the auto-import mechanism). This is the right place for house-style usings like `System.Collections.ObjectModel` or a project's own root namespace helpers.
- **Test-only namespaces** (TUnit, NUnit, xUnit): add them where the test infrastructure already scopes test settings — a `tests/Directory.Build.props`, or per test-project if only one framework's tests need them:

  ```xml
  <ItemGroup>
    <Using Include="TUnit.Core" />
    <Using Include="TUnit.Core.Executors" />
    <Using Include="TUnit.Assertions" />
    <Using Include="TUnit.Assertions.Extensions" />
  </ItemGroup>
  ```

  TUnit also ships `TUnitImplicitUsings`/`TUnitAssertionsImplicitUsings` MSBuild properties that add its own namespaces the same way `ImplicitUsings` does — set them once in a shared props file rather than per test project. See `csharp-tunit`.

A repo-root `Directory.Build.props` shared block typically looks like:

```xml
<Project>
  <PropertyGroup>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <Using Include="System.Collections.ObjectModel" />
    <!-- Platform namespace collides with our own Foundation-named type on Apple TFMs. -->
    <Using Remove="Foundation" Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'ios'" />
  </ItemGroup>
</Project>
```

`Using Remove` isn't limited to undoing `ImplicitUsings` — it also drops a platform- or SDK-implied global using that collides with an in-repo type of the same name (the Apple SDKs implicitly global-use `Foundation`, which collides with a type named `Foundation` in your own code).

## Conditioning `<Using>` on `TargetFramework` — and the props-vs-targets pitfall

Multi-targeted projects often need a different namespace, or a different alias target, per TFM:

```xml
<ItemGroup Condition="$([MSBuild]::IsTargetFrameworkCompatible('$(TargetFramework)', 'net9.0'))">
  <Using Include="System.Threading.Lock" Alias="Lock" />
</ItemGroup>
<ItemGroup Condition="!$([MSBuild]::IsTargetFrameworkCompatible('$(TargetFramework)', 'net9.0'))">
  <Using Include="System.Object" Alias="Lock" />
</ItemGroup>
```

`IsTargetFrameworkCompatible` is the right comparison (not string equality or `StartsWith`) — it correctly treats `net10.0` as compatible with a `net9.0` floor.

**This only works reliably in `Directory.Build.props` when the project cross-targets** (`<TargetFrameworks>`, plural). The .NET SDK's outer/inner build splits a multi-TFM project into a separate MSBuild invocation per TFM, and each inner invocation passes `TargetFramework` in as a *global property before evaluation starts* — so by the time `Directory.Build.props` is imported (early, before the SDK sets defaults), `$(TargetFramework)` is already populated for that inner build. Verified: a `<Using>` conditioned this way in `Directory.Build.props` for a project with `<TargetFrameworks>net8.0;net9.0;net10.0</TargetFrameworks>` correctly produces `Lock = System.Object` in the `net8.0` `GlobalUsings.g.cs` and `Lock = System.Threading.Lock` in `net9.0`'s.

**It silently breaks for a plain single-`<TargetFramework>` project.** There, `<TargetFramework>` is set by an ordinary `<PropertyGroup>` inside the project's own body — and `Directory.Build.props` is imported *before* that body runs. Verified: the same `Condition="'$(TargetFramework)' == 'net10.0'"` in `Directory.Build.props` evaluates against an empty value and never matches, while the identical condition in `Directory.Build.targets` (imported *after* the project body) sees `net10.0` correctly. Rule of thumb: a `TargetFramework`-conditioned `<Using>` (or any TFM-conditioned property) belongs in `Directory.Build.targets`, or must be written to also work for the cross-targeting case where `.props` timing happens to save you — don't rely on `Directory.Build.props` seeing `$(TargetFramework)` unless the repo exclusively cross-targets. See `dotnet-multi-targeting` for the outer/inner build split, TFM layout, and other cross-targeting gotchas this interacts with.

## Alias as a polyfill: one spelling, best type per TFM

The pattern above generalizes: alias one name to whichever type is best on a given TFM, so call sites never need `#if`. ReactiveUI does this for two names across its whole source tree:

- **`Lock`**: `System.Threading.Lock` on `net9.0`+ (the dedicated lock type — faster `EnterScope` path, and the C# 14 compiler pattern-matches a plain `lock` statement onto it automatically), `System.Object` otherwise (the `lock` statement falls back to `Monitor`, exactly like locking on any reference type always has).
- **`ArgumentExceptionHelper`**: aliased to `System.ArgumentNullException` on `net8.0`+ so `ArgumentExceptionHelper.ThrowIfNull(...)` resolves straight to the runtime's `ArgumentNullException.ThrowIfNull`, and to an in-repo `Namespace.Helpers.ArgumentValidation` polyfill type below that — same method name, same signature, hand-written body.

```xml
<ItemGroup Condition="$([MSBuild]::IsTargetFrameworkCompatible('$(TargetFramework)', 'net8.0'))">
  <Using Include="System.ArgumentNullException" Alias="ArgumentExceptionHelper" />
</ItemGroup>
<ItemGroup Condition="!$([MSBuild]::IsTargetFrameworkCompatible('$(TargetFramework)', 'net8.0'))">
  <Using Include="MyLib.Helpers.ArgumentValidation" Alias="ArgumentExceptionHelper" />
</ItemGroup>
```

Limits on the technique:

- **The aliased types must be source-compatible for every member the code actually calls.** An alias doesn't unify APIs — it only works if both sides of the condition expose the same members with the same signatures for what you use. `Lock` works because code only ever does `new()` and passes the instance to `lock`; it would break the moment code called a `Lock`-only member (`EnterScope`) that `object` doesn't have.
  - `lock (_lock) { }` is safe either way, but the *semantics* differ: locking a `Lock` uses the dedicated fast-path API; locking a plain `object` compiles to `Monitor.Enter`/`Exit` the same as it always has. Don't rely on `Lock`-specific guarantees (like it throwing if used with `Monitor` APIs directly) when the alias might resolve to `object`.
- **Aliases can't be generic-open before C# 12.** `using MyAlias = List<>;` was never legal at any version — you always alias a closed constructed type (`using IntList = List<int>;`) or a non-generic type/namespace. C# 12 widened what *kinds* of things you can alias in the first place (see `csharp-language-versions` — "alias any type"): tuples (`using Point = (int X, int Y);`) and arrays (`using IntArray = int[];`) are now legal alias targets; verified both compile and run against `net10.0`/C# 14. This is orthogonal to the open-generic restriction, which is unchanged.
- **This is a source-level trick, not a runtime abstraction.** Consumers outside the project (reflection, serialization by exact type name) still see the real, per-TFM type — an alias never appears in metadata.

## When not to reach for global usings

- **A pile of global usings hides what a file actually depends on.** Reading one `.cs` file no longer tells you its dependencies; you have to also check `Directory.Build.props`. Keep the global set to genuinely pervasive, low-risk namespaces (BCL collections, your own primitives) — don't globally import a library namespace that's only used in a handful of files.
- **Name collisions get harder to diagnose.** Two global-used namespaces that both define a `Lock`, `Signal`, or `Builder` type produce an ambiguity error (`CS0104`) at every call site that names it unqualified, with no `using` line in the file to point at as the cause. This is exactly why ReactiveUI's alias-seam pattern is deliberate and narrow — it aliases specific type names (`Lock`, `RxVoid`, `ISequencer`) one at a time, condition-gated so only one definition is ever active per TFM, rather than globally importing both competing namespaces unconditionally.
- **Analyzers/IDE still flag unused usings per-file** (IDE0005) even though the `using` line itself lives in build plumbing rather than the file — an unnecessary `<Using>` item doesn't hide from `EnforceCodeStyleInBuild`, it just moves where the fix has to happen (removing the item, not editing every file).
- Don't add a project-wide `<Using>` as a shortcut to avoid typing a qualifier in one place that needed it once — that's what a local `using` in the file is for.

## Aliases as a migration seam: one source tree, two library bindings

The polyfill trick above generalizes past TFMs: alias/import a set of *neutral* names in a dedicated props file, then bind that whole set to one library on one leaf and a different library on another — a shared `.cs` file compiles unmodified against either. ReactiveUI uses this to ship both a lean (`ReactiveUI.Primitives`-only) and a `.Reactive` (`System.Reactive`-backed) build from one source tree, factored into `src/ReactiveShim.props`:

```xml
<Project>
  <!-- Keyed on project-name suffix: a project ending in 'Reactive' is on the System.Reactive side. -->
  <ItemGroup Condition="'$(RxReactiveSeam)' != 'true'">
    <Using Include="ReactiveUI.Primitives.RxVoid" Alias="RxVoid"/>
    <Using Include="ReactiveUI.Primitives.Concurrency" />   <!-- ISequencer, Sequencer, TaskPoolSequencer -->
    <Using Include="ReactiveUI.Primitives.Signals" />        <!-- Signal<T> is generic: import, don't alias -->
    <Using Include="ReactiveUI.Primitives.Disposables.MultipleDisposable" Alias="ActivationDisposables"/>
  </ItemGroup>

  <PropertyGroup Condition="'$(RxReactiveSeam)' == 'true'">
    <DefineConstants>$(DefineConstants);REACTIVE_SHIM</DefineConstants>
  </PropertyGroup>
  <ItemGroup Condition="'$(RxReactiveSeam)' == 'true'">
    <Using Include="System.Reactive.Unit" Alias="RxVoid"/>
    <Using Include="System.Reactive.Concurrency" />                                   <!-- IScheduler, Scheduler, TaskPoolScheduler -->
    <Using Include="System.Reactive.Concurrency.IScheduler" Alias="ISequencer"/>
    <Using Include="System.Reactive.Concurrency.Scheduler" Alias="Sequencer"/>
    <Using Include="System.Reactive.Concurrency.TaskPoolScheduler" Alias="TaskPoolSequencer"/>
    <Using Include="ReactiveUI.Primitives.Reactive.Disposables.ContainerDisposable" Alias="ActivationDisposables"/>
  </ItemGroup>
</Project>
```

`RxReactiveSeam` itself is a computed property (`Directory.Build.props`), not hand-set per project — `MSBuildProjectName.EndsWith('Reactive')` — so which leaf a project is on is derived from its name, not a manually maintained list. The shared source only ever writes `RxVoid`, `ISequencer`, `Sequencer`, `TaskPoolSequencer`, `Signal`, `ActivationDisposables`; it never sees `System.Reactive` or `ReactiveUI.Primitives` directly, and it needs `#if REACTIVE_SHIM` only at the handful of spots where the two bindings aren't quite behaviorally interchangeable (there: `ContainerDisposable` vs `MultipleDisposable`, because a `System.Reactive` consumer needs a real `CompositeDisposable` to call its fluent helpers on).

Verified with a two-project scratch build outside the repo: one `Shared.cs` (linked via `<Compile Include>` into both leaves) referencing unqualified `IEngine`, `Engine`, `Signal`, `Box<T>`; a shared `Seam.props` aliasing `IEngine`/`Engine`/`Signal` to `OldLib.*` for one leaf and to `NewLib.*` (plus `DefineConstants SHIM`) for a leaf whose project name ends in `New`, with `Box<T>` handled by `<Using Include="OldLib.Generics" />`/`NewLib.Generics` namespace imports since it's generic. Both leaves built and ran unmodified, each resolving `Worker.Describe()` against its own library's `Engine.Run()`.

Two situations this earns its keep in:

- **Incremental library adoption/upgrade.** Point the neutral names at the old library today; when the new library is ready, flip the binding for one leaf (or one `Condition`) at a time and build both configurations in CI until every call site has been proven against the new side, then delete the old branch and the seam file itself — the seam is scaffolding for the migration, not permanent architecture.
- **Dual-targeting two library flavours from one source set** (ReactiveUI's lean vs `.Reactive` leaves) without `#if` scattered through every call site — the seam file is the one place that says which binding applies, everywhere else just reads as normal code.

Limits, same as the TFM polyfill case, plus:

- **Generic types can't be aliased** — `Signal<T>`/`Box<T>` above go in as a namespace `<Using Include>` on each side instead of a per-type `Alias`, same as the TFM polyfill's open-generic restriction.
- **Both bindings must be source-compatible for what the shared code actually calls** — if the two libraries' methods diverge, isolate that one spot behind `#if <FLAG>` (as `ActivationDisposables`/`ContainerDisposable` does) rather than editing the neutral names' meaning.
- **A collision between the two namespaces is a real risk if you ever import both at once** — keep the conditions mutually exclusive (as `'$(RxReactiveSeam)' != 'true'` / `== 'true'` are), not additive.
- **Keep the seam in one dedicated, well-commented props file** (`ReactiveShim.props`, imported from `Directory.Build.props`) — mixing seam aliases into the general shared-usings block makes it hard to tell "this is migration scaffolding, remove me later" from "this is a permanent house convention."
- **CI must build (and run tests for) both leaves.** A seam only proves the shared source compiles against each binding if something actually builds both configurations on every change — a seam nobody builds on one side silently rots until the next real migration attempt.

## Checklist

- [ ] Shared usings live in `Directory.Build.props` (or a scoped `tests/Directory.Build.props`), not copy-pasted `GlobalUsings.cs` files per project
- [ ] `ImplicitUsings` left on unless the project deliberately wants to `Remove` specific implicit entries
- [ ] TFM-conditioned `<Using>` items use `$([MSBuild]::IsTargetFrameworkCompatible(...))`, and live in `Directory.Build.targets` (or are verified to work for the repo's actual cross-targeting shape) rather than assumed to work in `.props`
- [ ] Any alias-as-polyfill pair verified source-compatible for every member actually called, not just for compiling `new()`
- [ ] No alias or global `Using` introduced that creates an unqualified name collision with another globally imported namespace
- [ ] Global-used namespace list stays small enough that a reader can still tell a file's real dependencies at a glance
- [ ] A migration seam (dual-library or dual-binding aliases) lives in one dedicated, commented props file, is keyed off a computed property rather than a hand-maintained list, and CI builds every leaf it feeds
- [ ] A migration seam has a removal plan — it's scaffolding for the transition, not a permanent second way to spell the same thing
