using System.Text.Json.Nodes;
using AxamlLs.Analysis;
using AxamlLs.Lsp;
using AxamlLs.Lsp.Handlers;
using AxamlLs.Telemetry;
using AxamlLs.Workspace;

namespace AxamlLs.Tests;

/// <summary>Cancelled requests stop their work, and stale answers become ContentModified (performance plan P5).</summary>
[Collection("sample")]
public class CancellationTests(SampleProjectFixture sample)
{
    private static readonly Dictionary<string, string> NoBuffers = [];

    private static CancellationToken Cancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        return cts.Token;
    }

    [Fact]
    public void A_cancelled_project_scan_stops_before_the_first_file()
    {
        var xaml = new ProjectXaml();
        Assert.Throws<OperationCanceledException>(() => xaml.Files(sample.Host, NoBuffers, ct: Cancelled()));
    }

    [Fact]
    public async Task Cancelled_searches_throw_instead_of_answering()
    {
        var context = new ProjectTestContext(sample);
        var (keyTarget, file) = context.TargetAt("MainView.axaml", "{StaticResource Accent}", 18);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Targets.OccurrencesAsync(keyTarget.Target, file, context.Files, sample.Host, Cancelled()));
        var (symbolTarget, second) = context.TargetAt("Second.axaml", "{Binding Title}", 10);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Targets.OccurrencesAsync(symbolTarget.Target, second, context.Files, sample.Host, Cancelled()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Rename.ComputeAsync(symbolTarget.Target, "Heading", second, context.Files, sample.Host, Cancelled()));
    }

    [Fact]
    public async Task A_cancelled_wait_leaves_the_project_loading_for_the_next_caller()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "axaml-ls-load-" + Guid.NewGuid().ToString("N")));
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "Broken.csproj"), "<Project>");
            using var indexes = new ProjectIndexes(dir.FullName);
            using var host = new ProjectHost(Path.Combine(dir.FullName, "Broken.csproj"), indexes);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.EnsureLoadedAsync(Cancelled()));
            // The load kept going: the next caller gets the same load, finished.
            await host.EnsureLoadedAsync().WaitAsync(TimeSpan.FromSeconds(60));
            Assert.True(host.EnsureLoadedAsync().IsCompletedSuccessfully);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task An_answer_computed_while_the_document_changed_is_content_modified()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "axaml-ls-modified-" + Guid.NewGuid().ToString("N")));
        using var metrics = new ServerMetrics();
        using var workspace = new WorkspaceManager(metrics);
        using var rpc = new JsonRpc(Stream.Null, Stream.Null);
        var client = new LspClient(rpc);
        var documents = new Documents(workspace, new ProjectLoadProgress(client, new Session()), metrics);
        try
        {
            var uri = Protocol.PathToUri(Path.Combine(dir.FullName, "View.axaml"));
            documents.Open(uri, "<Grid/>", 1);
            var parameters = new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } };

            var quiet = new ContentModifiedGuard(new FakeHandler(() => { }), documents);
            Assert.Equal("done", (string?)await quiet.HandleAsync(parameters, CancellationToken.None));

            // An edit arrives while the handler computes: the answer would be for the old text.
            var edited = new ContentModifiedGuard(new FakeHandler(() => documents.Change(uri, 2, t => t + " ")), documents);
            await Assert.ThrowsAsync<ContentModifiedException>(() => edited.HandleAsync(parameters, CancellationToken.None));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private sealed class FakeHandler(Action during) : IRequestHandler
    {
        public string Method => "test/fake";

        public void Advertise(JsonObject capabilities)
        {
        }

        public Task<JsonNode?> HandleAsync(JsonObject parameters, CancellationToken ct)
        {
            during();
            return Task.FromResult<JsonNode?>("done");
        }
    }
}
