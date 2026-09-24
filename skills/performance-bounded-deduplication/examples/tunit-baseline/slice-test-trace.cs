#:package Microsoft.Diagnostics.Tracing.TraceEvent@3.2.5
#:property PublishAot=false

// Slices one TUnit measurement trace into per-test and per-analysis windows and ranks them.
// Each GCAllocationTick (~100 KB sampled) is attributed to the innermost open window on its thread's timeline:
// analysis windows inside a test, else the test body. Duration is marker stop minus start.
// Output: TSV of KIND, SUBJECT, PATH, TICKS, SAMPLED_BYTES, DURATION_MS sorted by bytes, plus a console top-N.
// Fewer than ~5 ticks resolves nothing - treat those rows as "negligible or unmeasured", never as a ranking.
// Usage: dotnet run slice-test-trace.cs -- <trace.nettrace> <out.tsv> [--top 40]

using System.Globalization;
using System.Text;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

var tracePath = args[0];
var outPath = args[1];
var topIndex = Array.IndexOf(args, "--top");
var top = topIndex >= 0 ? int.Parse(args[topIndex + 1], CultureInfo.InvariantCulture) : 40;

var windows = new List<Window>();
var openTests = new Dictionary<string, Window>(StringComparer.Ordinal);
var openAnalyses = new Dictionary<int, Window>();
Window? currentTest = null;
Window? currentAnalysis = null;

using (var source = new EventPipeEventSource(tracePath))
{
    source.Dynamic.All += data =>
    {
        if (data.ProviderName != "AnalyzerMeasurement-Markers")
        {
            return;
        }

        var at = data.TimeStampRelativeMSec;
        switch (data.EventName)
        {
            case "TestStart":
            {
                var window = new Window("test", $"{data.PayloadByName("className")}.{data.PayloadByName("methodName")}", at);
                openTests[(string)data.PayloadByName("testId")] = window;
                currentTest = window;
                break;
            }

            case "TestStop" when openTests.Remove((string)data.PayloadByName("testId"), out var test):
                test.StopMs = at;
                test.Path = (int)data.PayloadByName("passed") == 1 ? "passed" : "failed";
                windows.Add(test);
                currentTest = null;
                break;

            case "AnalysisStart":
            {
                var window = new Window("analysis", (string)data.PayloadByName("analyzers"), at);
                openAnalyses[(int)data.PayloadByName("window")] = window;
                currentAnalysis = window;
                break;
            }

            case "AnalysisStop" when openAnalyses.Remove((int)data.PayloadByName("window"), out var analysis):
                analysis.StopMs = at;
                analysis.Path = (string)data.PayloadByName("path");
                windows.Add(analysis);
                currentAnalysis = null;
                break;
        }
    };

    // Tests run one at a time (the session hook serialises them), so the open windows are the attribution target.
    source.Clr.GCAllocationTick += (GCAllocationTickTraceData tick) =>
    {
        var target = currentAnalysis ?? currentTest;
        if (target is null)
        {
            return;
        }

        target.Ticks++;
        target.Bytes += tick.AllocationAmount64;
    };

    source.Process();
}

var ordered = windows.OrderByDescending(w => w.Bytes).ToList();
var builder = new StringBuilder("KIND\tSUBJECT\tPATH\tTICKS\tSAMPLED_BYTES\tDURATION_MS\n");
foreach (var w in ordered)
{
    builder.Append(w.Kind).Append('\t').Append(w.Subject).Append('\t').Append(w.Path).Append('\t')
        .Append(w.Ticks).Append('\t').Append(w.Bytes).Append('\t')
        .Append((w.StopMs - w.StartMs).ToString("0.000", CultureInfo.InvariantCulture)).Append('\n');
}

File.WriteAllText(outPath, builder.ToString());
Console.WriteLine($"{windows.Count} windows -> {outPath}");
foreach (var w in ordered.Where(w => w.Kind == "analysis" && w.Ticks >= 5).Take(top))
{
    Console.WriteLine($"{w.Bytes,14:N0} bytes {w.Ticks,5} ticks {w.StopMs - w.StartMs,9:0.0} ms  {w.Path,-10} {w.Subject}");
}

internal sealed class Window(string kind, string subject, double startMs)
{
    public string Kind { get; } = kind;

    public string Subject { get; } = subject;

    public double StartMs { get; } = startMs;

    public double StopMs { get; set; }

    public string Path { get; set; } = string.Empty;

    public int Ticks { get; set; }

    public long Bytes { get; set; }
}
