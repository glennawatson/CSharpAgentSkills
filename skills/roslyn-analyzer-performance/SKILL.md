---
name: roslyn-analyzer-performance
description: Use when writing, reviewing or optimising Roslyn analyzers, code fixes, refactorings or source generators — anything whose callbacks the compiler runs on every keystroke. Covers the three-path measurement that separates per-compilation from per-node cost, the allocation shapes that actually cost bytes, and the lazy-resolution pattern that is easy to get half-right. Measure with this before changing an analyzer for performance.
---

# Roslyn analyzer performance

An analyzer callback runs on every keystroke, over every matching node, in every open file. The
overwhelming majority of those invocations decide "nothing to report". Bytes spent on that path buy
nothing, and they are paid constantly.

**The clean path is the headline number.** A rule that allocates while reporting is spending bytes
to produce output. A rule that allocates while reporting nothing is spending bytes to produce
nothing.

## Measure on three paths, not two

Measuring only "clean" and "violating" corpora confounds two different costs, and a small corpus
makes the confusion worse: a few short sources are mostly compilation, so a rule that resolves ten
metadata types at compilation start looks like a per-node defect, and a real per-node defect hides
underneath it.

| Path | Corpus | What it isolates |
| --- | --- | --- |
| `startup` | one empty class | fixed cost every compilation pays before the rule can have anything to say |
| `clean` | non-violating sources | `startup` plus the per-node cost of deciding "nothing to report" |
| `violating` | sources that trigger the rule | the above plus the diagnostics it exists to produce |

`clean - startup` is the genuine per-node figure. Report the two separately.

`startup` needs no corpus, so it also covers subjects whose tests yield no extractable source.

**Force the rules on.** Opt-in descriptors report nothing at their shipped severity, so a corpus
never exercises them. Push every `SupportedDiagnostics` id into
`CSharpCompilationOptions.WithSpecificDiagnosticOptions` before measuring.

**Validate the corpus as you measure.** A clean corpus must report zero diagnostics and a violating
one more than zero. When a violating corpus reports nothing it usually means a type it needs is
absent from your reference set — rules targeting ASP.NET, Blazor or EF bind nothing against the
platform alone — and you are measuring a clean path wearing the wrong label. Flag those rows; never
average them in.

## The shapes that cost bytes

Audit these with a read-only Roslyn pass over the real syntax graph (see `roslyn-discovery`), then
let a trace decide which ones matter.

### Eager metadata resolution at compilation start

The single largest defect in a mature analyzer set, and the one most likely to be waved through as
"correct idiom". `RegisterCompilationStartAction` resolving types unconditionally means a
compilation containing nothing the rule matches still pays for the lookup — and the IDE creates
compilations constantly.

Best fix: **delete the compilation-start action.** If candidates are recognisable from syntax,
register a static syntax-node action and resolve only for nodes that survive the syntactic filter.

```csharp
public override void Initialize(AnalysisContext context)
{
    context.EnableConcurrentExecution();
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.RegisterSyntaxNodeAction(static c => Analyze(c), SyntaxKind.IfStatement);
}
```

A compilation with no `if` statement then costs nothing.

### Deferring without caching — the half-right fix

Deferring the lookup requires **two** properties at once:

1. at most one resolution per compilation, and
2. no resolution at all when nothing needs it.

Moving `GetTypeByMetadataName` out of compilation-start and into the per-node callback delivers only
the second. `Compilation.GetTypeByMetadataName` searches the compilation's references and **does not
memoise**, so a file with 500 matching nodes now resolves the same metadata 500 times — worse than
what you started with, in exactly the files the rule exists to analyse.

Keep compilation-start, because it is the only per-compilation scope available, and put a
resolve-on-first-demand holder in it:

```csharp
/// <summary>Resolves the marker types once per compilation, on first demand.</summary>
private sealed class Markers(Compilation compilation)
{
    private INamedTypeSymbol[]? _resolved;

    public INamedTypeSymbol[] Get() => _resolved ??= Resolve(compilation);

    private static INamedTypeSymbol[] Resolve(Compilation compilation) { /* ... */ }
}

context.RegisterCompilationStartAction(static start =>
{
    var markers = new Markers(start.Compilation);
    start.RegisterSyntaxNodeAction(c => Analyze(c, markers), SyntaxKind.MethodDeclaration);
});
```

- **Cache the empty answer too** — return an empty array, or `??=` re-resolves forever on a miss.
- **No lock, no `Lazy<T>`.** Reference assignment is atomic and resolution is deterministic and
  side-effect free, so a racing double-resolve yields equivalent results and costs less than
  synchronisation.
- **Never cache on the analyzer instance.** One instance is shared across concurrent compilations, so
  a static or instance field leaks symbols between them — a correctness bug, not just a leak.
- **Make the callback earn it.** Every check decidable from syntax — node kind, identifier text, an
  attribute's simple name, argument count, a modifier — happens before `Get()` is called.

### Mutator chains

Every `WithX(...)` returns a **brand new node**, so a three-link chain allocates three and discards
two.

```csharp
// three nodes allocated, two discarded
method.WithBody(null).WithExpressionBody(arrow).WithSemicolonToken(semi)

// one node
method.Update(method.AttributeLists, method.Modifiers, method.ReturnType,
    method.ExplicitInterfaceSpecifier, method.Identifier, method.TypeParameterList,
    method.ParameterList, method.ConstraintClauses, null, arrow, semi)
```

- **Mutating an existing node, 2+ changes → `Update(...)`**, passing unchanged children through.
  Read the real signature; never guess the parameter order.
- **Building a fresh node → the complete `SyntaxFactory` overload**, and build tokens with
  `SyntaxFactory.Token(leadingTrivia, kind, trailingTrivia)` so trivia never needs a mutator.
  Note `SyntaxFactory.Token(kind)` seeds *elastic marker* trivia on both sides — preserve that when
  collapsing, or formatting shifts.
- **A single `WithX` on an existing node is already one allocation.** Leave it. Only collapse a lone
  mutator when its receiver is a `SyntaxFactory` call whose full overload absorbs it.
- Annotation calls (`WithAdditionalAnnotations`) and parse-based construction often have no
  single-call equivalent. Leave them rather than forcing it.

### Iterator descendant walks

`DescendantNodes()`, `DescendantNodesAndSelf()`, `DescendantTokens()` and `DescendantTrivia()` each
allocate an enumerator plus its internal stack, and a `foreach` over one keeps walking after the
answer is known. Replace predicate-shaped walks with an indexed `ChildNodesAndTokens()` recursion
taking a **static** visitor and `ref` state, returning `false` to stop. A capturing lambda allocates
a display class per call and gives back what the walk saved.

Preserve any filtering the original did — skipping nested lambdas, local functions or nested types
changes which diagnostics the rule reports.

### Per-entry semantic binding

`SemanticModel.GetSymbolInfo` is not a lookup; it drives the binder. Inside a loop it creates binder
state per iteration. In order of preference:

1. **Refuse syntactically first** — check node kind, identifier text, argument count, then `continue`
   before touching the semantic model.
2. **Ask the declaration table** — `GetDeclaredSymbol` answers from declarations without invoking the
   binder. It returns null for a *reference*, so this is not a blanket substitution.
3. **Hoist what does not vary** — a `GetSymbolInfo` independent of the loop variable, or a
   `GetTypeByMetadataName` compared every iteration, belongs above the loop.

Always pass the `CancellationToken`.

### Containers and strings on the clean path

- Arrays where the final size is known; a capacity on anything growable. Zero-capacity `List<T>`
  reaching 20 items has allocated and abandoned five arrays.
- A `ConcurrentDictionary` costs a bucket array, a lock array and a `Tables` object. It is needed
  only when the same instance is *written* from callbacks the driver may run in parallel. Populated
  once inside compilation-start and only read afterwards → plain `Dictionary`.
- `Substring`, `Split`, `Trim`, `Replace`, `ToLowerInvariant` on a `string` receiver each copy. Use
  `AsSpan` slices, `SequenceEqual`, and `string.Equals(a, b, StringComparison.OrdinalIgnoreCase)`.
  `char.ToUpperInvariant` returns a char and allocates nothing — not the same thing.
- A string that becomes a diagnostic message argument is output, not waste. Only sites reached
  *before* the decision to report are in scope.

## Reading the trace honestly

- **Attribute, do not total.** Walk each allocation tick's stack outward to the nearest frame you
  own. Record the site as `frame <= AllocatedType` — that pairing is what lets a residual be
  *named*: a `Diagnostic`/`Location` the rule must produce, a Roslyn internal allocated on your
  behalf, or a type you still own and could remove.
- **Filter by type name, not namespace.** A test or benchmark project usually shares the namespace
  prefix of the code under test, so a namespace filter reports the harness.
- **Watch the sampling floor** — see `benchmarking`. One tick is not a measurement.
- A compilation-start allocation is once per compilation; a per-node one scales with file size. Do
  not rank them on the same axis without saying which is which.

### A closure captured at registration time is free — don't chase it

The idiom below is correct and has no cheaper form. `Initialize` and the compilation-start body run
**once per compilation**, so the display class is allocated once and handed to a node action that
may then run millions of times:

```csharp
context.RegisterCompilationStartAction(static start =>
{
    var types = new WellKnownTypes(start.Compilation);       // resolved once
    start.RegisterSyntaxNodeAction(ctx => Analyze(ctx, types), SyntaxKind.InvocationExpression);
});
```

A syntax audit that flags "lambda captures a local" reports every one of these, but a real
startup-time `<>c__DisplayClass` almost never shows up as the heaviest allocating frame — this
category can dominate the audit backlog by count while costing zero measured bytes. Frames that
*look* like closures in a trace are usually `+<>c.` — the compiler's cached singleton for a lambda
that captures nothing, which allocates nothing. What is attributed there is the **body** of the
compilation-start action, which is `eager-metadata`, a different shape with a different fix.

Only flag a closure allocated **inside a per-callback body**, which repeats per node.

## Validate an audit shape against the trace before it drives a campaign

A syntax audit finds *candidates*; only the trace says which cost anything. Before turning a shape
into work orders, check what it is worth in bytes. Two failure modes, and both have bitten:

- **A shape that costs nothing** — closure-capture above, which can produce a large finding count
  with zero measured bytes. Dispatching agents at it burns a campaign and churns correct code.
- **A shape dismissed as idiom that is actually the biggest defect** — eager metadata at compilation
  start looks textbook on read but the `startup` path can show it as the largest cost in the set.

The rule is symmetric: **neither** accept nor reject a shape by inspection. Join the audit to the
sweep and let the bytes decide which findings become work.

## Authoring diagnostics: describe the defect, not another tool's rule

When a rule overlaps something another analyzer, linter, or style tool already reports, treat that
tool as research input for deciding what to report and what to stay silent on — never as something
the shipped artifact names.

- **Diagnostic titles, messages, and descriptions, XML docs, code comments, and any preset
  `.editorconfig` comments describe the defect on its own terms**: what's wrong, why it bites at
  runtime, what to write instead. Never "port of X", "matches <rule-id>", "unlike <rule-id>".
- **If a rule intentionally stays silent on a shape because something else already reports it**,
  describe the shape itself ("an unbalanced brace is already a compiler diagnostic and isn't
  reported here") rather than naming the other tool or its rule id.
- **Build configuration that *consumes* another analyzer is fine and stays functional** —
  `.editorconfig` severity lines, `PackageReference`, `NoWarn` entries — the restriction is on prose
  in the shipped artifact, not on wiring the tool in.

## Don't

- Don't conclude a rule is "already optimal" from reading it. This is exactly where that goes wrong:
  the compilation-start pattern looks textbook and is the biggest defect in the set.
- Don't fix a shape the trace has not implicated, on a path that does not run hot.
- Don't change what a rule reports to make it faster. Every existing test must still pass.
- Don't suppress an analyzer complaint that the refactor triggers — restructure instead.
