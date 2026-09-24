---
name: dotnet-run
description: Use whenever you need to run a C#/.NET project from source during development — picking a project when a folder has more than one, passing arguments to the app instead of to `dotnet run`, choosing a launch profile, targeting a framework in a multi-targeted project, or deciding between `dotnet run`, `dotnet <dll>`, and a published apphost. Covers project-based `dotnet run`; for single-file `.cs` apps use `dotnet-file-based-apps` instead.
---

# dotnet run

**`dotnet run` is a development convenience, not a deployment mechanism.** It restores, builds, then launches the build output in one command. For anything that isn't interactive iteration — CI, containers, production — use `dotnet publish` and run the resulting apphost or `dotnet app.dll` directly. `dotnet run` resolves dependencies from the NuGet cache on the machine it runs on; a production host may not have that cache.

For single-file `.cs` apps (`dotnet run app.cs`, no `.csproj`), see `dotnet-file-based-apps` — that skill covers directives, caching, and AOT in depth. This skill is about running a real project.

## What actually happens

1. **Implicit restore** — unless `--no-restore`.
2. **Build** (`dotnet build` under the hood) — unless `--no-build`, which also implies `--no-restore`.
3. **Locate the run target** via the project's `RunCommand`/`RunArguments`/`RunWorkingDirectory` MSBuild properties (auto-derived from `OutputType`/`TargetPath` for a normal exe; override them for unusual launch shapes, e.g. `RunCommand=mono`).
4. **Launch** that command, with argv, environment, and working directory assembled from the CLI, any launch profile, and ambient state (see below).

Output goes to `bin/<Configuration>/<TargetFramework>/` as usual; nothing about `dotnet run` changes where `dotnet build` puts files.

```
Do:    dotnet run                     # iterate on a project
Do:    dotnet publish -c Release ...  # then dotnet ./out/app or the apphost — for real deployment
Don't: dotnet run                     # ...inside a container ENTRYPOINT or a CI "run the app" step
```

## Project selection

- No `--project`: uses the current directory. Exactly one `.csproj`/`.vbproj`/`.fsproj` must be there, or `dotnet run` fails with "Couldn't find a project to run."
- **More than one project file in the directory is an error** — `dotnet run` will not guess; pass `--project`.
- `--project <path>` accepts a project file or a directory containing exactly one. **It does not accept a `.sln`/`.slnx`** — pointing it at a solution fails with an MSBuild error (`<Solution>` element unsupported), not a helpful message. Pick the project.
- Multi-targeted project (`<TargetFrameworks>` plural) and no `-f`: `dotnet run` prompts/lists the available frameworks and exits rather than picking one. Always pass `-f|--framework <TFM>` for multi-targeted projects in scripts.

### File-based apps vs. a project in the same folder

If the directory *also* has a `.csproj`, passing a `.cs` file as the first argument does **not** switch to file-based mode — `dotnet run` treats it as an application argument to the project and warns:

```
Warning: 'standalone.cs' appears to be a file-based app but was passed as an
argument to the project '.../proj.csproj'. To run it as a file-based app, use
'dotnet run --file standalone.cs'. To pass it as an application argument, use
'dotnet run -- standalone.cs' to suppress this warning.
```

Use `--file <path.cs>` explicitly to force file-based mode when a project file is also present. With no project file present, `dotnet run app.cs` (or bare `dotnet app.cs`) detects file-based mode on its own — see `dotnet-file-based-apps`.

## Arguments: yours vs. the app's

Everything after a literal `--` goes to the application, untouched, in order. This is the only reliable way to pass app arguments — anything before `--` that `dotnet run` doesn't recognize is *also* forwarded, but a recognized option (like `--project`) sitting between two of your tokens gets stripped out first, which can silently reassociate the tokens around it.

```
Do:    dotnet run -- --input sample.txt --verbose
Don't: dotnet run --input sample.txt          # if --input isn't a dotnet-run option this "works" by luck;
                                               # it breaks the moment a future SDK adds an --input option,
                                               # or the moment another recognized flag sits in between
```

`-c`, `-p`/`--property`, `-r`, `-f`, etc. are dotnet-run options *unless* they appear after `--`. `dotnet run -- --property name=value` passes `--property name=value` to the app; without `--` it's parsed as an MSBuild property assignment instead.

`-p` is overloaded: with an `=` in its argument it's short for `--property` (`-p:Config=Release` or `-p Config=Release`); without one it's the deprecated short form of `--project` (still accepted, but write `--project` in scripts — it's clearer and not slated for removal).

## Inline C# from stdin (`dotnet run -`)

`dotnet run -` reads the whole program from standard input instead of a file — the `npx`/`python -` pattern, useful for CI steps that need a few dozen lines of real C# (typed args, `File`, `Process`) instead of fragile bash. Introduced in .NET SDK 10.0.100; present on both 10.0.401 and 11.0.100-rc.1 here. It runs through the same file-based-app machinery as `dotnet run app.cs` — see `dotnet-file-based-apps` for directives and caching mechanics.

**Heredoc** (preferred in scripts — keeps the program readable in place) and **pipe** both work, verified:

```bash
dotnet run - -- alpha beta <<'CS'
if (args is not [var a, var b]) { return 2; }
Console.WriteLine($"{a}/{b}");
CS

echo 'Console.WriteLine("hi");' | dotnet run -
cat probe.cs | dotnet run -
```

**Quote the heredoc marker — `<<'CS'`, not `<<CS`.** An unquoted marker lets the shell expand `$VAR` and `` `cmd` `` inside the C# *before* the compiler ever sees it — verified: `Console.WriteLine("$NAME");` in an unquoted heredoc came out with the shell's value of `$NAME` baked into the source (including inside a C# `$"..."` interpolated string, which made the shell eat the very `$` that C# also uses for interpolation). With `<<'CS'`, `$NAME` reaches the compiler literally; read real values with `Environment.GetEnvironmentVariable("NAME")` instead.

**`cd` to a scratch directory first** (`$RUNNER_TEMP` in Actions, `mktemp -d` locally) before piping in the script, matching the two real workflows this pattern comes from. Verified what actually crosses the `cd`: **`global.json` SDK pinning is resolved from the caller's current directory** (pin a repo's SDK feed control and it took effect on the stdin build), but — unlike a `.cs` file run in place — **`Directory.Build.props`/`Directory.Packages.props`/`nuget.config` from the caller's directory did *not* apply** in testing, because `dotnet run -` materializes the piped source into its own cache directory under the runfile cache, not the caller's directory, and MSBuild's implicit-import walk starts there. Still `cd` out of the repo: it keeps the program's own relative file I/O (`File.ReadAllText("./x")`) predictable and away from a build tree, and it's the one habit that's correct across every environment, not just the one you tested.

**Treat CI inputs as untrusted; never splice them into the C# text.** Pass them as environment variables (`env: SUITE: ${{ inputs.suite }}`) or as `dotnet run - -- "$SUITE" "$FILTER"` app arguments, then read them with `args is [var suite, var filter]` or `Environment.GetEnvironmentVariable`. Interpolating `${{ inputs.x }}` directly into the heredoc body is a script-injection hole — the value becomes literal C# text before the compiler runs. Write step outputs with `File.AppendAllLines(Environment.GetEnvironmentVariable("GITHUB_OUTPUT")!, [...])`; the program's `return n;` becomes the step's exit code, same propagation as any other `dotnet run`.

`#:package`/`#:property` directives at the top of the piped text work the same as in a file (`#:package Newtonsoft.Json@13.0.3` restored and ran cleanly, verified). Caching is the same content-hash mechanism as any file-based app, so a byte-identical rerun is fast — but each CI invocation typically differs (different args baked into env, or you tweak the script), so in practice you rarely get a cache hit, and there's no stable path for a later `--no-build` step to reuse. For a probe with real package dependencies, or anything you'll run more than a couple of times, save it as a checked-in `.cs` file instead of stdin — see `dotnet-file-based-apps`.

```csharp
using static System.Environment;

if (args is not [var suite, var filter]) { return 2; }
// ... use suite/filter, GetEnvironmentVariable("GITHUB_WORKSPACE"), etc.
return 0;
```

## Common options

| Option | Behaviour |
| --- | --- |
| `-c\|--configuration <CFG>` | Build/run configuration (default `Debug`). |
| `-f\|--framework <TFM>` | Required when the project multi-targets. |
| `-r\|--runtime`, `-a\|--arch`, `--os` | Target RID / architecture / OS for restore and run. |
| `--no-build` | Skip building; implies `--no-restore`. Requires the output to already exist for the requested config/TFM, or the launch fails with a "No such file or directory" process-start error. |
| `--no-restore` | Skip implicit restore only. |
| `--no-cache` | Skip up-to-date checks and always rebuild (mainly meaningful for file-based apps; see `dotnet-file-based-apps`). |
| `-lp\|--launch-profile <NAME>` | Select a named profile from launch settings (case-insensitive; ambiguous case-only differences error). |
| `--no-launch-profile` | Ignore launch settings entirely. |
| `-e\|--environment <K=V>` | Set an env var for the launched app only, not for `dotnet run` itself. Repeatable. Added in .NET SDK 9.0.200 — present on 10.0.401 and 11.0.100-rc.1, both verified here; not available on older 9.x SDKs before .200. |
| `-v\|--verbosity <LEVEL>` | MSBuild verbosity for the build step. |
| `-p\|--property:<K>=<V>` | MSBuild properties, e.g. `-p:Configuration=Release`. |
| `--interactive` | Allow the build to pause for credential prompts etc. |
| `--device` / `--list-devices` | Device targeting/listing for mobile-style workloads; present (and generally inert for ordinary console/library projects) on both 10.0.401 and 11.0.100-rc.1 SDKs. |

## launchSettings.json

Put profiles in `Properties/launchSettings.json` (`My Project/launchSettings.json` for VB). Top-level `profiles` object, one entry per named profile:

```json
{
  "profiles": {
    "Local": {
      "commandName": "Project",
      "commandLineArgs": "--input sample.txt",
      "environmentVariables": { "FOO": "bar" },
      "applicationUrl": "http://localhost:5005"
    }
  }
}
```

Two `commandName` values apply to `dotnet run`:

| `commandName` | Behaviour |
| --- | --- |
| `Project` | Build the project, run the command the project produces. `commandLineArgs` only applies when neither the CLI nor the project itself supplies arguments. |
| `Executable` | Run `executablePath` directly (still builds first unless `--no-build`). `workingDirectory`, if relative, resolves against the launch-settings file's directory, not the project directory. |

Other `commandName` values (e.g. IDE-specific ones) are silently skipped when `dotnet run` is auto-selecting a default profile, and error if selected explicitly by name.

Verified behaviour:

- **Default profile**: the first profile in file order whose `commandName` is one `dotnet run` supports (`Project` or `Executable`). No name needed to use it.
- **`environmentVariables`** land in the launched process; **explicit `commandLineArgs` on the CLI override the profile's**, but the profile's still apply when you don't pass any.
- **`applicationUrl`** sets `ASPNETCORE_URLS` in the launched process (an explicit `ASPNETCORE_URLS` in `environmentVariables` or via `-e` wins over it).
- `dotnet run` sets `DOTNET_LAUNCH_PROFILE` to the chosen profile's name in the child process.
- Precedence, lowest to highest: ambient OS env → launch-profile `environmentVariables` → `-e`/`--environment`.
- **`dotnet <app>.dll` ignores `launchSettings.json` entirely** — verified: none of the profile's env vars, args, or `DOTNET_LAUNCH_PROFILE` show up when launching the built DLL directly. Launch settings are a `dotnet run`/IDE-only concept.
- File-based apps use a flat `[AppName].run.json` next to the source file instead of `Properties/launchSettings.json` — same profile schema. See `dotnet-file-based-apps`.

## Working directory and exit codes

Verified:

- The launched app's **working directory is the caller's current directory** (wherever you invoked `dotnet run` from), *not* the project directory — even when running via `--project ../other/dir`. Override with `RunWorkingDirectory` in the project, or `workingDirectory` in an `Executable`-type launch profile.
- `dotnet run` **propagates the app's exit code** (returning `42` from `Main`/top-level code surfaces as `dotnet run`'s exit code).
- stdin/stdout/stderr pass through directly — interactive console apps work normally under `dotnet run`.
- Ctrl+C is forwarded to the child process like any other console app; there's no extra shutdown hook from `dotnet run` itself.

## Performance

- `dotnet run` builds on every invocation (an incremental build if nothing changed, but it still pays MSBuild evaluation overhead). For a tight edit/run loop where you're *not* changing code between runs — e.g. iterating on CLI args, or a script that runs the same binary many times — build once and add `--no-build` on the repeat runs.
- Build-server/MSBuild node reuse (the background `dotnet build` server process) is the default; `--disable-build-servers` forces a cold in-process build if you suspect stale server state.
- File-based apps have their own on-disk build cache (keyed on file content, directives, SDK version, implicit build files); `dotnet clean file-based-apps` or `dotnet clean <file>.cs && dotnet build <file>.cs` clear/force it. See `dotnet-file-based-apps` for the details — this doesn't apply to ordinary projects, which use the normal `obj`/`bin` incremental build.

## Gotchas

- **Directory.Build.props/targets, Directory.Packages.props, nuget.config, global.json are inherited from every parent directory** — verified for project-based runs too, not just file-based apps (`csharp-verification` and `dotnet-file-based-apps` cover the file-based case). A project nested under a repo picks up that repo's analyzers, warning levels, and central package versions.
- **Microsoft.Testing.Platform (MTP) test projects are executables** (`OutputType=Exe`), so `dotnet run` on one *works* — it builds and runs the test binary directly, bypassing the `dotnet test`/VSTest orchestration layer (filtering, `--results-directory`, IDE test-explorer integration, etc.). Prefer `dotnet test` for anything beyond "does this compile and pass"; reach for `dotnet run` on a test project only for a quick single-run check. See `csharp-tunit`.
- `DOTNET_ENVIRONMENT`/`ASPNETCORE_ENVIRONMENT` come from wherever you set them — ambient shell, or a launch profile's `environmentVariables` — `dotnet run` applies no default environment name of its own.
- A multi-targeted project without `-f` doesn't silently pick one; don't assume CI scripts using bare `dotnet run` will keep working after adding a second `<TargetFrameworks>` entry.

## Checklist

- [ ] Is this for interactive development? If it's CI, a container, or production, use `dotnet publish` output instead.
- [ ] More than one project file in the directory, or targeting a solution? Pass `--project <specific .csproj>`.
- [ ] Multi-targeted project? Pass `-f <tfm>`.
- [ ] Passing arguments to the app? Put them after a literal `--`.
- [ ] Need dev-time config (env vars, URL, args)? Use a `launchSettings.json` profile, not hardcoded flags, so `dotnet run` and an IDE agree.
- [ ] Running the same build repeatedly in a script? Build once, then `--no-build` on subsequent runs.
- [ ] Single throwaway `.cs` file, no project? Use `dotnet-file-based-apps` instead of scaffolding a project.
