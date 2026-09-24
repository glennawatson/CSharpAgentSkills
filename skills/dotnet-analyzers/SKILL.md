---
name: dotnet-analyzers
description: Use when adding, configuring, or reviewing Roslyn analyzers in a .NET repo — StyleSharp/PerformanceSharp/SecuritySharp (or an equivalent first-party analyzer set), the built-in CAxxxx code-quality rules, IDExxxx code-style rules, and SYSLIBxxxx obsoletion/source-generator diagnostics. Covers wiring analyzers in, severity precedence in .editorconfig, fixing vs. suppressing, and picking the highest-value rules to turn on.
---

# .NET analyzers: wiring, configuring, and fixing

An analyzer that isn't wired to fail the build is a linter nobody reads. Getting this right is
three separate jobs: **add** the analyzer packages so they run, **configure** severities so the
right things fail the build, and **fix** what they find instead of making them quiet. See
`dotnet-project-hygiene` for the `AnalysisLevel`/`TreatWarningsAsErrors` basics this goes deeper
on, and `csharp-verification` for why an analyzer warning is a failure, not noise.

## Adding analyzers

Reference analyzer packages the same way across every project with central package management:
version in `Directory.Packages.props`, the actual `PackageReference` once in `Directory.Build.props`
so every project that imports it gets the analyzer, `PrivateAssets="all"` so the analyzer doesn't
flow to your own package's consumers (it's a build-time tool, not a runtime dependency):

```xml
<!-- Directory.Packages.props -->
<PropertyGroup>
  <!-- StyleSharp.Analyzers, PerformanceSharp.Analyzers and SecuritySharp.Analyzers ship from the
       same release pipeline and always share a version. -->
  <RoslynCommonAnalyzersVersion>5.1.2</RoslynCommonAnalyzersVersion>
</PropertyGroup>
<ItemGroup>
  <PackageVersion Include="StyleSharp.Analyzers" Version="$(RoslynCommonAnalyzersVersion)" />
  <PackageVersion Include="PerformanceSharp.Analyzers" Version="$(RoslynCommonAnalyzersVersion)" />
  <PackageVersion Include="SecuritySharp.Analyzers" Version="$(RoslynCommonAnalyzersVersion)" />
</ItemGroup>

<!-- Directory.Build.props -->
<ItemGroup>
  <PackageReference Include="StyleSharp.Analyzers" PrivateAssets="all"/>
  <PackageReference Include="PerformanceSharp.Analyzers" PrivateAssets="all"/>
  <PackageReference Include="SecuritySharp.Analyzers" PrivateAssets="all"/>
  <PackageReference Include="Roslynator.Analyzers" PrivateAssets="All"/>
  <PackageReference Include="SonarAnalyzer.CSharp" PrivateAssets="all"/>
</ItemGroup>
```

A plain `PackageReference` (version omitted) inside `Directory.Build.props` is enough under CPM —
you don't need `GlobalPackageReference` here; that item exists for packages every project needs
even when a project doesn't otherwise reference anything (see `dotnet-central-package-management`).
Both forms need `PrivateAssets="all"` explicitly; CPM does not imply it.

Turn on the rest of the pipeline in the same `Directory.Build.props`:

```xml
<PropertyGroup>
  <AnalysisLevel>latest</AnalysisLevel>
  <AnalysisMode>All</AnalysisMode>            <!-- or AllEnabledByDefault, Recommended -->
  <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
  <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
</PropertyGroup>
```

Verified empirically (`/tmp/analyzers-verify`, SDK 11.0.100-rc.1): with `AnalysisMode=All` and
`EnforceCodeStyleInBuild=true`, both a CA rule (`CA1305`) and an IDE rule (`IDE0008`) fire on a
plain `dotnet build` with no `.editorconfig` entry for either — `AnalysisMode` really is the
fallback bucket for anything the file doesn't mention.

### Adopting the RoslynCommonAnalyzers presets

`StyleSharp`/`PerformanceSharp`/`SecuritySharp` ship ready-to-use presets at their repo root —
`recommended.editorconfig`, `recommended-performancesharp.editorconfig`,
`recommended-securitysharp.editorconfig` — each listing every rule at its default severity, with
opt-in rules commented out. Per the packages' own `docs/CONFIGURATION.md`: copy one in, or merge
its `[*.cs]` block into your existing `.editorconfig`, then tune. They configure entirely through
`.editorconfig` — no separate JSON file — using the same `AnalyzerConfigOptionsProvider` mechanism
the .NET SDK's own CA analyzers use, with a per-package option prefix (`stylesharp.`,
`performancesharp.`, `securitysharp.`) mirroring CA's `dotnet_code_quality.` prefix. Rule-specific
keys (`stylesharp.SST1315.union_member_naming`) win over the general one
(`stylesharp.union_member_naming`).

## Configuring: severity and precedence

Severity lives in `.editorconfig`, per rule:

```ini
[*.cs]
dotnet_diagnostic.CA1062.severity = warning
dotnet_diagnostic.IDE0060.severity = suggestion
```

Category-level fallback (`dotnet_analyzer_diagnostic.category-<Category>.severity`) sets a default
for every rule in that category that isn't individually overridden — useful for "everything in
`Naming` is a suggestion by default" without writing 40 lines. A per-rule
`dotnet_diagnostic.<ID>.severity` always wins over it — but see the precedence caveat below before
relying on it: an explicit `AnalysisMode` changes whether it does anything at all.

**Verified precedence, worked out from `dotnet build` behavior, not assumed** (three fresh projects,
`AnalysisLevel=latest`, `EnforceCodeStyleInBuild=true`, SDK `11.0.100-rc.1`, target `net10.0`):

1. `dotnet_diagnostic.<ID>.severity = none`/`NoWarn` — the rule never fires, full stop. Confirmed:
   `<NoWarn>$(NoWarn);CA1822</NoWarn>` plus `TreatWarningsAsErrors=true` builds clean on a file that
   triggers `CA1822` — `NoWarn` wins over warnings-as-errors, it isn't a race.
2. `dotnet_diagnostic.<ID>.severity = <level>` in `.editorconfig` sets that rule's severity
   regardless of `AnalysisMode`. Confirmed: setting `CA1822` to `suggestion` in `.editorconfig`
   removed it from the build-warning list even under `AnalysisMode=Recommended`.
3. `AnalysisMode`/`AnalysisLevel`, **when `AnalysisMode` is set explicitly** (`Recommended`, `All`,
   …) — this computes a per-rule severity that outranks category-level fallback. Confirmed: with
   `AnalysisMode=All` set, `dotnet_analyzer_diagnostic.category-Performance.severity = error` had
   **no effect** on `CA1822` (stayed `warning`) — an explicit `AnalysisMode` is not just a fallback
   bucket, it wins over category-level severity for rules it computes a severity for.
4. `dotnet_analyzer_diagnostic.category-<Category>.severity` — only actually takes effect as
   documented (a default for every rule in that category with no explicit `dotnet_diagnostic.<ID>`
   entry) when `AnalysisMode` is **left unset** at its SDK default. Confirmed: removing
   `AnalysisMode` entirely from the same project made the identical
   `dotnet_analyzer_diagnostic.category-Performance.severity = error` promote `CA1822` to `error`.
   **If your `Directory.Build.props` sets `AnalysisMode` explicitly — which this skill recommends —
   category-level severity is not a reliable lever for CA rules; use per-rule
   `dotnet_diagnostic.<ID>.severity` instead.**
5. `TreatWarningsAsErrors` / `WarningsAsErrors` — promotes whatever severity survived steps 1–4
   from warning to error. It cannot resurrect a rule steps 1–2 silenced.

So: **NoWarn and `.editorconfig` per-rule severity are suppressions/overrides that happen before
warnings-as-errors is even consulted, and both outrank an explicit `AnalysisMode`.** Dropping a
rule's severity to `none` and adding it to `NoWarn` do the same thing through different doors —
both need the same justification as a `#pragma`, not a reflex edit. Don't reach for category-level
severity as your primary lever in a repo that sets `AnalysisMode` explicitly; it silently no-ops for
rules `AnalysisMode` already assigns a severity to.

### Test-directory relaxations

A nested `.editorconfig` under `src/tests/` narrows scope for rules that don't fit test code —
`CA1812` (uninstantiated internal class — that's every test fixture), `CA2213`/`CA1063` (dispose
patterns that don't matter when the process exits after the run), or a project-specific analyzer's
hot-path rule:

```ini
# src/tests/.editorconfig — inherits the repo-root .editorconfig; only lists test-specific overrides
[*.cs]
dotnet_diagnostic.CA1812.severity = none # Avoid uninstantiated internal classes
dotnet_diagnostic.CA2213.severity = none # Disposable fields should be disposed

performancesharp.avoid_linq_on_hot_path = false
dotnet_diagnostic.PSH1100.severity = none # Tests can use LINQ for clarity
```

Don't put `root = true` in the nested file — it must inherit, not replace, the repo-root config.
Every relaxation gets a comment saying *why* it's fine in tests, same discipline as any other
suppression.

### Grouping convention in a large `.editorconfig`

Real `.editorconfig` files sort CA rules by numeric ID within a category comment block, and every
`none`/non-default severity carries an inline reason — either a real false-positive-for-us reason,
or "this rule is redundant with another one this repo treats as canonical":

```ini
###################
# CA10xx - Design
###################
dotnet_diagnostic.CA1000.severity = none # Do not declare static members on generic types — common factory pattern
dotnet_diagnostic.CA1001.severity = error # Types that own disposable fields should be disposable
dotnet_diagnostic.CA1051.severity = none # Duplicate of SST1401 (canonical) — do not declare visible instance fields
dotnet_diagnostic.CA1822.severity = none # Mark members as static — covered by PSH1414
dotnet_diagnostic.CA1860.severity = none # Avoid using 'Enumerable.Any()' extension method — covered by PSH1103
```

That "covered by `PSH####` (canonical)" pattern is deliberate: when a first-party analyzer and a
CA/Roslynator/SonarAnalyzer rule overlap, pick one as canonical (usually the one with the better
fixer or the more specific message) and silence the other with a comment naming its replacement —
never leave both live reporting the same defect twice.

SYSLIB rules get their own grouped block, same pattern:

```ini
# Microsoft .NET Runtime Obsoletions (SYSLIB0xxx)
dotnet_diagnostic.SYSLIB0011.severity = error # BinaryFormatter serialization is obsolete
...
# GeneratedRegex source generator
dotnet_diagnostic.SYSLIB1045.severity = error # Convert to 'GeneratedRegexAttribute' for compile-time regex generation
# LibraryImport (P/Invoke) source generator
dotnet_diagnostic.SYSLIB1054.severity = error # Use 'LibraryImportAttribute' instead of 'DllImportAttribute' ...
```

## Fixing, not suppressing

Fix the cause. A suppression is for a genuine false positive, scoped as narrowly as possible, with
a comment saying why:

```csharp
// The connection string's empty password is the fixture the secret-detection rule is measured
// against, not a real credential.
#pragma warning disable SES1203
private const string TestConnectionString = "Server=.;Password=;";
#pragma warning restore SES1203
```

`[SuppressMessage]` for a suppression that belongs to a whole member rather than a few lines, with
the same non-negotiable `Justification`. Never drop a rule's severity in `.editorconfig` just to
quiet one call site — that silences it everywhere, not just where you looked.

Apply a fixer to exactly the files you touched, one rule at a time, instead of reformatting the
tree:

```bash
dotnet format analyzers --include src/Orders/OrderService.cs --diagnostics SYSLIB1045
dotnet format style --include src/Orders/OrderService.cs --diagnostics IDE0008
```

See `csharp-verification` for the full gate (`dotnet build` clean, `dotnet format --verify-no-changes`
clean, suppressions never used to force green).

## High-value rules worth turning on

These come from `.editorconfig` blocks actually enabled at `error`/`warning` across multiple
first-party repos surveyed for this skill (`reactiveui`, `splat`, `Akavache`, `refit`,
`Fusillade`, `Primitives`):

- **`CA1848`** (use `LoggerMessage` delegates) — `error` everywhere surveyed. See
  `csharp-logging-diagnostics` for the `[LoggerMessage]` pattern it wants instead of
  `ILogger.LogInformation(...)`.
- **`CA1873`** (avoid expensive `Debug.Assert`/logging argument evaluation) — guard the argument
  with `IsEnabled` rather than computing it unconditionally; see `csharp-logging-diagnostics`.
- **`CA2007`** (`ConfigureAwait(false)`) — consistently *disabled* in these repos with the same
  reasoning each time: "Rx and library callers drive synchronization context themselves." Don't
  cargo-cult this rule on; decide it for your own library's actual callers.
- **`CA1305`/`CA1307`/`CA1309`/`CA1310`** (culture-sensitive string operations, culture-sensitive
  comparison) — real bugs in anything building cache keys, URLs, or persisted data from
  `ToString()`/`ToLower()` without an explicit `CultureInfo`/`StringComparison`. Write instead:
  `value.ToString(CultureInfo.InvariantCulture)`, `string.Equals(a, b, StringComparison.Ordinal)`.
- **`CA1822`** (mark members static) — frequently disabled in favor of an equivalent first-party
  rule (`PSH1414` in the surveyed repos) rather than left off; check whether your analyzer set has
  a canonical version before just enabling CA's.
- **`CA1851`** (possible multiple enumeration of `IEnumerable`) — real perf/correctness bug when a
  lazy sequence is enumerated twice (e.g., once for `Count()`, once for a `foreach`) and the source
  isn't idempotent. Materialize once: `var items = source.ToList();`.
- **`CA1859`/`CA1860`/`CA1861`/`CA1865`–`CA1867`** — cheaper API selection (`CA1859`: use the
  concrete type instead of the interface when nothing needs the abstraction; `CA1860`: `.Count == 0`
  over `.Any()` on a collection; `CA1861`: hoist a constant array literal out of a hot call site;
  `CA1865`–`CA1867`: the `char` overloads of `StartsWith`/`EndsWith`/`IndexOf`). The `char`
  overloads don't exist on `netstandard2.0`/.NET Framework — repos multi-targeting those disable
  `CA1865`–`CA1867` for exactly that reason; don't blanket-enable them without checking your TFMs.
- **`CA2263`** (prefer the generic overload when the type is known at the call site) — replaces
  `GetService(typeof(T))`-shaped calls with `GetService<T>()`.
- **`SYSLIB0xxx`** (runtime obsoletions — `BinaryFormatter`, CAS, `Thread.Abort`, weak crypto
  defaults, …) — `error`, not `warning`, in every repo surveyed. These mark APIs the runtime no
  longer supports correctly; there is no "later" for these, only "someone hits it in production."
- **`SYSLIB1045`** (`[GeneratedRegex]` instead of `new Regex(...)`/`Regex.IsMatch` with a constant
  pattern) and **`SYSLIB1054`** (`[LibraryImport]` instead of `[DllImport]`) — both source-generator
  suggestions, both `error` in the surveyed repos. See `csharp-aot-trimming` for why these matter
  beyond style: the generated code is trim/AOT-safe where reflection-based `Regex`/P/Invoke
  marshalling is not.
- **`SYSLIB1030`** (`JsonSourceGenerator` did not generate metadata for a type reachable from a
  `[JsonSerializable]` root) — a real gap in a `JsonSerializerContext`, not noise; see
  `csharp-json-source-generation`.

## Writing your own analyzer: describe the defect, not another tool's rule ID

If you're writing a first-party analyzer (see `roslyn-analyzer-performance` for making its hot
paths cheap), its diagnostic message and docs describe the defect **in its own terms** — "this
sequence is enumerated twice" — not "see CA1851" or "duplicate of SonarAnalyzer S1234". A consumer
without that other tool installed still needs the message to make sense, and the `.editorconfig`
comment convention above (`# covered by PSH1103`) is where cross-tool relationships belong — in
the *consuming* repo's config, not in the rule's own shipped text.

## Checklist

- [ ] Analyzer packages referenced once (`Directory.Packages.props` version +
      `Directory.Build.props` `PackageReference PrivateAssets="all"`), not per-project
- [ ] `AnalysisLevel=latest`, `AnalysisMode` picked deliberately (`Recommended` vs `All`),
      `EnforceCodeStyleInBuild=true`, `TreatWarningsAsErrors=true`
- [ ] First-party preset (`recommended*.editorconfig`) adopted or merged, not reinvented rule by rule
- [ ] Every non-default severity in `.editorconfig` has an inline reason, especially a "canonical
      rule" cross-reference when two analyzers cover the same defect
- [ ] Test-directory `.editorconfig` relaxations are scoped to the test tree, inherit (no
      `root = true`), and each line says why
- [ ] No repo-wide `NoWarn` used to silence a rule that should be fixed; suppressions are scoped
      and justified
- [ ] SYSLIB0xxx obsoletions and SYSLIB1045/1054/1030 source-gen suggestions are enabled, not left
      at their (often lower) shipped default
- [ ] `dotnet format analyzers --include <file> --diagnostics <ID>` used to apply one fixer to
      exactly the changed files, not a bare `dotnet format`
- [ ] Verified with a real build, per `csharp-verification` — not assumed from reading the rule
