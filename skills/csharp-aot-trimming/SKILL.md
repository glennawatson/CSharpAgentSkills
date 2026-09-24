---
name: csharp-aot-trimming
description: Use when writing or reviewing C#/.NET code that must survive trimming or Native AOT publishing — annotating reflection-using APIs, replacing reflection with source generators (JSON, logging, regex, configuration binding), avoiding Type.GetType/MakeGenericType/Activator pitfalls, feature switches, and verifying with a real trimmed/AOT publish.
---

# Trimming and Native AOT compatibility

Trimming removes code the analyzer can't prove is reachable; Native AOT additionally has no JIT,
so anything that needs to generate code at runtime (a new generic instantiation, `Reflection.Emit`)
simply doesn't work. Both fail the same way in practice: silently at first (a build that "works"),
then with a `MissingMethodException` or blank output in the field. The fix is to make the compiler
tell you at build time instead.

## Turn the analyzers on

```xml
<PropertyGroup>
  <IsAotCompatible>true</IsAotCompatible>
</PropertyGroup>
```

Setting `IsAotCompatible` to `true` (in an SDK-style project targeting .NET 8+) turns on
`PublishTrimmed`, `PublishAot`-aware analysis, and the trim/AOT/single-file analyzer rule sets
(`IL2xxx` trimming rules, `IL3xxx` AOT rules) *during ordinary builds* — you get the warnings in
your editor and CI without needing to actually publish AOT every time. For a library (not an
app), prefer `<IsTrimmable>true</IsTrimmable>` plus `<IsAotCompatible>true</IsAotCompatible>` so
consumers can trust the package's trim/AOT annotations.

Treat the warnings as errors in CI once the count is at zero — a newly introduced `IL2026` should
fail the build, not accumulate:

```xml
<PropertyGroup>
  <WarningsAsErrors>$(WarningsAsErrors);IL2026;IL2070;IL2075;IL3050</WarningsAsErrors>
</PropertyGroup>
```

## Annotating reflection-shaped APIs

When a member genuinely needs reflection over a type it didn't statically know the shape of
(a generic serializer, a DI container registering by type), tell the analyzer what members must
survive trimming instead of suppressing the warning:

```csharp
public static T? Create<
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>()
    where T : class
    => (T?)Activator.CreateInstance(typeof(T));
```

`[DynamicallyAccessedMembers]` on a parameter, generic type parameter, `this`, or return value
tells the trimmer "keep these members of whatever type flows here" — it propagates through call
chains, so callers of `Create<T>()` get checked too, not just this method's own body.

When a member truly cannot be made trim-safe (calls arbitrary user-supplied `Type` reflection,
wraps a non-AOT-safe third-party API), mark it so the *caller* gets the warning instead of it
disappearing:

```csharp
[RequiresUnreferencedCode("Uses reflection to enumerate all properties; trimming may remove them.")]
public static Dictionary<string, object?> ToDictionary(object source) { ... }

[RequiresDynamicCode("Creates a closed generic type at runtime via reflection.")]
public static object CreateGeneric(Type definition, Type argument) =>
    Activator.CreateInstance(definition.MakeGenericType(argument))!;
```

- `[RequiresUnreferencedCode]` — this member may access members that trimming could have removed;
  the trimmer can't statically verify safety through it.
- `[RequiresDynamicCode]` — this member needs to generate code at runtime (JIT a new generic
  instantiation, emit IL); it will throw on Native AOT (`RuntimeFeature.IsDynamicCodeSupported`
  is `false` there) even though it works fine trimmed-but-JIT.
- Both propagate: a caller of an annotated member gets the same warning unless it's also
  annotated (or the call is provably unreachable under a feature switch — see below), so the
  warning surfaces at the actual entry point an app author controls, not buried in a library.

Suppress only with a specific, justified reason, never as a blanket silence:

```csharp
[UnconditionalSuppressMessage("Trimming", "IL2075",
    Justification = "Type is always a concrete DTO produced by our own source generator; " +
                     "its shape is preserved via [DynamicallyAccessedMembers] on the caller.")]
```

A suppression without a real justification is a future bug wearing a green checkmark — the
`Justification` argument is mandatory for a reason; write the actual reason it's safe, not
"suppress warning."

## Replace reflection with source generators

The trim/AOT-safe fix for most reflection use is a source generator that emits the equivalent code
at compile time — no runtime type inspection needed at all:

- **JSON** — `System.Text.Json` source generation via a `JsonSerializerContext`:
  ```csharp
  [JsonSerializable(typeof(Order))]
  internal partial class AppJsonContext : JsonSerializerContext;

  var json = JsonSerializer.Serialize(order, AppJsonContext.Default.Order);
  ```
  This replaces the reflection-based serializer entirely for the registered types — no
  `RequiresUnreferencedCode` warning at the call site.
- **Logging** — `[LoggerMessage]` generates a strongly-typed logging method instead of the
  reflection- and boxing-heavy `ILogger.Log(LogLevel, ...)` overloads:
  ```csharp
  internal static partial class Log
  {
      [LoggerMessage(Level = LogLevel.Information, Message = "Order {OrderId} shipped")]
      public static partial void OrderShipped(ILogger logger, Guid orderId);
  }
  ```
- **Regex** — `[GeneratedRegex]` emits the state machine at compile time instead of building and
  caching it via reflection/`Reflection.Emit` at first use:
  ```csharp
  internal static partial class Patterns
  {
      [GeneratedRegex(@"^\d{3}-\d{4}$")]
      public static partial Regex PhoneNumber();
  }
  ```
- **Configuration binding** — the `Microsoft.Extensions.Configuration.Binder.SourceGeneration`
  generator backs `ConfigurationBinder.Get<T>()`/`Bind` with generated code instead of reflection
  when the analyzer/generator package is referenced; enable it the same way as the others — no
  API change at the call site, just no more `RequiresUnreferencedCode` from binding.

All four follow the same shape: a `partial` member plus an attribute, compiled by a generator
into ordinary trim-safe code. Prefer them over hand-written reflection wherever the library
offers a generator — see `csharp-source-generators` for writing your own when nothing off-the-shelf
covers your case.

## `Type.GetType` / `MakeGenericType` / `Activator` pitfalls

- **`Type.GetType(string)`** — the trimmer cannot know what string it'll see at runtime, so it
  cannot know which type to preserve; you get `IL2057` and, if the type was trimmed, a runtime
  `null`/`TypeLoadException`. Use `typeof(T)` wherever the type is statically known; if a string
  name genuinely must resolve dynamically (a plugin name from config), keep a static list of
  candidates and switch on it, or feed the trimmer a `TrimmerRootDescriptor`/root the assembly
  explicitly so removal doesn't happen for that name.
- **`MakeGenericType`** — constructs a new closed generic type at runtime. On Native AOT this
  needs `RequiresDynamicCode` because it may need to JIT a new instantiation the ahead-of-time
  compiler never saw; it will throw at runtime if that instantiation wasn't in the AOT-compiled
  set. Prefer generic methods resolved at compile time, or a small closed set of instantiations
  registered explicitly (a switch over the known types, each calling the generic method directly)
  so AOT sees every instantiation it needs to compile.
- **`Activator.CreateInstance(Type)`** — needs the constructor to survive trimming
  (`[DynamicallyAccessedMembers(PublicConstructors)]` on wherever the `Type` came from) and, for a
  generic type built via `MakeGenericType`, inherits that call's AOT risk too. Prefer a factory
  delegate or a source-generated activator over `Activator.CreateInstance` in AOT-targeted code.

## Feature switches

A feature switch lets code branch on a compile-time-knowable condition (is this feature enabled
in this build?) so the trimmer can remove the *entire* disabled branch, including whatever it
references — this is how the BCL itself removes globalization, `Regex.CompileToAssembly`, HTTP
diagnostics, etc. from a trimmed app that doesn't use them.

```csharp
public static class MyFeature
{
    [FeatureSwitchDefinition("MyLibrary.MyFeature.IsSupported")]
    public static bool IsSupported { get; } =
        AppContext.TryGetSwitch("MyLibrary.MyFeature.IsSupported", out var enabled) ? enabled : true;
}

public static void DoWork()
{
    if (MyFeature.IsSupported)
    {
        UseReflectionBasedPath();   // removable when the switch is false at publish time
    }
    else
    {
        UseFallbackPath();
    }
}
```

`[FeatureSwitchDefinition]` tells the trimmer that the guarded branch is dead code whenever the
named `AppContext` switch is set to `false` in the consuming app's project
(`<ItemGroup><RuntimeHostConfigurationOption Include="MyLibrary.MyFeature.IsSupported"
Value="false" Trim="true"/></ItemGroup>`), letting it remove the branch's dependencies too — this
is how you ship an optional reflection-based code path without forcing every trimmed/AOT consumer
to pay for it.

Check `RuntimeFeature.IsDynamicCodeSupported` at runtime (not a feature switch — a real runtime
capability check) to choose a JIT-dependent fast path vs. an AOT-safe fallback within the same
build:

```csharp
if (RuntimeFeature.IsDynamicCodeSupported)
{
    return CreateViaExpressionTree(type);
}
return CreateViaReflectionFallback(type);
```

## Verify by actually publishing

Analyzer warnings during a normal build catch most issues, but the only real proof is a trimmed
or AOT publish with warnings promoted to errors:

```bash
dotnet publish -c Release -r linux-x64 \
    -p:PublishAot=true \
    -p:TreatWarningsAsErrors=true \
    -p:WarningsAsErrors=""
```

For a library (no executable to AOT-publish), publish a small throwaway console app that
references it and exercises its public surface — a trimmed/AOT app that fails to start or throws
`MissingMethodException`/`NotSupportedException` at the reflection call site is the failure mode
analyzer warnings exist to prevent, so actually run the published binary, not just the publish
step, before calling the surface AOT-verified.

## Review checklist

- [ ] `<IsAotCompatible>true</IsAotCompatible>` (or `IsTrimmable` for a library) set and its
      warnings addressed, not ignored
- [ ] Reflection-shaped members carry `[DynamicallyAccessedMembers]`, `[RequiresUnreferencedCode]`,
      or `[RequiresDynamicCode]` as appropriate — not silently trimmer-unsafe
- [ ] Any `[UnconditionalSuppressMessage]` has a real `Justification`, not a placeholder
- [ ] JSON/logging/regex/config-binding reflection replaced with the matching source generator
      where available
- [ ] No `Type.GetType(string)` on a dynamic string without a root/known-candidate fallback
- [ ] `MakeGenericType`/`Activator.CreateInstance` usage is annotated or replaced with a closed
      set of compile-time-known instantiations
- [ ] Optional reflection-based paths are gated behind a `[FeatureSwitchDefinition]`, not
      unconditionally linked in
- [ ] Verified with an actual trimmed/AOT publish (warnings as errors) and a run of the published
      binary, not just a normal build
