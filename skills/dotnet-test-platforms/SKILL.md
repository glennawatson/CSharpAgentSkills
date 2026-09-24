---
name: dotnet-test-platforms
description: Use when deciding or explaining how `dotnet test` actually runs a C#/.NET test project — VSTest vs Microsoft.Testing.Platform (MTP), which one a given project uses, why a flag lands after `--` or not, TRX/coverage/exit-code differences, or migrating a project from VSTest to MTP. This is the authority on the platforms themselves; framework-specific test-writing flags live in `csharp-tunit`, `csharp-nunit`.
---

# .NET test execution: VSTest vs Microsoft.Testing.Platform (MTP)

`dotnet test` is a thin driver over one of **two unrelated test platforms**. They differ in process architecture, extension model, CLI options, and exit codes — mixing up which one a project uses is the source of most "why doesn't `--filter` work" / "why is there no testhost" confusion. Verified against the .NET 10.0.401 and .NET 11.0.100-rc.1 SDKs.

## The two platforms, in one paragraph each

**VSTest** is the original architecture (`Microsoft.NET.Test.Sdk`). The test project is an ordinary **library** (`OutputType` implicitly `Library`). `dotnet test` builds it, then launches a separate `vstest.console` process, which spawns a **testhost** process, which loads your test DLL through a **test adapter** (`xunit.runner.visualstudio`, `NUnit3TestAdapter`, MSTest's adapter). Discovery and execution happen through that adapter layer. Config comes from a `.runsettings` XML file; results and coverage come from loggers and data collectors running inside the vstest process tree.

**Microsoft.Testing.Platform (MTP)** removes that whole chain. The test project builds to a **self-contained executable** (`OutputType=Exe`, `Microsoft.Testing.Platform.MSBuild` generates the `Main`/entry point). There's no vstest.console, no testhost, no adapter — `dotnet <yourtests>.dll` (or the native apphost) *is* the test run. Capabilities that VSTest bundled centrally (TRX output, coverage, crash/hang dumps, retry) are opt-in NuGet **extensions** (`Microsoft.Testing.Extensions.*`) that each test app references directly, so the command-line surface is whatever extensions that specific app happens to have — there's no fixed global flag set the way VSTest has.

```
Do:    check IsTestingPlatformApplication (or look for OutputType=Exe + no adapter package) before assuming a project's flags
Don't: assume every test project takes --filter, --logger, --collect — those are VSTest-only
Don't: assume every test project takes --treenode-filter or MTP exit codes — those are MTP-only
```

## Which one is a project actually using?

Check with MSBuild directly — this is unambiguous where reading the `.csproj` isn't:

```bash
dotnet msbuild -getProperty:IsTestingPlatformApplication,EnableMSTestRunner,IsTestProject,OutputType
```

| `IsTestingPlatformApplication` | `IsTestProject` | Classification |
|---|---|---|
| `true` | any | MTP test application |
| `false`/empty | `true` | VSTest test project |
| `false`/empty | `false`/empty | not a test project |

Signals in the `.csproj` (verified empirically):

- **VSTest**: `<PackageReference Include="Microsoft.NET.Test.Sdk">` + a `*.runner.visualstudio` / `NUnit3TestAdapter` / MSTest adapter package, no `OutputType`. `dotnet new xunit` (v2, default) and a plain `dotnet new mstest` (no `--test-runner`) produce this. Running it prints the VSTest banner: `Test run for ....dll (.NETCoreApp,Version=v10.0)` / `A total of N test files matched the specified pattern.`
- **MTP**: `OutputType=Exe`, `EnableMSTestRunner=true` (MSTest ≥ 3.2) / `EnableNUnitRunner=true` (`NUnit3TestAdapter` ≥ 5.0) / `UseMicrosoftTestingPlatformRunner=true` (xUnit.v3), TUnit always. Running it prints `Running tests from ....dll (tfm|arch)` / `Test run summary: Passed!`.
- **Gotcha, verified**: `dotnet new mstest --sdk` / `--test-runner MSTest` (as templated by the 10.0.401 SDK) sets `EnableMSTestRunner=true` **and** `TestingPlatformDotnetTestSupport=true` — but also pins an old `MSTest` package version (3.6.4) where `IsTestingPlatformApplication` doesn't get wired to `EnableMSTestRunner`, so `dotnet msbuild -getProperty:IsTestingPlatformApplication` still reports `false` and native-MTP `dotnet test` (global.json opted in) refuses the project ("The following test projects are using VSTest test runner"). Bumping `MSTest` to 4.0.2 (and dropping the now-unneeded `Microsoft.NET.Test.Sdk`/pinned extension versions the template also added) fixed it. **Don't trust the property values in a template-generated `.csproj` at face value — verify with `-getProperty` after restore.**

## How `dotnet test` picks a runner

Runner selection was VSTest-only through .NET 9. Starting with **.NET 10 SDK** (MTP 1.7+ required), `dotnet test` reads `global.json`:

```json
{
  "test": { "runner": "Microsoft.Testing.Platform" }
}
```

`"VSTest"` is the explicit (and default, if the key is absent) value. Starting with **.NET 11 Preview 6**, `DOTNET_TEST_RUNNER=Microsoft.Testing.Platform` (or `VSTest`) overrides `global.json` for one invocation without editing the file.

Once `global.json` opts into MTP, **every** test project touched by that `dotnet test` invocation must be an MTP app — a VSTest project in the mix is a hard error (verified: `"global.json defines test runner to be Microsoft.Testing.Platform. All projects must use that test runner. The following test projects are using VSTest test runner: <project>"`). There's no per-project override once you're in native-MTP mode.

### Native MTP mode vs the VSTest-mode bridge — this is the confusing part

An MTP test app (`IsTestingPlatformApplication=true`) can be run by `dotnet test` in **two different ways**, and they look and behave differently:

1. **Native MTP mode** (`global.json` opts in): `dotnet test` talks to the app's own MTP protocol directly. Output looks like `Running tests from .../X.dll (tfm|arch)` / `Test run summary: Passed!`. Options are whatever the app's registered extensions expose (see below); there is no fixed VSTest-style flag set.
2. **VSTest-mode bridge** (`global.json` still says VSTest, project has `TestingPlatformDotnetTestSupport=true`): `dotnet test` still drives a VSTest-shaped flow, but instead of a testhost+adapter it launches the MTP exe directly. Output looks like `Run tests: '.../X.dll' [tfm|arch]` / `Tests succeeded:` — visually distinct from *both* the native-MTP output and plain-VSTest output. This is the `TestingPlatformDotnetTestSupport` MSBuild property's whole job: let an MTP app keep working under `dotnet test` before a repo (or its CI) has opted into `global.json`'s MTP mode.

Verified: the exact same `EnableMSTestRunner=true` project produced three different console banners across the three modes (plain-VSTest testhost banner when misclassified, VSTest-bridge banner with `TestingPlatformDotnetTestSupport=true`, native-MTP banner once `global.json` opted in and the project actually classified as MTP). If output format looks unfamiliar, that's the tell for which mode you're in — don't guess from the flags alone.

### Why arguments go after `--` in one mode but not the other

- **VSTest** (`dotnet test [options] -- <RunSettings NAME=VALUE ...>`): everything after `--` is inline `.runsettings` key/value pairs (`dotnet test -- MSTest.MapInconclusiveToFailed=True`), not arbitrary CLI flags.
- **MTP** (`dotnet test [dotnet-test options] -- <args passed verbatim to the test app>`): `dotnet test` only recognizes a fixed, small set of orchestration options (`--results-directory`, `--minimum-expected-tests`, `--list-tests`, etc. — see the synopsis below). Anything it doesn't recognize is forwarded to the test application *as a token*, and a **recognized** option sitting between two of your tokens gets stripped before forwarding — which can silently reassociate values. Putting extension-specific args (`--report-trx`, `--coverage`, `--treenode-filter`) after a literal `--` removes that ambiguity: `dotnet test --results-directory TestResults -- --report-trx --report-trx-filename A.trx`. Some options (`--minimum-expected-tests`) are recognized in **both** positions with different scope — before `--` it's a whole-run policy, after `--` it's forwarded to each module and applied per-module.

### .NET 11 changes (verified on 11.0.100-rc.1)

- `dotnet new xunit --xunit-version v3` generates an xUnit v3 project that is MTP by default (`OutputType=Exe`, `UseMicrosoftTestingPlatformRunner=true`, `xunit.v3.mtp-v2` package) — and **the template itself writes `"test": {"runner": "Microsoft.Testing.Platform"}` into `global.json`** if one exists in the directory tree. `--test-runner VSTest` opts back into the old shape.
- `dotnet new nunit --test-runner Microsoft.Testing.Platform` similarly opts an NUnit project into MTP (`EnableNUnitRunner=true`).
- New `dotnet test` flags for MTP mode: `--no-dependencies`, `--use-current-runtime`/`--ucr`, `--timeout`, `--maximum-failed-tests`, `--results-directory-layout per-module`, `--test-modules` exclusion via `!pattern`, `--device`/`--list-devices` for mobile targets, `--collect-test-map`/`--affected-tests` (experimental).
- `DOTNET_TEST_RUNNER` env var (Preview 6+) overrides `global.json` without editing it.

## VSTest in depth (verified)

Project shape: library, `Microsoft.NET.Test.Sdk`, a test-adapter package, no `OutputType`.

```console
dotnet test                                    # exit 0 pass / 1 fail — the only two VSTest exit codes
dotnet test --filter "FullyQualifiedName~Foo"  # property=value syntax; see table below
dotnet test -t                                 # --list-tests, short form -t
dotnet test --logger trx                       # writes TestResults/<host>_<date>.trx (verified)
dotnet test --collect "Code Coverage"           # Microsoft-format coverage (needs the VS-hosted collector)
dotnet test --collect:"XPlat Code Coverage"     # coverlet.collector -> TestResults/<guid>/coverage.cobertura.xml (verified)
dotnet test -s my.runsettings                   # .runsettings drives adapter config, blame/data-collector options, TargetPlatform
dotnet test --blame-hang --blame-hang-timeout 60s  # hang/crash dumps via the blame collector (testhost-process based)
```

`--filter` property support is framework-specific: MSTest (`FullyQualifiedName`, `Name`, `ClassName`, `Priority`, `TestCategory`), xUnit (`FullyQualifiedName`, `DisplayName`, `Traits`), NUnit (`FullyQualifiedName`, `Name`, `Priority`, `TestCategory`/`Category`, `Property`). Operators: `=`, `!=`, `~` (contains), `!~`; combine with `&`/`|` and parens. A bare value is `FullyQualifiedName~value`.

Exit codes (verified: pass → `0`, one failing `[Fact]` → `1`; VSTest never returns anything else). A run that discovers zero tests still returns `0` unless `.runsettings` sets `RunConfiguration.TreatNoTestsAsError=true`.

## MTP in depth (verified)

Project shape: `OutputType=Exe`, `IsTestingPlatformApplication=true` (transitively set by `EnableMSTestRunner`/`EnableNUnitRunner`/`UseMicrosoftTestingPlatformRunner`/TUnit), no VSTest adapter package. It runs three interchangeable ways:

```console
dotnet run --project tests/X -- --treenode-filter "/*/*/MyClass/*"   # fastest inner loop, TUnit's filter syntax
./bin/Debug/net9.0/X                                                  # the built apphost, directly — no dotnet needed
dotnet bin/Debug/net9.0/X.dll --list-tests                            # via dotnet exec, same process
dotnet test                                                            # orchestrated, with global.json opted into MTP
```

```console
dotnet test --list-tests                       # discovers without running (verified: "Discovered N tests.")
dotnet test -- --filter "TestMethod1"           # MSTest/NUnit/xUnit v3 on MTP: --filter, not --treenode-filter
dotnet test -- --treenode-filter "..."          # TUnit only — hierarchical /Assembly/Namespace/Class/Method path syntax
dotnet test -- --report-trx                     # needs Microsoft.Testing.Extensions.TrxReport referenced; writes bin/.../TestResults/*.trx (verified)
dotnet test --coverage                          # needs Microsoft.Testing.Extensions.CodeCoverage referenced; writes a .coverage file (verified)
dotnet test --minimum-expected-tests 5          # whole-run floor; add "-- --minimum-expected-tests 2" for a per-module floor too
```

**Extensions aren't built in.** Pass `--report-trx` to an app that never referenced `Microsoft.Testing.Extensions.TrxReport` and MTP fails with **exit code 5** (unrecognized option) rather than silently ignoring it. `dotnet test --help` only shows the options the *targeted* app actually registers.

### Exit codes (verified on 10.0.401 + MSTest 4.0.2)

| Code | Meaning | Verified how |
|---|---|---|
| `0` | success | passing run |
| `2` | at least one test failed | `Assert.Fail` in a `[TestMethod]` |
| `3` | aborted / timed out | doc-verified (`--timeout`, double Ctrl+C) |
| `5` | unrecognized option — extension not referenced | doc-verified |
| `8` | zero tests ran | `--filter` matching nothing — `Test run completed with non-success exit code: 8` |
| `9` | `--minimum-expected-tests` not met | `--minimum-expected-tests 5` against a 1-test project — `exit code: 9` |
| `13` | `--maximum-failed-tests` limit reached | doc-verified (.NET 11 Preview 7+, MTP 2.4+) |

`--ignore-exit-code <N>` (or a comma list) maps a specific code back to `0` — verified `--ignore-exit-code 2` turns a failing run's process exit code from `2` to `0` while the summary still reports the failure. Use it deliberately (e.g. suppressing 8 for an intentionally test-less scaffold project), not to paper over real failures.

### Config file, not runsettings

MTP has no `.runsettings` equivalent in the VSTest sense. Per-run configuration is `testconfig.json` (pointed at with `--config-file`) plus whatever command-line options the referenced extensions expose; there's no XML data-collector pipeline.

## Which frameworks support what (verified/doc-cross-checked)

| Framework | VSTest | MTP | How to get MTP |
|---|---|---|---|
| TUnit | no | **only** | always — TUnit has no VSTest adapter |
| MSTest | yes (default via `dotnet new mstest`) | yes | `EnableMSTestRunner=true`, or the `MSTest.Sdk` project SDK (sets it automatically); MSTest ≥ 3.2 |
| NUnit | yes (default) | yes | `EnableNUnitRunner=true`; `NUnit3TestAdapter` ≥ 5.0, or `dotnet new nunit --test-runner Microsoft.Testing.Platform` |
| xUnit v3 | possible but atypical | **default** (11 SDK templates) | native — `UseMicrosoftTestingPlatformRunner=true`, or `dotnet new xunit --xunit-version v3` |
| xUnit v2 | **only** | no | not supported; migrate to xUnit v3 for MTP |

See `csharp-tunit` and `csharp-nunit` for the framework-level testing conventions; this skill only covers the platform underneath them.

## Migrating a project from VSTest to MTP

1. Add the runner-enabling property (`EnableMSTestRunner`/`EnableNUnitRunner`/`UseMicrosoftTestingPlatformRunner`) or switch to the framework's MTP-first SDK/template.
2. Set (or let the package set) `OutputType=Exe`. The project now produces a real executable, not just a library — anything that referenced the test DLL as a library dependency needs `IsTestingPlatformApplication=false` set on *that* project instead, or it will pick up an unwanted transitive entry point (see `error CS8892` in MTP troubleshooting docs).
3. Replace `.runsettings` usage with `testconfig.json` / command-line options per extension.
4. Replace `coverlet.collector` + `--collect:"XPlat Code Coverage"` with `Microsoft.Testing.Extensions.CodeCoverage` + `--coverage` (Microsoft's MTP-native coverage extension), or `coverlet.MTP` if you need coverlet specifically on MTP.
5. Replace `--logger trx` / `-l trx` with `Microsoft.Testing.Extensions.TrxReport` + `--report-trx`.
6. Drop `--collect "Code Coverage"` / `--blame-*` / `--diag` — those are VSTest-specific; MTP's equivalents are `Microsoft.Testing.Extensions.CrashDump`/`HangDump` (`--crashdump`, `--hangdump`, `--hangdump-timeout`) and `--diagnostic`/`--diagnostic-output-directory`.
7. Update CI: exit-code handling changes (0/1 → the MTP table above), TRX/coverage output paths move under `bin/<config>/<tfm>/TestResults/` (or `<ArtifactsPath>/test/<project>/<pivot>` with the SDK artifacts layout) instead of the project-root `TestResults/`.
8. Opt the whole repo in via `global.json`'s `"test": {"runner": "Microsoft.Testing.Platform"}` only once every test project in every solution touched by `dotnet test` has migrated — a straggler VSTest project makes the whole `dotnet test` invocation fail outright.

For solutions that mix frameworks (e.g. MSTest and xUnit) that are all individually MTP, but need different extension args, set `TestingPlatformCommandLineArguments` per-project instead of passing shared args after `dotnet test --`.

## Gotchas

- **A solution can't mix MTP and VSTest projects under one `dotnet test` invocation.** Not "supported but weird" — it's rejected outright once `global.json` opts into MTP, and even the VSTest-mode bridge (`TestingPlatformDotnetTestSupport`) docs explicitly warn against solutions with both.
- **`--no-build` is risky in both platforms** for the same reason `csharp-tunit`/`csharp-verification` call out: it skips the build that would otherwise catch a stale binary, and for MTP specifically `--no-build` also implies `--no-restore`, so a project whose classification depends on a fresh restore (`IsTestingPlatformApplication`) can silently misclassify.
- **Running a single test fast**: for an MTP project, skip `dotnet test`'s orchestration entirely — `dotnet run --project tests/X -- --treenode-filter "..."` (TUnit) or `-- --filter "..."` (MSTest/NUnit/xUnit v3) rebuilds only that project and executes it directly. For VSTest, `dotnet test --filter ...` still goes through the full vstest.console/testhost spin-up; there's no equivalent shortcut because there's no standalone executable to run.
- **Parallelism across assemblies is unrelated to parallelism within a test.** For solution-level or multi-targeted runs, `TestTfmsInParallel` (VSTest, default `true` since .NET 9) and `--max-parallel-test-modules` (MTP, defaults to `Environment.ProcessorCount`) control how many test assemblies run concurrently; each framework's own in-process parallelism (e.g. TUnit's per-test parallel-by-default model, see `csharp-tunit`) is a separate, per-assembly setting.
- **Template-generated `.csproj` files can lie about their own classification** (see the MSTest 3.6.4 example above) — verify with `dotnet msbuild -getProperty:IsTestingPlatformApplication` after restore rather than trusting the presence of `EnableMSTestRunner=true` alone.
- **Output format is a reliable tell for which of the three modes you're in** (plain VSTest testhost banner / VSTest-bridge banner / native-MTP banner) when a flag mysteriously doesn't work — check which one you're actually looking at before assuming the flag is wrong.

## Checklist

- [ ] Confirmed the project's platform with `dotnet msbuild -getProperty:IsTestingPlatformApplication,IsTestProject` — not by reading the `.csproj` alone
- [ ] Confirmed which `dotnet test` mode applies (native MTP / VSTest-bridge / plain VSTest) by matching the console banner, if a flag isn't behaving as expected
- [ ] Framework-appropriate filter syntax used: `--filter` (VSTest/MSTest/NUnit/xUnit-on-MTP) vs `--treenode-filter` (TUnit)
- [ ] Test-app-specific args placed after a literal `--` in MTP mode; runsettings `NAME=VALUE` pairs after `--` in VSTest mode
- [ ] Extension packages referenced for any flag used (`Microsoft.Testing.Extensions.TrxReport`/`CodeCoverage`/`CrashDump`/`HangDump`) — an unregistered option is exit code 5, not a silent no-op
- [ ] CI exit-code handling matches the platform in use (0/1 for VSTest; 0/2/3/5/8/9/13 for MTP) rather than assuming pass/fail is always 0/1
- [ ] No VSTest and MTP projects mixed under one `dotnet test` invocation once `global.json` opts into MTP
- [ ] `--no-build` avoided when classification or freshness matters (see `csharp-verification`)
