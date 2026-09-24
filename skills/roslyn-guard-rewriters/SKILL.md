---
name: roslyn-guard-rewriters
description: Use to modernize hand-written argument guards to the .NET 6+ ThrowIf* helpers and to add missing null guards at public boundaries, across a codebase, via two standalone Roslyn rewriters ported from the CA1062 and CA1510-CA1513 analyzer/fixer source — no analyzer enablement or `dotnet format analyzers` required, the rewriters find and replace the patterns themselves.
---

# Roslyn guard rewriters — modernize and add guards without the analyzers

Two throwaway `dotnet run` Roslyn programs, one job each:

- **`./examples/add-null-guards.cs`** — finds reference-type parameters of externally
  visible methods/constructors that get dereferenced before any null check, and inserts
  `ArgumentNullException.ThrowIfNull(p)`; also converts the hand-written `if (p == null)
  throw ...`/`if (p is null) throw ...` shape it finds along the way. Ported from CA1062's
  analyzer (`ValidateArgumentsOfPublicMethods.cs` + its dataflow visitor) and CA1510's
  fixer shape.
- **`./examples/modernize-guards.cs`** — finds existing `if (...) throw new
  SomeException(...)` guards that already match one of the BCL's `ThrowIf*` helper shapes
  and rewrites them in place. Ported from `UseExceptionThrowHelpers.cs` (the CA1510-CA1513
  analyzer) and `UseExceptionThrowHelpersFixer.cs` (its fixer).

**Neither tool requires CA1062/CA1510-CA1513 to be enabled, a code-fix host, or `dotnet
format analyzers`.** Each parses a folder into a throwaway `CSharpCompilation`, matches the
same shapes the real analyzer/fixer match (re-implemented against the public `IOperation`
API, which works fine outside an analyzer host), and rewrites the syntax tree itself with a
`CSharpSyntaxRewriter` — same output as the stock fixer, different, standalone host. Enabling
the real analyzers in a sample project as a before/after diagnostic-count comparison is an
optional cross-check (see "Recommended order" below) — not a dependency of running either
tool.

Both follow `roslyn-rewriters` conventions: dry-run by default (prints `file:line  finding`),
`--write` to apply, trivia-preserving `CSharpSyntaxRewriter`, one `.cs`/folder argument.

**Emitted names are resolved at each insertion point, not hardcoded.** Both tools check —
via speculative binding (`SemanticModel.GetSpeculativeSymbolInfo`), not a text search for
`using System;` — whether the short name (`ArgumentNullException`, `ArgumentOutOfRangeException`,
etc., or the `--helper-type` name) actually binds to the intended type right where the call is
being inserted. If it does (an explicit, global, or implicit `using System;` is in scope and
nothing shadows the name), the emitted call is the short form:
`ArgumentNullException.ThrowIfNull(x)`. If it doesn't — no matching `using` in scope — it falls
back to `System.ArgumentNullException.ThrowIfNull(x)`, and only adds `global::` on top of that
if `System` itself is shadowed at that point by a local type/namespace. This means the two
rewriters never litter a file that already has `using System;` with fully-qualified names, and
never emit a short name that could silently bind to the wrong thing.

## Per-rule tables

Every matcher below cites the exact source file + method it was ported from
(the dotnet/sdk repository, `src/Microsoft.CodeAnalysis.NetAnalyzers/...`) — read the citation
comment above each matcher in the `.cs` files, not just this table, before trusting or
extending it.

### CA1062 — `add-null-guards.cs`

Source: `Microsoft.CodeQuality.Analyzers/QualityGuidelines/ValidateArgumentsOfPublicMethods.cs`
(entry point) + `Utilities/FlowAnalysis/.../ParameterValidationAnalysis/*.cs` (the real
dataflow) + CA1510's fixer shape for the if-throw conversion.

| Matches | Produces |
|---|---|
| Reference-type parameter of an externally visible method/constructor, dereferenced (member access, element access, delegate invocation, or `foreach` source) before any recognized validation, scanned linearly in document order | `ArgumentNullException.ThrowIfNull(p);` inserted at the top of the body (after `base(...)`/`this(...)` for constructors) |
| `if (p == null) throw new ArgumentNullException(nameof(p));` / `if (p is null) throw ...` found anywhere in the scanned body | Converted to `ArgumentNullException.ThrowIfNull(p);` in place |

Recognized validation (linear, not per-CFG-path): `ArgumentNullException.ThrowIfNull(p)`,
`p ?? throw ...`, or a prior guard already converted in the same pass.

### CA1510 — `ArgumentNullException.ThrowIfNull`

Source: `UseExceptionThrowHelpers.cs`, `IsParameterNullCheck` + the dispatch in the
`OperationKind.Throw` handler; fixer in `UseExceptionThrowHelpersFixer.cs`.

| Matches | Produces |
|---|---|
| `if (p is null) throw new ArgumentNullException(...);` | `ArgumentNullException.ThrowIfNull(p);` |
| `if (p == null) throw ...;` / `if (null == p) throw ...;` | same |
| Any of the above as the sole statement of a braced block | same |

Requires: no `else`; `p` is a **parameter reference** (not a field/local/property) of
reference type; the constructor has 0-2 args and, if present, a "meaningful" second arg
(message/inner exception) that is null/empty is still allowed — a non-null/non-empty
message or `>= 3` args blocks the match (this is the analyzer's own false-positive
heuristic, `HasPossiblyMeaningfulAdditionalArguments`).

### CA1511 — `ArgumentException.ThrowIfNullOrEmpty`

Source: `UseExceptionThrowHelpers.cs`, `IsNullOrEmptyCheck`/`IsArgLengthEqual0`/
`IsArgEqualStringEmpty`.

| Matches | Produces |
|---|---|
| `if (string.IsNullOrEmpty(arg)) throw new ArgumentException(...);` | `ArgumentException.ThrowIfNullOrEmpty(arg);` |
| `if (arg is null \|\| arg.Length == 0) throw ...;` | same |
| `if (arg == null \|\| arg == string.Empty) throw ...;` | same |

**Runtime-behavior note, not a bug:** `ThrowIfNullOrEmpty` throws `ArgumentNullException`
for a null argument and `ArgumentException` for an empty one — the hand-written guard it
replaces always threw `ArgumentException` for both. This is what the *stock* fixer produces
too, not a divergence introduced here — flag it in review if a caller specifically catches
`ArgumentException` around a call that used to throw it for null.

### CA1512 — `ArgumentOutOfRangeException.ThrowIf*`

Source: `UseExceptionThrowHelpers.cs`, `IsNegativeAndOrZeroComparison` +
`IsGreaterLessEqualThanComparison`.

| Matches | Produces |
|---|---|
| `if (arg is 0 \| arg == 0 \| 0 == arg) throw ...;` | `ThrowIfZero(arg)` |
| `if (arg < 0) throw ...;` / `if (0 > arg) throw ...;` | `ThrowIfNegative(arg)` |
| `if (arg <= 0) throw ...;` / `if (0 >= arg) throw ...;` | `ThrowIfNegativeOrZero(arg)` |
| `if (arg > other) throw ...;` / `if (other < arg) throw ...;` | `ThrowIfGreaterThan(arg, other)` |
| `if (arg >= other) throw ...;` / `if (other <= arg) throw ...;` | `ThrowIfGreaterThanOrEqual(arg, other)` |
| `if (arg < other) throw ...;` / `if (other > arg) throw ...;` | `ThrowIfLessThan(arg, other)` |
| `if (arg <= other) throw ...;` / `if (other >= arg) throw ...;` | `ThrowIfLessThanOrEqual(arg, other)` |
| `if (arg == other) throw ...;` | `ThrowIfEqual(arg, other)` |
| `if (arg != other) throw ...;` | `ThrowIfNotEqual(arg, other)` |

`other` is used verbatim from the original syntax (evaluated exactly once, same as before —
no side-effect analysis needed since it's relocated, not duplicated). Skipped: nullable
value types and enums (`AvoidComparing` in the source — a bare `T` `ThrowIf*` overload isn't
meaningful there the same way); `arg is < 0`/patterns with a relational pattern (analyzer's
own "future work" comment); compound `&&`/multi-clause conditions (see Extensions below for
the one compound shape this tool *does* add).

### CA1513 — `ObjectDisposedException.ThrowIf`

Source: `UseExceptionThrowHelpers.cs`, the `ObjectDisposedException` branch of the
`OperationKind.Throw` handler; fixer in `UseExceptionThrowHelpersFixer.cs`.

| Matches | Produces |
|---|---|
| `if (cond) throw new ObjectDisposedException(x.GetType().Name);` | `ObjectDisposedException.ThrowIf(cond, x);` |
| `if (cond) throw new ObjectDisposedException(x.GetType().FullName);` | same |
| `if (cond) throw new ObjectDisposedException(GetType().Name);` (implicit `this`) | `ObjectDisposedException.ThrowIf(cond, this);` |
| Any other `if (cond) throw new ObjectDisposedException(...)` shape (e.g. `new ObjectDisposedException(null)`, a `nameof(...)`, a hardcoded string) | **reported only, never rewritten** — matches the stock fixer exactly: it also can't derive an instance argument for these and leaves them as diagnostic-only |

Requires the containing type to be a reference type (skips `struct` — avoids boxing `this`
into the `object` overload of `ThrowIf`, same reason the real analyzer skips it). Unlike the
other three rules, the real analyzer reports **any** matching if/throw shape here regardless
of whether the fixer can act on it — this tool mirrors that by always emitting a finding,
and only emitting a rewrite for the two derivable shapes.

## Deliberate skips (ported from the source, not omissions)

- **An `else` on the guarding `if`** — the analyzer gives up ("these are rare"); so does
  this tool.
- **More than one statement in the guard's block** (e.g. a `Console.WriteLine` before the
  `throw`) — not a single-statement guard, no match.
- **Inverted conditions** (`arg is not null`, `arg != null`, `0 < arg` where `0` isn't the
  literal-first CA1512 shape) — the source only matches the specific operand orders listed
  in the tables above; an inverted guard that happens to be logically equivalent is not
  recognized (same in the stock analyzer).
- **A message/inner-exception argument that reads as meaningful** (non-null, non-empty,
  `>= 3` total args) — `HasPossiblyMeaningfulAdditionalArguments`; the guard might be
  carrying information a plain `ThrowIf*` call would silently drop.
- **Non-parameter operands** — a field, property, or local being null/range-checked instead
  of a parameter is out of scope for all four CA151x rules and for CA1062 (which is
  specifically about *parameters* of externally visible members).
- **Unconstrained generic `T`, or `T` with only an interface constraint** — not a reference
  type per Roslyn's `ITypeSymbol.IsReferenceType`, so `ThrowIfNull`/CA1062 don't apply; `T :
  class` or `T : SomeReferenceType` constraints do qualify.
- **Nullable value types and enums** for CA1512's `ThrowIf*` family (`AvoidComparing`).
- Everything in `add-null-guards.cs`'s own skip list (iterator methods reported not
  rewritten, no path-sensitivity, no interprocedural validation, extension `this` treated
  like any other parameter) — see that file's header comment and
  `csharp-nullable-migration`'s CA1062 section for the full list; unchanged by this move.

## Divergences from the stock fixer (deliberate, and why)

**`modernize-guards.cs` is intentionally more conservative than the real fixer in one
case: a mismatched `paramName` argument.** The stock fixer's `HasReplaceableArgumentName`
only checks that the paramName argument is a *constant* (a literal or `nameof(...)`) — it
does not check that the constant matches the parameter actually being checked. Given
`if (null == arg) throw new ArgumentNullException("somethingElse");`, the stock fixer
still rewrites it to `ArgumentNullException.ThrowIfNull(arg);`, silently discarding
`"somethingElse"`.

This tool reports that case instead of rewriting it: a mismatched name is often itself a
pre-existing bug (or, rarely, deliberate and worth a second look), and rewriting changes a
runtime-observable value (`ArgumentException.ParamName`, which callers can catch and read)
without anyone having looked at why it didn't match. A `nameof(param)` matching the checked
parameter, or no name argument at all, rewrites exactly as the stock fixer would — only an
actual mismatch is held back. This is the one point where dry-run output from this tool and
a live CA1510 diagnostic count can disagree after `--write`: expect that specific line to
remain flagged, on purpose.

## Extensions beyond the stock fixer

Both marked `EXTENSION` in the source, applied only where provably safe:

- **Compound range guards.** `if (i < 0 || i >= count) throw new
  ArgumentOutOfRangeException(nameof(i));` → a block of two `ThrowIf*` calls
  (`ThrowIfNegative(i); ThrowIfGreaterThanOrEqual(i, count);`), when both `||` operands
  independently match a CA1512 shape against the **same parameter**, and every operand
  (parameter uses and range bounds) is provably side-effect-free (no invocations,
  assignments, increments, `await`, object creation, or indexers in either half of the
  `||`). Preserves the original short-circuit order exactly (first clause throws first).
  This shape is genuinely beyond CA1512's own reach — the real analyzer doesn't attempt
  compound conditions at all, so this extension closes a real gap rather than merely
  restating the rule. Anything that fails the side-effect check is **reported, not
  rewritten**.
- **`--helper-type <Name>` polyfill mode.** Bypasses the "does the BCL `ThrowIf*` helper
  exist on this compilation" gate (the reason the analyzer would never fire on
  netstandard2.0/net4x — the helpers genuinely don't exist there) and instead emits calls to
  an unqualified `<Name>.ThrowIfXxx(...)` — the same convention as `csharp-guard-clauses`'
  `ArgumentExceptionHelper`/`ArgumentValidation` alias polyfill: same method names, same
  parameter order, picked up via a `<Using Alias="...">` the target project supplies. See
  "Polyfill mode" below.

Everything else this tool finds but can't prove equivalent to a `ThrowIf*` call (non-constant
paramName argument, a compound guard with a side effect, an `ObjectDisposedException` guard
with no derivable instance) is **reported with a reason, never guessed at**.

## Recommended order

1. **`add-null-guards.cs` before `modernize-guards.cs`**, per project. Adding a missing null
   guard changes what a method's callers experience (a clear `ArgumentNullException` instead
   of a deferred `NullReferenceException`); modernizing an *existing* guard's shape doesn't.
   Doing the addition pass first means the modernization pass immediately picks up the
   newly-added `if (p is null) throw ...`/`ThrowIfNull` shapes in the same run it would find
   hand-written ones, instead of needing a second pass later.
2. **One project at a time**, same as every `roslyn-rewriters`/`csharp-nullable-migration`
   pass: dry run → read the diff → `--write` → build → run tests → commit → next project.
3. **Post-rewrite verification is build, tests, and a re-run for idempotency** — that's the
   whole gate, and it doesn't need either analyzer enabled:
   - `dotnet build` — succeeds, 0 new errors.
   - Run the project's real test suite (`csharp-verification`) — a modernized guard is
     runtime-observable (see the CA1511 null-vs-empty-exception-type note above), so a clean
     build alone isn't enough.
   - **Re-run the same rewriter with `--write` again** — a second run against
     already-rewritten code should report 0 rewrites (only report-only findings, if any
     remain). Both tools are built this way; treat a non-zero second-pass rewrite count as a
     bug to investigate, not something to re-apply blindly.
4. **Optional cross-check, not a required step:** if the target project already has (or you're
   willing to add) `dotnet_diagnostic.CA1062.severity = warning` (plus
   `dotnet_code_quality.CA1062.api_surface = all` if needed) and
   `dotnet_diagnostic.CA151{0,1,2,3}.severity = warning` in `.editorconfig`, a before/after
   diagnostic-count comparison is a useful sanity check on top of build+tests. Neither tool
   depends on it, and there's no need to enable the analyzers, add the package, or run
   `dotnet format analyzers` just to use these rewriters. Anything the real analyzers still
   flag afterward is either a deliberate divergence (the paramName-mismatch case above) or a
   shape genuinely outside this tool's conservative reach (path-sensitive CA1062 dataflow, an
   `ObjectDisposedException` guard with no derivable instance, a compound guard with a side
   effect) — triage it by hand if you do check, the same way `csharp-nullable-migration`
   triages what its own rewriters leave behind.

## Polyfill mode

For netstandard2.0/net4x projects where the BCL `ThrowIf*` helpers don't exist (so the real
analyzer never fires there, and neither would this tool by default): follow
`csharp-guard-clauses`'s "Older TFMs" section to add the `<Using Alias="ArgumentExceptionHelper"
Include="..." />` MSBuild alias and the polyfill type (`ThrowIfNull`/`ThrowIfNullOrEmpty`/the
`ArgumentOutOfRangeException.ThrowIf*` family/`ObjectDisposedException.ThrowIf`, same names
and parameter order as the BCL), then run:

```sh
dotnet run modernize-guards.cs -- MyNetStandardProject --helper-type ArgumentExceptionHelper --write
```

The emitted calls (`ArgumentExceptionHelper.ThrowIfNull(arg);`,
`ArgumentExceptionHelper.ThrowIfNegative(i);`) compile clean against a netstandard2.0 polyfill
following that exact convention.

## Limits versus the real analyzers

- **CA1062's dataflow is path-sensitive; `add-null-guards.cs` is linear/textual.** A null
  check inside one `if`/`else` branch is treated as validating the parameter for the rest of
  the method textually after it, even on paths that don't pass through that branch — this can
  under-report, never mis-guard. The real analyzer is the backstop; see
  `csharp-nullable-migration`'s CA1062 section for the full gap list.
- **No interprocedural analysis** for either tool — validation happening inside a called
  private helper isn't recognized (CA1062's own limitation too, for validation methods not
  matching its recognized shapes/attributes/editorconfig option).
- **`modernize-guards.cs`'s compound-guard extension is the only place this tool exceeds the
  real analyzer's coverage** — everywhere else, if the real CA1510-CA1513/CA1062 wouldn't
  flag something, this tool doesn't either.
- **Neither tool attempts a fix requiring semantic proof it can't construct** — see
  "Divergences" and "Extensions" above for the exact set of report-only outcomes.

## Checklist

- [ ] `add-null-guards.cs` run before `modernize-guards.cs`, one project at a time
- [ ] Dry run reviewed before every `--write` — read the `file:line` findings, not just the count
- [ ] Report-only findings (paramName mismatch, non-constant name, side-effecting compound
      guard, undeliverable `ObjectDisposedException` instance) triaged by hand, not ignored
- [ ] Build + tests run after each `--write`, not just a clean compile
- [ ] Re-run after `--write` to confirm 0 further rewrites (idempotency)
- [ ] Verification gate is build + tests + idempotent re-run — no analyzer enablement or
      `dotnet format` step required; real CA1062/CA1510-CA1513 used only as an optional
      extra cross-check
- [ ] `--helper-type` used (never a bare rewrite) on netstandard2.0/net4x projects lacking
      the BCL helpers, pointed at a real polyfill following `csharp-guard-clauses`'s alias
      convention
- [ ] Each pass is its own commit, revertible independently

## Related skills

- `roslyn-rewriters` — the general conventions (dry-run, trivia preservation, workspace vs.
  syntax-only) both tools here follow.
- `csharp-guard-clauses` — the target-state catalogue of `ThrowIf*` helpers these rewriters
  produce, and the `ArgumentExceptionHelper`/`ArgumentValidation` polyfill convention
  `--helper-type` mode follows.
- `csharp-nullable-migration` — the broader nullable rollout playbook; its "Adding null
  guards at public boundaries" section points here for the CA1062 tool.
- `csharp-verification` — the build/analyzer/test gate to run after every `--write`.
