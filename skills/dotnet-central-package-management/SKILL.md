---
name: dotnet-central-package-management
description: Use when setting up, migrating, or debugging NuGet Central Package Management (CPM) — Directory.Packages.props discovery/chaining, PackageVersion vs PackageReference, VersionOverride, GlobalPackageReference, transitive pinning, lock files, package source mapping, or diagnosing NU100x/NU1109/NU1507 restore errors. For the one-paragraph "what CPM is" summary see `dotnet-project-hygiene`; this skill is the detailed reference.
---

# .NET Central Package Management (CPM)

CPM moves every package version out of individual `.csproj` files and into one `Directory.Packages.props`. Every claim below was verified against NuGet's restore/pack output on the .NET 10 SDK (10.0.401) — not just read from docs.

## How discovery actually works

- MSBuild walks up from the project directory and imports the **nearest** `Directory.Packages.props` it finds. Only one is auto-imported per project.
- If a subtree has its own `Directory.Packages.props` (e.g. to override one package for a nested solution), the parent one is **not** merged in automatically — you get only the nested file's content.
- To layer a nested file on top of a parent one, import the parent explicitly and then `Update` the versions you need to change:

```xml
<Project>
  <Import Project="$([MSBuild]::GetPathOfFileAbove(Directory.Packages.props, $(MSBuildThisFileDirectory)..))" />
  <ItemGroup>
    <PackageVersion Update="Newtonsoft.Json" Version="12.0.1" />
  </ItemGroup>
</Project>
```

- This is a repo-topology decision, not a per-project one — a stray nested `Directory.Packages.props` silently shadows the root one for everything under it. If you didn't intend that, you'll get versions nobody can find by looking at the root file.

## Turning it on

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="Newtonsoft.Json" Version="13.0.1" />
  </ItemGroup>
</Project>
```

Project files reference packages **without** a `Version`:

```xml
<ItemGroup>
  <PackageReference Include="Newtonsoft.Json" />
</ItemGroup>
```

`dotnet new packagesprops` scaffolds the file. A project can opt out locally with `<ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>` in its own `.csproj`.

### What happens if you get it wrong

| You did | Error | Verified message |
| --- | --- | --- |
| Put `Version="x"` on a `<PackageReference>` | **NU1008** | "cannot define a value for Version ... Projects using Central Package Management must define a Version value on a PackageVersion item." |
| `<PackageReference>` with no matching `<PackageVersion>` | **NU1010** | "do not define a corresponding PackageVersion item ... must declare PackageReference and PackageVersion items with matching names." |
| `<PackageVersion>` for a package that's *implicitly* referenced by the SDK (e.g. `Microsoft.NETCore.App`) | **NU1009** | central version conflicts with the SDK-controlled implicit reference — remove the `PackageVersion` instead. |
| `<PackageVersion Version="13.0.*">` (floating) | **NU1011** | "PackageVersion items cannot specify a floating version" — floating is rejected outright, verified by restore failure. |
| Central version lower than what the dependency graph requires | **NU1109** | see Transitive pinning below. |

Note: metadata like `IncludeAssets`/`PrivateAssets`/`Aliases` still belongs on the `<PackageReference>` item — only `Version` moves to `Directory.Packages.props`.

## VersionOverride — the escape hatch

`VersionOverride` lets one project deviate from the central version:

```xml
<PackageReference Include="Newtonsoft.Json" VersionOverride="13.0.3" />
```

Verified: `dotnet list package` shows the resolved version as `13.0.3` for that project while every other project stays on the central `13.0.1`. Use it sparingly, with a comment explaining why (an urgent security bump that hasn't landed centrally, a project stuck on an old API) — routine use defeats the point of CPM.

It can be turned off repo-wide:

```xml
<CentralPackageVersionOverrideEnabled>false</CentralPackageVersionOverrideEnabled>
```

With it disabled, any `VersionOverride` on any `PackageReference` fails restore with **NU1013** ("cannot specify a value for VersionOverride ... currently configured to disable this functionality") — verified; this is a different code than NU1008/NU1010, don't confuse them.

## GlobalPackageReference — one entry, every project

Packages every project needs but nobody should `PackageReference` individually — analyzers, SourceLink providers, build-time-only tooling — go in `Directory.Packages.props` as `GlobalPackageReference`:

```xml
<ItemGroup>
  <GlobalPackageReference Include="Microsoft.SourceLink.GitHub" Version="8.0.0" />
</ItemGroup>
```

NuGet applies `IncludeAssets="Runtime;Build;Native;contentFiles;Analyzers"` and `PrivateAssets="All"` to these automatically — they're build-time/dev dependencies only, never a compile-time or transitive reference. Verified by packing a library with a `GlobalPackageReference`: the resulting `.nuspec` had **no** `<dependency>` entry for it at all, confirming `PrivateAssets="All"` keeps it out of the package's public dependency graph entirely (unlike transitive pinning below, which explicitly does add to the nuspec).

## Per-TargetFramework versions

Use MSBuild conditions when a package drops support for an older TFM in newer releases:

```xml
<ItemGroup>
  <PackageVersion Include="PackageA" Version="1.0.0" Condition="'$(TargetFramework)' == 'netstandard2.0'" />
  <PackageVersion Include="PackageA" Version="2.0.0" Condition="'$(TargetFramework)' == 'net8.0'" />
</ItemGroup>
```

## Transitive pinning

```xml
<CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
```

A `<PackageVersion>` entry for a package **no project references directly** pins that package's version wherever it shows up transitively. Verified setup: `Directory.Packages.props` had a `PackageVersion` for `Microsoft.Extensions.Logging.Abstractions` with no project referencing it directly (only `Microsoft.Extensions.Logging`, which depends on it). `dotnet list package --include-transitive` still listed it under **Transitive Package**, not Top-level — the pin doesn't change how it's *displayed*, only what version restore resolves to.

**Pack behavior (verified by extracting the .nuspec from a real `.nupkg`):** packing the library promoted the pinned transitive dependency into the nuspec's `<dependencies>` group as a sibling of the direct one:

```xml
<dependencies>
  <group targetFramework="net10.0">
    <dependency id="Microsoft.Extensions.Logging" version="8.0.0" exclude="Build,Analyzers" />
    <dependency id="Microsoft.Extensions.Logging.Abstractions" version="8.0.0" exclude="Build,Analyzers" />
  </group>
</dependencies>
```

This matches Microsoft's documented behavior exactly. It means **transitive pinning changes your package's public contract** when you're authoring a library — consumers now see a dependency you never wrote a `PackageReference` for. Evaluate it deliberately in library projects; it's uncontroversial in application/executable projects where there's no downstream nuspec.

**Only raise pins, never lower them.** Verified: pinning `Microsoft.Extensions.Logging.Abstractions` to `7.0.0` when `Microsoft.Extensions.Logging 8.0.0` requires `>= 8.0.0` fails restore with:

```
NU1109: Detected package downgrade: Microsoft.Extensions.Logging.Abstractions from 8.0.0 to centrally defined 7.0.0.
Update the centrally managed package version to a higher version.
 CpmVerify.Lib -> Microsoft.Extensions.Logging 8.0.0 -> Microsoft.Extensions.Logging.Abstractions (>= 8.0.0)
 CpmVerify.Lib -> Microsoft.Extensions.Logging.Abstractions (>= 7.0.0)
```

NuGet prints both paths through the graph so you can see exactly what forced the higher floor. This makes transitive pinning safe for its main real use — fixing a vulnerable transitive package: if the fix needs a *lower* version than the graph demands, NU1109 stops you immediately instead of silently downgrading a dependency underneath code that needs the newer API.

### Using it to fix a vulnerable transitive package

`NuGetAudit` (on by default) flags known CVEs during restore as **NU1901–NU1904** (low/moderate/high/critical), plus **NU1900** if vulnerability data itself couldn't be fetched. Verified live: a `GlobalPackageReference` on `Microsoft.SourceLink.GitHub 8.0.0` pulled in `Microsoft.Build.Tasks.Git 8.0.0`, which has a real published advisory, and restore reported:

```
warning NU1902: Package 'Microsoft.Build.Tasks.Git' 8.0.0 has a known moderate severity vulnerability, https://github.com/advisories/GHSA-23fw-v26w-5fgq
```

- `NuGetAuditMode` controls scope: `direct` (only top-level packages) or `all` (top-level + transitive). Projects targeting `net10.0`+ default to `all`; lower TFMs default to `direct`.
- To fix a flagged transitive package, add a `PackageVersion` for it (even though it's not referenced directly) with a patched version, and turn on `CentralPackageTransitivePinningEnabled` — this is the sensible, common case, so the pin only ever *raises* the resolved version above the vulnerable one and NU1109 can't fire.
- Inspect the graph before and after: `dotnet list package --include-transitive` shows resolved versions; `dotnet list package --vulnerable --include-transitive` is required to see transitive CVEs — verified that `--vulnerable` alone (without `--include-transitive`) reported "no vulnerable packages" even though the exact same restore had just emitted an NU1902 warning for a transitive package.
- `dotnet nuget why <project> <packageId>` shows which top-level package pulled in a transitive one — use it before deciding whether to pin or to upgrade the direct dependency instead.

## Version ranges and floating versions

Closed/bounded ranges work fine in `<PackageVersion>` — verified `Version="[13.0.1,14.0.0)"` restores cleanly. Only a **floating** version (`13.0.*`, or a range with an open upper bound written as a floating pattern) is rejected, with **NU1011**, verified above. There's no CPM-specific escape for this — floating versions defeat the reproducibility CPM exists for, so treat NU1011 as a signal to pick and pin an exact version, not to search for a bypass flag.

## Lock files with CPM

```xml
<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
```

Verified: the generated `packages.lock.json` records a transitively pinned package with `"type": "CentralTransitive"` — a third category alongside NuGet's normal `Direct` and `Transitive`, distinguishing "pinned via CPM, not referenced directly" from both.

`dotnet restore --locked-mode` fails restore instead of silently updating the lock file when it's stale. Verified: bumping the central `PackageVersion` for a `CentralTransitive` entry without regenerating the lock file produces:

```
NU1004: Mistmatch between the requestedVersion of a lock file dependency marked as CentralTransitive and the version specified
in the central package management file. Lock file version [8.0.0, ), central package management version [9.0.0, ). ...
Disable the RestoreLockedMode MSBuild property or pass an explicit --force-evaluate option to run restore to update the lock file.
```

(Yes, "Mistmatch" is a typo in NuGet's own message — don't grep for the correct spelling in CI logs.) Use `--force-evaluate` (or a plain `dotnet restore` without `--locked-mode`) to regenerate the lock file after a legitimate central version bump, then commit the updated `packages.lock.json`.

## Multiple sources + package source mapping

CPM and multiple package sources are a bad combination without mapping: **NU1507** fires as a warning whenever more than one source is configured:

```
There are 2 package sources defined in your configuration. When using central package management, please map your
package sources with package source mapping or specify a single package source.
```

Why it pairs with CPM specifically: CPM centralizes *which version* you get, but with multiple unmapped sources NuGet can still resolve a given package ID from whichever source answers first — a dependency-confusion risk (a public feed serving a package with the same name as an internal one). Package Source Mapping in `nuget.config` fixes which source is authoritative per package ID pattern:

```xml
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
    <add key="internal" value="https://pkgs.example.com/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
    <packageSource key="internal">
      <package pattern="Contoso.*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
```

Verified nuance: NU1507's source count only considers HTTP(S) package sources. A config with one HTTP source plus one local filesystem folder source (`<add key="local" value="./local-feed" />`) did **not** trigger NU1507 — only adding a second HTTP(S) source did. Adding `packageSourceMapping` for that same two-HTTP-source config made the warning disappear on the next restore, confirming mapping is the fix, not just a config nicety.

## Migrating an existing repo to CPM

1. Create `Directory.Packages.props` at the repo root (or run `dotnet new packagesprops`) with `ManagePackageVersionsCentrally=true`.
2. For every version currently on a `<PackageReference>` across the repo, add one `<PackageVersion>` entry centrally, then strip `Version` from each `<PackageReference>`. Where projects disagree on a version for the same package, pick one (usually the highest) and expect some projects to need testing against the bump.
3. Decide `CentralPackageTransitivePinningEnabled` up front — turning it on later can surface NU1109 downgrade errors you didn't have before, because pins that used to float now have a floor.
4. Do a clean restore repo-wide to catch what didn't migrate cleanly: `rm -rf **/obj **/bin && dotnet restore YourSolution.slnx`. NU1008/NU1009/NU1010 during this restore point at exactly the stray version or missing entry to fix.
5. For a large repo, hand-migrating every `.csproj` is tedious and error-prone; the community `CentralisedPackageConverter` dotnet tool (`dotnet tool install CentralisedPackageConverter --global`, then `central-pkg-converter /path/to/repo`) scans project files, moves versions into `Directory.Packages.props`, and supports `--transitive-pinning` and a `--dry-run` preview. It's third-party, not a Microsoft tool — review its diff before committing, the same as any bulk mechanical edit.

## Common restore errors — quick reference

| Code | Meaning | Verified? |
| --- | --- | --- |
| NU1008 | `PackageReference` has a `Version` under CPM | Yes — exact restore failure reproduced |
| NU1009 | `PackageVersion` defined for an SDK-implicit package | Docs-confirmed |
| NU1010 | `PackageReference` has no matching `PackageVersion` | Yes |
| NU1011 | `PackageVersion` uses a floating version | Yes |
| NU1013 | `VersionOverride` used while `CentralPackageVersionOverrideEnabled=false` | Yes |
| NU1109 | Package downgrade — central/pinned version below what the graph requires | Yes, with full graph paths shown |
| NU1004 | `packages.lock.json` stale relative to central versions under `--locked-mode` | Yes |
| NU1507 | Multiple HTTP(S) sources configured without package source mapping (CPM-specific warning) | Yes, including that local folder sources don't count and mapping clears it |
| NU1900 | NuGet couldn't fetch vulnerability data from a source | Yes (seen incidentally against an unreachable feed) |
| NU1901–NU1904 | Known vulnerability at low/moderate/high/critical severity (NuGetAudit) | Yes — real advisory on a SourceLink dependency |

## Checklist

- [ ] Exactly one `Directory.Packages.props` is authoritative per project — check for a shadowing nested file before assuming the root one applies
- [ ] `PackageReference` items never carry `Version`; every one has a matching `PackageVersion`
- [ ] `VersionOverride` usage is rare, commented, and intentional — not a workaround for CPM friction
- [ ] Build-only/analyzer packages use `GlobalPackageReference`, not a `PackageReference` copy-pasted into every project
- [ ] `CentralPackageTransitivePinningEnabled` is a deliberate choice, made with pack/nuspec impact in mind for library projects
- [ ] Vulnerable transitive packages are fixed by raising a pin (safe, NU1109-guarded), not by suppressing the audit warning by default
- [ ] `packages.lock.json` (if used) is committed and regenerated deliberately, not edited by hand
- [ ] More than one package source is paired with Package Source Mapping in `nuget.config`
- [ ] A repo-wide migration to CPM was done as its own deliberate change, verified with a clean `dotnet restore`, not a drive-by