// Turns an existing TUnit suite into a first-pass allocation and duration baseline.
//
// - [Before(TestDiscovery)] starts ONE in-process EventPipe session (GC verbose + the marker source) when
//   MEASURE_TRACE_DIRECTORY is set, and forces tests to run one at a time so windows never interleave.
// - The assembly-level test executor brackets every test body with markers; hooks and discovery stay outside.
// - Call BeginAnalysis/EndAnalysis from the verifier's driver override to bracket just the analyzer run and to
//   classify it as clean/violating from the diagnostics it actually produced (never from the test name).
// - [After(TestSession)] stops the session after rundown so stacks resolve.
//
// Slice the trace with slice-test-trace.cs. This is triage - which subjects cost anything, and corpora for the
// real A/B - not the verdict: a test body also parses, builds compilations and asserts.

using System.Collections.Immutable;
using System.Diagnostics.Tracing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing.Parsers;
using TUnit.Core;
using TUnit.Core.Executors;
using TUnit.Core.Interfaces;

[assembly: TestExecutor<AnalyzerMeasurement.MeasuredTestExecutor>]

namespace AnalyzerMeasurement;

/// <summary>Brackets each test body with markers and lets TUnit report the original exception.</summary>
public sealed class MeasuredTestExecutor : ITestExecutor
{
    /// <inheritdoc />
    public async ValueTask ExecuteTest(TestContext context, Func<ValueTask> action)
    {
        TestTraceSession.BeginTest(context);
        var passed = false;
        try
        {
            await action().ConfigureAwait(false);
            passed = true;
        }
        finally
        {
            TestTraceSession.EndTest(passed);
        }
    }
}

/// <summary>Owns the single trace session and the current test and analysis windows.</summary>
public static class TestTraceSession
{
    private static EventPipeSession? _session;
    private static Task? _copy;
    private static string _testId = string.Empty;
    private static int _nextWindow;
    private static int _window;
    private static ImmutableArray<DiagnosticAnalyzer> _analyzers;

    /// <summary>Starts the session before discovery so every test body is inside it.</summary>
    /// <param name="context">The discovery context, used to serialise the run.</param>
    [Before(HookType.TestDiscovery)]
    public static void Start(BeforeTestDiscoveryContext context)
    {
        var directory = Environment.GetEnvironmentVariable("MEASURE_TRACE_DIRECTORY");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        context.Settings.Parallelism.MaximumParallelTests = 1;
        var path = Path.Combine(directory, $"{typeof(TestTraceSession).Assembly.GetName().Name}-{Environment.ProcessId}.nettrace");
        var keywords = ClrTraceEventParser.Keywords.GC | ClrTraceEventParser.Keywords.GCHandle | ClrTraceEventParser.Keywords.Exception;
        EventPipeProvider[] providers =
        [
            new(ClrTraceEventParser.ProviderName, EventLevel.Verbose, (long)keywords),
            new(MeasurementEvents.Instance.Name, EventLevel.Informational, long.MaxValue),
        ];

        var session = new DiagnosticsClient(Environment.ProcessId).StartEventPipeSession(providers);
        _session = session;
        _copy = Task.Run(async () =>
        {
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 16384, useAsync: true);
            await session.EventStream.CopyToAsync(stream).ConfigureAwait(false);
        });
    }

    /// <summary>Stops the session after the last test, letting rundown finish.</summary>
    /// <returns>A task that completes when the trace is flushed.</returns>
    [After(HookType.TestSession)]
    public static async Task StopAsync()
    {
        if (_session is not { } session || _copy is not { } copy)
        {
            return;
        }

        try
        {
            await session.StopAsync(CancellationToken.None).ConfigureAwait(false);
            await copy.ConfigureAwait(false);
        }
        finally
        {
            session.Dispose();
            _session = null;
        }
    }

    /// <summary>Opens a test body window.</summary>
    /// <param name="context">The running test.</param>
    public static void BeginTest(TestContext context)
    {
        if (_session is null)
        {
            return;
        }

        var details = context.Metadata.TestDetails;
        _testId = details.TestId;
        MeasurementEvents.Instance.TestStart(_testId, details.ClassType.FullName ?? string.Empty, details.MethodName);
    }

    /// <summary>Closes the test body window, closing an interrupted analysis window as unresolved.</summary>
    /// <param name="passed">Whether the body completed.</param>
    public static void EndTest(bool passed)
    {
        if (_session is null)
        {
            return;
        }

        if (_window != 0)
        {
            MeasurementEvents.Instance.AnalysisStop(_window, "unresolved", -1);
            _window = 0;
        }

        MeasurementEvents.Instance.TestStop(_testId, passed ? 1 : 0);
    }

    /// <summary>Opens an analysis window; call immediately before the verifier creates its analyzer driver.</summary>
    /// <param name="analyzers">The analyzers the driver runs.</param>
    public static void BeginAnalysis(ImmutableArray<DiagnosticAnalyzer> analyzers)
    {
        if (_session is null || _window != 0)
        {
            return;
        }

        _analyzers = analyzers;
        var names = new string[analyzers.Length];
        for (var i = 0; i < analyzers.Length; i++)
        {
            names[i] = analyzers[i].GetType().FullName ?? analyzers[i].GetType().Name;
        }

        _window = ++_nextWindow;
        MeasurementEvents.Instance.AnalysisStart(_window, _testId, string.Join("|", names));
    }

    /// <summary>Closes the analysis window, classifying the path from the diagnostics produced.</summary>
    /// <param name="diagnostics">The diagnostics the driver returned.</param>
    public static void EndAnalysis(ImmutableArray<Diagnostic> diagnostics)
    {
        if (_session is null || _window == 0)
        {
            return;
        }

        var count = 0;
        var crashed = false;
        foreach (var diagnostic in diagnostics)
        {
            crashed |= diagnostic.Id == "AD0001";
            count += Reports(diagnostic.Id) ? 1 : 0;
        }

        var path = crashed || _analyzers.Length != 1 ? "unresolved" : count == 0 ? "clean" : "violating";
        MeasurementEvents.Instance.AnalysisStop(_window, path, count);
        _window = 0;
    }

    private static bool Reports(string id)
    {
        foreach (var analyzer in _analyzers)
        {
            foreach (var descriptor in analyzer.SupportedDiagnostics)
            {
                if (descriptor.Id == id)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
