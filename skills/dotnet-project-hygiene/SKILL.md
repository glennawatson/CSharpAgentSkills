---
name: dotnet-project-hygiene
description: Use when setting up or reviewing a .NET repo's build plumbing — Directory.Packages.props / central package management, Directory.Build.props/.targets, solution format, global.json SDK pinning, analyzer/warning settings, .editorconfig, deterministic/CI builds, and lock files. Prefer the repo's existing conventions over introducing new ones.
---

# .NET project hygiene

The repo's build plumbing (`.props`, `.targets`, `.editorconfig`, `global.json`) is infrastructure other people rely on. Match what's already there before adding something new — a second, conflicting way to pin versions or set warnings is worse than the thing it "fixes."

## Central package management (CPM)

One version per package, set once, referenced everywhere. Put it in `Directory.Packages.props` at the repo root:

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="Microsoft.Extensions.Logging" Version="10.0.0" />
  </ItemGroup>
</Project>
```

Project files then reference packages **without** a version:

```xml
<ItemGroup>
  <PackageReference Include="Microsoft.Extensions.Logging" />
</ItemGroup>
```

- `CentralPackageTransitivePinningEnabled` makes a `PackageVersion` entry also apply when that package arrives transitively, so you can raise a vulnerable transitive dependency centrally. It only affects packages you list, and a pinned package becomes a dependency of any package you pack. Turn it on unless you have a specific reason not to.
- For discovery rules, `GlobalPackageReference`, pinning behaviour, lock files, source mapping, migration and error codes, see `dotnet-central-package-management`.
- **`VersionOverride`** lets one project deviate from the central version. Use it sparingly and only with a comment saying why (e.g. a security patch that hasn't landed centrally yet); it defeats the point of CPM if used routinely.
- If a repo already has scattered per-project versions and no `Directory.Packages.props`, migrating is a deliberate, repo-wide change — don't do it as a drive-by inside an unrelated task.

## Directory.Build.props / Directory.Build.targets

MSBuild auto-imports these by walking up from each project file — no explicit `<Import>` needed.

- **`Directory.Build.props`**: imported *early*, before the SDK sets defaults. Put properties here that projects should be able to override (`TargetFramework`, `LangVersion`, `Nullable`, analyzer settings).
- **`Directory.Build.targets`**: imported *late*, after the project file. Put things here that should win regardless of what an individual project sets, or that depend on items/properties the project defines (custom targets, post-build steps).
- One set at the repo root is usually enough. A second, nested `Directory.Build.props` in a subtree (e.g. `tests/`) is for narrowing scope (test-only packages, relaxed nullable) — know that MSBuild merges them, it doesn't replace the parent, so don't duplicate properties trying to "reset" them.

## Solution format: `.slnx` only

`.slnx` is the solution format. `.sln` is legacy: migrate it, don't keep both. `.slnx` is short, diff-friendly XML with no GUIDs to collide on merge, and `dotnet new sln` already creates it by default on SDK 10+:

```bash
dotnet new sln -n MyRepo
dotnet sln MyRepo.slnx add src/MyProject/MyProject.csproj
dotnet build MyRepo.slnx
dotnet sln MyRepo.sln migrate   # convert a legacy .sln, then delete the .sln
```

`.slnf` solution filters are still valid alongside `.slnx`, as named subsets of projects. See `dotnet-slnx` for migration, format details, filters and gotchas.

## global.json — pin the SDK, don't wing it

```json
{
  "sdk": {
    "version": "10.0.401",
    "rollForward": "latestFeature"
  }
}
```

- Pin to a specific installed SDK version so `dotnet build` is reproducible across machines and CI, instead of whatever floating SDK happens to be first on `PATH`.
- `rollForward` controls what happens when the pinned version isn't installed: `latestPatch` (default-ish, narrowest — same feature band, higher patch), `latestFeature` (same major, allows a newer feature band — good for "give me at least this, but let CI have a newer bugfix band"), `latestMinor`/`latestMajor` (wider, rarely what you want), `disable` (exact match only, fails otherwise). Pick the narrowest one that still lets CI machines with slightly newer SDKs build.
- A prerelease SDK (`-rc`, `-preview`) installed alongside stable ones is exactly the case `global.json` exists for — without it, `dotnet` picks the highest installed SDK, prerelease included, and behavior becomes machine-dependent.

## Language version, nullable, implicit usings

```xml
<PropertyGroup>
  <LangVersion>latest</LangVersion>
  <Nullable>enable</Nullable>
  <ImplicitUsings>enable</ImplicitUsings>
</PropertyGroup>
```

- `LangVersion` defaults to the highest version the targeted TFM supports (`net10.0` → C# 14) — set it explicitly only when you need to pin below that, or `latest`/`preview` to opt into a newer compiler than the TFM implies.
- `Nullable enable` is the default for new templates and should be the default for you too; a codebase with it off is a codebase with a known, tracked debt, not a reason to leave new code unannotated.
- Don't add `<TargetFrameworks>` (plural) with a long multi-TFM list speculatively. Every extra TFM multiplies build/test time and `#if` complexity. Support what you ship, not what you might ship.

## Analyzers and warnings

```xml
<PropertyGroup>
  <AnalysisLevel>latest</AnalysisLevel>
  <AnalysisMode>Recommended</AnalysisMode>
  <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
  <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
</PropertyGroup>
```

- `AnalysisLevel` picks the analyzer rule *vintage* (`latest` tracks the SDK you're on); `AnalysisMode` picks the *default severity bucket* (`Minimum`, `Recommended`, `All`) for rules that don't have an explicit `.editorconfig` entry.
- `EnforceCodeStyleInBuild` makes `.editorconfig` style rules (IDE0xxx) fail the build, not just show up as IDE squiggles — without it, style rules are cosmetic and silently rot.
- `TreatWarningsAsErrors` plus the two settings above is what turns "the analyzers said something" into "the build won't produce a broken artifact." See `csharp-verification` for why a warning-emitting build is not a done build.
- Don't add a repo-wide `<NoWarn>` list to make a warning go away; suppress the specific rule at the specific `.editorconfig` scope (or a targeted `#pragma` with a comment) instead.

## .editorconfig

Rule severities live per-rule under `[*.cs]` sections:

```ini
[*.cs]
dotnet_diagnostic.CA1062.severity = warning
dotnet_diagnostic.IDE0060.severity = suggestion
```

- `.editorconfig` is where per-rule severity actually lives; `AnalysisMode` is only the fallback for rules the file doesn't mention.
- Lowering a rule's severity to make a build pass is a suppression, same as `#pragma warning disable` — it needs the same justification, not a reflex edit. See `csharp-verification`.
- **A long, deliberately organized `.editorconfig` is not append-only.** Sort a new line into the section it belongs to, not onto the end of the file:
  - **Find the right `[glob]` section first.** A file with sections at both the root and narrow, later scopes (e.g. a trailing `[**/tests/**/*.cs]`) can silently rescope a rule to tests if you anchor on "the last line that looks similar" instead of checking which `[...]` block your target line actually sits inside.
  - **Then the right comment-header group** (e.g. `# Maintainability`, `# Naming`) and insert in the same order the file already uses. Options belong in the options block, not mixed in among `dotnet_diagnostic.*` severity lines.
  - **Test-only relaxations belong in a `tests/`-scoped file** (e.g. `tests/.editorconfig`), not a narrow glob bolted onto the root file. Product-code rules stay in the root file, in their proper group.
  - **Verify after editing** that the inserted line landed inside the section you intended — an edit that "applied" is not the same as one that landed in the right place.

## Deterministic and reproducible builds

```xml
<PropertyGroup>
  <Deterministic>true</Deterministic>
  <ContinuousIntegrationBuild Condition="'$(CI)' == 'true'">true</ContinuousIntegrationBuild>
</PropertyGroup>
```

- `Deterministic` (on by default for SDK-style projects) makes two builds from identical source produce byte-identical output.
- `ContinuousIntegrationBuild` strips machine-specific paths from PDBs and is required for SourceLink to resolve correctly from a released package — set it conditionally so local `dotnet build` still embeds local paths for a debugger.
- **SourceLink**: add the provider package (`Microsoft.SourceLink.GitHub`, `.GitLab`, etc.) so consumers stepping into your NuGet package's code get it fetched from source control instead of a stale local copy. Needs `ContinuousIntegrationBuild=true` and `EmbedUntrackedSources=true` on the CI build that produces the package.

## NuGet audit and lock files

- `NuGetAudit` (on by default in current SDKs) checks restored packages against the NuGet vulnerability feed and warns on known CVEs — leave it on; treat `NU1900`-series warnings as real findings, not noise to suppress.
- **Package lock files** (`packages.lock.json`, via `<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>`): use them when you need byte-for-byte reproducible restores across machines/CI regardless of what's newly published upstream (e.g. compliance, supply-chain requirements, or flaky transitive updates breaking builds). They're extra ceremony (a file to commit and refresh on every version bump) — don't add them to a repo that hasn't asked for that guarantee; CPM already gives you a single place to control versions.

## .NET 11 (net11.0, RC1)

- **`net11.0` targeting.** `net11.0` compiles and defines `NET11_0_OR_GREATER` as expected. .NET 11 is at RC1 with GA expected November 2026 — keep `net10.0` the default TFM for anything shipping before then; add `net11.0` only where you have a concrete reason (probing new APIs, an explicit multi-target). If a prerelease SDK is installed alongside stable ones, pin `global.json` (see above) so `dotnet build` doesn't silently pick the RC.
- **MSBuild server and the Native AOT CLI path are on by default** in SDK 11 — repeated builds reuse persistent `MSBuild`/`VBCSCompiler` node processes without any opt-in property (a `dotnet build` leaves `nodeReuse:true` MSBuild processes running). `--disable-build-servers` is there for the case you need a truly clean, non-cached build (CI diagnosing a flaky build, or benchmarking cold build time) — reach for it deliberately, not as a default habit.
- **CA1873** (avoid potentially expensive logging calls — flags computing/formatting log arguments that a guarding `IsEnabled` check would have skipped) has improved detection in SDK 11's analyzer set. If it fires on code that previously passed, it's very likely a real one — see `csharp-logging-diagnostics` for the `IsEnabled` guard pattern it wants.
- **Solution filters:** create a `.slnf` with `dotnet new slnf -s App.slnx`, then edit it with `dotnet sln App.slnf add/remove/list`. There is no separate subcommand: `dotnet sln` accepts the `.slnf` as its file argument. See `dotnet-slnx`.

## Build files: comment only genuinely confusing XML

In `.csproj`, `.props`, and `.targets`, a comment is for a bit of XML whose behavior genuinely isn't obvious — not for narrating what a property does or why it was changed.

- **Default to no comment.** A named property, a `PackageReference`, a TFM list, an ordinary `ItemGroup` explains itself; if a reader can tell what it does by reading it, say nothing.
- **One line, only when the XML really is confusing** — evaluation order, a non-obvious MSBuild interaction, a workaround for a specific error code. Never a multi-line block or a rationale essay; background and trade-offs belong outside the build file.
- **Comments describe the file as it is, not its history.** No "now", "previously", "used to", "no longer", "originally" — those describe a change, not the current XML. Never comment on something that's absent: removing a property or an `ItemGroup` leaves nothing to explain, so it earns no comment either.

## Don't fight the existing setup

Before changing any of the above, check what the repo already does — the whole point is one consistent, discoverable answer per concern. A new `Directory.Build.props` property that duplicates one already set at the root, a second CPM-like scheme, or a personal `.editorconfig` override are all worse than living with the existing convention.

- **Don't create or edit repo config as a side effect of an unrelated task.** `.editorconfig`, `global.json`, `nuget.config`, and `Directory.Build.props` severities shape the whole repo; a failing analyzer or a red CI leg is a reason to fix the code and report the cause, not to add or loosen config to make the symptom go away.

## Checklist

- [ ] Versions come from one place (`Directory.Packages.props`, CPM on); no scattered per-project versions
- [ ] `Directory.Build.props`/`.targets` used for their intended timing (early vs. late), not duplicated per project
- [ ] Solution file is `.slnx`; any legacy `.sln` migrated and deleted (see `dotnet-slnx`)
- [ ] `global.json` pins the SDK with a deliberate `rollForward`, especially if prerelease SDKs are installed
- [ ] `Nullable`, `ImplicitUsings` enabled; `LangVersion` explicit only when pinning below the TFM default
- [ ] `TreatWarningsAsErrors` + `EnforceCodeStyleInBuild` + `AnalysisLevel latest` — warnings and style rules actually fail the build
- [ ] Rule severities live in `.editorconfig`, not ad hoc `#pragma`/`NoWarn`
- [ ] `ContinuousIntegrationBuild` set for CI/package-producing builds; SourceLink wired for published packages
- [ ] `NuGetAudit` left on; lock files added only when the repo genuinely needs reproducible restores
- [ ] Changes match the repo's existing conventions instead of introducing a parallel one
