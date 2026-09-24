---
name: dotnet-slnx
description: Use when creating, converting, editing, or troubleshooting a .NET solution file — choosing .slnx vs .sln, running `dotnet new sln`/`dotnet sln add|remove|list|migrate`, hand-editing solution XML, wiring solution filters (.slnf), or fixing "multiple solution files" / legacy-tooling build errors. The detailed authority on solution file format and hygiene.
---

# .NET solution files: `.slnx` is the only format to create or keep

`.sln` is yesterday's news. `.slnx` is the only solution format to create in a new repo, and any `.sln` found in a repo you're already touching is legacy to migrate on sight, not an acceptable alternative to leave alone. There is no case for a *new* `.sln` file.

## Why

- **Plain XML**, not a bespoke text format — readable, diffable, and mergeable like any other project file.
- **No GUIDs.** `.sln` assigns a GUID per project and per build-configuration line; two branches adding projects independently collide on those lines constantly. `.slnx` has none of that.
- **No configuration/platform matrix noise.** `.sln` spells out every `{GUID}.Debug|Any CPU.ActiveCfg = ...` line per project per config. `.slnx` infers Debug/Release and the platform list from the referenced projects; you only add `<Configurations>`/per-project overrides when a project genuinely needs to diverge.
- **Folders are just nesting**, not synthetic solution-folder GUID entries.

Current tooling support:
- **.NET SDK CLI**: `slnx` support landed in .NET SDK 9.0.200 ([Introducing SLNX support in the .NET CLI](https://devblogs.microsoft.com/dotnet/introducing-slnx-support-dotnet-cli/)). Starting in **.NET 10**, `dotnet new sln` generates `.slnx` by default; pass `--format sln` to opt back into the legacy format if a specific tool genuinely can't read `.slnx` yet.
- **MSBuild**: solution builds against `.slnx` are supported from MSBuild 17.12; `before.<name>.slnx.targets`/`after.<name>.slnx.targets` customization files need MSBuild 17.14+.
- **Visual Studio**: opens/builds `.slnx` natively; VS 2026 adds a **Default Solution File Format** option (Tools > Options > Projects and Solutions > General) to make `.slnx` the default for new solutions.
- **Rider**: `.slnx` support shipped in the 2024.2.6/2024.3 line (JetBrains blog, [Support for SLNX Solution Files](https://blog.jetbrains.com/dotnet/2024/10/04/support-for-slnx-solution-files/)) — recent Rider versions are fine; anything older is not.
- **VS Code C# Dev Kit**: activates on and works with `.slnx` workspaces; if a specific old Dev Kit build doesn't, updating the extension is the fix, not reverting to `.sln`.

## New solutions

```bash
dotnet new sln -n MyRepo          # .slnx by default on .NET 10 and .NET 11 SDKs
dotnet new sln -n MyRepo --format slnx   # explicit, same result
```

`dotnet new sln --help` shows `-f|--format <sln|slnx>` with **`Default: slnx`** on .NET 10 and .NET 11 SDKs. A brand-new solution is just:

```xml
<Solution>
</Solution>
```

Never pass `--format sln` for a new solution unless a named, specific tool in the repo's pipeline cannot yet read `.slnx` — and if you hit that, treat it as a reason to upgrade or replace the tool (see Gotchas), not a reason to standardize on `.sln`.

## Migrating an existing `.sln`

```bash
dotnet sln MyRepo.sln migrate
```

This generates `MyRepo.slnx` next to the `.sln`, printing `.slnx file .../MyRepo.slnx generated.` and producing valid XML, including an inferred `<Configurations>` block with the platforms the old file declared.

Do the whole migration as one change, not just the `migrate` step:

1. Run `dotnet sln <file>.sln migrate`.
2. **Delete the `.sln`** in the same commit. Never keep both — they drift the moment someone edits only one, and see the build-error gotcha below.
3. Update every place that names the `.sln` explicitly: CI workflow YAML (`dotnet build MyRepo.sln`, `dotnet test MyRepo.sln`), local build scripts, README "getting started" snippets, `global.json`-adjacent tooling or wrapper scripts, IDE launch/run configs that hardcode the path.
4. Search the repo for the literal `.sln` string (not just `*.sln` files) to catch references inside scripts and docs.

## Format essentials

```xml
<Solution>
  <Folder Name="/src/">
    <Project Path="src/MyApp/MyApp.csproj" />
    <Project Path="src/MyApp.Core/MyApp.Core.csproj" />
  </Folder>
  <Folder Name="/tests/">
    <Project Path="tests/MyApp.Tests/MyApp.Tests.csproj" />
  </Folder>
  <Folder Name="/">
    <File Path="README.md" />
    <File Path="Directory.Build.props" />
  </Folder>
</Solution>
```

- `<Folder Name="/src/">` is inferred automatically from the project's path when you `dotnet sln add` — you don't hand-place projects into folders for the common case.
- Add `<Configurations>`/per-project `<Platform>`/`<BuildType>` overrides only when a project genuinely needs a non-default configuration or platform; the default (Debug/Release, no platform matrix) is inferred and needs no XML.
- Solution items (files with no build action, e.g. `README.md`) go under `<File Path="..."/>` inside a folder, same as `.sln` "Solution Items".

## Editing: prefer the CLI, hand edits are fine when you need them

```bash
dotnet sln MyRepo.slnx add src/NewProject/NewProject.csproj
dotnet sln MyRepo.slnx add src/NewProject/NewProject.csproj -s src   # into an explicit solution folder
dotnet sln MyRepo.slnx remove src/OldProject/OldProject.csproj
dotnet sln MyRepo.slnx list
```

The CLI writes project paths **relative to the `.slnx` file, with forward slashes**, even when invoked from a subdirectory or given an absolute path on input (e.g. `src/Demo.Lib/Demo.Lib.csproj`). Folders (`<Folder Name="/src/">`) are created and removed automatically as projects are added/removed.

Because `.slnx` is plain, well-formed XML, a hand edit (adding a `<Project Path="..."/>` line) is low-risk and fine for quick fixes — just keep the same relative-forward-slash convention the CLI uses, and prefer the CLI when you can, since it also validates names (invalid Windows filenames, duplicate names, length limits) for you.

## Solution filters (`.slnf`)

`.slnf` is not a legacy format and is not replaced by `.slnx`. It serves a different purpose: a named subset of a solution's projects, so you can open, build or test part of a large repo (for example `Core.slnf` or `Windows.slnf`). Use it alongside the `.slnx`, which stays the single source of truth for the full project list. Point every filter at the `.slnx`.

- `dotnet new slnf -n MyFilter -s MyRepo.slnx` creates the filter file, pointing at the given parent solution (`-s|--parent-solution`, defaulting to `<name>.slnx`). The `slnf` template is new in .NET 11 — it doesn't exist on the .NET 10 SDK.
- `dotnet sln <file>.slnf list|add|remove` all work directly against a `.slnf`, editing its `projects` array. Support for `.slnf` was introduced incrementally: `list` in .NET SDK 9.0.3xx, `add`/`remove` in .NET 11.
- `dotnet sln <file>.slnf add <path>` writes the added project path with **backslashes** (`src\\Demo.Lib\\Demo.Lib.csproj`) even when run on Linux — `.slnf` is JSON that historically follows Windows path conventions; don't "fix" this to forward slashes by hand, and don't be surprised by it in a diff.
- `dotnet build MyFilter.slnf` / `dotnet test MyFilter.slnf` work like they do against a solution file directly.
- When a folder holds both a `.slnx` and `.slnf`, commands that pick a file implicitly use the `.slnx`. Always name the filter explicitly (`dotnet build Core.slnf`) when you mean the subset.

## Gotchas

- **Both `.sln` and `.slnx` in the same folder → build fails.** `dotnet build` with no argument in a folder containing both reports `MSB1011: Specify which project or solution file to use because this folder contains more than one project or solution file.` This is the concrete failure mode of keeping both during a slow migration — don't; delete the `.sln` in the same change as the `migrate`.
- **Tooling that only reads `.sln`.** Some third-party build/CI tasks, older MSBuild/Visual Studio installs, and older VS Code C# Dev Kit builds may not understand `.slnx` yet. Check by grepping CI workflows and build scripts for `.sln` usage and by checking the tool's own changelog/version requirements (MSBuild 17.12+, SDK 9.0.200+ CLI, current Rider/VS Code Dev Kit). The fix is to **upgrade the tool**, not to keep a `.sln` around for it — a stray `.sln` immediately reintroduces the drift risk above.
- **`dotnet sln`/`dotnet build`/`dotnet test` with no explicit file** search the current directory and use the one solution/filter file they find; if none or more than one exist, they error rather than guess. Always pass the file explicitly in CI scripts even when there's only one, so the script doesn't silently start failing the day a second file appears.

## Checklist

- [ ] New repo: `dotnet new sln` used with no `--format sln` override — result is `.slnx`
- [ ] Existing `.sln` found while working in a repo: migrated with `dotnet sln <file>.sln migrate`, `.sln` deleted in the same change
- [ ] No repo has both `.sln` and `.slnx` side by side after the change
- [ ] CI workflows, build scripts, README snippets, and `dotnet build`/`dotnet test` invocations updated to the `.slnx` filename
- [ ] Projects added/removed via `dotnet sln add|remove`, not error-prone manual GUID-style edits (hand edits of the simple XML are fine when the CLI isn't handy)
- [ ] Any `.slnf` files point at the `.slnx`, not a leftover `.sln`
- [ ] Any tool that still requires `.sln` is flagged for upgrade, not used as a reason to keep `.sln` in the repo
