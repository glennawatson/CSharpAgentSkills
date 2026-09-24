// Marker events that bracket one test body and one analyzer run inside it. The EventPipe session subscribes to
// this source; the slicer pairs start/stop by payload and attributes GC allocation ticks that fall between them.
// Compiled into the test project by measure-tests.targets - no change to the test sources.

using System.Diagnostics.Tracing;

namespace AnalyzerMeasurement;

/// <summary>Test and analysis window markers.</summary>
[EventSource(Name = "AnalyzerMeasurement-Markers")]
public sealed class MeasurementEvents : EventSource
{
    /// <summary>The single instance the hooks write through.</summary>
    public static readonly MeasurementEvents Instance = new();

    /// <summary>A test body started.</summary>
    /// <param name="testId">The TUnit test id.</param>
    /// <param name="className">The test class.</param>
    /// <param name="methodName">The test method.</param>
    [Event(1, Level = EventLevel.Informational)]
    public void TestStart(string testId, string className, string methodName) => WriteEvent(1, testId, className, methodName);

    /// <summary>A test body finished.</summary>
    /// <param name="testId">The TUnit test id.</param>
    /// <param name="passed">1 when the body completed, 0 when it threw.</param>
    [Event(2, Level = EventLevel.Informational)]
    public void TestStop(string testId, int passed) => WriteEvent(2, testId, passed);

    /// <summary>An analyzer driver run started inside the current test.</summary>
    /// <param name="window">A process-unique window number.</param>
    /// <param name="testId">The owning test.</param>
    /// <param name="analyzers">The analyzer type names, pipe-separated.</param>
    [Event(3, Level = EventLevel.Informational)]
    public void AnalysisStart(int window, string testId, string analyzers) => WriteEvent(3, window, testId, analyzers);

    /// <summary>An analyzer driver run finished.</summary>
    /// <param name="window">The window number from <see cref="AnalysisStart"/>.</param>
    /// <param name="path">clean, violating or unresolved, decided from the diagnostics actually produced.</param>
    /// <param name="diagnostics">The number of diagnostics the measured analyzers reported.</param>
    [Event(4, Level = EventLevel.Informational)]
    public void AnalysisStop(int window, string path, int diagnostics) => WriteEvent(4, window, path, diagnostics);
}
