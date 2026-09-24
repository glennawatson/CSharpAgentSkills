# CSharpAgentSkills

Agent skills for C# and .NET work: modern language features, performance, async, testing, Roslyn analysis and rewriting, build and project configuration, and a verification-first workflow.

Each skill is a folder under [`skills/`](skills) holding a `SKILL.md` (YAML frontmatter with `name` and `description`, then Markdown instructions) and optional companion files such as runnable `examples/*.cs`. This is the open [Agent Skills](https://agentskills.io) format, so the same folders work with any agent that loads `SKILL.md` skills. The agent reads each skill's `description` and loads the full skill when a task matches it.

The skills target .NET 10 and C# 14 by default, with clearly marked sections for .NET 11 and C# 15. Most examples run as single-file apps (`dotnet run app.cs`), which need the .NET 10 SDK or later.

## Skills

### Language

| Skill | What it covers |
| --- | --- |
| [csharp-language-versions](skills/csharp-language-versions) | Working out the effective C# version per target framework, the feature-by-version table, polyfills for older targets, and keeping preview features out of product code. |
| [csharp-polyfills](skills/csharp-polyfills) | Why `LangVersion` is independent of the target framework, which modern features work on older targets as-is, which need a polyfilled type, which are impossible, and how to write and wire polyfills. |
| [csharp-15-syntax](skills/csharp-15-syntax) | C# 15 in depth: union types (and their boxing of value-type cases), `closed` hierarchies, collection expression arguments, extension indexers, labeled `break`/`continue`. |
| [csharp-pattern-matching](skills/csharp-pattern-matching) | Switch expressions, property/relational/list patterns, real exhaustiveness, and when a pattern stops being readable. |
| [csharp-modern-types](skills/csharp-modern-types) | Choosing between class, record, record struct and readonly struct; primary constructor pitfalls; `required`, `init`, `with` and the `field` keyword. |
| [csharp-nullability](skills/csharp-nullability) | Writing nullable-enabled code properly: flow attributes, boundary guards, when `!` is acceptable, and `Try*` signatures. |
| [csharp-nullable-migration](skills/csharp-nullable-migration) | Turning nullable reference types on in an existing codebase: rollout order, safe Roslyn rewriters for the mechanical part, and per-site triage for the rest. |
| [csharp-static-lambdas](skills/csharp-static-lambdas) | What `static` on a lambda or local function really does: a compile-time guarantee of no captured state, not an optimisation in itself, plus the state-passing pattern and expression-tree caveats. |
| [csharp-discards](skills/csharp-discards) | Using `_` to say a value is deliberately unused: return values, `out _`, deconstruction, patterns and lambda parameters, when not to discard, and expression-tree limits. |
| [csharp-extension-members](skills/csharp-extension-members) | C# 14 `extension` blocks: extension properties, static members, operators and indexers. |
| [csharp-generics-modern](skills/csharp-generics-modern) | Generic math, static abstract members, `allows ref struct`, constraints, and avoiding boxing with constrained generics. |
| [csharp-collections-modern](skills/csharp-collections-modern) | Collection expressions, `params` spans, choosing a collection type, read-only APIs, `CollectionsMarshal`, and LINQ on hot paths. |
| [csharp-string-handling](skills/csharp-string-handling) | `string.Create` by .NET version, raw and UTF-8 literals, interpolation handlers, `SearchValues`, and comparison rules. |

### Design and correctness

| Skill | What it covers |
| --- | --- |
| [csharp-api-design](skills/csharp-api-design) | Public surface rules, `Try*` versus exceptions, binary versus source breaking changes, API tracking, and domain naming. |
| [csharp-guard-clauses](skills/csharp-guard-clauses) | Null and value guards with the .NET 8+ `ThrowIf*` helpers (`ThrowIfNull`, `ThrowIfNegative`, `ThrowIfGreaterThan` and the rest), where guards belong, the analyzer rules and fixers, and polyfills for older targets. |
| [csharp-error-handling](skills/csharp-error-handling) | What to catch and where, exception filters, rethrowing correctly, guard helpers, and throw helpers for hot paths. |
| [csharp-async](skills/csharp-async) | async/await correctness and performance: deadlocks, contexts, `ValueTask`, `ConfigureAwait`, and elision. |
| [dotnet-runtime-async](skills/dotnet-runtime-async) | .NET 11 runtime async (no compiler state machine): how to enable and detect it, what changes, and the Mono/WebAssembly warning for apps and libraries. |
| [csharp-disposable-patterns](skills/csharp-disposable-patterns) | Thread-safe `IDisposable`/`IAsyncDisposable`: idempotent `Interlocked` disposal, replaceable and composite disposables, disposal exceptions and ownership, with a rewriter for the classic `bool _disposed` pattern. |
| [csharp-concurrency-primitives](skills/csharp-concurrency-primitives) | `Lock`, `SemaphoreSlim`, `Interlocked`, channels, `ConcurrentDictionary` pitfalls, and lazy initialisation. |
| [csharp-docs](skills/csharp-docs) | Concise XML documentation and comments that describe current behaviour, not history. |
| [csharp-debugging](skills/csharp-debugging) | Hypothesis-driven diagnosis of bugs, exceptions and failing CI runs. |

### Performance

| Skill | What it covers |
| --- | --- |
| [csharp-performance](skills/csharp-performance) | Hot-path techniques and what the runtime now optimises for you by version, with helper classes in `helpers/`. |
| [csharp-data-layout](skills/csharp-data-layout) | Cache-friendly data design: struct-of-arrays, field ordering, flat data over inheritance. |
| [benchmarking](skills/benchmarking) | Trustworthy BenchmarkDotNet numbers, reading real differences, and EventPipe allocation traces. |
| [performance-bounded-deduplication](skills/performance-bounded-deduplication) | Removing duplication under hard allocation and time budgets, with Roslyn tooling and A/B harnesses. |

### Source generation, AOT and analyzers

| Skill | What it covers |
| --- | --- |
| [csharp-source-generators](skills/csharp-source-generators) | Incremental generators done right: cacheable pipelines, record models on netstandard2.0, testing cache hits, packaging. |
| [csharp-json-source-generation](skills/csharp-json-source-generation) | Source-generated System.Text.Json contexts, `HttpClient` and Refit usage, and the other reflection-to-generator swaps. |
| [csharp-aot-trimming](skills/csharp-aot-trimming) | Writing trim- and native-AOT-compatible code and verifying it with a real publish. |
| [csharp-logging-diagnostics](skills/csharp-logging-diagnostics) | `[LoggerMessage]`, structured logging, `ActivitySource` tracing and `Meter` metrics without allocations. |
| [dotnet-analyzers](skills/dotnet-analyzers) | Adding and configuring analyzers (StyleSharp, PerformanceSharp, SecuritySharp, CA, IDE, SYSLIB), severity precedence, and fixing rather than suppressing. |
| [roslyn-analyzer-performance](skills/roslyn-analyzer-performance) | Measuring and optimising analyzers, code fixes and generators, and authoring diagnostics. |

### Roslyn tooling

| Skill | What it covers |
| --- | --- |
| [roslyn-discovery](skills/roslyn-discovery) | Read-only Roslyn programs that answer structural questions: API surface, implementers, usages. |
| [roslyn-rewriters](skills/roslyn-rewriters) | Safe, structure-aware repo-wide edits with Roslyn rewriters instead of find-and-replace. |
| [roslyn-guard-rewriters](skills/roslyn-guard-rewriters) | Standalone rewriters ported from the CA1062 and CA1510–CA1513 analyzer and fixer source: add missing null guards at public boundaries and convert hand-written guards to `ThrowIf*` helpers, with a polyfill mode for older targets. |
| [roslyn-duplicate-detection](skills/roslyn-duplicate-detection) | Measuring duplication and finding shared-helper candidates locally. |
| [csharp-lsp](skills/csharp-lsp) | Using language-server data without trusting stale diagnostics over `dotnet build`. |
| [csharp-assembly-inspection](skills/csharp-assembly-inspection) | Questions about the built artifact: shipped types, resources, references and package contents. |

### Build, projects and tooling

| Skill | What it covers |
| --- | --- |
| [dotnet-project-hygiene](skills/dotnet-project-hygiene) | `Directory.Build.props`, `global.json`, language and analysis settings, `.editorconfig`, deterministic builds, and build-file comments. |
| [dotnet-central-package-management](skills/dotnet-central-package-management) | How CPM works: `Directory.Packages.props`, transitive pinning, `GlobalPackageReference`, lock files, source mapping, and error codes. |
| [dotnet-slnx](skills/dotnet-slnx) | `.slnx` as the only solution format, migrating from `.sln`, and `.slnf` filters for project subsets. |
| [dotnet-multi-targeting](skills/dotnet-multi-targeting) | Target framework and platform layout (`Platforms/` folders), framework detection in MSBuild, polyfills, and building Windows targets on Linux/macOS. |
| [dotnet-msbuild-usings](skills/dotnet-msbuild-usings) | Global usings and aliases in MSBuild, framework-conditioned aliases, and aliases as a migration seam between library versions. |
| [dotnet-run](skills/dotnet-run) | How `dotnet run` works, launch profiles, and running C# inline from stdin or a heredoc in scripts and CI. |
| [dotnet-file-based-apps](skills/dotnet-file-based-apps) | Throwaway single-file `dotnet run app.cs` programs in place of shell or Python scripts, and their AOT defaults. |

### Testing

| Skill | What it covers |
| --- | --- |
| [dotnet-test-platforms](skills/dotnet-test-platforms) | Microsoft.Testing.Platform versus VSTest, how `dotnet test` chooses, exit codes, and migrating. |
| [csharp-tunit](skills/csharp-tunit) | TUnit tests: async assertions, hooks, data sources, executors, and isolating global state. |
| [csharp-nunit](skills/csharp-nunit) | Modern NUnit 4: `Assert.EnterMultipleScope`, the constraint model, lifecycle, parallelism, and the MTP runner. |
| [csharp-verification](skills/csharp-verification) | The done gate: clean build, satisfied analyzers, passing tests, and coverage. |
| [tdd](skills/tdd) | Red/green/refactor on the real failing test. |

### Workflow

| Skill | What it covers |
| --- | --- |
| [executing-plans](skills/executing-plans) | Working through an agreed plan with real verification at each step. |
| [subagent-driven-development](skills/subagent-driven-development) | Orchestrating parallel workers to build a feature. |
| [dispatching-subagents](skills/dispatching-subagents) | Splitting independent work items across concurrent workers. |
| [commit-messages](skills/commit-messages) | Conventional Commits messages. |

## Installing

Clone the repository once, then link the skill folders into the directory your agent reads. Symlinks keep every agent on the same copy, and `git pull` updates them all.

```sh
git clone https://github.com/glennawatson/CSharpAgentSkills.git ~/source/CSharpAgentSkills
```

The shared `~/.agents/skills` directory is read by Codex, GitHub Copilot and OpenCode, so one link covers all three. Claude Code reads `~/.claude/skills`.

### Claude Code

User-level (every project):

```sh
mkdir -p ~/.claude/skills
ln -s ~/source/CSharpAgentSkills/skills/* ~/.claude/skills/
```

Project-level: link or copy the folders into `.claude/skills/` in the repository.

### GitHub Copilot

Personal skills live in `~/.copilot/skills` or `~/.agents/skills`; project skills live in `.github/skills`, `.claude/skills` or `.agents/skills`. Skills are used by the Copilot CLI, the cloud agent, code review, and agent mode in VS Code and JetBrains IDEs.

```sh
mkdir -p ~/.agents/skills
ln -s ~/source/CSharpAgentSkills/skills/* ~/.agents/skills/
```

### Codex

Codex reads `~/.agents/skills` for personal skills and `.agents/skills` from the working directory up to the repository root for project skills.

```sh
mkdir -p ~/.agents/skills
ln -s ~/source/CSharpAgentSkills/skills/* ~/.agents/skills/
```

### OpenCode

OpenCode reads `~/.config/opencode/skills`, `~/.agents/skills` and `~/.claude/skills` globally, and `.opencode/skills`, `.agents/skills` and `.claude/skills` in the project. Any of the links above works; to keep it separate:

```sh
mkdir -p ~/.config/opencode/skills
ln -s ~/source/CSharpAgentSkills/skills/* ~/.config/opencode/skills/
```

### Other agents

Any agent that supports the Agent Skills format can use these folders. Point it at `skills/`, or link the folders into its skills directory.

To install only some skills, link individual folders instead of `skills/*`. Some skills reference each other by name (for example `csharp-tunit` points to `tdd`), so related skills work best together.

## Contributing

See [AGENTS.md](AGENTS.md) for the layout and authoring rules.

## License

[MIT](LICENSE)
