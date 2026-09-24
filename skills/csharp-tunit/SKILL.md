---
name: csharp-tunit
description: Use when writing or reviewing C#/.NET tests with TUnit — test structure, async assertions, data-driven tests, lifecycle hooks, and especially controlling execution context with test executors (scoped state/scheduler setup) and isolating global state into serial assemblies instead of leaning on static classes or sleeping. Pairs with the tdd skill.
---

# Writing decent TUnit tests

TUnit runs on **Microsoft.Testing.Platform** (not VSTest). Tests run **in parallel by design**, and each test gets a **fresh instance** of its class. Most setup belongs in the constructor; hooks are for async or shared resources. By default TUnit discovers and runs tests through compile-time **source generation** (fast, AOT-safe); `--reflection` / `[assembly: ReflectionMode]` falls back to runtime reflection — only needed for interop with other source generators (e.g. bUnit's Razor components).

## TUnit is async-first — write async tests

This is the design decision everything else follows from. **Every assertion returns a `Task` and only runs when you `await` it.** Before the `await`, `Assert.That(...)` has merely *built* a chain of rules — nothing has been checked.

- **Make every test `public async Task` and `await` every assertion.** A sync `void` test with an un-awaited `Assert.That(...)` compiles, runs, and **passes silently without checking anything** — the worst kind of false green. A built-in analyzer flags the missing `await`; never suppress it.
- **Why async-everywhere:** you never have to track which calls need `await`; custom assertions can do real async work (DB/HTTP) with no sync-over-async; and you sidestep the deadlocks that mixing sync and async invites. The `Task` overhead is negligible on modern .NET.
- **Don't fight it.** No `void` tests (except where a hook genuinely can't be async), no blocking. If a value is async, `await` it; if an assertion is on async code, pass the delegate and let TUnit run it: `await Assert.That(async () => await Op()).Throws<T>()`.
- **An awaited assertion returns the validated subject** — capture it: `var circle = await Assert.That(shape).IsTypeOf<Circle>();` then assert on `circle` with no cast.

## Shape of a test

```csharp
public class CalculatorTests
{
    private readonly Calculator _sut = new();   // ctor runs per test — fresh state, no leakage

    [Test]
    public async Task Add_TwoPositives_ReturnsSum()
    {
        var result = _sut.Add(2, 3);
        await Assert.That(result).IsEqualTo(5);   // assertions are awaited
    }
}
```

- `Assert.That(x).IsEqualTo(...)` / `.IsNull()` / `.IsTrue()` / `.Contains(...)` / `.Throws<T>()` — **await every assertion.**
- Name tests `Method_Scenario_ExpectedResult`. One behavior per test (see `tdd`).
- Accept a `CancellationToken` parameter on tests and pass it down — TUnit supplies one and honors timeouts.

## Naming & organizing the test class

- **The test class is named after the class under test: `<ClassName>Tests`.** `Calculator` → `CalculatorTests`, `OrderService` → `OrderServiceTests`. One test class per production class is the default; a reader should find the tests for `Foo` by looking for `FooTests`. Mirror the namespace too (`Acme.Billing.Calculator` → tests in `Acme.Billing.Tests` / `CalculatorTests`).
- **Don't invent unrelated names** (`MathChecks`, `CoreScenarios`) — the name is a pointer back to what's covered.
- **Name every test file, type, and method for the behavior under test, never for the chore that produced it.** Avoid names built around the task rather than the feature — banned words include `Touched`, `Changed`, `New`, `Old`, `Misc`, `Temp`, `Coverage`, `Fix`, `Updated`, `WIP` (`GetCoverageTests` says nothing; `OrderService_AppliesBulkDiscount_Tests` does). Tests live with the feature they exercise, not in a grab-bag file named after the session that added them.
- **Split into `partial class` files only when there's a meaningful reason to separate sections** — and keep the same class name across the files. Good reasons: a large surface with distinct behavioural areas (`OrderServiceTests.Pricing.cs`, `OrderServiceTests.Refunds.cs`, `OrderServiceTests.Validation.cs`), or separating a heavy data-driven block from the plain cases. The split is organisational; it stays one logical `OrderServiceTests`.

```csharp
// OrderServiceTests.Pricing.cs
public partial class OrderServiceTests
{
    [Test]
    public async Task Total_AppliesBulkDiscount() { ... }
}

// OrderServiceTests.Refunds.cs — same class, different concern
public partial class OrderServiceTests
{
    [Test]
    public async Task Refund_RestocksInventory() { ... }
}
```

- **Don't split for the sake of it.** If the whole class fits comfortably in one file, keep it in one file — `partial` is for genuinely distinct sections, not arbitrary line-count quotas. Shared fixtures/fields/helpers live in one of the partials (or a `.Common.cs` partial) so every part sees them.

## New instance per test — the #1 gotcha

Class fields reset every test because each test runs on its own instance. This is intentional: it kills cross-test state leakage and enables parallelism.

```csharp
private int _value;                 // ❌ resets to 0 every test
private static int _shared;         // ⚠️ shared across tests AND threads — only if you truly mean it
```

If you think you need shared state, you almost always want `[ClassDataSource<T>]` / a fixture (per-class data) or a **test executor** (ambient/global process state — see "Execution context" below) instead of a raw `static`. Prefer independent tests.

## Data-driven tests

```csharp
[Test]
[Arguments(2, 3, 5)]
[Arguments(-1, 1, 0)]
public async Task Add_Cases(int a, int b, int expected)
    => await Assert.That(_sut.Add(a, b)).IsEqualTo(expected);

[Test]
[MethodDataSource(nameof(Cases))]
public async Task Add_FromMethod(int a, int b, int expected) { ... }
public static IEnumerable<(int, int, int)> Cases() => [ (2,3,5), (-1,1,0) ];
```

Use `[ClassDataSource<T>]` for sources that need async init or disposal (implement `IAsyncInitializer` / `IAsyncDisposable` on the injected type). Control lifetime with `Shared`: `None` (default, new instance per injection), `PerClass`, `PerAssembly`, `PerTestSession` (one instance for the whole run — best for expensive things like containers), or `Keyed` (shared per `Key` value). Never mutate a shared instance's state from a test — concurrent tests make the order unpredictable.

For the Cartesian product of parameter values, put `[MatrixDataSource]` on the method *and* `[Matrix(...)]` on each parameter:

```csharp
[Test]
[MatrixDataSource]
public async Task Multiply([Matrix(2, 3)] int a, [Matrix(4, 5)] int b)
    => await Assert.That(a * b).IsGreaterThan(0);   // generates (2,4) (2,5) (3,4) (3,5)
```

## Lifecycle hooks (only when the constructor won't do)

| Attribute | Runs | Method | Use for |
|-----------|------|--------|---------|
| `[Before(Test)]` / `[After(Test)]` | each test | instance | async setup/teardown |
| `[Before(Class)]` / `[After(Class)]` | once per class | static | shared expensive resource |
| `[Before(Assembly)]` / `[After(Assembly)]` | once per assembly | static | global bring-up/teardown |
| `[Before(TestSession)]` / `[After(TestSession)]` | once per run | static | process-wide bring-up/teardown |
| `[Before(TestDiscovery)]` / `[After(TestDiscovery)]` | once, before/after discovery | static | inspect/adjust what got discovered |
| `[BeforeEvery(...)]` / `[AfterEvery(...)]` | every test/class/assembly **in the session**, regardless of where it's defined | static only | cross-cutting global hooks; put these in their own file (e.g. `GlobalHooks.cs`) since they affect the whole suite |

Hooks may be `void` or `async Task` (never `async void`), and may take the matching context (`TestContext`, `ClassHookContext`, `AssemblyHookContext`, `TestSessionContext`, `BeforeTestDiscoveryContext`, …) and a `CancellationToken`. `[After]`/`[AfterEvery]` hooks always run even after failure, and TUnit collects exceptions from all of them rather than stopping at the first. Check results in cleanup:

```csharp
[After(Test)]
public async Task Cleanup(TestContext ctx)
{
    if (ctx.Execution.Result?.State == TestState.Failed)
        await CaptureDiagnostics();   // screenshot, logs, etc.
}
```

`TestContext` exposes its surface through typed sub-properties — `Metadata` (name, class, display name), `Execution` (result, retry info, `OverrideResult`), `Output` (`WriteLine`, artifacts), `Parallelism`, `Dependencies`, `StateBag`, `Events`, and `Isolation`. `TestContext.Current` gives you the currently-running test's context from anywhere (test body, hook, or a helper called from either), and `Isolation.GetIsolatedName("todos")` builds a unique resource name (`test_42_todos`) so parallel tests don't collide on shared external resources (tables, queues, cache keys).

## Common toggles & tricks

Attributes that earn their keep. Most apply at **method, class, or assembly** level, and **the more specific wins** (method > class > assembly) — so set a project default at the assembly and override per-test.

| Attribute | Does | Notes |
|-----------|------|-------|
| `[Timeout(ms)]` | Fails the test if it runs too long, cancels the token | Accept the `CancellationToken` param and thread it through, or the timeout can't actually abort the work. Each `[Retry]` attempt gets a fresh window. |
| `[Retry(n)]` | Re-runs **only on failure**, up to n times, stops on first pass | For flaky externals. Subclass and override `ShouldRetry` to retry only on, say, transient HTTP. |
| `[Repeat(n)]` | Runs n **extra** times unconditionally | Stress/consistency checks — pairs well with a concurrency probe. Different intent from `[Retry]`. |
| `[Skip("reason")]` | Skips at discovery; reason is required | Subclass for conditional skips (`WindowsOnly`). For runtime conditions use `Skip.Test("reason")` inside the test or a `[Before]` hook. |
| `[Explicit]` | Runs only when the filter selects *only* explicit tests | Local seeders / dev utilities you don't want in a normal CI run. |
| `[DependsOn(nameof(Other))]` | Orders this test after another, **without** serializing the whole suite | Prefer over `[NotInParallel(Order=)]` — unrelated tests still parallelize. Many `[DependsOn]` is a smell; consider one test or shared setup. |
| `[Category("X")]` | Tags for filtering | `--treenode-filter "/*/*/*/*[Category=X]"`. |
| `[NotInParallel("key")]` | Prevents overlap with tests sharing that key | Keyless = runs totally alone (most restrictive). Use keys so unrelated tests still run. |
| `[ParallelGroup("key")]` | Group runs internally parallel, isolated from other groups (phased) | For batching a family of tests that can overlap each other but nothing else. |
| `[ParallelLimiter<T>]` | Caps concurrent count for tests sharing limiter `T` | `T : IParallelLimit`. For limited external connections. |

Async-timing helpers (instead of `Task.Delay` + guesswork):

- `await Assert.That(task).CompletesWithin(TimeSpan.FromSeconds(5))` — assert a task finishes in time.
- `await Assert.That(() => value).WaitsFor(s => s.IsEqualTo(true), timeout: ...)` — poll until a nested assertion passes (`Eventually` is the alias).
- `await Assert.That(task).IsCompletedSuccessfully()` / `.IsFaulted()` / `.IsCanceled()` — task-state checks.
- Chain with `.And` / `.Or`; wrap several independent asserts in `using (Assert.Multiple())` so all failures report at once instead of stopping at the first. `.And` and `.Or` **cannot be mixed** in one chain (`MixedAndOrAssertionsException` at runtime) — split into separate `Assert.That(...)` calls instead.
- Add context to a failure with `.Because("reason")` on the end of a chain — it's folded into the failure message, not a separate assert.
- `.Member(x => x.Prop, p => p.IsEqualTo(...))` asserts on a property (works on nested paths too, e.g. `o => o.Customer.Address.City`) without a separate `Assert.That` and cast.

Runner flags worth knowing (pass after `--` when using `dotnet test`): `--treenode-filter "/Asm/Namespace/Class/Method"` (wildcards `*`), `--maximum-parallel-tests N` (or `TUNIT_MAX_PARALLEL_TESTS`), `--fail-fast`, `--list-tests`, `--coverage --coverage-output-format cobertura`, `--output Detailed` (to see `Console`/`Output.WriteLine`). Don't use `--no-build` — it can run stale binaries.

**.NET SDK `dotnet test` flags** (native to `dotnet test` itself, no `--` separator needed, since TUnit already runs on Microsoft.Testing.Platform — set `"test": {"runner": "Microsoft.Testing.Platform"}` in `global.json` so `dotnet test` talks MTP directly instead of shelling out to VSTest): `--no-dependencies` to skip rebuilding unrelated referenced projects on a rerun, `--maximum-failed-tests N` to bail out early on a broken change, `--timeout <duration>` to bound a hung run, `--test-modules <expr>` to include/exclude specific test assemblies from a multi-project run, `--results-directory-layout per-module` when merging TRX/coverage from several test projects would otherwise collide on file names. Same rule as above: fine for the inner loop, run the unrestricted suite before reporting the gate as passed (see `csharp-verification`).

## Execution context — test executors are the tool

This is what separates flaky tests from solid ones. The big lever in TUnit is the **test executor**: a wrapper that runs around every test it's attached to, so you can establish predefined state, swap a scheduler, or reset static/global state *before* the test and tear it back down *after* — at method, class, or assembly scope. ReactiveUI/Akavache lean on this heavily. It's the right answer whenever a test needs ambient state set up; reach for it before you reach for `[Before]`/`[After]` plumbing or hand-rolled statics.

### How a test executor works

Implement `ITestExecutor` — one method that receives the test body as a `Func<ValueTask>` and is responsible for invoking it. Everything before the call is setup; the `finally` is teardown that always runs:

```csharp
// Saves and restores a global handler around each test, so a test that
// registers its own handler can't leak into the next one. (ReactiveUI.Extensions)
internal sealed class UnhandledExceptionTestExecutor : ITestExecutor
{
    public async ValueTask ExecuteTest(TestContext context, Func<ValueTask> testAction)
    {
        var previous = UnhandledExceptionHandler.CurrentHandler;   // capture ambient state
        try
        {
            await testAction();                                     // run the test
        }
        finally
        {
            UnhandledExceptionHandler.Register(previous);           // always restore
        }
    }
}
```

Attach it where you want it to apply — and the same executor can sit at any scope:

```csharp
[assembly: TestExecutor<AkavacheTestExecutor>]   // every test in the assembly
[TestExecutor<AkavacheTestExecutor>]             // every test in this class
public class CacheDatabaseTests { ... }

[Test]
[TestExecutor<ResetOnlyExecutor>]                // just this one test
public async Task WithTaskPoolScheduler_Sets_Scheduler() { ... }
```

This is how ReactiveUI sets *predefined variables / ambient state* per test: executors named for the state they install — `ResetOnlyExecutor`, `WithVirtualTimeSchedulerExecutor`, `WithCacheSizesExecutor`, `AvaloniaTestExecutor` (rebuilds the Splat/ReactiveUI locator and runs the body on the UI thread). The executor reset-and-rebuilds the global builder state each test, e.g.:

```csharp
public async ValueTask ExecuteTest(TestContext context, Func<ValueTask> testAction)
{
    try
    {
        await ResetStateAsync();      // clear caches, reset the locator/builder
        ConfigureBuilder();           // install this run's predefined state
        await testAction();
    }
    finally
    {
        await ResetStateAsync();      // leave nothing behind for the next test
    }
}
```

### Why this beats static classes holding state

Don't park shared/ambient state in a `static` helper class and mutate it from setup — that state is shared across every test *and every thread*, so it leaks, races, and forces ordering assumptions. A test executor scopes the state to exactly the tests it decorates, captures-and-restores deterministically, and composes (assembly default + a per-class or per-method override). It's the same idea as a fixture, but it owns the *execution* of the test, which is what you need when the state is global to the process (schedulers, a service locator, a static handler).

### Executors are underrated — reach for one on purpose

Executors aren't a niche escape hatch; TUnit ships its own for exactly these two shapes, so treat them as the template rather than reinventing the pattern:

- **Platform-specific setup.** `[TestExecutor<STAThreadExecutor>]` runs a test on a dedicated STA thread (COM/WinForms/WPF interop). `[Culture("de-AT")]` is a thin attribute over `CultureExecutor` that sets `CurrentCulture`/`CurrentUICulture` for the test and restores it after — apply it at method, class, or `[assembly: Culture("en-US")]` scope. Both are proof that "wrap the test, install some ambient runtime state, always restore it" is a first-class pattern, not a workaround.
- **DI-specific setup you want replicated across many classes.** When several test classes all need "open a scope, hand the test something from it, close the scope" — the same shape as a `using` block — write one executor instead of copy-pasting `[Before(Test)]`/`[After(Test)]` into every class. Stash the scope in `context.StateBag` so the test body can pull services back out:

```csharp
internal sealed class ScopedDiTestExecutor : ITestExecutor
{
    private static readonly IServiceProvider Root = BuildProvider();   // built once

    public async ValueTask ExecuteTest(TestContext context, Func<ValueTask> testAction)
    {
        using var scope = Root.CreateScope();
        context.StateBag["DiScope"] = scope;
        try
        {
            await testAction();
        }
        finally
        {
            context.StateBag.TryRemove("DiScope", out _);
        }
    }
}

[TestExecutor<ScopedDiTestExecutor>]
public class OrderServiceTests
{
    [Test]
    public async Task Places_Order()
    {
        TestContext.Current!.StateBag.TryGetValue<IServiceScope>("DiScope", out var scope);
        var service = scope!.ServiceProvider.GetRequiredService<IOrderService>();
        await Assert.That(await service.PlaceAsync()).IsNotNull();
    }
}
```

This is a different problem from `IClassConstructor` / `DependencyInjectionDataSourceAttribute<TScope>` (which resolve *constructor parameters* for one class from a DI container) — reach for an executor when you want the same scoped setup/teardown wrapped around test *execution* itself, replicated identically across many classes, without a hook block in each one.

### When the state really is global: isolate it into its own assembly, parallelism off

Some state can't be made per-test no matter what — a process-wide service locator, a static registry, a shared SQLite handle. The pattern ReactiveUI/Akavache use: **pull those tests into their own test `.csproj` and turn parallelism off for the whole assembly**, so the serial assembly pays the cost while the rest of the suite still runs fully parallel. Akavache literally splits `Akavache.Core.Tests` (serial) from `Akavache.Core.Tests.Parallel`.

- Mark the whole assembly serial: `[assembly: NotInParallel]` (in an `AssemblyAttributes.cs`), and/or set `"parallel": false` in that project's `testconfig.json`.
- Attach the resetting executor assembly-wide: `[assembly: TestExecutor<MyResetExecutor>]` so every test in it starts from a clean global slate.
- Keep everything that *doesn't* touch global state in normal parallel assemblies — don't let one shared dependency drag the whole suite serial.

```csharp
// AssemblyAttributes.cs in the serial test project
[assembly: NotInParallel(nameof(UnhandledExceptionHandler))]
[assembly: TestExecutor<UnhandledExceptionTestExecutor>]
```

### Parallelism, briefly

Tests run in parallel by default. For a single test that can't (shared file, port), `[NotInParallel]` on the method (optionally with `Order` to sequence a group) is enough — you don't need a whole serial assembly for one test. Needing `[NotInParallel]` *often* is the smell that says "extract the global state behind an executor, or isolate it."

### Two more rules

- **Never `Console.WriteLine` in a test — assert instead.** A write-line is not a check: nobody reads the output, and the test still passes when the value is wrong. To pin down an unknown expected value, assert what you expect and read the actual value from the assertion's failure diff, or explore it in a throwaway scratch app outside the repo. Never commit a "dump" test whose only job is printing something.
- **Never block on async in a test.** No `.Result`, no `.GetAwaiter().GetResult()` — `await` it. Blocking can deadlock and defeats the platform's scheduling.
- **For Rx/time-based code, drive virtual time, don't sleep.** Install a virtual-time scheduler (via an executor like `WithVirtualTimeSchedulerExecutor`) and advance it, or order concurrent async with a barrier-style sequencer (ReactiveUI's `TestSequencer`, where each side `await`s `AdvancePhaseAsync()` at each checkpoint). A 30-second debounce test then runs in microseconds and is 100% deterministic — never `Task.Delay` and hope.

## Document tests with XML docs

Tests are code others read to learn what the system *promises*. Document them like any other API — this is the ReactiveUI convention and it pays off in review. Keep it concise (see `csharp-docs` — say what the signature doesn't, no novels):

- **`<summary>` on every test** stating the behavior under test, not a restatement of the name. "Verifies that throttling emits only the last value within the window." One line.
- **`<param>` for each data-driven parameter** (`[Arguments]` / `[MethodDataSource]` inputs) — what the value represents and, where useful, why that case matters.
- **`<returns>` on async tests** — the conventional `A task representing the asynchronous test.` (ReactiveUI uses exactly this). Hooks and helpers that return `Task` get one too.
- **Document executors, fixtures, and data-source methods** as well — `<summary>` on the `ITestExecutor`/`IAsyncInitializer` type explaining what state it sets up and tears down, so the next person knows what ambient context a `[TestExecutor<T>]` installs.

```csharp
/// <summary>Verifies that <see cref="Calculator.Divide"/> throws when the divisor is zero.</summary>
/// <param name="dividend">The numerator under test.</param>
/// <returns>A task representing the asynchronous test.</returns>
[Test]
[Arguments(10)]
[Arguments(-3)]
public async Task Divide_ByZero_Throws(int dividend) =>
    await Assert.That(() => _sut.Divide(dividend, 0)).Throws<DivideByZeroException>();
```

If the repo enforces docs via analyzers (StyleCop SA1600 etc.), undocumented tests are build warnings — treat them as failures (see `csharp-verification`).

## Checklist

- [ ] Test class named `<ClassUnderTest>Tests`; `partial` split only for meaningful distinct sections
- [ ] Each test asserts one behavior, named `Method_Scenario_Expected`
- [ ] Test is `async Task` with XML `<summary>`, `<param>`s, and `<returns>` documented
- [ ] No instance-field state assumed across tests (fresh instance each run)
- [ ] Every `Assert.That` is awaited; no `.Result`/`.GetAwaiter()`
- [ ] Ambient/global state set up via a `[TestExecutor<T>]`, not a mutable static helper class
- [ ] Unavoidable global state isolated into its own serial assembly (`[assembly: NotInParallel]` + resetting executor), rest stays parallel
- [ ] No real-time `Task.Delay` for timing — virtual time (via an executor) or a sequencer instead
- [ ] `[NotInParallel]` only where a real shared resource forces it
- [ ] `CancellationToken` accepted and threaded through

