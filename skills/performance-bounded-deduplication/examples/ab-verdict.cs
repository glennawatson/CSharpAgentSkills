// Decides an A-B-B-A comparison of two sweep reports per order and prints only breaches that reproduce in BOTH.
//
// Input TSVs (one per build per order) need: SUBJECT, PATH, TICKS, SAMPLED_BYTES, ITERATIONS, MEAN_MS, ERROR_MS.
//   r1 = baseline run first, then candidate;  r2 = candidate first, then baseline.
// Bytes per iteration: resolved "more"/"less" only when both runs have >= 5 GC ticks and the runs still differ
// after widening each by 2 ticks (a tick is a ~100 KB sample, so fewer resolves nothing).
// Time: resolved "slower"/"faster" only when the mean +/- error intervals do not overlap.
// A breach is a resolved worsening beyond the limit. It counts only when the same subject and path breach the
// same metric in both orders; a one-order breach is "unresolved" and needs a longer confirmation run.
// Usage: dotnet run ab-verdict.cs -- <r1-base.tsv> <r1-cand.tsv> <r2-cand.tsv> <r2-base.tsv> [--alloc-limit 5] [--time-limit 10]

using System.Globalization;

var allocLimit = Option("--alloc-limit", 5);
var timeLimit = Option("--time-limit", 10);
var r1 = Compare(Load(args[0]), Load(args[1]));
var r2 = Compare(Load(args[3]), Load(args[2]));

foreach (var (label, rows) in new[] { ("r1", r1), ("r2", r2) })
{
    foreach (var group in rows.Values.GroupBy(r => r.Path).OrderBy(g => g.Key, StringComparer.Ordinal))
    {
        var before = group.Sum(r => r.Before.BytesPerIteration);
        var after = group.Sum(r => r.After.BytesPerIteration);
        Console.WriteLine($"== {group.Key} {label}: bytes per iteration, summed: {before:N0} -> {after:N0} ({Percent(before, after):+0.0;-0.0;0.0}%)");
    }
}

var reproduced = 0;
foreach (var (key, first) in r1)
{
    if (!r2.TryGetValue(key, out var second))
    {
        continue;
    }

    var bytes = first.BytesBreach(allocLimit) && second.BytesBreach(allocLimit);
    var time = first.TimeBreach(timeLimit) && second.TimeBreach(timeLimit);
    if (!bytes && !time)
    {
        continue;
    }

    reproduced++;
    Console.WriteLine($"   REPRODUCED {(bytes ? "bytes " : string.Empty)}{(time ? "time " : string.Empty)}{key}: "
        + $"bytes {first.BytesPercent:+0.0;-0.0}% / {second.BytesPercent:+0.0;-0.0}%; time {first.TimePercent:+0.0;-0.0}% / {second.TimePercent:+0.0;-0.0}%");
}

Console.WriteLine($"{reproduced} reproduced breach(es)");
return 0;

double Option(string name, double fallback)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 ? double.Parse(args[index + 1], CultureInfo.InvariantCulture) : fallback;
}

static double Percent(double before, double after) => before == 0 ? 0 : 100 * (after - before) / before;

static Dictionary<string, Sample> Load(string path)
{
    var lines = File.ReadAllLines(path);
    var header = lines[0].Split('\t');
    int Column(string name) => Array.IndexOf(header, name);
    var map = new Dictionary<string, Sample>(StringComparer.Ordinal);
    foreach (var line in lines.Skip(1))
    {
        var f = line.Split('\t');
        var iterations = double.Parse(f[Column("ITERATIONS")], CultureInfo.InvariantCulture);
        map[f[Column("SUBJECT")] + "|" + f[Column("PATH")]] = new Sample(
            f[Column("PATH")],
            double.Parse(f[Column("SAMPLED_BYTES")], CultureInfo.InvariantCulture) / iterations,
            double.Parse(f[Column("TICKS")], CultureInfo.InvariantCulture),
            double.Parse(f[Column("MEAN_MS")], CultureInfo.InvariantCulture),
            double.Parse(f[Column("ERROR_MS")], CultureInfo.InvariantCulture));
    }

    return map;
}

static Dictionary<string, Row> Compare(Dictionary<string, Sample> before, Dictionary<string, Sample> after)
{
    var rows = new Dictionary<string, Row>(StringComparer.Ordinal);
    foreach (var (key, b) in before)
    {
        if (after.TryGetValue(key, out var a))
        {
            rows[key] = new Row(b.Path, b, a);
        }
    }

    return rows;
}

internal sealed record Sample(string Path, double BytesPerIteration, double Ticks, double MeanMs, double ErrorMs);

internal sealed record Row(string Path, Sample Before, Sample After)
{
    private const double TickSlack = 2;
    private const double MinimumTicks = 5;

    public double BytesPercent => Before.BytesPerIteration == 0 ? 0 : 100 * (After.BytesPerIteration - Before.BytesPerIteration) / Before.BytesPerIteration;

    public double TimePercent => Before.MeanMs == 0 ? 0 : 100 * (After.MeanMs - Before.MeanMs) / Before.MeanMs;

    public bool BytesBreach(double limit) =>
        BytesPercent > limit
        && Before.Ticks >= MinimumTicks
        && After.Ticks >= MinimumTicks
        && After.BytesPerIteration * (1 - (TickSlack / After.Ticks)) > Before.BytesPerIteration * (1 + (TickSlack / Before.Ticks));

    public bool TimeBreach(double limit) =>
        TimePercent > limit && After.MeanMs - After.ErrorMs > Before.MeanMs + Before.ErrorMs;
}
