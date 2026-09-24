---
name: csharp-logging-diagnostics
description: Use when writing or reviewing C#/.NET logging, tracing, or metrics code — [LoggerMessage] source-generated logging, structured message templates, ActivitySource/Activity for tracing, Meter/Counter/Histogram for metrics, and avoiding allocation on hot paths. Applies to library and service code that emits diagnostics.
---

# Logging, tracing, and metrics — structured and allocation-light

Diagnostics code runs on every request, so its cost compounds. The theme across logging, tracing, and metrics here is the same: emit structured data through the purpose-built API, not interpolated strings through a general one — it's both faster and machine-readable by the tools that consume it (log aggregators, trace backends, `dotnet-counters`).

## Logging: `[LoggerMessage]` source generation

```csharp
internal static partial class Log
{
    [LoggerMessage(Level = LogLevel.Information, Message = "User {UserId} created with name {UserName}")]
    public static partial void UserCreated(this ILogger logger, int userId, string userName);
}

// call site
_logger.UserCreated(user.Id, user.Name);
```

- The generator writes a method that checks `IsEnabled(LogLevel)` before doing any work, and calls `ILogger.Log` with a **cached `Func<...>` formatter** and the arguments passed as strongly-typed parameters — no `params object[]` array allocation, no boxing of value types (`int`, `enum`, etc.), and the message is only formatted if the level is actually enabled.
- Compare to `_logger.LogInformation("User {UserId} created with name {UserName}", id, name)`: functionally similar, but it allocates the `object[]` params array (and boxes `id`) on every call whether or not the level is enabled, unless the compiler can prove otherwise. `[LoggerMessage]` avoids that categorically.
- Make the containing type/method `partial` — the generator fills in the body. An extension-method form (`this ILogger logger`) reads naturally at call sites; an instance-log-field form works too if the type already holds an `ILogger`.
- One `[LoggerMessage]` method per distinct message shape. Don't build the message with string concatenation or interpolation and pass the result as the template — see below.

## Structured templates, never interpolation

```csharp
// ❌ the "message" is now just data — no structured field, can't query by UserId
_logger.LogInformation($"User {userId} created");

// ✅ {UserId} is a named structured field the logging provider can index/filter on
_logger.UserCreated(userId, userName);
```

- The `{UserId}`/`{UserName}` placeholders in a `[LoggerMessage]`/`ILogger` template are **not** string interpolation — they're named holes that structured sinks (Application Insights, Seq, OpenTelemetry log exporters, JSON console formatters) capture as separate fields alongside the rendered message. `$"..."` collapses everything into one opaque string and throws that queryability away.
- Keep placeholder names `PascalCase` and stable — they become the field names downstream. Renaming one is a breaking change to dashboards/queries built against it, same care as a public API rename.
- Exceptions go in the dedicated `Exception` parameter (`[LoggerMessage(..., Message = "...")]` with an `Exception ex` parameter, or `LogError(ex, "...")`), not interpolated into the message text — the provider handles stack traces separately.

## `IsEnabled` checks for expensive arguments

`[LoggerMessage]` already guards the call for you, but if computing an argument is itself expensive (serializing a large object, walking a collection), guard that computation explicitly — the generated method still evaluates its arguments before checking, same as any normal method call:

```csharp
if (_logger.IsEnabled(LogLevel.Debug))
{
    _logger.ExpensiveDebugDump(SerializeFullState(state));
}
```

## Scopes for ambient context

```csharp
using (_logger.BeginScope(new Dictionary<string, object> { ["OrderId"] = orderId }))
{
    await ProcessOrderAsync(orderId, ct).ConfigureAwait(false);
    // every log line emitted inside this scope carries OrderId, without
    // threading it through every LoggerMessage call
}
```

Use scopes for context that spans multiple log calls within one logical operation (a request ID, an order ID) rather than repeating the same field on every call.

## Tracing: `ActivitySource` / `Activity`

```csharp
private static readonly ActivitySource s_source = new("MyApp.Orders");

public async Task ProcessOrderAsync(Order order, CancellationToken ct)
{
    using var activity = s_source.StartActivity("ProcessOrder");
    activity?.SetTag("order.id", order.Id);
    activity?.SetTag("order.item_count", order.Items.Count);

    await DoWorkAsync(order, ct).ConfigureAwait(false);
}
```

- `StartActivity` returns `null` when nothing is listening (no exporter/processor registered) — it's cheap to call unconditionally, but **always null-check with `?.`** before adding tags; don't build tag values (string formatting, ToString on a large object) unconditionally if that cost matters, since it still runs even when the activity is null.
- If you're about to do real work to build tag data, check `s_source.HasListeners()` first to skip it entirely when nothing is collecting:
  ```csharp
  if (s_source.HasListeners())
  {
      activity?.SetTag("order.detail", BuildExpensiveDetail(order));
  }
  ```
- One `ActivitySource` per logical component, named like the OpenTelemetry convention (`CompanyOrProduct.ModuleName`, e.g. `MyApp.Orders`), created once as a `static readonly` field — not per call. Register it with the tracing provider by name (OpenTelemetry's `AddSource("MyApp.Orders")`), which is why the name matters.
- For a fixed, known-at-once set of tags, pass them as an `ActivityTagsCollection` or an `IEnumerable<KeyValuePair<string, object>>` to `StartActivity` directly instead of calling `SetTag` repeatedly — one allocation instead of N, and it lets sampling decisions see the tags immediately.
- `Activity` nesting is automatic via `Activity.Current` — a child `StartActivity` call inside an ongoing one becomes its child span without you threading anything through explicitly (it flows via `AsyncLocal`).

## Metrics: `Meter`, `Counter<T>`, `Histogram<T>`, `IMeterFactory`

```csharp
public sealed class OrderMetrics
{
    private readonly Counter<long> _ordersProcessed;
    private readonly Histogram<double> _processingDuration;

    public OrderMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create("MyApp.Orders");
        _ordersProcessed = meter.CreateCounter<long>("orders.processed", unit: "{order}");
        _processingDuration = meter.CreateHistogram<double>("orders.processing.duration", unit: "s");
    }

    public void RecordProcessed(string status)
    {
        var tags = new TagList { { "status", status } };
        _ordersProcessed.Add(1, tags);
    }
}
```

- Get `Meter` instances from **`IMeterFactory`** (registered via `services.AddMetrics()`), not `new Meter(...)` directly — the factory ties the meter's lifetime to DI disposal and lets the metrics infrastructure discover it, mirroring how `ILoggerFactory` works for logging.
- `Counter<T>` for monotonically increasing values (requests handled, bytes sent); `Histogram<T>` for distributions you want percentiles/buckets over (latency, payload size); `UpDownCounter<T>` for values that go up and down (active connections, queue depth).
- Use **`TagList`** (a `struct` that holds a small inline set of tags) instead of allocating a `KeyValuePair<string, object>[]` or `Dictionary` per call — it avoids a heap allocation on every `Add`/`Record` call, which matters because metrics calls happen on hot paths by design.
- Naming: follow the OpenTelemetry semantic convention style — dot-separated, lowercase, unit-suffixed where it helps (`http.server.request.duration`), and set `unit:` using UCUM-style short codes (`s`, `ms`, `By`, or `{order}` for a count of a specific thing) so exporters render axes correctly.

## Inspecting diagnostics live

- **`dotnet-counters monitor -p <pid>`** (or `--name <processName>`) — live view of `EventCounter`/`Meter` metrics from a running process without any code changes or redeploy; good first check for "is this counter even being recorded."
- **`dotnet-trace collect -p <pid>`** — captures an `EventPipe` trace (CPU, GC, and any `ActivitySource`/custom `EventSource` events) into a `.nettrace` file for offline analysis (e.g. in PerfView or Visual Studio). See `benchmarking` for trace-based performance verification.
- Both are global tools (`dotnet tool install -g dotnet-counters`/`dotnet-trace`) and attach to an already-running process — no code instrumentation beyond the `Meter`/`ActivitySource` calls above.

## .NET 11: declarative Activity tracing rules (net11.0+, `Microsoft.Extensions.Diagnostics`)

`services.AddTracing(...)` (namespace `Microsoft.Extensions.Diagnostics.Tracing`) registers `Activity` listeners declaratively instead of you constructing an `ActivityListener` and calling `ActivitySource.AddActivityListener` by hand:

```csharp
services.AddTracing(tracing => tracing
    .AddListener("otel", listener =>
    {
        listener.Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData;
    })
    .EnableTracing(sourceName: "MyApp.Orders", operationName: "*", listenerName: "otel", scopes: ActivitySourceScopes.None));
```

- `ITracingBuilder.AddListener(name, Action<ActivityListenerBuilder>)` configures one named listener (`Sample`/`SampleUsingParentId`/`ActivityStarted`/`ActivityStopped`/`ExceptionRecorder` — the same knobs `ActivityListener` exposes today, just built through a builder instead of the type's settable fields).
- `EnableTracing`/`DisableTracing` (both on `ITracingBuilder` and directly on `TracingOptions`, so it also binds from config) take `sourceName`, `operationName`, `listenerName`, and `ActivitySourceScopes` — this is the declarative rule: "this named listener listens to this source/operation," addable/removable per rule instead of writing `ShouldListenTo` predicate logic.
- `TracingOptions.Rules` is a `List<TracingRule>` (`SourceName`, `OperationName`, `ListenerName`, `Scopes`, `Enable`) — bindable from `appsettings.json`/`IConfiguration` via `AddConfiguration(IConfiguration)`, so sampling/enable rules can live in config instead of C#.
- Your `ActivitySource`/`StartActivity`/`SetTag` instrumentation code above is unchanged — this only replaces how the *listener side* (what subscribes and samples) gets wired up.

## .NET 11: `MemoryCache` metrics

`Microsoft.Extensions.Caching.Memory`'s `MemoryCache` has a constructor overload taking `IMeterFactory` (`MemoryCache(IOptions<MemoryCacheOptions>, ILoggerFactory, IMeterFactory)`, present from the `11.0.0` package on, absent in `10.0.0`). When you register the cache through DI (`services.AddMemoryCache()`) and `IMeterFactory` is available (`services.AddMetrics()`), the cache reports its own OpenTelemetry metrics (hit/miss counts, entry count, eviction reasons) automatically — no code change at cache call sites, and no custom `Counter<T>` wiring needed just to see cache effectiveness. Drop any hand-rolled counters around `IMemoryCache.TryGetValue`/`Set` that exist purely to track hit rate once on net11.0's `MemoryCache`, and confirm the emitted instrument names against the installed package version.

## `EventSource` — briefly

`EventSource` is the lower-level, older mechanism `Meter`/`ActivitySource` and the runtime's own counters (GC, ThreadPool) are built on. Write a custom `EventSource` only when you need ETW/EventPipe-level integration that `Meter`/`ActivitySource` don't cover (e.g. a very low-overhead event contract consumed by `dotnet-trace` directly). For ordinary application metrics and traces, prefer `Meter` and `ActivitySource` — they're the higher-level, OpenTelemetry-aligned APIs and cover the vast majority of needs.

## Checklist

- [ ] Log calls go through `[LoggerMessage]`-generated methods, not `LogInformation("...", args)` with a params array on hot paths
- [ ] No `$"..."` interpolation into a log message — structured `{Placeholder}` templates only
- [ ] Expensive log argument computation guarded by `IsEnabled`
- [ ] Scopes used for context spanning multiple log lines, not repeated per-call fields
- [ ] `Activity?.SetTag` calls are null-conditional; expensive tag-building guarded by `HasListeners()`
- [ ] `ActivitySource`/`Meter` created once (static or DI-owned via `IMeterFactory`), not per call
- [ ] Metrics tags passed via `TagList`, not a freshly allocated array/dictionary per call
- [ ] Metric and span names follow dot-separated, lowercase, unit-suffixed convention
- [ ] `dotnet-counters`/`dotnet-trace` used to confirm diagnostics are actually emitted, not assumed

See `csharp-performance` for allocation-avoidance techniques generally, and `benchmarking` for verifying diagnostics overhead with real traces rather than assumptions.
