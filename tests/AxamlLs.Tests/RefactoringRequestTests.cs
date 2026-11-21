using System.Text.Json.Nodes;
using AxamlLs.Lsp;
using AxamlLs.Telemetry;

namespace AxamlLs.Tests;

/// <summary>Refactorings through the server: listed cheaply, edited on resolve (features plan F10).</summary>
public class RefactoringRequestTests
{
    private const string View = "<Grid xmlns=\"https://github.com/avaloniaui\">\n  <Button Content=\"Save\" Width=\"80\"/>\n</Grid>";

    private static readonly JsonObject Resolving = new()
    {
        ["textDocument"] = new JsonObject
        {
            ["codeAction"] = new JsonObject { ["resolveSupport"] = new JsonObject { ["properties"] = new JsonArray("edit") } },
        },
    };

    private static async Task<(LspTestClient Client, string Uri, JsonObject Sort)> Open(ServerMetrics metrics, JsonObject? capabilities)
    {
        var client = new LspTestClient(metrics);
        var uri = Protocol.PathToUri(Path.Combine(Path.GetTempPath(), "axaml-ls-refactor-" + Guid.NewGuid().ToString("N"), "View.axaml"));
        await client.OpenAsync(uri, View, capabilities);
        var position = new JsonObject { ["line"] = 1, ["character"] = 4 };
        var actions = (await client.RequestAsync("textDocument/codeAction", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri },
            ["range"] = new JsonObject { ["start"] = position, ["end"] = position.DeepClone() },
            ["context"] = new JsonObject { ["diagnostics"] = new JsonArray(), ["only"] = new JsonArray("refactor") },
        }))["result"]!.AsArray();
        Assert.All(actions, a => Assert.StartsWith("refactor", (string?)a!["kind"]));
        return (client, uri, actions.Single(a => (string?)a!["title"] == "Sort attributes")!.AsObject());
    }

    [Fact]
    public async Task Listed_with_data_then_resolved_to_an_edit()
    {
        using var metrics = new ServerMetrics();
        var (client, uri, sort) = await Open(metrics, Resolving.DeepClone().AsObject());
        await using var _ = client;
        Assert.Null(sort["edit"]);
        Assert.NotNull(sort["data"]);

        var resolved = (await client.RequestAsync("codeAction/resolve", sort.DeepClone()))["result"]!;
        var edit = resolved["edit"]!["changes"]![uri]!.AsArray().Single()!;
        Assert.Equal("Width=\"80\" Content=\"Save\"", (string?)edit["newText"]);

        await client.NotifyAsync("textDocument/didChange", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri, ["version"] = 2 },
            ["contentChanges"] = new JsonArray(new JsonObject { ["text"] = View + "\n" }),
        });
        var stale = await client.RequestAsync("codeAction/resolve", sort.DeepClone());
        Assert.Equal(-32801, (int?)stale["error"]?["code"]);
    }

    [Fact]
    public async Task Editors_that_cannot_resolve_get_the_edit_in_the_list()
    {
        using var metrics = new ServerMetrics();
        var (client, uri, sort) = await Open(metrics, capabilities: null);
        await using var _ = client;
        Assert.NotNull(sort["edit"]!["changes"]![uri]);
    }
}
