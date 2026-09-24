#:package BenchmarkDotNet@0.15.2
#:package Microsoft.CodeAnalysis.CSharp@4.14.0
#:property PublishAot=false
#:property TargetFramework=net10.0

// A/B one analyzer across two assembly snapshots with BenchmarkDotNet, for when the one-session sweep flagged a
// subject and you need a precise, allocation-attributed answer.
//
// - The snapshot directory is a [Params], so baseline and candidate run in the same session, same corpus.
// - Each snapshot loads in its own AssemblyLoadContext so the two copies of the same assembly never collide.
// - MemoryDiagnoser gives the deterministic Allocated column (the A/B number); EventPipeProfiler(GcVerbose)
//   writes a .nettrace per benchmark whose ticks name the allocating frames.
// - In-process toolchain: a file-based app has no project for BenchmarkDotNet to generate from.
// - Three corpora: startup (one empty class), clean, violating. Subtract startup from clean for the per-node cost.
//
// Run pinned, then read the trace (see the benchmarking skill for nettrace analysis):
//   taskset -c 0-6 nice -n -20 dotnet run -c Release SnapshotAbBenchmarks.cs -- \
//     --analyzer LockTargetAnalyzer --baseline /abs/ab-base/StyleSharp/analyzers --candidate /abs/ab-cand/StyleSharp/analyzers \
//     --clean clean.cs --violating violating.cs
// Compare error bars, not means; a one-order or overlapping difference is unresolved.

using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

SnapshotAbBenchmarks.Settings = BenchSettings.Parse(args);
var config = ManualConfig.Create(DefaultConfig.Instance)
    .AddJob(Job.Default
        .WithToolchain(InProcessEmitToolchain.Instance)
        .WithWarmupCount(5)
        .WithIterationCount(20))
    .AddDiagnoser(MemoryDiagnoser.Default)
    .AddDiagnoser(new EventPipeProfiler(EventPipeProfile.GcVerbose));
BenchmarkRunner.Run<SnapshotAbBenchmarks>(config);

/// <summary>Runs one analyzer from a snapshot over one corpus path.</summary>
[MemoryDiagnoser]
public class SnapshotAbBenchmarks
{
    /// <summary>The command-line settings, set before BenchmarkDotNet constructs the class.</summary>
    public static BenchSettings Settings { get; set; } = null!;

    private CSharpCompilation _compilation = null!;
    private ImmutableArray<DiagnosticAnalyzer> _analyzer;

    /// <summary>Which snapshot: "baseline" or "candidate".</summary>
    [Params("baseline", "candidate")]
    public string Snapshot { get; set; } = "baseline";

    /// <summary>Which corpus path.</summary>
    [Params("startup", "clean", "violating")]
    public string CorpusPath { get; set; } = "startup";

    /// <summary>Loads the snapshot's analyzer in isolation and builds the compilation once.</summary>
    [GlobalSetup]
    public void Setup()
    {
        var directory = Snapshot == "baseline" ? Settings.BaselineDirectory : Settings.CandidateDirectory;
        var context = new AssemblyLoadContext(Snapshot, isCollectible: false);
        context.Resolving += (ctx, name) =>
        {
            var candidate = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(candidate) ? ctx.LoadFromAssemblyPath(candidate) : null;
        };

        DiagnosticAnalyzer? instance = null;
        foreach (var file in Directory.EnumerateFiles(directory, "*.dll"))
        {
            var assembly = context.LoadFromAssemblyPath(file);
            var type = assembly.GetTypes().FirstOrDefault(t => t.Name == Settings.AnalyzerName && typeof(DiagnosticAnalyzer).IsAssignableFrom(t));
            if (type is not null)
            {
                instance = (DiagnosticAnalyzer)Activator.CreateInstance(type)!;
                break;
            }
        }

        _analyzer = [instance ?? throw new InvalidOperationException($"{Settings.AnalyzerName} not found in {directory}")];
        var text = CorpusPath switch
        {
            "startup" => "internal sealed class Startup { }",
            "clean" => File.ReadAllText(Settings.CleanPath),
            _ => File.ReadAllText(Settings.ViolatingPath),
        };

        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToArray();
        var forced = instance.SupportedDiagnostics.Select(d => new KeyValuePair<string, ReportDiagnostic>(d.Id, ReportDiagnostic.Warn));
        _compilation = CSharpCompilation.Create(
            "Bench",
            [CSharpSyntaxTree.ParseText(text)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary).WithSpecificDiagnosticOptions(forced).WithConcurrentBuild(false));
    }

    /// <summary>One analyzer pass over the compilation; a fresh driver each time so no result is cached.</summary>
    /// <returns>The number of diagnostics, returned so the work is not optimised away.</returns>
    [Benchmark]
    public async Task<int> AnalyzeAsync()
    {
        var withAnalyzers = _compilation.WithAnalyzers(
            _analyzer,
            new CompilationWithAnalyzersOptions(new AnalyzerOptions([]), null, concurrentAnalysis: false, logAnalyzerExecutionTime: false));
        return (await withAnalyzers.GetAnalyzerDiagnosticsAsync(CancellationToken.None).ConfigureAwait(false)).Length;
    }
}

/// <summary>Command-line settings for the snapshot A/B.</summary>
/// <param name="AnalyzerName">The analyzer type name.</param>
/// <param name="BaselineDirectory">The baseline snapshot's analyzer directory.</param>
/// <param name="CandidateDirectory">The candidate snapshot's analyzer directory.</param>
/// <param name="CleanPath">A source file the analyzer reports nothing on.</param>
/// <param name="ViolatingPath">A source file the analyzer reports on.</param>
public sealed record BenchSettings(string AnalyzerName, string BaselineDirectory, string CandidateDirectory, string CleanPath, string ViolatingPath)
{
    /// <summary>Reads the settings from <c>--name value</c> pairs.</summary>
    /// <param name="args">The command-line arguments.</param>
    /// <returns>The settings.</returns>
    public static BenchSettings Parse(string[] args)
    {
        string Get(string name) => args[Array.IndexOf(args, name) + 1];
        return new(Get("--analyzer"), Get("--baseline"), Get("--candidate"), Get("--clean"), Get("--violating"));
    }
}
