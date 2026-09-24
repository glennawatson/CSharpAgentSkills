---
name: csharp-source-generators
description: Use when writing or reviewing a Roslyn incremental source generator — IIncrementalGenerator pipeline design, value-equatable models (records, EquatableArray, netstandard2.0 polyfills), ForAttributeWithMetadataName, avoiding Compilation/ISymbol/SyntaxNode in the cached pipeline, generated-code hygiene, marker-attribute injection, MSBuild option plumbing, packaging, and cacheability testing. Cross-reference roslyn-analyzer-performance for callback cost.
---

# Incremental source generators

A generator's pipeline reruns on every keystroke across every open file, same as an analyzer (see
`roslyn-analyzer-performance`). The entire design of `IIncrementalGenerator` exists to let Roslyn
skip stages whose *input* didn't change — that only works if every stage's output is something
Roslyn can cheaply compare for equality. Get that wrong and you have a correct generator that
recomputes everything on every edit anyway.

## `IIncrementalGenerator` only

`ISourceGenerator` (the older, non-incremental interface) reruns its entire body on every edit to
every file in the compilation, with no caching. There is no reason to write a new one — always
implement `IIncrementalGenerator`.

```csharp
[Generator(LanguageNames.CSharp)]
public sealed class OrderGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var models = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                "Demo.GenerateOrderAttribute",
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, ct) => OrderModel.From(ctx, ct))
            .WithTrackingName("OrderModels");

        context.RegisterSourceOutput(models, static (spc, model) => Emit(spc, model));
    }
}
```

## `ForAttributeWithMetadataName` for attribute-driven generators

For any generator triggered by a marker attribute, use
`SyntaxProvider.ForAttributeWithMetadataName` instead of a hand-rolled
`CreateSyntaxProvider`/`GetDeclaredSymbol` walk. The Roslyn team measures it as at least 99x more
efficient: it short-circuits on a fast syntactic check (does this node even have an attribute with
this simple name?) before touching semantics, so files without the attribute cost almost nothing —
exactly the cheap-clean-path discipline `roslyn-analyzer-performance` asks of analyzers.

```csharp
context.SyntaxProvider.ForAttributeWithMetadataName(
    fullyQualifiedMetadataName: "Demo.GenerateOrderAttribute",
    predicate: static (node, _) => node is ClassDeclarationSyntax c && c.Modifiers.Any(SyntaxKind.PartialKeyword),
    transform: static (ctx, ct) => OrderModel.From((ClassDeclarationSyntax)ctx.TargetNode, ctx.SemanticModel, ct));
```

Only fall back to `CreateSyntaxProvider` when there's genuinely no attribute to key off (a
generator driven purely by syntactic shape, e.g. "every partial method with no body") — and even
then, filter as much as possible in the syntactic `predicate` before the `transform` touches
semantics.

**Never scan for indirect membership** — "every type that (transitively) implements
`IMySerializable`", "every type derived, directly or indirectly, from `BaseModelType`", or an
unsealed marker attribute consumers subclass. None of these are decidable from one type's own
syntax, so the generator ends up walking `AllInterfaces`/the base-type chain of *every* type in the
compilation on every edit — Roslyn's own cookbook calls this out as one of the few designs that
"cannot be done incrementally," full stop. Require a marker attribute applied directly to the type,
and seal it; use an analyzer (not the generator) to nudge users who apply it to the wrong shape.

## Value-equatable models — no compiler objects in the pipeline

Every pipeline stage's output must implement structural equality so the incremental engine can
detect "this output is the same as last time, skip downstream work." `ISymbol`, `SyntaxNode`,
`Compilation`, and `Location` **do not have useful value equality for this purpose** — the same
logical class recompiles to a *different* `ISymbol`/`SyntaxNode` instance on every keystroke even
when nothing about it changed, so keeping one in your model output means the pipeline recomputes
every downstream stage every time, defeating incrementality entirely. `ISymbol` is worse than the
others: holding one in a cached value can root an entire old `Compilation` in memory.

```csharp
// ❌ ISymbol never compares equal across edits — this stage never caches, and roots the Compilation
private static INamedTypeSymbol Transform(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    => (INamedTypeSymbol)ctx.TargetSymbol;

// ✅ a plain record of primitives/strings — value-equatable for free
private sealed record OrderModel(string Namespace, string ClassName, EquatableArray<string> PropertyNames)
{
    public static OrderModel From(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        var symbol = (INamedTypeSymbol)ctx.TargetSymbol;
        var props = symbol.GetMembers().OfType<IPropertySymbol>().Select(p => p.Name).ToArray();
        return new OrderModel(symbol.ContainingNamespace.ToDisplayString(), symbol.Name, new EquatableArray<string>(props));
    }
}
```

Extract everything you need (names, accessibility, namespace, attribute argument values) into
plain data — `string`, `int`, `bool`, `enum`, nested `record`s — inside the `transform` delegate,
*before* the value leaves the syntax/semantic-model stage. `record` (and `record struct`) give you
compiler-synthesized `Equals`/`GetHashCode` for free, which matters beyond convenience: it means
adding a field to a model never silently desyncs a hand-written `Equals` from what the type
actually holds, so it costs nothing to split one big model into several small, purpose-built ones
(type info, member info, attribute-argument info, diagnostic info) — exactly what a cache-friendly
pipeline wants, per "use multiple transformations" and "build a data model" in Roslyn's own
guidance. Prefer `readonly record struct` for small leaf models with no nested reference-typed
collections (no heap allocation, value semantics); use `sealed record` for larger or nested models.

`ImmutableArray<T>` and ordinary arrays both have reference-typed `Equals` (two arrays with
identical contents are *not* equal) — records compare their members with `Equals`, so a collection
member silently breaks a record's equality the same way a raw `ISymbol` does. Wrap any list-shaped
model member in a small value-equatable wrapper:

```csharp
public readonly struct EquatableArray<T>(ImmutableArray<T> array) : IEquatable<EquatableArray<T>>
    where T : IEquatable<T>
{
    private readonly ImmutableArray<T> _array = array;

    public bool Equals(EquatableArray<T> other) => _array.AsSpan().SequenceEqual(other._array.AsSpan());
    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in _array)
        {
            hash.Add(item);
        }
        return hash.ToHashCode();
    }
}
```

### `record`/`init`/`required` on the forced `netstandard2.0` target

A generator project targets `netstandard2.0` (see Packaging below), but the *C# language version*
is a compiler switch, independent of the target framework — set `<LangVersion>latest</LangVersion>`
and the compiler accepts `record`, `record struct`, `init`, and `required` even though the
`netstandard2.0` reference assemblies predate them. The compiler only needs a handful of marker
types to exist somewhere in the closure; ship them yourself as `internal` polyfills:

```csharp
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { } // enables `init` accessors -> positional/record-with-init members

    internal sealed class RequiredMemberAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
    internal sealed class CompilerFeatureRequiredAttribute(string featureName) : Attribute
    {
        public string FeatureName { get; } = featureName;
    }
}

namespace System.Diagnostics.CodeAnalysis
{
    [AttributeUsage(AttributeTargets.Constructor)]
    internal sealed class SetsRequiredMembersAttribute : Attribute { }
}
```

`IsExternalInit` alone is enough for positional `record`/`record struct`/`readonly record struct`
(their synthesized properties use `init`); `required` members additionally need
`RequiredMemberAttribute` and `CompilerFeatureRequiredAttribute`, and a constructor that sets every
required member itself needs `SetsRequiredMembersAttribute`. Keep every polyfill `internal` — a
public one can collide with the real BCL type once the consumer's own TFM supplies it. `System
.HashCode` is also absent on `netstandard2.0`; combine hashes by hand (`hash * 31 + next`) rather
than add a `Microsoft.Bcl.HashCode` dependency for it. Verified: a `netstandard2.0` project with
only the polyfills above compiles `sealed record`, `readonly record struct`, and a `required`
member, and a `net10.0` consumer referencing it confirms structural equality still holds for all
three. [PolySharp](https://www.nuget.org/packages/PolySharp) source-generates this same polyfill
set (and more) if you'd rather not hand-maintain the file — reference it `PrivateAssets="all"` so
it stays build-time-only.

## Keep `Compilation` out of the cached pipeline

`context.CompilationProvider` gives you the whole `Compilation`, which changes on essentially
every edit anywhere in the project — `Combine`-ing it into a per-node stage poisons that stage's
caching the same way holding an `ISymbol` does, because the combined output's equality now depends
on an object that's "new" every time.

```csharp
// ❌ every node's output is now tied to the whole-compilation identity — cache never hits
var withCompilation = models.Combine(context.CompilationProvider);

// ✅ only pull the one fact you need out of the compilation, itself value-equatable
var langVersion = context.CompilationProvider.Select(static (c, _) => ((CSharpCompilation)c).LanguageVersion);
var withLangVersion = models.Combine(langVersion);
```

General rule for `Combine`: project each side down to its smallest, most-stable value *before*
combining, not after — combining two big providers and projecting the result is strictly worse,
since the combine itself reruns whenever either raw input changes even if the eventual projection
wouldn't have. If you unavoidably need the full `Compilation` (rare — e.g. resolving a well-known
type by metadata name), do that resolution inside a `RegisterSourceOutput`/
`RegisterImplementationSourceOutput` callback fed by `CompilationProvider` directly, not folded
into the per-node cached stage.

## `WithTrackingName` and testing cacheability

Tag each pipeline stage with `WithTrackingName("...")` and assert on the tracked steps in tests to
catch a caching regression before it ships — this is the only reliable way to verify
incrementality; reading the pipeline code is not enough (nothing about a compiling generator tells
you whether it recomputes on every keystroke).

Enable step tracking via `GeneratorDriverOptions`, run the driver **twice** against compilations
that differ only in a file the generator doesn't care about, and assert every tracked output's
`IncrementalStepRunReason` on the second run is `Cached` or `Unchanged`:

```csharp
var options = new GeneratorDriverOptions(
    disabledOutputs: IncrementalGeneratorOutputKind.None,
    trackIncrementalGeneratorSteps: true);

GeneratorDriver driver = CSharpGeneratorDriver.Create([new OrderGenerator().AsSourceGenerator()], driverOptions: options);

driver = driver.RunGeneratorsAndUpdateCompilation(compilation1, out _, out _);
driver = driver.RunGeneratorsAndUpdateCompilation(compilation2, out _, out _);   // compilation2 differs in an unrelated file

var runResult = driver.GetRunResult().Results[0];
var trackedSteps = runResult.TrackedSteps["OrderModels"];
Assert.True(trackedSteps.All(s => s.Outputs.All(o => o.Reason is IncrementalStepRunReason.Cached
                                                    or IncrementalStepRunReason.Unchanged)));
```

A step that reports `Modified` or `New` when nothing relevant to it changed is the caching bug —
usually traced back to a raw `ISymbol`/`Compilation`/array leaking into that step's output type, or
a `Combine` ordered so a volatile input sits upstream of a stable one. Verified: this pattern (two
runs, an unrelated file edited between them, `TrackedSteps[name]` inspected) reports `Unchanged`/
`Cached` for a `ForAttributeWithMetadataName` stage and a downstream `Combine` with
`AnalyzerConfigOptionsProvider`, and reports the expected new source on the first run.

## Emitting diagnostics correctly

Prefer reporting diagnostics from a companion **analyzer**, not the generator itself: Roslyn's own
cookbook explicitly does not recommend issuing diagnostics from a generator, since doing so without
quietly breaking incrementality is an advanced topic — a diagnostic tied to a volatile input can
pin a pipeline stage to "always new." An analyzer also reports independently of whether the
generator ran, reacting the instant a user misuses your marker attribute.

When a generator-emitted diagnostic is unavoidable, report it from `RegisterSourceOutput`/
`RegisterImplementationSourceOutput`, never by throwing — a thrown exception crashes the whole
compiler host, not just your generator's output:

```csharp
context.RegisterSourceOutput(models, static (spc, model) =>
{
    if (model.PropertyNames.IsEmpty)
    {
        spc.ReportDiagnostic(Diagnostic.Create(NoPropertiesRule, model.Location));
        return;
    }
    Emit(spc, model);
});

private static readonly DiagnosticDescriptor NoPropertiesRule = new(
    id: "DEMOGEN001",
    title: "No properties to generate",
    messageFormat: "Type '{0}' has [GenerateOrder] but no public properties",
    category: "DemoGenerator",
    defaultSeverity: DiagnosticSeverity.Warning,
    isEnabledByDefault: true);
```

If a diagnostic needs a `Location`, capture it in the model during `transform` — `Location` is
value-equatable, unlike `ISymbol`/`SyntaxNode`, so it's safe to carry, but don't reach back into a
live `SyntaxNode`/`SemanticModel` from inside `RegisterSourceOutput`; those aren't available there.

## Generated-code hygiene

Emit source as text — a `StringBuilder`/indented-text-writer wrapper that tracks indent level, not
`SyntaxNode`s run through `NormalizeWhitespace`. `AddSource` only accepts a `string`/`SourceText`
regardless, and the Roslyn team has measured `NormalizeWhitespace` as too expensive and not
designed for this: building syntax trees just to throw away their identity at the `AddSource`
boundary buys nothing.

```csharp
spc.AddSource($"{model.ClassName}.g.cs", SourceText.From($$"""
// <auto-generated/>
#nullable enable

namespace {{model.Namespace}}
{
    partial class {{model.ClassName}}
    {
        public global::System.String Describe() => "{{model.ClassName}}";
    }
}
""", Encoding.UTF8));
```

- **`// <auto-generated/>`** at the top suppresses most analyzers/style rules on the file and
  marks it clearly as non-source-of-truth.
- **`#nullable enable`** explicitly — generated code doesn't inherit the consumer's nullable
  context from `.editorconfig`/project settings, so omitting it means warnings misfire either way.
- **`global::`-qualify every type reference** — the generated file has no `using` directives of
  the consumer's choosing, and an unqualified name can bind to something unexpected.
- **`[System.CodeDom.Compiler.GeneratedCode("YourGenerator", "1.0.0")]`** on emitted members lets
  tooling (analyzers, coverage tools) recognize and skip them.
- **`hintName` passed to `AddSource` must be unique per generator run** — two outputs with the
  same hint name is a hard compiler error, not a warning; derive it from the fully-qualified type
  name (namespace + containing types + name), not just the simple name, so two `Order` types in
  different namespaces don't collide.
- Use `SourceText.From(code, Encoding.UTF8)` — an explicit encoding avoids a BOM mismatch between
  runs that can itself defeat caching on some hosts.

## `RegisterPostInitializationOutput` for marker attributes

Ship the marker attribute your generator looks for (`GenerateOrderAttribute` above) as part of
the generator itself via `RegisterPostInitializationOutput`, rather than requiring consumers to
hand-write or reference it separately. This runs once per compilation (not per-node) and its
output is visible to the rest of the pipeline, including `ForAttributeWithMetadataName`:

```csharp
context.RegisterPostInitializationOutput(static ctx => ctx.AddSource(
    "GenerateOrderAttribute.g.cs",
    SourceText.From("""
    // <auto-generated/>
    #nullable enable
    namespace Demo
    {
        [System.AttributeUsage(System.AttributeTargets.Class)]
        internal sealed class GenerateOrderAttribute : System.Attribute;
    }
    """, Encoding.UTF8)));
```

Mark the attribute `internal` unless consumers need to reference it beyond applying it (e.g.
reading it back via reflection) — internal avoids polluting the consumer's public surface (see
`csharp-api-design`). An `internal` type defined identically in multiple referenced projects
(common once `InternalsVisibleTo` is involved) makes the compiler warn about the duplicate; add
`[Microsoft.CodeAnalysis.Embedded]` to the marker type and call `context
.AddEmbeddedAttributeDefinition()` in the same callback to suppress that — it excludes the type
from lookup when seen coming from a different assembly.

## MSBuild and `.editorconfig` options

Consumers customize generator behavior through `AnalyzerConfigOptionsProvider`, not through
constructor parameters or config files the generator reads off disk (a generator has no ambient
filesystem access to the project). Read a project-wide switch via `GlobalOptions`:

```csharp
var emitLogging = context.AnalyzerConfigOptionsProvider.Select(static (options, _) =>
    options.GlobalOptions.TryGetValue("build_property.MyGenerator_EnableLogging", out var v)
        && v.Equals("true", StringComparison.OrdinalIgnoreCase));

context.RegisterSourceOutput(models.Combine(emitLogging), static (spc, pair) => Emit(spc, pair.Left, pair.Right));
```

To expose an MSBuild property under `build_property.*`, declare it visible to the compiler from a
`.props`/`.targets` file shipped in the package — a plain `<PropertyGroup>` value is invisible to
analyzers/generators until it's opted in:

```xml
<ItemGroup>
    <CompilerVisibleProperty Include="MyGenerator_EnableLogging" />
    <CompilerVisibleItemMetadata Include="AdditionalFiles" MetadataName="MyGenerator_EnableLogging" />
</ItemGroup>
```

The metadata line makes per-file options available as
`build_metadata.AdditionalFiles.MyGenerator_EnableLogging` via
`context.AnalyzerConfigOptionsProvider.GetOptions(additionalText)`, so a user can opt a single
`<AdditionalFiles>` item in or out. MSBuild round-trips these values through a generated
`.editorconfig` file, which loses non-trivial values (embedded `;`, the editorconfig comment
character) — encode a list as space- rather than semicolon-separated if you need one.

For file-shaped input beyond C#/VB sources, use `context.AdditionalTextsProvider` — filter it
syntactically (`Where(f => f.Path.EndsWith(".json"))`) before calling `GetText`, and always pass the
`CancellationToken` through to `GetText(ct)` and any loop your `Select` runs: incremental generators
run inside an IDE that cancels work on every keystroke, and an uncancellable transform stalls
typing. Prefer `ThrowIfCancellationRequested()` over trying to save partial results.

## Packaging

A generator ships as an analyzer package, not an ordinary library reference:

```xml
<PropertyGroup>
  <TargetFramework>netstandard2.0</TargetFramework>
  <LangVersion>latest</LangVersion>
  <IncludeBuildOutput>false</IncludeBuildOutput>
  <DevelopmentDependency>true</DevelopmentDependency>
  <IsRoslynComponent>true</IsRoslynComponent>
  <EnforceExtendedAnalyzerRules>true</EnforceExtendedAnalyzerRules>
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="4.4.0" PrivateAssets="all" />
  <None Include="$(OutputPath)\$(AssemblyName).dll" Pack="true" PackagePath="analyzers/dotnet/cs" Visible="false" />
</ItemGroup>
```

- **Target `netstandard2.0`**, not `net10.0` — the generator runs *inside the compiler process*
  (`csc`/the IDE's Roslyn host), whose target framework you don't control. See the polyfill section
  above for using modern C# (`record`, `init`, `required`) on that target anyway.
- **Pin `Microsoft.CodeAnalysis.CSharp` to the oldest Roslyn you support**, with
  `PrivateAssets="all"` so it doesn't flow to consumers — a generator built against a newer Roslyn
  API than the consumer's compiler host can fail to load entirely. If you need a newer API while
  still supporting an older host, multi-target the generator project across several
  `Microsoft.CodeAnalysis.CSharp` versions and pack each into its own `analyzers/dotnet/roslynX.Y/cs`
  folder (e.g. `roslyn4.4`, `roslyn4.11`) alongside the default `analyzers/dotnet/cs` — the SDK
  picks the highest folder not newer than the host's compiler. This is real extra project-structure
  cost; only take it on for a specific newer-API need.
- **`IsRoslynComponent=true`** tells the SDK this project is an analyzer/generator, keeping it
  buildable across the full range of hosts (.NET Framework `csc`, the VS IDE, current `dotnet
  build`) rather than just the SDK's own TFM.
- **`EnforceExtendedAnalyzerRules=true`** — the `Microsoft.CodeAnalysis.Analyzers` package's rule
  `RS1036` asks for this on every new analyzer/generator project; it enables stricter checks that
  catch calls unsafe from the concurrent incremental pipeline.
- **Pack into `analyzers/dotnet/cs`**, not `lib/`, so consumers load it as an analyzer/generator,
  not an ordinary reference assembly. `DevelopmentDependency=true` keeps it out of the consumer's
  published dependency closure.

## Testing with snapshot tests

Drive the generator directly with `CSharpGeneratorDriver` and snapshot the emitted source, rather
than only asserting behavior of the compiled output:

```csharp
var compilation = CSharpCompilation.Create("Tests",
    [CSharpSyntaxTree.ParseText(source)],
    references,
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

GeneratorDriver driver = CSharpGeneratorDriver.Create(new OrderGenerator());
driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var diagnostics);

var result = driver.GetRunResult().Results[0];
await Verify(result.GeneratedSources[0].SourceText.ToString());   // snapshot-testing library
Assert.Empty(diagnostics);
Assert.Empty(outputCompilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
```

A snapshot test catches accidental output-formatting churn and missing-`global::`/hygiene
regressions that a purely behavioral test (compile and invoke the generated method) would miss.
Pair it with the `WithTrackingName` cacheability assertions above — one proves *what* gets
generated, the other proves it doesn't get regenerated unnecessarily; both work off the same
`GeneratorDriver`. `Microsoft.CodeAnalysis.Testing`'s `CSharpSourceGeneratorTest` wraps this setup
for common cases, but driving the driver directly is usually less code once `TrackedSteps`
assertions are needed too. For general Roslyn callback cost once the generator is correct, apply
`roslyn-analyzer-performance`.

## Review checklist

- [ ] Implements `IIncrementalGenerator`, not `ISourceGenerator`
- [ ] Attribute-driven trigger uses `ForAttributeWithMetadataName`; no scan for indirect interface/base-type membership
- [ ] Pipeline models are `record`/`record struct` of primitives — no `ISymbol`/`SyntaxNode`/`Compilation`/`Location`-heavy state
- [ ] Any list-shaped model member uses a value-equatable wrapper (`EquatableArray<T>`), not a raw array
- [ ] `netstandard2.0` project using `record`/`init`/`required` ships the `IsExternalInit`/`RequiredMemberAttribute` polyfills (or PolySharp)
- [ ] `Compilation`/`CompilationProvider` not combined into cached per-node stages directly; `Combine` ordered smallest/most-stable-first
- [ ] Pipeline stages tagged with `WithTrackingName` and a test asserts `Cached`/`Unchanged` reasons via `TrackIncrementalGeneratorSteps`
- [ ] Diagnostics reported by a companion analyzer where possible; any generator-emitted diagnostic uses `ReportDiagnostic` in a source-output callback, never thrown
- [ ] Generated source built as text (`StringBuilder`/indented writer), not `SyntaxNode` + `NormalizeWhitespace`
- [ ] Generated files: `// <auto-generated/>`, `#nullable enable`, `global::`-qualified names, unique `hintName`
- [ ] Marker attribute emitted via `RegisterPostInitializationOutput`, kept internal (and `[Embedded]`) unless consumers need it
- [ ] Consumer-facing options read via `AnalyzerConfigOptionsProvider`/`CompilerVisibleProperty`, not files read off disk
- [ ] `CancellationToken` forwarded into `GetText`/loops in every transform
- [ ] Package targets `netstandard2.0`, pins Roslyn version with `PrivateAssets="all"`, sets `IsRoslynComponent`/`EnforceExtendedAnalyzerRules`, packs to `analyzers/dotnet/cs` with `DevelopmentDependency=true`
- [ ] Snapshot test covers emitted source; cacheability verified separately from correctness

## Sources

- [Incremental generators design doc](https://raw.githubusercontent.com/dotnet/roslyn/main/docs/features/incremental-generators.md)
- [Incremental generators cookbook](https://raw.githubusercontent.com/dotnet/roslyn/main/docs/features/incremental-generators.cookbook.md)
- [Microsoft.CodeAnalysis.Analyzers rules (RS1036 `EnforceExtendedAnalyzerRules`)](https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/Microsoft.CodeAnalysis.Analyzers.md)
- Andrew Lock, "Creating a source generator" series (andrewlock.net): [Part 9 — avoiding performance pitfalls](https://andrewlock.net/creating-a-source-generator-part-9-avoiding-performance-pitfalls-in-incremental-generators/), [Part 10 — testing pipeline caching](https://andrewlock.net/creating-a-source-generator-part-10-testing-your-incremental-generator-pipeline-outputs-are-cacheable/), [Part 14 — supporting multiple SDK versions](https://andrewlock.net/creating-a-source-generator-part-14-supporting-multiple-sdk-versions-in-a-source-generator/)
- [PolySharp](https://www.nuget.org/packages/PolySharp)
