using System.Diagnostics.Metrics;

namespace AxamlLs.Telemetry;

/// <summary>
/// The server's timings and counts, on a <see cref="Meter"/> named <c>AxamlLs</c>: read them live with
/// <c>dotnet-counters monitor -p &lt;pid&gt; --counters AxamlLs</c> (no package needed; editors start the server as
/// <c>dotnet AxamlLs.dll</c>, so find it by pid, not by name). One instance per server, passed to what records into it,
/// so tests can listen to theirs alone (<see cref="Meter"/> identifies it).
/// </summary>
public sealed class ServerMetrics : IDisposable
{
    public const string MeterName = "AxamlLs";

    /// <summary>The phases of <see cref="Analysis.XamlAnalyzer.Analyze"/>, plus the cross-file diagnostics pass.</summary>
    public static class Phase
    {
        public const string Parse = "parse";
        public const string WellFormed = "wellformed";
        public const string Resolve = "resolve";
        public const string Diagnostics = "diagnostics";
    }

    private readonly TimeProvider _time;
    private readonly Histogram<double> _analysis;
    private readonly Histogram<double> _request;
    private readonly Counter<long> _cancelled;
    private readonly Counter<long> _rebuilds;
    private readonly Counter<long> _scanned;

    public ServerMetrics(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        Meter = new Meter(MeterName, typeof(ServerMetrics).Assembly.GetName().Version?.ToString(3));
        _analysis = Meter.CreateHistogram<double>("analysis.duration", "ms", "Time per analysis phase of one document");
        _request = Meter.CreateHistogram<double>("request.duration", "ms", "Time from receiving a request to having its answer");
        _cancelled = Meter.CreateCounter<long>("request.cancelled", description: "Requests answered with RequestCancelled");
        _rebuilds = Meter.CreateCounter<long>("index.rebuilds", description: "Merged project indexes rebuilt because a file's facts changed");
        _scanned = Meter.CreateCounter<long>("files.scanned", description: "Files read and scanned for facts (cache misses)");
    }

    public Meter Meter { get; }

    /// <summary>A start time for one of the <c>…Since</c> methods.</summary>
    public long Timestamp() => _time.GetTimestamp();

    public void AnalysisPhaseSince(string phase, long start) =>
        _analysis.Record(Elapsed(start), new KeyValuePair<string, object?>("phase", phase));

    public void RequestSince(string method, long start) =>
        _request.Record(Elapsed(start), new KeyValuePair<string, object?>("method", method));

    public void RequestCancelled(string method) => _cancelled.Add(1, new KeyValuePair<string, object?>("method", method));

    public void IndexRebuilt(string index) => _rebuilds.Add(1, new KeyValuePair<string, object?>("index", index));

    public void FilesScanned(int count)
    {
        if (count > 0) _scanned.Add(count);
    }

    private double Elapsed(long start) => _time.GetElapsedTime(start).TotalMilliseconds;

    public void Dispose() => Meter.Dispose();
}
