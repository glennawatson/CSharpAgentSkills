---
name: csharp-assembly-inspection
description: Use when a C#/.NET question is about the built artifact rather than the source: manifest resources, assembly references, what types or members actually shipped, whether a NuGet package still carries PRI/resource payloads, or whether the compiled surface matches the code. Prefer a throwaway single-file `dotnet run inspect.cs` tool using `System.Reflection.Metadata` and `PEReader` for read-only ECMA inspection; use `System.Reflection` only when runtime-visible type/member details are required.
---

# Compiled artifact inspection (.NET)

Roslyn answers questions about source. It does not answer questions about the binary that was actually produced. When the problem is "what shipped?", "what resources are really in this DLL?", "does this package still carry a `.pri` file?", or "what public members does the compiled type expose?", switch layers and inspect the artifact directly.

## Use the right layer

- **File/package layout first** for loose payload questions: `unzip -l package.nupkg`, `find obj -name '*.pri'`, `find bin -name '*.resources'`.
- **`System.Reflection.Metadata` + `PEReader`** for read-only ECMA metadata: assembly name, references, manifest resources, type definitions, exported types. This is the default because it does not load the assembly.
- **`System.Reflection`** only when you need the runtime surface: did type `X` survive, is it abstract, which methods are virtual, which properties are public.

Start with metadata. Escalate to reflection only if metadata is not enough.

## Metadata-first example

Use a throwaway single-file program when the question is about managed contents:

```csharp
using System;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

foreach (var path in args)
{
    Console.WriteLine($"ASSEMBLY {path}");
    using var stream = File.OpenRead(path);
    using var pe = new PEReader(stream);
    if (!pe.HasMetadata)
    {
        Console.WriteLine("  no metadata");
        continue;
    }

    var reader = pe.GetMetadataReader();
    var assembly = reader.GetAssemblyDefinition();
    Console.WriteLine($"  name: {reader.GetString(assembly.Name)}");
    Console.WriteLine($"  manifest resources: {reader.ManifestResources.Count}");
    foreach (var handle in reader.ManifestResources)
    {
        var resource = reader.GetManifestResource(handle);
        Console.WriteLine(
            $"    {reader.GetString(resource.Name)} | attrs={resource.Attributes} | impl={resource.Implementation.Kind} | offset={resource.Offset}");
    }

    Console.WriteLine($"  assembly references: {reader.AssemblyReferences.Count}");
    foreach (var handle in reader.AssemblyReferences)
    {
        var reference = reader.GetAssemblyReference(handle);
        Console.WriteLine($"    {reader.GetString(reference.Name)}");
    }
}
```

This settles questions like:

- Does the managed DLL embed any manifest resources at all?
- Which assemblies does it reference after the actual build?
- Did the package change the managed dependency graph?

## Reflection example

When the question is about the runtime-visible shape of a type, load the assembly deliberately:

```csharp
using System;
using System.Reflection;

var asm = Assembly.LoadFrom(Environment.GetEnvironmentVariable("RXDLL"));
var t = asm.GetType("System.Reactive.Concurrency.LocalScheduler");
Console.WriteLine($"Type: {t} (abstract={t?.IsAbstract})");

foreach (var m in t!.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
{
    var kind = m.IsAbstract ? "abstract" : m.IsVirtual ? "virtual" : "";
    Console.WriteLine(
        $"  {kind} {m.ReturnType.Name} {m.Name}{(m.IsGenericMethodDefinition ? "<T>" : "")}({string.Join(", ", Array.ConvertAll(m.GetParameters(), p => p.ParameterType.Name))})");
}

foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
    Console.WriteLine($"  prop {p.PropertyType.Name} {p.Name}");
```

Use this for API-surface confirmation after a package upgrade or shim migration.

## Practical workflow

1. Inspect the package/output layout if the issue mentions `.pri`, `.resources`, native assets, or build payloads.
2. Run a metadata query over the DLL to separate "managed manifest resource" from "loose file in the package/output".
3. If needed, load the assembly and inspect the exact type/member surface.
4. Only after that, go back to source-level Roslyn to explain where the artifact came from.

## Important distinctions

- `ManifestResources.Count == 0` means no managed embedded manifest resources in that assembly. It does **not** prove the package/output contains no loose `.pri`, `.resources`, or native files.
- A NuGet package can ship extra files next to the DLL even when the DLL metadata is empty. Inspect both the package archive and the assembly.
- Reflection can fail if dependencies are missing and can trigger type-loading behavior. If metadata answers the question, stop there.
- For source questions like "who implements this interface?" or "which files use this symbol?", use `roslyn-discovery` instead.

## Anti-patterns

- Guessing from source that a resource or reference must still exist in the built DLL.
- Using reflection first for a question metadata could answer safely.
- Treating a package payload problem as if it must be a managed manifest-resource problem.
- Declaring a build pipeline issue solved without inspecting the generated artifact.
