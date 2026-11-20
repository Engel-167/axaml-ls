using System.Collections.Concurrent;
using AxamlLs.Analysis;
using AxamlLs.Telemetry;
using AxamlLs.Text;
using AxamlLs.Workspace;
using AxamlLs.Xaml;

namespace AxamlLs.Lsp;

/// <summary>One analysis of a document version. A class, so it is published to other threads with one reference write.</summary>
internal sealed record DocumentAnalysis(int Version, ProjectSnapshot? Project, XamlAnalyzer Analyzer, LineIndex Lines);

/// <summary>An .axaml file the editor has open, with its unsaved text.</summary>
internal sealed class OpenDocument
{
    private DocumentAnalysis? _cached;

    public required string Uri { get; init; }
    public required string Path { get; init; }

    /// <summary>Text and Version change together under <c>lock (doc)</c>.</summary>
    public string Text { get; set; } = "";
    public int Version { get; set; }

    /// <summary>The scheduled diagnostics run, cancelled by the next one (under <c>lock (doc)</c>).</summary>
    public CancellationTokenSource? Pending { get; set; }

    /// <summary>
    /// The last analysis. Valid while its <see cref="DocumentAnalysis.Version"/> equals <see cref="Version"/> and its project
    /// snapshot is the project's current one.
    /// </summary>
    public DocumentAnalysis? Cached { get => Volatile.Read(ref _cached); set => Volatile.Write(ref _cached, value); }

    /// <summary>Held while analyzing, so concurrent requests for the same version wait for one analysis instead of each running it.</summary>
    public object AnalyzeLock { get; } = new();

    /// <summary>The diagnostics last sent and the project snapshot they were computed with (under <c>lock (doc)</c>).</summary>
    public (string Json, ProjectSnapshot? Project)? LastPublished { get; set; }
}

/// <summary>
/// The open documents and their analyses. Lock order: <see cref="OpenDocument.AnalyzeLock"/> before <c>lock (doc)</c>;
/// a document's own lock is a leaf (nothing else is taken while holding it).
/// </summary>
internal sealed class Documents(WorkspaceManager workspace, ProjectLoadProgress progress, ServerMetrics metrics)
{
    private readonly ConcurrentDictionary<string, OpenDocument> _open = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<ProjectHost, ProjectXaml> _projectXaml = new();

    public IEnumerable<OpenDocument> All => _open.Values;

    public bool TryGet(string uri, out OpenDocument doc) => _open.TryGetValue(uri, out doc!);

    public OpenDocument Open(string uri, string text, int version)
    {
        var doc = new OpenDocument { Uri = uri, Path = Protocol.UriToPath(uri), Text = text, Version = version };
        _open[uri] = doc;
        return doc;
    }

    /// <summary>Applies an edit to an open document; null when the document isn't open.</summary>
    public OpenDocument? Change(string uri, int? version, Func<string, string> edit)
    {
        if (!_open.TryGetValue(uri, out var doc)) return null;
        lock (doc)
        {
            doc.Version = version ?? doc.Version + 1;
            doc.Text = edit(doc.Text);
        }
        return doc;
    }

    public OpenDocument? Close(string uri)
    {
        if (!_open.TryRemove(uri, out var doc)) return null;
        lock (doc) doc.Pending?.Cancel();
        return doc;
    }

    /// <summary>The project a document belongs to (loading it, unless <paramref name="startLoading"/> is false).</summary>
    public ProjectHost? HostFor(OpenDocument doc, bool startLoading = true) => workspace.GetHostFor(doc.Path, startLoading);

    /// <summary>The document analyzed at its current version against its project's current snapshot (cached).</summary>
    public DocumentAnalysis Analyze(OpenDocument doc)
    {
        var host = HostFor(doc);
        if (host != null) progress.Announce(host);
        var project = host?.Current;
        // Fast path without the lock: this version was already analyzed.
        var cached = doc.Cached;
        int current;
        lock (doc) current = doc.Version;
        if (cached != null && cached.Version == current && ReferenceEquals(cached.Project, project)) return cached;

        lock (doc.AnalyzeLock)
        {
            string text;
            int version;
            lock (doc)
            {
                text = doc.Text;
                version = doc.Version;
            }
            cached = doc.Cached; // someone may have finished it while we waited
            if (cached != null && cached.Version == version && ReferenceEquals(cached.Project, project)) return cached;

            var indexes = host?.Indexes ?? workspace.IndexesFor(System.IO.Path.GetDirectoryName(doc.Path)!);
            var context = ProjectContext.For(doc.Path, host, indexes, OpenBuffers, host == null ? null : () => ProjectFiles(host));
            var analyzer = XamlAnalyzer.Analyze(text, project, host?.CompiledBindingsDefault ?? false, context, metrics);
            var analysis = new DocumentAnalysis(version, project, analyzer, new LineIndex(text));
            doc.Cached = analysis;
            Log.Debug($"Analyzed {System.IO.Path.GetFileName(doc.Path)} v{version}: {analyzer.Result.Diagnostics.Count} diagnostics (project {(project == null ? "not loaded" : "loaded")})");
            return analysis;
        }
    }

    /// <summary>The current text, its line index and parse tree: from the last analysis when it is of this version, else freshly made.</summary>
    public static (string Text, LineIndex Lines, XmlDoc? Doc) CurrentText(OpenDocument doc)
    {
        string text;
        int version;
        lock (doc)
        {
            text = doc.Text;
            version = doc.Version;
        }
        if (doc.Cached is { } c && c.Version == version) return (text, c.Lines, c.Analyzer.Result.Doc);
        return (text, new LineIndex(text), null);
    }

    /// <summary>Unsaved editor contents by file path (they win over the disk when indexing the project).</summary>
    public IReadOnlyDictionary<string, string> OpenBuffers()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var d in _open.Values)
        {
            lock (d) map[d.Path] = d.Text;
        }
        return map;
    }

    /// <summary>Every .axaml file of a project, analyzed, with open buffers winning over the disk.</summary>
    public IReadOnlyList<XamlFile> ProjectFiles(ProjectHost host, IReadOnlyDictionary<string, string>? buffers = null) =>
        _projectXaml.GetOrAdd(host, _ => new ProjectXaml()).Files(host, buffers ?? OpenBuffers());

    /// <summary>
    /// Text of a file as edits and locations must see it: the open buffer if there is one, else the disk.
    /// Each file is read at most once per provider, so use one provider per request.
    /// </summary>
    public Func<string, (string Text, LineIndex Lines)> TextProvider()
    {
        var open = OpenBuffers();
        var cache = new Dictionary<string, (string, LineIndex)>(StringComparer.Ordinal);
        return path =>
        {
            if (cache.TryGetValue(path, out var hit)) return hit;
            var text = open.TryGetValue(path, out var t) ? t : File.ReadAllText(path);
            return cache[path] = (text, new LineIndex(text));
        };
    }
}
