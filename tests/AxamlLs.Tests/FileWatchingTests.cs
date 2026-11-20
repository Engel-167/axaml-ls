using System.Text.Json.Nodes;
using AxamlLs.Lsp;
using AxamlLs.Telemetry;
using AxamlLs.Workspace;
using Microsoft.Extensions.Time.Testing;

namespace AxamlLs.Tests;

/// <summary>File changes from the editor (performance plan P1), and the fallbacks when the editor can't or won't send them.</summary>
public class FileWatchingTests
{
    private static readonly ClientCapabilities Relative = new("test", WorkDoneProgress: false, WatchedFilesRegistration: true, RelativePatterns: true);

    /// <summary>A registration call the editor answers at once, with a result or with an error.</summary>
    private static Func<string, JsonObject, CancellationToken, Task<JsonObject>> Answer(bool accept = true, List<JsonObject>? sent = null) =>
        (method, parameters, _) =>
        {
            sent?.Add(parameters);
            return Task.FromResult(accept
                ? new JsonObject { ["id"] = "x", ["result"] = null }
                : new JsonObject { ["id"] = "x", ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "no" } });
        };

    private static JsonObject Notification(params (string Path, int Type)[] changes) => new()
    {
        ["changes"] = new JsonArray([.. changes.Select(c => (JsonNode)new JsonObject { ["uri"] = Protocol.PathToUri(c.Path), ["type"] = c.Type })]),
    };

    private static string TempFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "axaml-ls-watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Views"));
        return root;
    }

    [Fact]
    public void Registration_uses_globs_or_relative_patterns()
    {
        var global = EditorFileWatcher.RegistrationParams("axaml-watch", baseUri: null)["registrations"]![0]!;
        Assert.Equal("workspace/didChangeWatchedFiles", (string?)global["method"]);
        var watchers = global["registerOptions"]!["watchers"]!.AsArray();
        Assert.Equal("**/*.{axaml,xaml,cs,csproj,props,targets}", (string?)watchers[0]!["globPattern"]);
        Assert.Equal(7, (int?)watchers[0]!["kind"]);
        Assert.Equal(5, (int?)watchers[1]!["kind"]); // assets: created and deleted only

        var relative = EditorFileWatcher.RegistrationParams("axaml-watch-1", "file:///p")["registrations"]![0]!["registerOptions"]!["watchers"]![0]!["globPattern"]!;
        Assert.Equal("file:///p", (string?)relative["baseUri"]);
        Assert.Equal("**/*.{axaml,xaml,cs,csproj,props,targets}", (string?)relative["pattern"]);
    }

    [Fact]
    public void Notification_types_map_to_changes()
    {
        var root = Path.Combine(Path.GetTempPath(), "p");
        var parameters = Notification((Path.Combine(root, "A.axaml"), 1), (Path.Combine(root, "B.cs"), 2), (Path.Combine(root, "C.svg"), 3), (Path.Combine(root, "D.cs"), 9));
        parameters["changes"]!.AsArray().Add(new JsonObject { ["uri"] = "untitled:Untitled-1", ["type"] = 1 });
        Assert.Equal(
            [new FileChange(Path.Combine(root, "A.axaml"), FileChangeKind.Created), new FileChange(Path.Combine(root, "B.cs"), FileChangeKind.Changed), new FileChange(Path.Combine(root, "C.svg"), FileChangeKind.Deleted)],
            EditorFileWatcher.Changes(parameters));
    }

    [Fact]
    public void A_project_gets_only_its_own_changes_outside_pruned_folders()
    {
        var root = TempFolder();
        var sent = new List<JsonObject>();
        var watcher = new EditorFileWatcher(Answer(sent: sent), () => Relative, new FakeTimeProvider());
        using var source = watcher.SourceFor(root);
        try
        {
            Assert.True(source.Reliable);
            Assert.Equal(Protocol.PathToUri(root), (string?)sent.Single()["registrations"]![0]!["registerOptions"]!["watchers"]![0]!["globPattern"]!["baseUri"]);
            var seen = new List<FileChange>();
            source.Changed += seen.AddRange;
            var view = Path.Combine(root, "Views", "A.axaml");
            watcher.OnDidChangeWatchedFiles(Notification((view, 2), (Path.Combine(root, "obj", "Debug", "X.cs"), 1), (Path.Combine(root + "-other", "B.cs"), 1)));
            Assert.Equal([new FileChange(view, FileChangeKind.Changed)], seen);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_refused_registration_falls_back_to_a_file_system_watcher()
    {
        var root = TempFolder();
        var watcher = new EditorFileWatcher(Answer(accept: false), () => Relative, new FakeTimeProvider());
        using var source = watcher.SourceFor(root);
        try
        {
            Assert.True(source.Reliable); // the fallback watcher is
            var seen = new List<FileChange>();
            source.Changed += seen.AddRange;
            watcher.OnDidChangeWatchedFiles(Notification((Path.Combine(root, "Views", "A.axaml"), 2)));
            Assert.Empty(seen); // the editor's events no longer count
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task An_unanswered_registration_times_out_into_the_fallback()
    {
        var root = TempFolder();
        var time = new FakeTimeProvider();
        var watcher = new EditorFileWatcher((_, _, ct) => new TaskCompletionSource<JsonObject>().Task.WaitAsync(ct), () => Relative, time);
        using var source = watcher.SourceFor(root);
        var switched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Overflowed += () => switched.TrySetResult();
        try
        {
            Assert.False(source.Reliable); // pending: the file index refreshes every second meanwhile
            time.Advance(EditorFileWatcher.RegistrationTimeout);
            await switched.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(source.Reliable);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void An_editor_that_stays_silent_after_the_server_writes_a_file_is_replaced()
    {
        var root = TempFolder();
        var time = new FakeTimeProvider();
        var watcher = new EditorFileWatcher(Answer(), () => Relative, time);
        using var source = watcher.SourceFor(root);
        var switches = 0;
        source.Overflowed += () => switches++;
        var seen = new List<FileChange>();
        source.Changed += seen.AddRange;
        var view = Path.Combine(root, "Views", "A.axaml");
        try
        {
            // Reported in time: nothing happens.
            watcher.ExpectChange(view);
            watcher.OnDidChangeWatchedFiles(Notification((view, 2)));
            time.Advance(EditorFileWatcher.SilenceLimit);
            Assert.Equal(0, switches);
            Assert.Single(seen);

            // Never reported: the folder stops relying on the editor.
            watcher.ExpectChange(view);
            time.Advance(EditorFileWatcher.SilenceLimit);
            Assert.Equal(1, switches);
            watcher.OnDidChangeWatchedFiles(Notification((view, 2)));
            Assert.Single(seen);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Without_dynamic_registration_a_file_system_watcher_is_used()
    {
        var root = TempFolder();
        var watcher = new EditorFileWatcher(Answer(), () => new ClientCapabilities("test", WorkDoneProgress: false));
        using var source = watcher.SourceFor(root);
        Assert.IsType<FileSystemChangeSource>(source);
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void Content_changes_keep_the_listing_and_structural_ones_relist()
    {
        var root = PerformanceInfrastructureTests.MakeFolder();
        var changes = new TestFileChangeSource();
        using var index = new FileIndex(root, changes, new FakeTimeProvider());
        try
        {
            Assert.Single(index.WithExtensions(".axaml"));
            var added = Path.Combine(root, "Views", "New.axaml");
            File.WriteAllText(added, "<New/>");
            changes.Raise(new FileChange(Path.Combine(root, "Views", "A.axaml"), FileChangeKind.Changed));
            Assert.False(index.Contains(added)); // a content change does not relist
            changes.Raise(new FileChange(added, FileChangeKind.Created));
            Assert.True(index.Contains(added));
            File.Delete(added);
            changes.Raise(new FileChange(added, FileChangeKind.Deleted));
            Assert.False(index.Contains(added));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task The_server_registers_workspace_watchers_after_initialized()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "axaml-ls-reg-" + Guid.NewGuid().ToString("N")));
        using var metrics = new ServerMetrics();
        try
        {
            await using var client = new LspTestClient(metrics);
            var capabilities = new JsonObject { ["workspace"] = new JsonObject { ["didChangeWatchedFiles"] = new JsonObject { ["dynamicRegistration"] = true } } };
            await client.OpenAsync(Protocol.PathToUri(Path.Combine(dir.FullName, "View.axaml")), "<Grid/>", capabilities);
            // A request after didOpen: by its answer, the read loop has handled initialized and didOpen.
            await client.RequestAsync("textDocument/foldingRange", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = Protocol.PathToUri(Path.Combine(dir.FullName, "View.axaml")) } });
            var registration = Assert.Single(client.ServerRequests, r => (string?)r["method"] == "client/registerCapability");
            Assert.Equal("**/*.{axaml,xaml,cs,csproj,props,targets}", (string?)registration["params"]!["registrations"]![0]!["registerOptions"]!["watchers"]![0]!["globPattern"]);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
