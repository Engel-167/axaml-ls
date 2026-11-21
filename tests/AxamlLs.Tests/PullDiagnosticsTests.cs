using System.Text.Json.Nodes;
using AxamlLs.Lsp;
using AxamlLs.Telemetry;

namespace AxamlLs.Tests;

/// <summary>Diagnostics the editor asks for (features plan F9), and push for editors that don't.</summary>
public sealed class PullDiagnosticsTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "axaml-ls-pull-" + Guid.NewGuid().ToString("N")));

    // Not well formed: reported with or without a project.
    private const string Broken = "<Grid xmlns=\"https://github.com/avaloniaui\"><Border></Grid>";

    private static readonly JsonObject PullCapabilities = new()
    {
        ["textDocument"] = new JsonObject { ["diagnostic"] = new JsonObject { ["dynamicRegistration"] = false } },
        ["workspace"] = new JsonObject { ["diagnostics"] = new JsonObject { ["refreshSupport"] = true } },
    };

    public void Dispose() => _dir.Delete(recursive: true);

    private string Uri(string name) => Protocol.PathToUri(Path.Combine(_dir.FullName, name));

    private static Task<JsonObject> Pull(LspTestClient client, string uri, string? previous = null) =>
        client.RequestAsync("textDocument/diagnostic", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri }, ["previousResultId"] = previous })
            .ContinueWith(t => t.Result["result"]!.AsObject(), TaskScheduler.Default);

    private static Task Change(LspTestClient client, string uri, int version, string text) =>
        client.NotifyAsync("textDocument/didChange", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri, ["version"] = version },
            ["contentChanges"] = new JsonArray(new JsonObject { ["text"] = text }),
        });

    [Fact]
    public async Task Full_then_unchanged_then_full_after_an_edit()
    {
        using var metrics = new ServerMetrics();
        await using var client = new LspTestClient(metrics);
        var uri = Uri("View.axaml");
        await client.OpenAsync(uri, Broken, PullCapabilities.DeepClone().AsObject());
        Assert.NotNull(client.InitializeResult!["capabilities"]!["diagnosticProvider"]);

        var first = await Pull(client, uri);
        Assert.Equal("full", (string?)first["kind"]);
        Assert.NotEmpty(first["items"]!.AsArray());
        var again = await Pull(client, uri, (string?)first["resultId"]);
        Assert.Equal("unchanged", (string?)again["kind"]);
        Assert.Equal((string?)first["resultId"], (string?)again["resultId"]);

        await Change(client, uri, 2, Broken.Replace("<Border>", "<Border/>", StringComparison.Ordinal));
        var fixedResult = await Pull(client, uri, (string?)first["resultId"]);
        Assert.Equal("full", (string?)fixedResult["kind"]);
        Assert.Empty(fixedResult["items"]!.AsArray());

        await Task.Delay(DiagnosticsPublisherDelay);
        Assert.DoesNotContain(client.Notifications, n => (string?)n["method"] == "textDocument/publishDiagnostics");
    }

    private const int DiagnosticsPublisherDelay = 600;

    [Fact]
    public async Task An_edit_others_depend_on_asks_the_editor_to_refresh()
    {
        using var metrics = new ServerMetrics();
        await using var client = new LspTestClient(metrics);
        var styles = Uri("Styles.axaml");
        const string withKey = "<ResourceDictionary xmlns=\"https://github.com/avaloniaui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"><SolidColorBrush x:Key=\"Accent\" Color=\"Red\"/></ResourceDictionary>";
        await client.OpenAsync(styles, withKey, PullCapabilities.DeepClone().AsObject());
        // The first edit records what other files depend on; the second changes it (a key disappears).
        await Change(client, styles, 2, withKey.Replace("Red", "Blue", StringComparison.Ordinal));
        await Task.Delay(DiagnosticsPublisherDelay);
        while (client.ServerRequests.TryDequeue(out _))
        {
        }
        await Change(client, styles, 3, withKey.Replace("Accent", "Primary", StringComparison.Ordinal));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!client.ServerRequests.Any(r => (string?)r["method"] == "workspace/diagnostic/refresh") && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.Contains(client.ServerRequests, r => (string?)r["method"] == "workspace/diagnostic/refresh");
    }

    [Fact]
    public async Task Editors_that_do_not_pull_get_pushed_diagnostics()
    {
        using var metrics = new ServerMetrics();
        await using var client = new LspTestClient(metrics);
        var uri = Uri("View.axaml");
        await client.OpenAsync(uri, Broken);
        Assert.Null(client.InitializeResult!["capabilities"]!["diagnosticProvider"]);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!client.Notifications.Any(n => (string?)n["method"] == "textDocument/publishDiagnostics") && DateTime.UtcNow < deadline) await Task.Delay(50);
        var published = client.Notifications.First(n => (string?)n["method"] == "textDocument/publishDiagnostics");
        Assert.NotEmpty(published["params"]!["diagnostics"]!.AsArray());
    }
}
