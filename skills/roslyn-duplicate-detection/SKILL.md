---
name: roslyn-duplicate-detection
description: Use to measure code duplication in a C#/.NET repo and to find the duplicated method/accessor/constructor bodies that should become a shared helper - locally, without a Sonar server. Write a throwaway read-only Roslyn program (a single-file `dotnet run detect-clones.cs` app) that normalizes each body to a token-kind stream and groups equal fingerprints, so renamed copies still match. Also use it to verify a de-duplication effort actually hit its target percentage.
---

# Roslyn duplicate detection - find and verify clones, structurally

The duplication *percentage* a Sonar server reports is computed off a token-based copy-paste detector that runs in CI, not by the analyzer package your build loads - so `dotnet build` never tells you the number, and you are blind between pushes. You have two good local options, and they answer different questions:

- **A token-window tool (jscpd)** gives you a fast *percentage* and finds duplicated runs of tokens anywhere, including a clone buried inside two otherwise-different methods. It is the closest local proxy for the Sonar metric. `npx --yes jscpd src --pattern "src/**/*.cs" --ignore "**/bin/**,**/obj/**,**/tests/**,**/benchmarks/**,**/tools/**,**/*.Designer.cs,**/*.g.cs" --min-tokens 100 --reporters console`.
- **A structural Roslyn detector (this skill)** works at the granularity of a whole *body* - a method, accessor, constructor, operator, or local function - and can canonicalize identifiers and literals so a copy that only renamed its variables still matches (a "type-2" clone). That is exactly the unit you extract into a helper, and it survives the renames a token tool splits on. It parses the real syntax tree, so it never trips on comments or strings.

Use jscpd for the headline number; use this for **what to extract** and to prove each clone set collapsed. They are complementary - run both.

This is a read-only sibling of `roslyn-discovery` and `roslyn-rewriters`: same single-file `dotnet run` harness, same parse-don't-guess principle. It *reports*; you (or a rewriter from `roslyn-rewriters`) do the edit.

## How it works

`./examples/detect-clones.cs` is a complete, runnable detector. It:

1. Parses every `.cs` under a root (skipping `bin`/`obj`/`tests`/`benchmarks`/`tools`/generated).
2. For each method/accessor/constructor/operator/local-function body over `--min-tokens` (default 75), walks its `DescendantTokens()` and emits a normalized stream: each token becomes its `SyntaxKind`, except identifiers become `$id` and literals become `$lit` (unless `--exact`). Trivia - whitespace and comments - is ignored for free because you are iterating tokens.
3. Hashes the stream and groups equal hashes. A group of 2+ is a clone set.
4. Reports the clone sets (with `file:line` spans), the total duplicated line count, and a duplication percentage (`duplicated lines / total code lines`).

Run it from the repo root:

```
dotnet run <path-to>/detect-clones.cs -- src --min-tokens 100 --top 30
```

`--exact` switches to type-1 (identical text) matching, closer to what a token tool counts. The default type-2 mode is what surfaces "these eight analyzers are the same body with a different `SyntaxKind`" - the highest-value extractions.

## Reading the output and acting on it

Each clone set is a candidate for one helper. For each:

- **Extract the shared body into a `static internal` helper method**, in a domain-named helper class (not `DedupHelper`). Parameterize *only* the parts that differ between instances - a `SyntaxKind`, a selector delegate, a descriptor. If the instances differ in more than a couple of small ways, they may not be a true clone; do not contort call sites to force a merge.
- **Give every helper you add its own unit test** - a real behaviour-asserting test named for what the helper does, covering its cases. A helper with no test is half the job.
- **Do not merge coincidental look-alikes.** Two bodies that hash the same but mean genuinely different things, or that will evolve independently, are better left apart - say so rather than gluing them together. A false merge that hurts readability is worse than the duplication.
- Keep the host repo's discipline in the helper: if it runs on a hot path (an analyzer's per-node callback), the helper stays allocation-free on the no-diagnostic path - no LINQ, no `DescendantNodes()`, `netstandard2.0`-safe.

Then **re-run the detector to prove it**: the clone set you extracted should be gone and the percentage should drop. That closing measurement is the point - "I extracted a helper" is not the same as "the duplication metric moved."

## Verifying a de-duplication goal

To decide whether a target ("under 1.5%, ideally under 0.5%") is met, run **both** tools and report both numbers, because they measure differently:

- jscpd's percentage is the closest analogue to the Sonar gate - quote it as the headline.
- the Roslyn detector's clone-set count going to zero (at your `--min-tokens`) is the stronger signal that no *extractable* duplication remains.

State the `--min-tokens` you used and whether you ran type-1 or type-2 - a lower threshold or type-2 finds more, so a bare percentage without those is not reproducible. If a residual clone set is duplication that *should* stay (two rules that legitimately look alike but will diverge), name it and exclude it from the verdict rather than pretending it is gone. And note that Sonar's own number will still only settle on the next CI run - the local tools tell you when you are in range, not the official figure.

## Notes

- Syntax-only: no build, no `MSBuildWorkspace`, no project context needed - just `CSharpSyntaxTree.ParseText`. Fast enough to run on every iteration.
- Method granularity is a feature for the "extract a helper" job and a limitation for fine-grained clones - pair with jscpd when a clone is a fragment, not a whole body.
- It is throwaway. Keep it in this skill's `examples/`, or drop a copy in a scratch dir - do not commit it into the product repo unless the team wants a standing check.
- Pin `Microsoft.CodeAnalysis.CSharp` to a version the box already restores (the example pins `4.14.0`); syntax-only parsing does not need the newest line.
