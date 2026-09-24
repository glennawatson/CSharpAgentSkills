---
name: csharp-nunit
description: Use when writing or reviewing C#/.NET tests with modern NUnit (4.x) — the constraint model, `Assert.EnterMultipleScope()`, lifecycle and parallelism attributes, data-driven tests, and running on Microsoft.Testing.Platform. Steers away from NUnit 2/3-era idioms (classic asserts, `Assert.Multiple(() => ...)`, format-string messages). Pairs with the tdd skill.
---

# Writing decent NUnit tests

NUnit 4.x is the constraint model (`Assert.That(actual, constraint)`) running by default on VSTest, with first-class support for **Microsoft.Testing.Platform** via NUnit3TestAdapter's NUnit runner. This skill targets NUnit 4.6.x with NUnit.Analyzers — treat the analyzer as required, not optional.

## Don't write NUnit 2/3-era tests

A lot of NUnit code online still shows classic asserts and lambda-based multiple assertions. In NUnit 4 these are **legacy, not idiomatic**:

```csharp
// ❌ Classic assert — moved to NUnit.Framework.Legacy in NUnit 4, don't reach for it in new code
ClassicAssert.AreEqual(expected, actual);
Assert.IsNotNull(thing);

// ❌ Assert.Multiple with a lambda — allocates a closure, and awaiting inside the
// lambda doesn't do what you think (the delegate isn't awaited by Assert.Multiple)
Assert.Multiple(() =>
{
    Assert.That(a, Is.EqualTo(1));
    Assert.That(b, Is.EqualTo(2));
});
```

```csharp
// ✅ Constraint model, always
Assert.That(actual, Is.EqualTo(expected));

// ✅ Disposable multiple-assertion scope (NUnit 4.2+) — no lambda, no closure
using (Assert.EnterMultipleScope())
{
    Assert.That(a, Is.EqualTo(1));
    Assert.That(b, Is.EqualTo(2));
}
```

If you're maintaining an old suite full of `ClassicAssert`/`StringAssert`/`CollectionAssert`, that's `NUnit.Framework.Legacy` — leave it if migrating wholesale is out of scope, but don't add more of it.

## `Assert.EnterMultipleScope()` instead of `Assert.Multiple(() => ...)`

Introduced in **NUnit 4.2**, `Assert.EnterMultipleScope()` returns an `IDisposable` that you open with a `using` block (or `using var`). It replaced the lambda form as the preferred API — NUnit's own analyzer (`NUnit2045`/`NUnit2056`) now nudges `Assert.Multiple(() => ...)` toward it.

Why it's better than the lambda:

- **No closure allocation** and no delegate indirection — it's a plain scope, not a callback.
- **Works naturally with `await` inside the block.** The lambda form's delegate is a `Func<Task>` only if you opt in, and awaiting the wrong overload silently doesn't collect failures the way you expect. A `using` scope has no such trap — `await` freely between assertions and every `Assert.That` inside is still gathered.
- **Clearer stack traces** — failures point at the actual assertion line inside the `using` block instead of a lambda frame.

```csharp
[Test]
public async Task GetOrder_ReturnsExpectedFields()
{
    var order = await _sut.GetOrderAsync(42);

    using (Assert.EnterMultipleScope())
    {
        Assert.That(order.Id, Is.EqualTo(42));
        Assert.That(order.Status, Is.EqualTo(OrderStatus.Shipped));

        var lines = await _sut.GetLineItemsAsync(order.Id);   // await freely inside the scope
        Assert.That(lines, Has.Count.GreaterThan(0));
    }
}
```

Scopes nest and compose across `async` calls in the same method — there's no separate `Assert.MultipleAsync`; `EnterMultipleScope()` is the one API for both sync and async bodies. If you're stuck on NUnit 3, `Assert.Multiple(() => ...)` is the only option — upgrade if you can.

## The constraint model — `Assert.That` is the only assert you write

```csharp
Assert.That(actual, Is.EqualTo(expected));
Assert.That(3.14159, Is.EqualTo(3.14).Within(0.01));
Assert.That(numbers, Is.EquivalentTo(new[] { 3, 2, 1 }));   // set-like, ignores order
Assert.That(numbers, Has.Exactly(1).EqualTo(2));
Assert.That(numbers, Is.Ordered);
Assert.That(text, Does.Contain("world"));
Assert.That(text, Does.StartWith("hello"));
```

**Compound constraints** — `.And` / `.Or` chain onto the same actual value, and `Has.Property("Name").EqualTo(...)` reaches into a member without a separate variable:

```csharp
Assert.That(value, Is.GreaterThan(0).And.LessThan(10));
Assert.That(value, Is.EqualTo(1).Or.EqualTo(2).Or.GreaterThan(4));
Assert.That(exception, Has.Property("Message").EqualTo("boom"));
```

Compose one expressive constraint instead of several asserts on the same value. Chain it with `.And`/`.Or`, `Has.Property(...)`, `Throws...With...`, and modifiers such as `.Within(...)`, `.Using(...)`, `.IgnoreCase` and `.After(timeout, pollInterval)`. Check `docs.nunit.org` for the exact call before using a constraint method not shown here.

### Exceptions

```csharp
// Sync — constraint model
Assert.That(() => sut.Divide(1, 0), Throws.TypeOf<DivideByZeroException>().With.Message.Contains("zero"));

// Async delegate — assert against the result of the awaited call
await Assert.ThatAsync(async () => await sut.AddAsync(2, 3), Is.EqualTo(5));

// Classic exception helper for async void-ish throws — still the standard way to assert an async Task throws
Assert.ThrowsAsync<InvalidOperationException>(async () =>
{
    await sut.FailingOperationAsync();
});
```

`Assert.ThatAsync` applies a constraint to the *result* of an async delegate (`Func<Task<T>>`) and must be awaited — it isn't a polling helper. For polling a value until it settles, use the `.After(timeoutMs, pollIntervalMs)` modifier on a normal constraint:

```csharp
Assert.That(() => cache.IsWarmed, Is.True.After(2000, 20));
```

### Messages are interpolated strings, not format args

NUnit 4 dropped the `params object[]` composite-format overloads. The message parameter is a plain string or an interpolated string — no separate format arguments:

```csharp
// ❌ NUnit 3 style — no longer compiles against modern overloads
Assert.That(actual, Is.EqualTo(expected), "Expected {0} but got {1}", expected, actual);

// ✅ NUnit 4 — interpolate directly; only formats the string if the assertion actually fails
Assert.That(actual, Is.EqualTo(expected), $"Expected {expected} but got {actual}");
```

## Lifecycle

Default fixture lifecycle creates **one instance of the fixture class for the whole class**, so instance fields persist across `[Test]` methods unless reset in `[SetUp]`. Opt into a fresh instance per test instead — it's safer, especially once you parallelize:

```csharp
[TestFixture]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class OrderServiceTests
{
    private readonly OrderService _sut = new();   // fine to init inline — a new instance every test

    [SetUp]
    public void SetUp() { /* only for things that can't be a field initializer, e.g. async setup */ }

    [Test]
    public void Total_AppliesDiscount() { ... }
}
```

- With `InstancePerTestCase`, plain field initializers or a constructor do what `[SetUp]` used to — reach for `[SetUp]`/`[TearDown]` only when setup needs to be `async Task` (constructors can't be async) or needs the `TestContext`.
- `IDisposable`/`IAsyncDisposable` on the fixture is honored per instance under `InstancePerTestCase`, so it's a reasonable teardown spot too — pick one pattern (hooks or dispose) per fixture, don't mix.
- `[OneTimeSetUp]`/`[OneTimeTearDown]` are still per-class (static-ish, run once regardless of instance lifecycle) — use them only for genuinely expensive shared resources (a container, a compiled schema), and make the fixture `[NonParallelizable]` if that resource isn't safe for concurrent use.
- Async setup: `[SetUp]`/`[OneTimeSetUp]` can be `async Task` — never `async void`.

## Parallelism

```csharp
[TestFixture]
[Parallelizable(ParallelScope.All)]     // this fixture's tests run in parallel with each other and other fixtures
public class OrderServiceTests { ... }

[assembly: Parallelizable(ParallelScope.Fixtures)]   // assembly default: fixtures run in parallel, tests within a fixture don't
[assembly: LevelOfParallelism(4)]
```

- Put `[Parallelizable(ParallelScope.All)]` on fixtures by default; reserve `[NonParallelizable]` for a fixture that genuinely shares a resource (a fixed port, a shared file, a singleton you can't isolate) — treat needing it as a prompt to isolate the resource instead, not a default.
- Avoid `static` mutable state in the fixture or the code under test; combined with `InstancePerTestCase` and parallel execution, static state is exactly what races.
- Output is not a check. Never use `Console.WriteLine` or `TestContext.Out` in place of an assertion: a test that prints a value still passes when the value is wrong. To find an unknown expected value, assert what you expect and read the actual value from the failure message, or explore in a scratch app outside the repo. Use `TestContext.Out.WriteLine(...)` only for genuine diagnostics alongside real assertions; it is captured per test, unlike `Console.WriteLine` under parallel runs. `TestContext.CurrentContext` exposes the running test's name, properties and work directory.
- `[CancelAfter(ms)]` on a test cancels a `CancellationToken` parameter after the timeout — accept the token and thread it through, the same way you would for a real caller-supplied cancellation:

```csharp
[Test]
[CancelAfter(5000)]
public async Task ReadAsync_CompletesInTime(CancellationToken cancellationToken)
{
    var result = await _sut.ReadAsync(cancellationToken);
    Assert.That(result, Is.Not.Null);
}
```

## Data-driven tests

```csharp
[TestCase(1, 2, 3)]
[TestCase(-1, 1, 0)]
public void Add_Cases(int a, int b, int expected)
    => Assert.That(a + b, Is.EqualTo(expected));

[TestCaseSource(nameof(Cases))]
public void Add_FromSource(int a, int b, int expected)
    => Assert.That(a + b, Is.EqualTo(expected));

private static IEnumerable<TestCaseData> Cases()
{
    yield return new TestCaseData(1, 2, 3).SetName("OnePlusTwo");
    yield return new TestCaseData(-5, 5, 0).SetName("NegFivePlusFive");
}
```

- Prefer a `static` typed source method returning `IEnumerable<TestCaseData>` over `object[]` arrays — `.SetName(...)` gives readable test explorer names, `.Returns(...)` lets the test method itself return a value for NUnit to compare.
- `[Values(1, 2, 3)]` on a parameter + another `[Values(...)]` on a second parameter gives the **combinatorial** cross-product by default; add `[Combinatorial]`/`[Pairwise]`/`[Sequential]` on the method to control the strategy explicitly. `[Range(1, 10, 2)]` generates a numeric sequence.
- Generic test fixtures (`[TestFixture(typeof(int))]` over a class with a generic type parameter) exist for testing the same behavior across types — reach for them before hand-duplicating a fixture per type.

## Running: Microsoft.Testing.Platform vs VSTest

NUnit3TestAdapter 5+ ships an **NUnit runner** on Microsoft.Testing.Platform (MTP) as an alternative to the classic VSTest adapter path. Opt in per project:

```xml
<PropertyGroup>
  <OutputType>Exe</OutputType>
  <EnableNUnitRunner>true</EnableNUnitRunner>
  <TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>
</PropertyGroup>
```

With that in place, `dotnet run` in the test project executes the suite directly through MTP. To make `dotnet test` use the MTP path too (rather than falling back to VSTest), set it at the repo root in `global.json`:

```json
{
  "test": { "runner": "Microsoft.Testing.Platform" }
}
```

Once that's set, **every** test project `dotnet test` touches is expected to be MTP-enabled — mixing an MTP project with a VSTest-only project in the same `dotnet test` invocation is an error. Filtering uses the familiar VSTest-style expression on both paths: `dotnet test --filter "FullyQualifiedName~Add_Cases"`. `--list-tests` enumerates discovered tests without running them.

Required package set:

```xml
<ItemGroup>
  <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
  <PackageReference Include="NUnit" Version="4.6.*" />
  <PackageReference Include="NUnit3TestAdapter" Version="6.*" />
  <PackageReference Include="NUnit.Analyzers" Version="4.14.*">
    <PrivateAssets>all</PrivateAssets>
    <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
  </PackageReference>
</ItemGroup>
```

Treat `NUnit.Analyzers` as required, not optional, and build with warnings as errors so a flagged pattern (a constant passed as `actual`, a suggestion to use `EnterMultipleScope`, a redundant assertion) fails the build rather than sitting as a warning nobody reads:

```xml
<TreatWarningsAsErrors>true</TreatWarningsAsErrors>
```

## Async tests

- Every test that awaits anything is `async Task` — never `async void`. An `async void` test can't be awaited by the runner, so a failure inside it can crash the process instead of failing the test.
- Never block on async work with `.Result` or `.GetAwaiter().GetResult()` inside a test — `await` it. Blocking risks deadlocks and defeats the point of `[CancelAfter]`/timeout handling, which relies on being able to observe cancellation through the awaited chain.
- `Assert.ThrowsAsync<T>(...)` for asserting an async delegate throws; `Assert.ThatAsync(...)` for asserting a constraint against an async delegate's result. Both must be awaited.

## Migrating from NUnit 3

| NUnit 3 idiom | NUnit 4 replacement |
|---|---|
| `Assert.AreEqual(expected, actual)` / `Assert.IsTrue(x)` | `Assert.That(actual, Is.EqualTo(expected))` / `Assert.That(x, Is.True)` — or `NUnit.Framework.Legacy.ClassicAssert` if you truly can't touch the call site yet |
| `StringAssert.*` / `CollectionAssert.*` | `Assert.That(...)` with `Does.*` / `Is.EquivalentTo(...)` / `Has.*`, or `NUnit.Framework.Legacy.StringAssert` / `CollectionAssert` as a fallback |
| `Assert.Multiple(() => { ... })` | `using (Assert.EnterMultipleScope()) { ... }` |
| `Assert.That(actual, Is.EqualTo(expected), "msg {0}", arg)` | `Assert.That(actual, Is.EqualTo(expected), $"msg {arg}")` — composite-format overloads are gone |
| Default one-instance-per-fixture lifecycle assumed safe under parallel runs | Explicit `[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]` when you want fresh state per test |
| Hand-rolled `Task.Delay` + re-check loops for eventual state | `Is.<Constraint>.After(timeoutMs, pollIntervalMs)` |
| `dotnet test` always meant VSTest | `dotnet test` can run on Microsoft.Testing.Platform via `EnableNUnitRunner` + `global.json` `test.runner` |

## Checklist

- [ ] `Assert.That(actual, constraint)` everywhere; no bare `ClassicAssert`/`StringAssert`/`CollectionAssert` in new code
- [ ] Multiple related assertions grouped with `using (Assert.EnterMultipleScope())`, not `Assert.Multiple(() => ...)`
- [ ] Messages are plain or interpolated strings, no leftover `{0}`-style format args
- [ ] Async tests are `async Task`, never `async void`; no `.Result`/`.GetAwaiter().GetResult()`
- [ ] `[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]` set where fresh-instance-per-test matters (most fixtures)
- [ ] `[Parallelizable(ParallelScope.All)]` is the default; `[NonParallelizable]` only where a real shared resource forces it
- [ ] No mutable `static` state read or written by tests
- [ ] Data-driven cases use typed `[TestCase]`/`TestCaseData` sources with `.SetName(...)`, not magic `object[]` arrays
- [ ] `NUnit.Analyzers` referenced and warnings treated as errors
- [ ] `[CancelAfter]`/`CancellationToken` threaded through for tests that can hang
- [ ] No `Console.WriteLine` standing in for an assertion; tests named for the behaviour under test
