---
name: dotnet-multi-targeting
description: Use when a library needs to target more than one TFM (netstandard2.0, multiple net8.0/9.0/10.0-style TFMs, or platform TFMs like -android/-ios/-maccatalyst/-windows10.0.x) — choosing the TFM list, laying out platform-specific source so it stays visible in the IDE but compiles per TFM, detecting TFM/platform in MSBuild conditions, polyfilling APIs missing on older targets, and compiling net*-windows/net4x TFMs from a Linux or macOS host with `EnableWindowsTargeting`.
---

# .NET multi-targeting

Multi-targeting is a tax: every extra `TargetFrameworks` entry multiplies build time, test matrix, and the surface for `#if`/folder mistakes. Pay it only for TFMs you actually ship. When you do pay it, keep platform-specific source in folders MSBuild switches per TFM — never scatter large platform-specific blocks behind `#if`.

## Choosing the TFM list

A typical cross-platform library targets:

- **`netstandard2.0`** — only if you still support .NET Framework consumers or old Xamarin. Drop it the moment nothing needs it; it's the TFM with the thinnest BCL and the most polyfill debt (see `csharp-language-versions`).
- **The current LTS/STS `net*.0`** targets you actually support (e.g. `net8.0;net9.0;net10.0`) — not every TFM since the dawn of .NET Core, and not a speculative future one.
- **Platform TFMs** where you ship platform-specific code: `net10.0-android`, `net10.0-ios`, `net10.0-maccatalyst`, `net10.0-windows10.0.19041.0`. Each platform TFM needs its workload installed to build (`android`/`ios`/`maccatalyst` need the mobile workloads; `-windows10.0.x` needs `EnableWindowsTargeting=true` to cross-compile from Linux/macOS — see below).

Don't add `<TargetFrameworks>` speculatively (see `dotnet-project-hygiene`). Every leg is a real build/test cost.

### Conditional TFMs per host OS

Build platform TFMs only where the host can actually build them, by composing the list in a shared property instead of hardcoding it in every `.csproj`. ReactiveUI's `Directory.Build.props` does this with intermediate properties and OS conditions:

```xml
<PropertyGroup>
  <MyLibCoreTargets>net8.0;net9.0;net10.0</MyLibCoreTargets>
  <MyLibAppleTargets>net10.0-ios;net10.0-maccatalyst;net10.0-macos</MyLibAppleTargets>
  <MyLibWindowsTargets>net10.0-windows10.0.19041.0</MyLibWindowsTargets>

  <MyLibFinalTargets>$(MyLibCoreTargets)</MyLibFinalTargets>
  <!-- Windows TFMs build (reference-only) anywhere EnableWindowsTargeting is set, so add them everywhere. -->
  <MyLibFinalTargets>$(MyLibFinalTargets);$(MyLibWindowsTargets)</MyLibFinalTargets>
  <!-- Apple TFMs need Xcode/mobile workloads: only add on macOS or Windows, never bare Linux CI. -->
  <MyLibFinalTargets Condition="$([MSBuild]::IsOsPlatform('Windows')) or $([MSBuild]::IsOsPlatform('OSX'))">$(MyLibFinalTargets);$(MyLibAppleTargets)</MyLibFinalTargets>
</PropertyGroup>
```

Each `.csproj` then sets `<TargetFrameworks>$(MyLibFinalTargets)</TargetFrameworks>`. A host that can't build a platform TFM gets a narrower list automatically — **never delete a platform TFM from the list because one machine can't build it**; condition it out for that host instead, so CI (which has the workload) still builds the full matrix.

`$([MSBuild]::IsOsPlatform('Windows'))` / `'OSX'` / `'Linux'` are the functions to branch on host OS; don't parse `$(OS)` by hand.

### `TargetPlatformMinVersion` / `SupportedOSPlatformVersion`

Both are set per platform TFM, conditioned on the platform identifier, not string-matched on the whole TFM:

```xml
<SupportedOSPlatformVersion Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'ios'">15.0</SupportedOSPlatformVersion>
<SupportedOSPlatformVersion Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'android'">35.0</SupportedOSPlatformVersion>
<SupportedOSPlatformVersion Condition="$(TargetFramework.EndsWith('-windows10.0.19041.0'))">10.0.19041.0</SupportedOSPlatformVersion>
<TargetPlatformMinVersion Condition="$(TargetFramework.EndsWith('-windows10.0.19041.0'))">10.0.19041.0</TargetPlatformMinVersion>
```

- `SupportedOSPlatformVersion` is the floor your code is annotated/analyzed against (drives `CA1416` platform-compat warnings).
- `TargetPlatformMinVersion` is the floor the *compiled output* refuses to run below — set it separately; it does not default from `SupportedOSPlatformVersion`.

## The platform-folder pattern

For anything beyond a few divergent lines, put platform-specific source in folders and let MSBuild pick the folder per TFM — not `#if` sprinkled through shared files. A folder is diffable, lets each platform's code use its own idioms/nullability freely, and keeps `git blame`/reviews sane; a giant `#if ANDROID ... #elif IOS ...` block interleaved with shared logic does none of that.

The trick: exclude platform folders from `Compile` for everyone, re-add them as `None` (so every file is still visible in Solution Explorer / the IDE and searchable), then re-include the right subfolder as `Compile` per TFM condition. This is exactly what ReactiveUI's `ReactiveUI.csproj` does:

```xml
<ItemGroup>
  <Compile Remove="Platforms\**\*.cs" />
  <None Include="Platforms\**\*.cs" />
</ItemGroup>

<ItemGroup Condition="$(TargetFramework.StartsWith('netstandard'))">
  <Compile Include="Platforms\netstandard2.0\**\*.cs" />
</ItemGroup>

<ItemGroup Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'ios' or $([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'maccatalyst'">
  <Compile Include="Platforms\apple-common\**\*.cs" />
  <Compile Include="Platforms\ios\**\*.cs" />
  <Compile Include="Platforms\uikit-common\**\*.cs" />
</ItemGroup>
<ItemGroup Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'tvos'">
  <Compile Include="Platforms\apple-common\**\*.cs" />
  <Compile Include="Platforms\tvos\**\*.cs" />
  <Compile Include="Platforms\uikit-common\**\*.cs" />
</ItemGroup>
```

Points worth copying:

- **`Compile Remove` then `None Include`, both unconditional**, run first so nothing is compiled by default and nothing disappears from the project view.
- **Per-TFM `ItemGroup`s re-add just that TFM's folders as `Compile`.** iOS and tvOS both pull in `apple-common` and `uikit-common` — shared "common" folders let related platforms reuse code without a folder-per-symbol-combination explosion. ReactiveUI's actual layout: `android`, `apple-common`, `ios`, `mac`, `net`, `netstandard2.0`, `tvos`, `uikit-common`, each included by exactly the TFMs that need it.
- **Detect the platform with `$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)'))`**, not by string-matching the raw TFM — it normalizes `net10.0-ios` and a future `net11.0-ios` to the same `'ios'` check without editing the condition when a new TFM year lands.
- For a family of plain (non-platform) TFMs sharing one folder, `$(TargetFramework.StartsWith('net4'))` or an explicit OR-list of exact TFM strings is fine — there's no identifier function for "any net4x" the way there is for platform identifiers.

**When `#if` is still right:** a handful of divergent lines inside an otherwise-shared file (a single API call that differs, a type that needs `NET8_0_OR_GREATER` vs not). Once a platform's code grows past "a few lines you can hold in your head next to the shared logic," move it to a folder. Don't let a shared file accumulate five platforms' worth of `#if ANDROID` / `#elif IOS` / `#elif TVOS` blocks — that's the folder pattern's job.

## TFM detection reference

| Need | Use |
| --- | --- |
| Which platform (android/ios/windows/...) | `$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)'))` |
| "Is this TFM at least net9.0-equivalent" (works across platform suffixes) | `$([MSBuild]::IsTargetFrameworkCompatible('$(TargetFramework)', 'net9.0'))` |
| Raw TFM identifier (`.NETStandard`, `.NETCoreApp`, ...) | `$(TargetFrameworkIdentifier)` |
| Cheap prefix check on the TFM string | `$(TargetFramework.StartsWith('net8.0'))` / `.EndsWith(...)` |

Prefer `IsTargetFrameworkCompatible` over `StartsWith('net9.0') or StartsWith('net10.0') or ...` when the real question is a version floor — it also matches platform TFMs (`net10.0-ios` is compatible with `net9.0`) and doesn't need updating every time a new TFM ships. Reach for `StartsWith`/`EndsWith` only for structural checks that aren't really about a version floor (TFM family, a specific Windows SDK suffix like `-windows10.0.19041.0`).

`GetTargetPlatformIdentifier` returns an empty string for non-platform TFMs (`net10.0`, `netstandard2.0`) — safe to compare against `'android'`/`'ios'`/etc. without a null check.

## TFM-conditional package versions

Under central package management (`dotnet-project-hygiene`), a package that ships different major versions per TFM family still gets one `PackageVersion` entry — condition an intermediate property, then reference that property:

```xml
<PropertyGroup>
  <MauiVersion Condition="$(TargetFramework.StartsWith('net9'))">9.0.90</MauiVersion>
  <MauiVersion Condition="$(TargetFramework.StartsWith('net10'))">10.0.110</MauiVersion>
</PropertyGroup>

<ItemGroup>
  <PackageVersion Include="Microsoft.Maui.Controls" Version="$(MauiVersion)" />
</ItemGroup>
```

`Directory.Packages.props` conditions are evaluated per inner build (per TFM), so `$(TargetFramework)` is already set when these `Condition`s run — this is the one place a `PackageVersion` is allowed to vary per project without `VersionOverride`, because the variance tracks the TFM, not the individual project.

## Polyfills for older TFMs

`netstandard2.0` (and `net4x`) are missing BCL types and attributes that newer code assumes exist. Two approaches, often combined:

- **Shared polyfill source compiled only into the old TFM(s).** A `Polyfills/` folder with small marker types (`IsExternalInit`, `RequiredMemberAttribute`, `Index`/`Range`, nullable attributes), each guarded with `#if !NET` so it's a no-op on modern TFMs that already ship the real type, then included only for the TFMs that need it:

  ```xml
  <ItemGroup Condition="$(TargetFramework.StartsWith('net4'))">
    <Compile Include="$(MSBuildThisFileDirectory)Polyfills\*.cs" Link="Polyfills\%(Filename)%(Extension)" />
  </ItemGroup>
  ```

- **PolySharp-style packages** for the same job when you don't want to own the shim source — a source-generator package that injects the same marker types at compile time, conditioned per TFM via `Directory.Packages.props` (see `dotnet-project-hygiene` for central package management).
- **Conditional package references** for real functionality gaps, not just marker attributes — e.g. `System.Text.Json`, `System.Memory`, or `System.ComponentModel.Annotations` only where the TFM doesn't ship them in-box:

  ```xml
  <ItemGroup Condition="$(TargetFramework.StartsWith('netstandard')) or $(TargetFramework.StartsWith('net4'))">
    <PackageReference Include="System.ComponentModel.Annotations" />
  </ItemGroup>
  ```

- **`#if NET*_OR_GREATER`** for API gaps that are a few lines, not a whole type — call the modern API when available, fall back otherwise. See `csharp-language-versions` for which C# language features need a polyfilled runtime type versus compiling on `LangVersion` alone, and verify polyfill coverage against your *lowest* TFM.

## The `Using Alias` trick: one call site, TFM-specific binding

`Using` items with `Alias` let call sites stay byte-identical across TFMs while the bound symbol changes underneath. The general mechanism is covered in `dotnet-msbuild-usings`; the pattern worth knowing here is the `Lock` alias:

```xml
<!-- Alias Lock to the dedicated System.Threading.Lock on .NET 9+ (faster EnterScope fast path),
     and to a plain object elsewhere (the lock statement falls back to Monitor). -->
<ItemGroup Condition="$([MSBuild]::IsTargetFrameworkCompatible('$(TargetFramework)', 'net9.0'))">
  <Using Include="System.Threading.Lock" Alias="Lock" />
</ItemGroup>
<ItemGroup Condition="!$([MSBuild]::IsTargetFrameworkCompatible('$(TargetFramework)', 'net9.0'))">
  <Using Include="System.Object" Alias="Lock" />
</ItemGroup>
```

Every project field can then say `private readonly Lock _gate = new();` and `lock (_gate) { ... }` — on `net9.0`+ the compiler binds `Lock` to the real `System.Threading.Lock` and the C# 13+ compiler pattern-matches the `lock` statement onto its fast `EnterScope` path; on older TFMs `Lock` is just `object` and `lock` falls back to `Monitor`. No `#if` at any call site.

Where the condition lives matters. In `Directory.Build.props` it only works for projects using `<TargetFrameworks>` (plural): each inner build receives `TargetFramework` as a global property before the props are imported. A single-`<TargetFramework>` project sets it after `Directory.Build.props` is imported, so the condition sees an empty value and never matches. Put TFM-conditioned `Using` items in `Directory.Build.targets` if any project uses the singular form.

The same trick covers a helper method that has a fast framework-provided equivalent on newer TFMs and a hand-written fallback on older ones:

```xml
<ItemGroup Condition="$([MSBuild]::IsTargetFrameworkCompatible('$(TargetFramework)', 'net8.0'))">
  <Using Include="System.ArgumentNullException" Alias="ArgumentExceptionHelper" />
</ItemGroup>
<ItemGroup Condition="!$([MSBuild]::IsTargetFrameworkCompatible('$(TargetFramework)', 'net8.0'))">
  <Using Include="MyLib.Helpers.ArgumentValidation" Alias="ArgumentExceptionHelper" />
</ItemGroup>
```

`ArgumentExceptionHelper.ThrowIfNull(x)` resolves to the runtime's optimized `ArgumentNullException.ThrowIfNull` on `net8.0`+, and to a hand-rolled polyfill type elsewhere — same call site, TFM-appropriate binding, and the fallback type only needs to exist (and only gets compiled) on the older TFMs.

## Building and testing every TFM

- `dotnet build`/`dotnet test` on the solution builds every TFM of every multi-targeted project in one pass — no per-TFM loop needed for a normal CI run. Use `dotnet build -f <tfm>` or `dotnet test -f <tfm>` to isolate one leg while debugging a single-TFM failure.
- `-getItem:Compile -f <tfm>` (as used above) is the fastest way to confirm which files actually feed a given TFM's compile — cheaper than a full build when you're only checking the folder-inclusion wiring.
- **Platform TFMs need their workload installed** (`dotnet workload install android`/`ios`/`maccatalyst`/`maui`) or the SDK errors trying to resolve the platform's reference assemblies. `-windows10.0.x` TFMs build (reference-only, no app can run) on non-Windows once `<EnableWindowsTargeting>true</EnableWindowsTargeting>` is set — they still won't produce a runnable app off Windows, but the library compiles and analyzers run.
- **A host that can't build a platform TFM** (no workload, wrong OS): condition that TFM out of the list for that host (see "Conditional TFMs per host OS" above) so the rest of the matrix still builds — never delete the TFM from the project because your current machine can't build it. CI, which has the workloads, is the source of truth for the full matrix.

## Building Windows targets on Linux/macOS (`EnableWindowsTargeting`)

A `net*-windows` (WPF, WinForms, WinUI/Windows App SDK) or `net4x` TFM is not "unbuildable off Windows" — **compiling** it is a normal cross-target build once `EnableWindowsTargeting` is set. Only **running** the output (launching the assembly, executing its tests) needs an actual Windows host. Don't drop a Windows TFM from `TargetFrameworks`, and don't tell a reader a Windows-targeted project "can't be built here," just because the current host is Linux/macOS — build it; gate *running* it instead.

```xml
<PropertyGroup>
  <!-- No-op on Windows; lets net*-windows TFMs compile (reference-only) on Linux/macOS too. -->
  <EnableWindowsTargeting>true</EnableWindowsTargeting>
</PropertyGroup>
```

Set it once, unconditionally, in the repo's `Directory.Build.props` — it's a no-op on a Windows host, so there's no reason to condition it on OS. This is exactly what ReactiveUI's `src/Directory.Build.props` does.

On Linux:

- A `net10.0-windows` WPF/WinForms project **without** `EnableWindowsTargeting` fails restore/build with:
  ```
  error NETSDK1100: To build a project targeting Windows on this operating system, set the EnableWindowsTargeting property to true.
  ```
- The same project **with** `<EnableWindowsTargeting>true</EnableWindowsTargeting>` builds cleanly on Linux and produces `bin/.../net10.0-windows/*.dll` — no Windows workload, no MSBuild extras, nothing else needed.
- A library multi-targeting `net10.0;net10.0-windows;net472` builds all three TFMs on Linux in one `dotnet build`. `net472` needed no explicit package reference for reference assemblies — the modern SDK restores `Microsoft.NETFramework.ReferenceAssemblies` implicitly (visible in `obj/project.assets.json`); don't add it by hand unless pinning a specific version.
- Trying to run the compiled `net*-windows` output on Linux fails — a class library with no `net*-windows` runnable host has no `Microsoft.WindowsDesktop.App` shared framework available off Windows, so `dotnet exec`/`dotnet run`/`dotnet test` cannot launch it there. This is why **test** projects need Windows TFMs gated to a Windows host, not just built:

  ```xml
  <!-- Windows-specific TFMs only on Windows: off-Windows these test projects fall back to the plain
       net TFMs so they run on the Linux/macOS CI legs. Building them as net*-windows off-Windows would
       produce assemblies the runner cannot launch (no Microsoft.WindowsDesktop.App). -->
  <MyLibTestTargets>net8.0;net9.0;net10.0</MyLibTestTargets>
  <MyLibTestTargets Condition="$([MSBuild]::IsOsPlatform('Windows'))">$(MyLibTestTargets);net10.0-windows10.0.19041.0</MyLibTestTargets>
  ```

  See `dotnet-test-platforms` for running the matrix; `csharp-verification` for what counts as a verified build vs. a skipped one.

What works off-Windows: plain compilation of WPF/WinForms/`net4x` class libraries and apps (`UseWPF`/`UseWindowsForms`), including multi-targeted projects that mix Windows and non-Windows TFMs. **Check current Microsoft docs before relying on** the following — they need a Windows host or Windows-only tooling:

- WinUI 3 / Windows App SDK projects that need MSIX packaging, or any step that invokes the Windows App SDK's packaging/deployment tooling.
- XAML compiler tasks and designer-time builds that shell out to Windows-only components.
- COM references (`<COMReference>`) — these resolve via the Windows type library importer, which needs Windows.
- Code-signing steps and any `Publish`/`ClickOnce` pipeline that assumes a Windows toolchain.

Treat plain compile (`dotnet build`) as the thing `EnableWindowsTargeting` reliably buys you off-Windows; treat packaging/signing/COM/designer tooling as needing verification against the actual doc/tooling before assuming it works.

CI pattern: Linux legs run the full `dotnet build`/`dotnet restore` across every TFM including the Windows ones (catches compile errors early, on the cheaper/faster runners); a Windows leg is reserved for `dotnet test`/`dotnet run` of the Windows-TFM legs and anything above that genuinely needs Windows.

## Checklist

- [ ] TFM list matches what's actually shipped/supported, not a speculative superset (`dotnet-project-hygiene`)
- [ ] Platform TFMs added only on hosts that can build them, via a conditioned shared property, never deleted for a host that can't
- [ ] `SupportedOSPlatformVersion`/`TargetPlatformMinVersion` set per platform, conditioned on `GetTargetPlatformIdentifier`, not by string-matching the whole TFM
- [ ] Large platform-specific code lives in a `Platforms/<name>/` folder switched by TFM condition (`Compile Remove` + `None Include` + per-TFM `Compile Include`), not scattered `#if`
- [ ] Platform/version checks use `GetTargetPlatformIdentifier`/`IsTargetFrameworkCompatible` over ad hoc `StartsWith` chains where the question is really a version floor
- [ ] Polyfills for older TFMs verified against the *lowest* TFM, not assumed from the highest (`csharp-language-versions`)
- [ ] Any `Using Alias` TFM-conditional bindings documented at the alias site and cross-referenced to `dotnet-msbuild-usings`
- [ ] Whole-solution `dotnet build`/`dotnet test` covers every TFM in CI; no TFM silently untested
- [ ] AOT/trim settings checked per TFM where relevant — see `csharp-aot-trimming`
- [ ] Solution format and other build plumbing follow `dotnet-project-hygiene`/`dotnet-slnx`
- [ ] `net*-windows`/`net4x` TFMs built (not dropped) on Linux/macOS via `EnableWindowsTargeting`; only running/testing them is gated to a Windows host
