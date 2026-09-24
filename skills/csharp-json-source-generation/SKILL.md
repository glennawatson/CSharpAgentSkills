---
name: csharp-json-source-generation
description: Use when writing or reviewing C#/.NET code that serializes or deserializes JSON — declaring a System.Text.Json JsonSerializerContext, choosing GenerationMode and naming/ignore options, wiring a context into JsonSerializerOptions/HttpClient/Refit, polymorphism and enums-as-strings with source gen, and catching accidental reflection-based serialization before it breaks trimming or Native AOT.
---

# System.Text.Json source generation

Reflection-based `JsonSerializer.Serialize<T>(value)` inspects `T`'s properties at runtime. That
inspection needs metadata trimming can remove and, on Native AOT, code generation the runtime
can't do. A `JsonSerializerContext` moves that inspection to compile time: the compiler emits the
property list, converters, and read/write code for each type you register, so the serializer never
reflects. See `csharp-aot-trimming` for how this fits the broader reflection-elimination story, and
`csharp-source-generators` if you're writing a generator rather than consuming one.

## Declare a context

```csharp
internal sealed record OrderLine(string Sku, int Quantity, decimal UnitPrice);
internal sealed record Order(int Id, string Customer, OrderStatus Status, List<OrderLine> Lines);

internal enum OrderStatus { Pending, Shipped, Delivered }

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(Order))]
[JsonSerializable(typeof(List<Order>))]
internal partial class AppJson : JsonSerializerContext;
```

- One `[JsonSerializable]` per **root type** you actually serialize or deserialize, including
  containers: `Order` and `List<Order>` are separate roots, and registering one does not register
  the other. Register every closed generic shape a method actually sends or reads (`Order[]`,
  `List<Order>`, `Order?`, …).
- A property typed `object` needs its *runtime* type registered too — the generator can't infer
  what you'll assign at runtime.
- `partial class Foo : JsonSerializerContext` — the compiler fills in the body. Keep it `internal`
  unless another assembly needs the generated `TypeInfo` properties directly.
- **Always set `JsonSourceGenerationOptions(JsonSerializerDefaults.Web)`** (or your own naming
  policy) on every context. A context with no options expects exact-case PascalCase property names.
  A service sending `{"id":5,"customer":"Ada"}` into a plain context silently deserializes into an
  object of default values — no exception, wrong data. Verified: an `Order(int Id, string Customer)`
  read through an unconfigured context back into `{"id":2,"name":"Grace"}`-shaped JSON produced
  `Id=0, Name=null`; adding `JsonSerializerDefaults.Web` fixed it. This is the single most common
  source-generation bug in the wild.
- Other useful `[JsonSourceGenerationOptions]` members: `PropertyNamingPolicy` (or the
  `JsonSerializerDefaults.Web` shortcut), `DefaultIgnoreCondition` (e.g.
  `JsonIgnoreCondition.WhenWritingNull`), `UseStringEnumConverter` (blanket enum-as-string policy),
  `WriteIndented`, `Converters` via `[JsonConverter]` on the type/property instead.

### One context per assembly/feature vs many

Prefer **one context per feature or bounded area** (an `OrdersJsonContext`, a `PaymentsJsonContext`)
rather than one giant context for the whole app, and rather than one context per type. A context is
just a `partial class` — grouping related root types keeps `[JsonSerializable]` lists reviewable and
keeps unrelated features from needing a rebuild when one type's shape changes. Combine multiple
contexts at the `JsonSerializerOptions` level (below) rather than duplicating `[JsonSerializable]`
entries across contexts.

## `GenerationMode`: metadata vs fast-path

```csharp
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Serialization)]
[JsonSerializable(typeof(Order))]
internal partial class SerializeOnlyJson : JsonSerializerContext;
```

- `Metadata` (default component) generates `JsonTypeInfo` the general serializer engine drives —
  supports every feature (streaming, polymorphism, `JsonTypeInfo<T>` introspection).
- `Serialization` ("fast path") additionally emits a direct `Utf8JsonWriter`-driving `Serialize`
  method for types simple enough to qualify (no custom converters, no unsupported converters on
  members, not indented-with-non-default-writer in every case) — faster, less indirection, but
  serialization-only and **not used for async serialization of large/streamed payloads** (it falls
  back to metadata mode there; small payloads that fit the write buffer still use fast path).
- `Default` (the type-level and context-level default) generates both; the runtime picks fast path
  automatically for serialize calls when eligible. Set `GenerationMode` per type via
  `[JsonSerializable(typeof(T), GenerationMode = ...)]` when only one type in a context needs the
  restriction; it overrides the context-level attribute for that type.
- Don't hand-pick `Serialization`-only unless you've profiled and confirmed the type qualifies and
  you never deserialize it through this context — `Metadata` is the safe default.

## Using the context

Prefer the `JsonTypeInfo<T>` overloads — they're the only form the analyzer treats as fully
AOT-safe:

```csharp
var json = JsonSerializer.Serialize(order, AppJson.Default.Order);
var back = JsonSerializer.Deserialize(json, AppJson.Default.Order);
```

Verified difference: `JsonSerializer.Serialize(order, options)` — even with
`options.TypeInfoResolver` set to a context — still produces `IL2026`/`IL3050` warnings, because
that overload is annotated `[RequiresUnreferencedCode]`/`[RequiresDynamicCode]` unconditionally (the
analyzer can't see through `options` to know a source-generated resolver is attached).
`Serialize(order, AppJson.Default.Order)` produces zero warnings. Use the `JsonTypeInfo<T>` form at
call sites where you can; fall back to `options` only when the type is chosen dynamically.

### Wiring a context into `JsonSerializerOptions`

```csharp
private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
{
    TypeInfoResolver = AppJson.Default,
};
```

Build `JsonSerializerOptions` **once** and reuse it — constructing a new instance per call throws
away the cached property/converter metadata and, worse, invites someone to reach for the
reflection-based constructor overload out of habit. Call `options.MakeReadOnly()` after
configuring it: it locks the instance (verified — a `WriteIndented` mutation after `MakeReadOnly()`
throws `InvalidOperationException`) so a later contributor can't silently change shared behavior,
and it's required before the options can be reused as a `TypeInfoResolver` source safely across
threads.

### Combining multiple contexts

```csharp
var options = new JsonSerializerOptions
{
    TypeInfoResolver = JsonTypeInfoResolver.Combine(OrdersJson.Default, PaymentsJson.Default),
};
options.TypeInfoResolverChain.Add(SharedJson.Default);       // append
options.TypeInfoResolverChain.Insert(0, PriorityJson.Default); // prepend
```

`TypeInfoResolverChain` and `TypeInfoResolver` are two views of the same list — editing one is
reflected by the other. Resolvers are queried in order; the first non-null result wins, so put the
more specific/override context earlier.

### Catch accidental reflection

```xml
<PropertyGroup>
  <JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault>
</PropertyGroup>
```

Set this in any project meant to stay AOT/trim-safe, even before you publish trimmed — it turns a
forgotten reflection-based `Serialize`/`Deserialize` call into an immediate
`InvalidOperationException` in ordinary CI/test runs instead of a silent trap that only surfaces
after a trimmed publish. Verified: with the property set, a bare
`JsonSerializer.Serialize(new Widget(1))` (no context, no resolver) throws
`"Reflection-based serialization has been disabled for this application"` at the first call, on a
normal `dotnet run` — no publish step needed. `PublishTrimmed`/`PublishAot` set this to `false`
automatically if you don't set it yourself; verified an `IsAotCompatible=true` project publishing
`-p:PublishAot=true` ran with `JsonSerializer.IsReflectionEnabledByDefault == false` and produced
correct output with no IL warnings at build.

`IsAotCompatible=true` (see `csharp-aot-trimming`) is what surfaces `IL2026`/`IL3050` at build time
for any reflection-based JSON call still in the project — treat those as the thing to fix, not
suppress; the fix is almost always "pass the `JsonTypeInfo<T>`/context instead."

## HttpClient

```csharp
using var content = JsonContent.Create(order, AppJson.Default.Order);
var order = await client.GetFromJsonAsync("orders/5", AppJson.Default.Order);
```

Both `GetFromJsonAsync`/`PostAsJsonAsync`/`JsonContent.Create` take a `JsonTypeInfo<T>` or a
`JsonSerializerContext` overload — use them instead of the generic `<T>` overloads so the call is
AOT-safe. Verified: `GetFromJsonAsync(url, Context.Default.T)` against a stub handler round-trips
correctly; the same call through a context missing `JsonSerializerDefaults.Web` silently produced a
zeroed-out record (the naming trap above, reproduced over HTTP).

## Refit

Refit's generated clients (`RestService.ForGenerated<T>`) take a context the same way:

```csharp
IOrdersApi api = RestService.ForGenerated<IOrdersApi>(client, OrdersJson.Default);
```

- Refit does **not** write `[JsonSerializable]` entries for you — its client generator and STJ's
  source generator run independently and don't see each other's output. List every request/reply
  type (and list/array shapes) on your context yourself.
- Passing a context turns reflection-based JSON off for that client: a type the context doesn't
  list throws `NotSupportedException` naming the missing type — fix by adding it, not by reaching
  for `allowReflectionFallback: true` (that flag exists for staged migration of a large app and is
  explicitly not trim/AOT-safe).
- To combine your own `JsonSerializerOptions` (naming policy, custom converters) with a context:
  `RefitSettings settings = new(new SystemTextJsonContentSerializer(options)); settings.UseJsonContext(context);`
  or `RestService.ForGenerated<T>(client, context, settings)` — your settings win; the context only
  supplies metadata for types your options' resolver doesn't already know.
  `SystemTextJsonContentSerializer.ForContext(context)` builds a serializer straight from a context
  with no reflection fallback.
- A Refit interface method can take a `JsonTypeInfo<T>` parameter (matched by `T` against the body
  or reply type) so a client built without a context still gets per-call generated metadata; Refit's
  generator enforces this at build time (`RF014` if the parameter can't be matched).
- See `~/source/rxui/website/docs/documentation/refit/serialization/json.md` and
  `.../refit/aot.md` for the full walkthrough this section summarizes.

## Polymorphism, enums, records

```csharp
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(CourierShipment), "courier")]
[JsonDerivedType(typeof(PickupShipment), "pickup")]
internal abstract record Shipment(string Reference);

internal sealed record CourierShipment(string Reference, string TrackingNumber) : Shipment(Reference);
internal sealed record PickupShipment(string Reference, string Store) : Shipment(Reference);
```

Declare polymorphism with `[JsonPolymorphic]`/`[JsonDerivedType]` attributes, not a runtime contract
modifier (`DefaultJsonTypeInfoResolver.Modifiers`) — a contract modifier relies on
`DefaultJsonTypeInfoResolver`, which is reflection-based and unsafe under Native AOT. Verified
round-trip: serializing a `CourierShipment` through the base `Shipment` root writes the `kind`
discriminator and deserializes back to the concrete `CourierShipment` type via
`JsonSerializer.Deserialize(json, AppJson.Default.Shipment)`.

- **Enums as strings**: either `[JsonConverter(typeof(JsonStringEnumConverter<TEnum>))]` on the enum
  (use the generic form — the non-generic `JsonStringEnumConverter` isn't Native AOT-supported), or
  `UseStringEnumConverter = true` in `[JsonSourceGenerationOptions]` for a blanket policy across the
  context. Custom names per member: `[JsonStringEnumMemberName]` (.NET 9+).
- **Records/init/required**: source generation fully supports positional records, `init` setters,
  and `required` members — verified a `record` with `required int X { get; init; }` members
  serializes/deserializes correctly through a generated context with no extra attributes needed.
- **`required` means "must be in the JSON".** Deserialization throws `JsonException` when a
  `required` property is missing from the document, even if its type is nullable. Don't add
  `required` to satisfy the nullable compiler: when in doubt, make reference-typed members nullable
  and handle missing values where each type is consumed. Conversely, a non-nullable `string` quietly
  accepts a JSON `null` unless you set `RespectNullableAnnotations = true` (on
  `[JsonSourceGenerationOptions]` or `JsonSerializerOptions`), which makes it throw. See
  `csharp-nullable-migration`.
- **Custom converters**: a `JsonConverter<T>` you write by hand works unchanged under source
  generation — apply it with `[JsonConverter]` on the type/property, or add it to
  `JsonSourceGenerationOptions.Converters`/the options' `Converters` list. The generator emits code
  that calls your converter; it doesn't need to understand its internals.
- **`JsonNode`/`JsonDocument`**: when you only need to poke at part of a payload (read one field,
  don't know or care about the full shape), reach for `JsonNode`/`JsonDocument` instead of a type —
  neither needs a `JsonSerializerContext` entry, and both stay reflection-free.
- **Anonymous types can't be source-generated** — there's no type declaration for
  `[JsonSerializable]` to point at. Declare a real (even `internal sealed record`) type instead. This
  also matters for `dotnet run app.cs` file-based apps: they run with reflection-based serialization
  disabled by default (see `dotnet-file-based-apps`), so declare the record(s) and the context in
  the same file rather than reaching for an anonymous object.

## .NET 11 additions (verified on 11.0.100-rc.1)

- `JsonNamingPolicy.PascalCase` — a new built-in policy alongside camelCase/snake_case/kebab-case.
  Verified: `JsonNamingPolicy.PascalCase.ConvertName("helloWorld")` → `"HelloWorld"`.
- Per-member naming policy overrides via `[JsonNamingPolicy]` on an individual property/class,
  independent of the context/options-level `PropertyNamingPolicy`.
- `JsonSerializerOptions.GetTypeInfo<T>()`/`TryGetTypeInfo<T>(out JsonTypeInfo<T>)` — generic
  overloads that return `JsonTypeInfo<T>` directly, no manual downcast from the non-generic
  `GetTypeInfo(Type)`. Verified working against a `Combine`d resolver.
- Union type serialization (`JsonTypeInfoKind.Union`, `JsonUnionAttribute`,
  `JsonUnionTypeStructuralClassifier`) for C# union types (a C# 15 preview language feature) — both
  the reflection-based serializer and source generator support it; not exercised here since union
  types require the preview language feature enabled.
- `JsonSerializerOptions.InferClosedTypePolymorphism` — infers polymorphic metadata for a closed
  C# hierarchy without requiring `[JsonDerivedType]` on every case; explicit attributes still win
  when present.

These are additive — everything earlier in this skill is unchanged on .NET 11.

## Other reflection → source-gen swaps

| Reflection-based API | Source-generated replacement | Skill |
| --- | --- | --- |
| `Regex` compiled/cached at runtime | `[GeneratedRegex]` | `csharp-aot-trimming` |
| `ILogger.Log(LogLevel, ...)` boxing overloads | `[LoggerMessage]` | `csharp-logging-diagnostics`, `csharp-aot-trimming` |
| `[DllImport]` / `Marshal.GetDelegateForFunctionPointer` | `[LibraryImport]` | `csharp-aot-trimming` |
| `IConfiguration.Get<T>()`/`Bind` via reflection | `Microsoft.Extensions.Configuration.Binder.SourceGeneration` (`EnableConfigurationBindingGenerator`) | `csharp-aot-trimming` |
| Hand-rolled `IValidatableObject`/reflection-based options validation | `[OptionsValidator]` | `dotnet-analyzers` |
| Ad-hoc reflection over a type's shape | A custom `IIncrementalGenerator` | `csharp-source-generators` |

## Review checklist

- [ ] Every root type actually sent/received is on a context, including collection/array/generic
      shapes — not just the element type
- [ ] Context has `JsonSourceGenerationOptions(JsonSerializerDefaults.Web)` (or an explicit naming
      policy) — never an unconfigured context talking to camelCase JSON
- [ ] Call sites use `Context.Default.T` (`JsonTypeInfo<T>`) where the type is statically known;
      `options.TypeInfoResolver` only where it must be chosen dynamically
- [ ] `JsonSerializerOptions` instances are built once (`static readonly`) and `MakeReadOnly()`d,
      never constructed per call
- [ ] `JsonSerializerIsReflectionEnabledByDefault=false` set in any project meant to stay
      trim/AOT-safe, so a stray reflection call fails loudly in CI, not silently after publish
- [ ] Polymorphism declared with `[JsonPolymorphic]`/`[JsonDerivedType]` attributes, not a
      `DefaultJsonTypeInfoResolver` contract modifier, if Native AOT is a target
- [ ] Enums-as-strings uses the generic `JsonStringEnumConverter<TEnum>` (or
      `UseStringEnumConverter`), never the non-generic `JsonStringEnumConverter`
- [ ] No anonymous type reaches `JsonSerializer` — declared record/class instead
- [ ] `IsAotCompatible`/`IL2026`/`IL3050` warnings addressed at the call site (pass a
      `JsonTypeInfo<T>`/context), not suppressed
- [ ] Refit clients pass their context to every creation/registration overload
      (`ForGenerated`, `AddRefitGeneratedClient`, `RefitSettings.ForJsonContext`) rather than
      relying on `allowReflectionFallback: true`
