---
name: dotnet-file-based-apps
description: Use whenever you need to run throwaway C#/.NET code — an allocation or timing probe, a Roslyn query or rewriter, an API-shape spike, a compiler-behaviour test, an analyzer false-positive repro. .NET 10+ runs a single `.cs` file directly via `dotnet run app.cs`, with NuGet packages, SDK and MSBuild properties declared inline as `#:` directives. Never scaffold a `.csproj` + `Program.cs` pair for throwaway work.
---

# File-based apps — the default way to run throwaway .NET code

**A `.csproj` + `Program.cs` pair is the wrong shape for a throwaway program.** Since .NET 10, `dotnet run app.cs` builds and runs a single C# file with no project file at all. Packages, SDK and MSBuild properties are declared inline. One file, one command, nothing to clean up.

Reach for this for: allocation and timing probes, Roslyn discovery and rewriter programs, "does the compiler actually allow this?" spikes, analyzer false-positive repros, and any other few-dozen-line program whose whole purpose is to answer one question and then be deleted. The same file is also the right tool for ad-hoc scripting that would otherwise reach for Python/Node/shell — file IO, HTTP calls, JSON/XML/CSV parsing, process control. Parse structured data with a real parser instead of pattern-matching it with regex; a `.cs` file with `System.Text.Json`/`System.Xml`/a CSV package is more reliable than a line-oriented text filter and no harder to write.

## The shape

```csharp
#:package Microsoft.CodeAnalysis.CSharp@4.14.0

using Microsoft.CodeAnalysis.CSharp;

var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(args[0]));
Console.WriteLine(tree.GetRoot().DescendantNodes().Count());
```

```bash
dotnet run probe.cs               # also: dotnet probe.cs, or dotnet run --file probe.cs
dotnet run probe.cs -- arg1 arg2  # args after --
```

## Directives

All go at the top of the file, prefixed `#:`.

| Directive | Purpose |
| --- | --- |
| `#:package Name@Version` | NuGet reference. `@*` for latest. A bare name only resolves under central package management |
| `#:property Key=Value` | Any MSBuild property — `TargetFramework`, `LangVersion`, `Optimize`, `PublishAot` |
| `#:sdk Microsoft.NET.Sdk.Web` | Non-default SDK |
| `#:project ../Lib/Lib.csproj` | Reference a real project — how you probe *this repo's* types |
| `#:include helpers.cs` | Pull in another file (SDK 10.0.300+). Cannot add top-level statements |

Pipe from stdin for one-liners: `echo 'Console.WriteLine(1);' | dotnet run -`. Stdin mode materializes the piped source into its own runfile cache directory rather than building in place, so it does **not** pick up a `Directory.Build.props`/`Directory.Packages.props`/`nuget.config` from the caller's current directory the way `dotnet run file.cs` picks one up from the file's own directory and parents — `global.json` SDK pinning still applies to both. See `dotnet-run` for the verified details.

Pass environment variables to the run without exporting them in the shell: `dotnet run probe.cs -e FOO=bar` (repeatable). Format a file-based app the same way as a project: `dotnet format probe.cs` (SDK 11+) runs analyzers/style fixers directly against the single file — no `.csproj` needed for either.

## .NET 11: multi-file apps (net11.0+ SDK)

`#:include` (present since SDK 10.0.300) is unchanged in shape, but SDK 11 rounds out the workflow around it:

- `dotnet format <entry>.cs` now works on a file-based app and fixes style/analyzer issues across the entry file and its `#:include`d files together.
- Native AOT publishes of file-based apps can **reuse prior publish output**, so iterating on a probe that publishes AOT (`dotnet publish app.cs -p:PublishAot=true`) is noticeably faster on the second and later runs — no code change needed to opt in.
- `dotnet project convert app.cs` still generates the equivalent multi-file `.csproj`/`Program.cs` layout when a probe graduates past throwaway, `#:include` files and all.

## The two traps that will bite you

**1. Implicit build files are inherited from parent directories.** A file-based app picks up `Directory.Build.props`, `Directory.Packages.props`, `nuget.config` and `global.json` from its own directory *and every parent*. Drop a probe inside a repo and it inherits that repo's `TreatWarningsAsErrors`, its full analyzer set, and its central package versions — so your ten-line probe fails on the repo's style rules instead of answering your question.

**Put throwaway probes outside the repo tree** (the session scratchpad is ideal). Use `#:project` to reach back in when you need the repo's own types. If you must keep one inside a repo, give it its own directory with an isolating `Directory.Build.props`.

**2. Native AOT is on by default.** File-based apps set `PublishAot=true`. `dotnet run` still JIT-runs the file, but the AOT defaults apply: trim and AOT analyzers report warnings, and reflection-based `System.Text.Json` is disabled. Treat an AOT warning or reflection failure as a prompt to use the AOT-friendly API, not to switch AOT off:

- `System.Text.Json`: a source-generated `JsonSerializerContext` (below), or `JsonDocument`/`JsonNode` for read-only poking.
- Regex: `[GeneratedRegex]`. Logging: `[LoggerMessage]`. Configuration binding: the configuration binder source generator. See `csharp-json-source-generation` and `csharp-aot-trimming`.
- Replace your own reflection (`Activator.CreateInstance`, `Type.GetType(string)`, `MakeGenericType`) with generics or a direct call.

Add `#:property PublishAot=false` only when a dependency you can't change fails at runtime because it depends on reflection or dynamic code, and has no source-generated alternative. BenchmarkDotNet harnesses that load types with `Activator.CreateInstance` are a real example. Roslyn syntax probes and `MSBuildWorkspace` probes run fine with the default. It's the last resort, never the first reaction to a warning.

## Caching

Builds are cached under the temp directory, keyed on file content, directives, SDK version, and implicit build files. Two consequences:

- Editing a `Directory.Build.props` may not invalidate the cache. Force it: `dotnet clean probe.cs && dotnet build probe.cs`.
- Running the same app concurrently contends over build outputs. Build once, then run with `--no-build`.

Clear everything with `dotnet clean file-based-apps` (`--days N` to bound it).

## `System.Text.Json` needs a source-generated context

File-based apps run with reflection-based serialization disabled, so `JsonSerializer.Serialize(new { … })` or a bare `Deserialize<T>(…)` throws `InvalidOperationException: Reflection-based serialization has been disabled for this application` (verified on SDK 11.0.100-rc.1 with `dotnet run`). Anonymous types can't be source-generated, so declare a real type, add a `[JsonSerializable]` partial `JsonSerializerContext` in the same file, and pass its generated `TypeInfo` to the call:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

var body = JsonSerializer.Serialize(new Req("model", "prompt"), AppJson.Default.Req);
var res  = JsonSerializer.Deserialize(text, AppJson.Default.Res);

internal sealed record Req(string model, string prompt);
internal sealed record Res(string id);

[JsonSerializable(typeof(Req))]
[JsonSerializable(typeof(Res))]
internal partial class AppJson : JsonSerializerContext;
```

Reading only? `JsonDocument`/`JsonNode` need no context — reach for those to poke at a response without declaring types.

## Calling a reference implementation in another language

When the goal is to compare against a reference implementation's own library (a formatter, a parser, a linter written in another language) rather than to script something, a single `.cs` app can own the whole comparison: it spawns the other runtime with `System.Diagnostics.Process`, passes the input as JSON on stdin, reads the result as JSON from stdout, and parses it with a `JsonSerializerContext` as above. The other language is a thin adapter around its library — no logic, no file editing — and the `.cs` app is what decides the process arguments, working directory, and how the result is asserted against.

## When a real project is still right

Eject when the thing stops being throwaway: multiple source files that need to co-evolve, something you'll commit and maintain, or anything needing a test project. `dotnet project convert app.cs` generates the equivalent `.csproj` from your directives.

Benchmarks are the notable exception — BenchmarkDotNet wants a real Release-configured project, so `csharp-performance` still applies there. A quick allocation *probe* using `GC.GetAllocatedBytesForCurrentThread` is fine as a file-based app; a committed benchmark suite is not.

## Related

- `roslyn-discovery`, `roslyn-rewriters`, `roslyn-duplicate-detection` — all built on this harness
- `csharp-assembly-inspection` — same harness, pointed at compiled output
- `csharp-performance` — for real benchmarks rather than throwaway probes
