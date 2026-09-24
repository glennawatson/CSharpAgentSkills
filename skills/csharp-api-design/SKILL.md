---
name: csharp-api-design
description: Use when designing or reviewing a public C#/.NET API surface — what to expose, sealed vs unsealed, Try* vs exceptions vs result types, overload and CancellationToken shape, what counts as a breaking change, and how to track and obsolete public members over time.
---

# Public API design

A public member is a promise. Every member you expose is one more thing callers can depend on,
one more thing you must keep working, and one more thing you must document. The default answer
to "should this be public?" is no.

## Minimal surface by default

- **`internal` by default.** Make a type or member `public` only when something outside the
  assembly needs it. Use `InternalsVisibleTo` for test assemblies instead of widening real API.
- **Never `InternalsVisibleTo` between two shipping libraries.** IVT is for test projects only —
  between production assemblies it makes one package's private detail a load-bearing contract of
  another, with none of the compatibility discipline a real public member gets. To share an
  internal helper across leaves of the same repo, link the source instead: put it under a shared
  folder (e.g. `src/Shared/`) and reference it from each project with
  `<Compile Include="..\Shared\Foo.cs" Link="Shared\Foo.cs" />`, so every leaf compiles its own
  `internal` copy and the copies never clash. Anything that genuinely needs to cross an assembly
  boundary is public API, not an IVT hole.
- **Name every type, member, and file for the behavior it represents**, not for the change that
  introduced it — a reader six months out has no context for *why* you touched it, only for *what
  it is*. Avoid names that describe the edit rather than the thing (`Temp`, `New`, `Old`, `Fix`,
  `Updated`, `Misc`) — they say nothing about the API's purpose and read as unfinished.
- **`sealed` by default.** An unsealed class invites overriding you never designed for, and
  removing virtual dispatch later is a breaking change. Leave a class open for inheritance only
  when you have an actual extensibility scenario and have designed the protected surface for it
  (virtual members with clear override contracts, a documented invariant callers must preserve).
- **Prefer composition over an extensibility point.** If callers need to vary behavior, a
  delegate, an injected strategy interface, or an event usually beats a virtual method — it
  doesn't require reasoning about base-class invariants under override.
- Every public member costs review, docs (see `csharp-docs`), and forward-compatibility risk.
  When in doubt, leave it `internal` and widen later — that's a safe, additive change. Widening a
  narrow surface is easy; narrowing a public one is a breaking change.

```csharp
// ❌ everything public "just in case"
public class RequestPipeline
{
    public List<Middleware> Middlewares = new();
    public void RunStage(int index) { ... }
}

// ✅ minimal, sealed, the internals stay internal
public sealed class RequestPipeline
{
    private readonly List<Middleware> _middlewares = new();

    public void Use(Middleware middleware) => _middlewares.Add(middleware);

    public Task<Response> RunAsync(Request request, CancellationToken cancellationToken = default) { ... }
}
```

## `Try*` pattern vs exceptions vs result types

Pick based on whether failure is **expected** (part of normal control flow) or **exceptional**
(a bug, a broken invariant, a genuinely unusual condition):

- **`Try*` (`bool TryParse(string s, out T value)`)** — failure is common and the caller is
  expected to branch on it, on a hot or frequently-failing path (parsing user input, dictionary
  lookups). No exception, no allocation, no stack unwind. Always pair with a throwing overload
  when one exists (`Parse` for `TryParse`) so callers can pick per call site.
- **Exceptions** — failure is a violated precondition, an unrecoverable environment problem, or
  something the immediate caller usually can't handle and shouldn't be forced to check at every
  call site (I/O failure, invalid argument, broken invariant). See `csharp-error-handling` for
  throwing mechanics.
- **Result types (`Result<T>`, `OneOf<T, Error>`, or a domain-specific outcome type)** — failure
  is expected *and* carries structured detail the caller needs to branch on, and a `bool`/`out`
  pair isn't expressive enough (multiple distinct failure reasons, a validation error list). Costs
  more ceremony than `Try*`; reach for it when the failure shape genuinely needs more than a flag.

Don't use exceptions for expected control flow (validation failures on every call, "not found" in
a lookup that's routinely empty) — the stack-unwind cost and the caller's forced `try/catch` are
both wrong for something that happens routinely.

## Overload design and `CancellationToken`

- **`CancellationToken` is always the last parameter, with a default only on the outermost
  public entry point that callers invoke directly** (a library API, not an internal helper) —
  `default` value means the compiler can synthesize the no-token overload for you without an
  explicit second signature to maintain:
  ```csharp
  public Task<Data> FetchAsync(string id, CancellationToken cancellationToken = default) { ... }
  ```
  Internal/private helpers that forward a token should take it as a required parameter — a
  default there just hides a place where the token was dropped.
- **Overloads that differ only in trailing optional parameters** are a source-compat convenience,
  not a substitute for real design — see binary-break traps below.
- Design overload sets so the compiler can pick unambiguously: don't add an overload whose
  arguments are implicitly convertible to an existing overload's parameter types in a way that
  makes common call sites ambiguous or silently picks the wrong one.

## Accept general, return specific

Accept the most general useful type so more callers can call you without adapting; return the
most specific useful type so callers get maximum capability without a cast.

```csharp
// ✅ accepts anything enumerable; returns a concrete, capability-rich type
public ImmutableArray<T> Snapshot<T>(IEnumerable<T> source) => source.ToImmutableArray();

// ❌ over-constrains the input, under-delivers on the output
public IEnumerable<T> Snapshot<T>(List<T> source) => source.ToList();
```

Same logic for parameters: prefer `IReadOnlyList<T>`/`ReadOnlySpan<T>` over `List<T>`/`T[]` when
you only read; prefer `IEnumerable<T>` only when you truly need just one pass and don't need
`Count` or indexing (repeated enumeration of a lazy `IEnumerable<T>` is a classic footgun for
callers).

## Optional-parameter binary-break traps

An optional parameter's default value is **baked into the caller's compiled IL** at the call
site, not looked up at runtime. That makes optional parameters a **source-compatible,
binary-breaking** change in both directions:

```csharp
// v1
public void Connect(string host, int port = 80) { ... }

// v2 — caller compiled against v1, calling Connect("host") without recompiling,
// still passes the OLD baked-in default (80) even if you change it to 443 here.
public void Connect(string host, int port = 443) { ... }
```

- Changing a default value does not change behavior for already-compiled callers — only for
  callers who recompile. This routinely surprises people who expect it to behave like a runtime
  default.
- Adding a new optional parameter in the middle, or reordering parameters, breaks positional
  callers at the binary level even though named-argument source callers are fine.
- **Prefer overloads over optional parameters for public API that ships as a binary
  (NuGet package, plugin contract).** An overload is an explicit, independently versioned member;
  changing which overload old binaries bind to is visible and controlled, not silently baked in.
  Optional parameters are fine for internal code and for APIs where source-only consumption
  (source generators, project-to-project references always rebuilt together) is guaranteed.

## What counts as a breaking change

| Change | Source break | Binary break |
| --- | --- | --- |
| Add a member to a `class` | No | No |
| Add a member to an `interface` (no default impl) | Yes — implementers must add it | Yes |
| Add a member to an `interface` with a default interface member | Usually no | Usually no, but see below |
| Remove/rename any public member | Yes | Yes |
| Change a method's return type (even covariant-looking) | Sometimes | Always |
| Add an overload that is ambiguous with an existing call site | Yes for that call site | No |
| Change a parameter's default value | No | No (old binaries keep the old baked-in value) |
| Seal a previously unsealed class | Only for derivers | Only for derivers |
| Add a parameter (even optional) in the middle | No if named args used | Yes for positional binary callers |
| Move a type to a different namespace/assembly | Yes | Yes |

**Default interface members (DIMs)** let you add a member to an interface without breaking
existing implementers — they inherit the default body. They still change the interface's binary
identity (adding a member changes its vtable layout for reference purposes) and can introduce
*diamond ambiguity* if two interfaces a type implements both supply a default for the same
signature — that becomes a compile error at the implementing type, not silently resolved. Use DIMs
for genuine "add capability to an existing contract" evolution, not as a routine substitute for a
new interface, since callers can't tell from `is`/pattern matching alone which implementation a
DIM path resolves to without care.

## Public API tracking

Track the shipped public surface explicitly rather than relying on code review to catch
accidental additions or removals. The common pattern (`Microsoft.CodeAnalysis.PublicApiAnalyzers`)
uses two text files per target:

- `PublicAPI.Shipped.txt` — the surface that has shipped in a released version; changing this
  file is itself a signal something broke compatibility.
- `PublicAPI.Unshipped.txt` — new members added since the last release; the analyzer flags any
  public member that isn't listed in one of the two files (`RS0016: Add public types and members
  to the declared API`) and flags anything listed that no longer exists (`RS0017`).

Workflow: add a member, the analyzer errors until you add its signature to
`PublicAPI.Unshipped.txt`; on release, move those lines into `PublicAPI.Shipped.txt`. This turns
"did we mean to expose that?" into a build failure instead of a post-hoc discovery, and gives you
a reviewable diff of exactly what a PR adds to or removes from the public contract.

An API-approval test (verifying a generated surface snapshot, e.g. via a snapshot-testing library)
achieves the same goal for projects that prefer a single committed file over the two-file
shipped/unshipped split. Either way: **the public surface should have a diff a reviewer can see**,
not just live implicitly in `public` keywords scattered through the source.

## Obsoletion process

Don't remove a public member outright — deprecate, then remove after a compatibility window:

```csharp
[Obsolete("Use ConnectAsync(ConnectOptions, CancellationToken) instead. Scheduled for removal in v5.0.", error: false)]
public Task ConnectAsync(string host, int port) => ConnectAsync(new ConnectOptions(host, port), default);
```

- Start with `error: false` (a warning) for at least one minor/major release cycle so consumers
  have a build that still compiles while they migrate.
- The message names the replacement and, ideally, the version it disappears in — a caller reading
  a warning should not have to go find your changelog.
- Only flip to `error: true` (or delete the member) in a release that is already otherwise a
  breaking-change boundary (a major version bump under semantic versioning) — don't force a
  compile break on a supposedly-compatible release.
- Keep the obsolete member's implementation correct (usually by forwarding to the replacement)
  for as long as it's obsolete-but-present — an obsolete member that silently does the wrong thing
  is worse than one that doesn't exist.

## Review checklist

- [ ] New member is `internal` unless it must be `public`
- [ ] New class is `sealed` unless inheritance is a designed scenario
- [ ] Failure mode matches expected-vs-exceptional (`Try*` / exception / result type)
- [ ] `CancellationToken` last, defaulted only on outermost public entry points
- [ ] Parameters accept the most general useful type; returns give the most specific useful type
- [ ] No optional-parameter binary-break trap on a shipped binary surface — overloads used instead
- [ ] Interface changes considered for both source and binary breaks; DIM diamond conflicts checked
- [ ] Public API tracked (`PublicAPI.Shipped/Unshipped.txt` or an approval test) and updated
- [ ] Any removal goes through `[Obsolete(message, error: false)]` first, not straight to deletion
