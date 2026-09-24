---
name: dotnet-runtime-async
description: Use when targeting net11.0 and deciding whether/how to use Runtime Async (runtime-native async, no compiler state machine) — enabling it per project, reasoning about stack traces/ExecutionContext/allocation changes, checking whether a method compiled runtime-async, or assessing risk for libraries that also ship to Mono targets (Blazor WebAssembly, MAUI, Android/iOS). Read this before flipping `<Features>runtime-async=on</Features>` or `<UseRuntimeAsync>` on a shipping project.
---

# .NET 11 Runtime Async — how it works and what changes

Runtime Async replaces the compiler-generated `IAsyncStateMachine` struct + builder with runtime/JIT-managed
suspension and resumption. The C# compiler still lowers `await` the same way at the language level; what
changes is the IL it emits and who executes the suspend/resume logic. This is the in-depth authority on the
feature — `csharp-async` has a short pointer to this skill and keeps its own micro-optimization advice; this
skill covers the mechanism, the enablement story, and the Mono/WebAssembly risk in depth. Don't edit
`csharp-async` — cross-reference it.

**Verified against**: `dotnet/runtime` design docs (`docs/design/specs/runtime-async.md`,
`docs/design/coreclr/botr/runtime-async-codegen.md`), the Roslyn `Runtime Async Design.md`, the .NET 11
RC1 runtime release notes, and an empirical build/reflect/benchmark pass against the installed
`11.0.100-rc.1` SDK. Runtime Async is still a preview-tagged feature at RC1 — mechanics below could still
shift before the final .NET 11 release. Re-verify on your SDK before relying on exact numbers.

## How it works

- The compiler emits an async method as a method tagged `[MethodImpl(MethodImplOptions.Async)]` (IL flag
  `0x2000`, the `async` keyword in raw IL) instead of a normal method whose body constructs and drives a
  state-machine struct. There is **no nested `<Method>d__N` state-machine type, no `AsyncStateMachineAttribute`,
  no async method builder** in the emitted IL — confirmed by decompiling a runtime-async build: the class has
  zero nested types where the classic build has one.
- Inside the method body, `await expr` lowers to a call to a runtime helper —
  `System.Runtime.CompilerServices.AsyncHelpers.Await(expr)` (or `AwaitAwaiter`/`UnsafeAwaitAwaiter` for
  non-`Task`/`ValueTask` awaitables, dynamic awaits, etc.) — also marked `MethodImplOptions.Async`. The JIT
  recognizes the `call <async-method>` immediately followed by `call AsyncHelpers.Await` pattern and compiles
  it as a single suspension point rather than two separate calls.
- Suspension is a *runtime* ABI concept, not a compiler one: an async method gains an extra "Continuation"
  in/out parameter. On suspension the JIT-generated code allocates a `Continuation` object, copies hoisted
  locals into it, and chains it to the continuation returned by the callee; on resumption it copies locals
  back out. This replaces the compiler-generated state-machine struct's `MoveNext`/hoisted-field dance.
- Only `Task`, `Task<T>`, `ValueTask`, `ValueTask<T>` (and their `Configured*Awaitable` wrappers) get the
  runtime-async call-site optimization; anything else falls back to the `AwaitAwaiter`/`UnsafeAwaitAwaiter`
  path (still runtime-async, just not the fast-pattern call sequence). Async iterators
  (`IAsyncEnumerable<T>`) and any method returning a different Task-like type are **not** transformed and
  keep the classic compiler state machine — this is spelled out in the Roslyn design doc as a limitation of
  the current transformation.
- **Stack traces**: with runtime-async, the actual method frames appear on a *live* execution stack (a
  debugger's call-stack window, a `new StackTrace()` inside the method, a profiler's live sample) instead of
  interleaved `MoveNext`/`AsyncMethodBuilderCore.Start` infrastructure frames. Verified from the .NET 11 RC1
  release notes' own before/after example: a 3-level async chain went from 13 frames to 5. **This is a live-
  stack change only** — caught-exception stack traces (`catch (Exception ex) { ex.StackTrace }`) already look
  clean with the classic model because of existing `ExceptionDispatchInfo` handling, so don't expect a visible
  diff there.
- **`ExecutionContext` capture skip**: every `Task`/`ValueTask` continuation (runtime-async or classic) now
  skips capturing/restoring `ExecutionContext` when there's nothing to flow (no `AsyncLocal<T>` set, no
  ambient `SynchronizationContext`). This is a broader .NET 11 runtime change, not exclusive to runtime-async
  methods, but it compounds with runtime-async's lower per-await overhead.
- The JIT also compiles a dedicated runtime-async variant of an ordinary synchronous `Task`/`ValueTask`-
  returning method (rather than routing through a thunk), tail-merges multiple suspension points in one method
  into shared code to shrink generated size, caches/reuses `Continuation` objects for pooled call shapes, and
  recognizes `Task.FromResult`/`Task.CompletedTask`/`ValueTask.FromResult` as intrinsics on the async path.
  Implicit tail calls from an async method that directly returns another async call are re-enabled under
  runtime-async. All from the RC1 release notes' "Runtime Async performance improvements" section.
- NativeAOT and ReadyToRun (crossgen2) both compile runtime-async methods, including inlining them — this was
  a restriction lifted during the .NET 11 preview cycle and is now validated end-to-end per the release notes.

### Telling whether a method was compiled runtime-async

```csharp
var m = typeof(SomeType).GetMethod(nameof(SomeType.SomeAsyncMethod))!;

bool isRuntimeAsync = m.GetMethodImplementationFlags()
    .HasFlag(System.Reflection.MethodImplAttributes.Async); // reflection surfaces this as .Async

bool hasStateMachine = m.GetCustomAttributes(false)
    .Any(a => a.GetType().Name == "AsyncStateMachineAttribute");
// hasStateMachine == !isRuntimeAsync in practice — a method has one or the other, never both
```

Empirically (11.0.100-rc.1 SDK, decompiled with ilspycmd / inspected via reflection): a runtime-async method
has `MethodImplAttributes.Async` set and **no** `AsyncStateMachineAttribute` and **no** nested state-machine
type; a classic async method has the attribute, the nested `d__N` type, and `MethodImplAttributes.IL` (not
`Async`).

## How it's enabled

**Empirically verified — this is not "on by default" for user code on the RC1 SDK.** A plain
`<TargetFramework>net11.0</TargetFramework>` class library, built with the installed `11.0.100-rc.1` SDK,
produced a classic compiler state machine (`AsyncStateMachineAttribute`, nested `d__0` type) with **no**
project settings at all. Runtime Async only appeared after adding:

```xml
<PropertyGroup>
  <Features>runtime-async=on</Features>
</PropertyGroup>
```

after which the method compiled with `MethodImplAttributes.Async` and no state-machine type. Setting
`<UseRuntimeAsync>true</UseRuntimeAsync>` alone (without `Features`) did **not** enable it in this test, and
`<UseRuntimeAsync>false</UseRuntimeAsync>` did **not** disable it when `Features=runtime-async=on` was also
set — so treat `UseRuntimeAsync` as unproven/unwired at the public SDK level on this build and use the
`Features` switch as the one you can rely on. `<EnablePreviewFeatures>true</EnablePreviewFeatures>` is **not**
required — that gate was removed from the compiler in an earlier .NET 11 preview.

- **The BCL is different.** `dotnet/runtime`'s own libraries have been built with `runtime-async=on` since
  .NET 11 Preview 4 — release notes call this out explicitly ("Runtime libraries are now compiled with
  runtime-async"), specifically to get broad functional/perf validation. So BCL methods you call may already
  be runtime-async internally regardless of what your own project does — this is transparent to your code
  either way (see interop, below).
- **Per-TFM control works cleanly.** In a multi-targeted project you can condition the `Features` property on
  `$(TargetFramework)`, e.g. only turn it on for `net11.0` and leave `net10.0`/`netstandard2.0` builds alone —
  verified by building a `net10.0;net11.0` multi-target with the property conditioned on
  `'$(TargetFramework)'=='net11.0'`; each TFM's output had the expected codegen for that TFM.
- **Interop between runtime-async and classic async is transparent at the call boundary.** Both shapes still
  return `Task`/`Task<T>`/`ValueTask`/`ValueTask<T>` with the same public signature; a caller compiled with
  the classic model awaits a runtime-async callee exactly like any other `Task`-returning method (and vice
  versa). The compiler itself "has no idea whether a method from a referenced assembly is compiled with
  runtime async or not" — direct quote from the Roslyn design doc. You cannot see or control the callee's
  choice from the caller's IL.
- Given this is a still-shifting preview switch, **re-check the exact property name/value on your SDK before
  shipping** — don't assume `Features=runtime-async=on` is the final RTM spelling; it's what's documented and
  what this skill verified on RC1.

## What it means for existing advice (see `csharp-async`)

- **`ConfigureAwait(false)` still matters.** It's still correct on every TFM that predates or doesn't run
  runtime-async, still a no-op (not harmful) where it doesn't apply, and the design docs don't describe any
  change to `SynchronizationContext`/`ConfigureAwait` semantics — only to the cost of context capture when
  there's *nothing* to capture. Keep writing it in library code.
- **`ValueTask` on hot paths, eliding pointless `async` wrappers, avoiding `async` on synchronous CPU-bound
  code** — all still valid. Runtime Async lowers the constant factor of an `await`; it does not remove the
  cost of allocating a `Task` per call or wrapping already-synchronous work in an unnecessary state machine
  (of either kind).
- **Don't stop measuring.** A quick empirical check ([MemoryDiagnoser], BenchmarkDotNet 0.14.0, net11.0
  RC1, `[Features]runtime-async=on` vs off, 100 `await Task.Yield()`-backed calls per iteration) showed
  runtime-async at ~17.4 µs / 5.7 KB allocated vs. classic at ~24.3 µs / 9.6 KB allocated on the same SDK —
  roughly 30% faster and ~40% less allocation for this specific await-heavy microbenchmark. Treat this as "a
  real, measurable effect exists," not as a number to quote for your workload — benchmark your own hot path,
  see `benchmarking`.
- **Debugging**: the RC1 release notes state breakpoints now bind correctly inside runtime-async methods and
  the debugger can step through `await` boundaries without landing in generated infrastructure. Live call
  stacks are shorter and read as real call chains (see above). If a tool you use parses stack traces by
  pattern-matching `MoveNext`/`<...>d__N` frames, it will see different (fewer, cleaner) frames under
  runtime-async — see Gotchas.
- **`AsyncLocal<T>`/`ExecutionContext` still flow correctly** across runtime-async suspension points — the
  change is purely an optimization that skips the capture/restore when there is nothing to flow, not a
  behavior change to ambient-state propagation. Don't rely on `ExecutionContext` *not* flowing; it still does
  whenever you actually have `AsyncLocal<T>` state or a captured `SynchronizationContext`.

## Prominent warning: Mono targets — Runtime Async is a CoreCLR feature

**None of the design docs, JIT/VM spec, or .NET 11 release notes mention Mono support for Runtime Async.**
Every mechanism described — the `Continuation` object ABI, `DispatchContinuations`, JIT-recognized call
patterns, tiered-compilation interaction, crossgen2/R2R inlining — is CoreCLR JIT/VM machinery. The release
notes explicitly confirm **NativeAOT and ReadyToRun** support (both CoreCLR-family compilation modes) and
separately describe **"CoreCLR on WebAssembly"** as its own, still-experimental target with its own section —
implying it is distinct from, not the same as, the Mono-based `browser-wasm` runtime that Blazor WebAssembly
ships today. Nothing in the docs states Mono implements the `MethodImplOptions.Async` execution model, the
continuation-based suspension ABI, or the `AsyncHelpers` intrinsic recognition.

**The docs do not settle whether the compiler emits a fallback, whether the SDK disables the feature
automatically for Mono-based RIDs/TFMs, or whether it's a hard build/runtime error.** Given the absence of
any stated Mono support and the CoreCLR-specific nature of every mechanism involved, treat it as **likely
won't work on Mono** — but verify on your actual target rather than trusting this or any other secondhand
description, since the feature is still preview and this could change before .NET 11 ships.

Concretely, this affects:

- **.NET 11 Blazor WebAssembly** — runs on Mono today. An app project built for Blazor WASM should not be
  expected to get Runtime Async even if it targets `net11.0` and sets `Features=runtime-async=on`; verify
  whether the build errors, silently ignores the switch, or produces code that fails at runtime.
- **Android / iOS / MAUI** — status varies by configuration (Mono is still used in some configurations;
  others increasingly use NativeAOT/CoreCLR-style toolchains). The docs do not state a blanket answer for
  .NET 11. **Check which runtime your specific target configuration uses before assuming either way.**

### For library authors: your net11.0 assembly may also be consumed by a Mono host

This is the sharper risk. A library targets `net11.0`, gets built once, and that single assembly is what
*both* a CoreCLR consumer and a Blazor WASM (Mono) consumer load — there's no separate "Mono build" unless
you explicitly make one. If your `net11.0` library assembly is compiled with `runtime-async=on` (or picks it
up by default at RTM) and a Mono host cannot execute `MethodImplOptions.Async` methods, the failure surfaces
in your *consumer's* Blazor app, not in your own test suite — likely as a `MissingMethodException`,
`BadImageFormatException`, or an outright crash on first call, not a clean compile-time error.

Do this before shipping a `net11.0` library that might be consumed by a WASM/Mono host:

1. **Opt the library out of runtime-async explicitly** rather than relying on whatever the default turns
   out to be at RTM:
   ```xml
   <PropertyGroup>
     <Features>runtime-async=off</Features> <!-- or simply omit runtime-async=on -->
   </PropertyGroup>
   ```
   Given the RC1 SDK's *actual* default is already "off" unless you opt in (see above), the safest posture for
   a WASM-facing library today is: **don't add `Features=runtime-async=on`** unless you've verified your Mono
   consumers are unaffected. Re-verify this default hasn't flipped when you upgrade SDKs.
2. **If you want the runtime-async perf win for CoreCLR consumers without risking Mono consumers**, ship a
   dedicated build for the WASM/Mono path — either a separate TFM condition (`Features` conditioned off for a
   `browser-wasm` `RuntimeIdentifier` build) or a distinct package/target if your library already
   multi-targets. There is no single-assembly way to be "runtime-async for CoreCLR, classic for Mono" — IL
   compiled one way is compiled that way for every consumer of that assembly.
3. **Test under an actual Blazor WASM host (or a `browser-wasm` RID publish) before shipping**, not just
   under a desktop CoreCLR test run passing. A CoreCLR unit test suite proves nothing about Mono behavior.
4. **Flag it in package docs** if you ship a WASM-facing package: state explicitly whether the package uses
   Runtime Async, and if it does, that WASM/Mono consumers should verify compatibility before upgrading.

**The rule, stated plainly: verify on a real WASM host before shipping a `net11.0` library that might reach
Mono; when in doubt, opt out of `runtime-async` for that assembly.** Don't guess from this document or any
other secondhand summary — the authoritative docs don't settle the exact failure mode, so empirical
verification on your target is the only reliable answer.

## Gotchas

- **Multi-targeted libraries get different codegen per TFM.** A library targeting `net11.0` +
  `net9.0`/`net10.0`/`netstandard2.0` produces classic state machines on the older TFMs and (if you opt in)
  runtime-async on `net11.0` — these are genuinely different IL bodies with different perf characteristics.
  Benchmark each TFM separately; a win on `net11.0` says nothing about the others.
- **Reflection code that looks for `IAsyncStateMachine`/`AsyncStateMachineAttribute` breaks silently.** Any
  code (yours, a DI container's, a serializer's, a source generator's) that special-cases async methods by
  checking for the state-machine attribute or type will see a runtime-async method as "not async" — because
  by the old signal, it isn't. Check `MethodImplAttributes.Async` too if you need to detect async methods
  reliably across both models.
- **Tools/profilers that parse state-machine shapes may misbehave** until they're updated for runtime-async's
  continuation model. The RC1 release notes note the async profiler was updated to instrument both models
  uniformly — but third-party tools built against the old shape may not have caught up yet.
- **Stack-trace-parsing tests are the most likely silent breakage.** A test that asserts on the exact frame
  count or exact frame text of a *live* stack trace (not a caught exception's `.StackTrace`) will see fewer,
  differently-named frames under runtime-async. Caught-exception traces are unaffected (see above), so prefer
  asserting on those instead of live-stack introspection where you can.

## Checklist

- [ ] Confirmed on your actual SDK whether `net11.0` gets Runtime Async by default or needs
      `<Features>runtime-async=on</Features>` — don't assume; the RC1 SDK required the explicit switch
- [ ] If enabling explicitly, conditioned the property per-`$(TargetFramework)` in multi-targeted projects
- [ ] Still using `ConfigureAwait(false)`, `ValueTask` on measured hot paths, and eliding pointless `async`
      wrappers — Runtime Async changes the constant factor, not the rules (`csharp-async`)
- [ ] Benchmarked your own workload (net11.0 vs. prior TFM, and runtime-async on vs. off) before claiming a
      win — see `benchmarking`
- [ ] Any code that detects "is this an async method" via `AsyncStateMachineAttribute`/`IAsyncStateMachine`
      also checks `MethodImplAttributes.Async`
- [ ] If shipping a `net11.0` library that might be consumed by Blazor WebAssembly, MAUI, or another
      Mono-based host: verified behavior on an actual WASM/Mono target, not assumed compatibility
- [ ] Defaulted a WASM-facing library to `runtime-async` **off** unless Mono compatibility has been
      explicitly verified
- [ ] Stack-trace assertions in tests target caught-exception traces, not live-stack frame counts, where
      possible — see `csharp-debugging`
- [ ] Verified AOT/trimming implications separately if the library also publishes NativeAOT — see
      `csharp-aot-trimming`
