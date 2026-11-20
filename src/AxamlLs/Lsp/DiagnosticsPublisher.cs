using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using AxamlLs.Analysis;
using AxamlLs.Telemetry;
using AxamlLs.Workspace;

namespace AxamlLs.Lsp;

/// <summary>
/// Pushes diagnostics for open documents: debounced after edits, re-run when the project changes, skipped when nothing
/// another document depends on changed, and not re-sent when identical to the last publish.
/// </summary>
internal sealed class DiagnosticsPublisher(Documents documents, LspClient client, ServerMetrics metrics)
{
    /// <summary>What other files' diagnostics depend on, per project folder: style facts and the set of resource keys.</summary>
    private sealed record CrossFileFacts(ProjectStyles? Styles, HashSet<string>? Keys);

    /// <summary>Delay after an edit before analysing, so typing doesn't analyse every intermediate text.</summary>
    public const int EditDelayMs = 150;
    private const int OthersDelayMs = 400;

    private readonly ConcurrentDictionary<string, CrossFileFacts> _crossFileFacts = new(StringComparer.Ordinal);

    /// <summary>
    /// Analyses a document after <paramref name="delayMs"/> and publishes its diagnostics; a newer schedule of the same
    /// document cancels this one. With <paramref name="notifyOthers"/>, the other open documents are re-checked too when
    /// this one changed something they depend on.
    /// </summary>
    public void Schedule(OpenDocument doc, int delayMs, bool notifyOthers = false)
    {
        CancellationTokenSource cts;
        lock (doc)
        {
            doc.Pending?.Cancel();
            doc.Pending = cts = new CancellationTokenSource();
        }
        _ = Task.Run(async () =>
        {
            try
            {
                if (delayMs > 0) await Task.Delay(delayMs, cts.Token).ConfigureAwait(false);
                var analysis = documents.Analyze(doc);
                if (cts.IsCancellationRequested) return;
                var diagnostics = new JsonArray();
                var start = metrics.Timestamp();
                foreach (var (d, unnecessary) in DocumentDiagnostics.Collect(analysis.Analyzer))
                    diagnostics.Add(Protocol.Diagnostic(d, analysis.Lines, unnecessary));
                metrics.AnalysisPhaseSince(ServerMetrics.Phase.Diagnostics, start);
                if (cts.IsCancellationRequested) return;
                if (ShouldPublish(doc, diagnostics, analysis.Analyzer.Project))
                    await client.PublishDiagnosticsAsync(doc.Uri, analysis.Version, diagnostics).ConfigureAwait(false);
                if (notifyOthers && CrossFileFactsChanged(doc, analysis.Analyzer)) ScheduleOthers(doc);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log.Error($"Analysis of {doc.Path} failed", ex);
            }
        });
    }

    /// <summary>Re-checks the open documents of a project whose code changed.</summary>
    public void OnProjectUpdated(ProjectHost host)
    {
        foreach (var doc in documents.All)
        {
            if (documents.HostFor(doc, startLoading: false) == host) Schedule(doc, delayMs: 0);
        }
    }

    /// <summary>Clears a closed document's diagnostics in the editor.</summary>
    public Task ClearAsync(string uri) => client.PublishDiagnosticsAsync(uri, null, []);

    /// <summary>
    /// False when the list is the one last sent, for the same project snapshot (another file's edit, a .cs save that
    /// didn't matter). A new snapshot always publishes once, so the first result after loading is confirmed.
    /// </summary>
    private static bool ShouldPublish(OpenDocument doc, JsonArray diagnostics, ProjectSnapshot? project)
    {
        var json = diagnostics.ToJsonString();
        lock (doc)
        {
            if (doc.LastPublished is { } last && last.Json == json && ReferenceEquals(last.Project, project)) return false;
            doc.LastPublished = (json, project);
            return true;
        }
    }

    /// <summary>
    /// Whether this document's edit changed something other files' diagnostics read: classes, selectors, template parts,
    /// keys set in code (one <see cref="ProjectStyles"/> object while those stay the same) or the set of resource keys.
    /// Typing inside a value or moving lines changes neither, so the other open files are not re-checked.
    /// </summary>
    private bool CrossFileFactsChanged(OpenDocument doc, XamlAnalyzer analyzer)
    {
        var root = analyzer.ProjectDirectory ?? Path.GetDirectoryName(doc.Path) ?? "";
        var styles = analyzer.StyleProvider?.Invoke();
        var keys = analyzer.ResourceProvider?.Invoke() is { } resources ? resources.Select(r => r.Key).ToHashSet(StringComparer.Ordinal) : null;
        var before = _crossFileFacts.GetValueOrDefault(root);
        _crossFileFacts[root] = new CrossFileFacts(styles, keys);
        if (before == null) return true;
        return !ReferenceEquals(before.Styles, styles) ||
               (before.Keys == null) != (keys == null) || before.Keys != null && !before.Keys.SetEquals(keys!);
    }

    private void ScheduleOthers(OpenDocument changed)
    {
        foreach (var other in documents.All)
        {
            if (other != changed) Schedule(other, OthersDelayMs);
        }
    }
}
