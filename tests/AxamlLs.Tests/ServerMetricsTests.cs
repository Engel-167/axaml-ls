using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json.Nodes;
using AxamlLs.Lsp;
using AxamlLs.Telemetry;

namespace AxamlLs.Tests;

/// <summary>The server reports its timings on the AxamlLs meter (what dotnet-counters shows).</summary>
public class ServerMetricsTests
{
    private sealed record Measurement(string Instrument, double Value, IReadOnlyDictionary<string, object?> Tags);

    /// <summary>Records what this test's server measures, and nothing from servers in other tests.</summary>
    private static MeterListener Listen(ServerMetrics metrics, ConcurrentQueue<Measurement> into)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (ReferenceEquals(instrument.Meter, metrics.Meter)) l.EnableMeasurementEvents(instrument);
            },
        };
        void Add(Instrument i, double v, ReadOnlySpan<KeyValuePair<string, object?>> tags) =>
            into.Enqueue(new Measurement(i.Name, v, tags.ToArray().ToDictionary(t => t.Key, t => t.Value)));
        listener.SetMeasurementEventCallback<double>((i, v, tags, _) => Add(i, v, tags));
        listener.SetMeasurementEventCallback<long>((i, v, tags, _) => Add(i, v, tags));
        listener.Start();
        return listener;
    }

    [Fact]
    public async Task Hover_reports_request_duration_and_analysis_phases()
    {
        using var metrics = new ServerMetrics();
        var seen = new ConcurrentQueue<Measurement>();
        using var listener = Listen(metrics, seen);
        // A folder without a project file: the document is analysed on its own.
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "axaml-ls-metrics-" + Guid.NewGuid().ToString("N")));
        try
        {
            var uri = Protocol.PathToUri(Path.Combine(dir.FullName, "View.axaml"));
            await using (var client = new LspTestClient(metrics))
            {
                await client.OpenAsync(uri, "<Grid xmlns=\"https://github.com/avaloniaui\"/>");
                var response = await client.RequestAsync("textDocument/hover", new JsonObject
                {
                    ["textDocument"] = new JsonObject { ["uri"] = uri },
                    ["position"] = new JsonObject { ["line"] = 0, ["character"] = 2 },
                });
                Assert.Null(response["error"]);
            }

            Assert.Contains(seen, m => m.Instrument == "request.duration" && (string?)m.Tags["method"] == "textDocument/hover" && m.Value >= 0);
            foreach (var phase in new[] { ServerMetrics.Phase.Parse, ServerMetrics.Phase.WellFormed, ServerMetrics.Phase.Resolve })
                Assert.Contains(seen, m => m.Instrument == "analysis.duration" && (string?)m.Tags["phase"] == phase);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Unknown_methods_share_one_tag_value()
    {
        using var metrics = new ServerMetrics();
        var seen = new ConcurrentQueue<Measurement>();
        using var listener = Listen(metrics, seen);
        await using (var client = new LspTestClient(metrics))
        {
            var response = await client.RequestAsync("custom/whatever", new JsonObject());
            Assert.Equal(ErrorCodes.MethodNotFound, (int?)response["error"]?["code"]);
        }
        Assert.Contains(seen, m => m.Instrument == "request.duration" && (string?)m.Tags["method"] == "unknown");
        Assert.DoesNotContain(seen, m => m.Tags.TryGetValue("method", out var v) && (string?)v == "custom/whatever");
    }
}
