using AxamlLs.Telemetry;

namespace AxamlLs.Workspace;

/// <summary>
/// One project folder's file index and the facts derived from its files (x:Key resources, style classes and template
/// parts), owned by <see cref="WorkspaceManager"/>: one instance per folder, not shared statics, so tests and
/// projects never see each other's caches.
/// <list type="bullet">
/// <item>Per-file facts are cached by <see cref="FileFactsCache{T}"/>: a file is re-read when the index saw a change
/// and its timestamp moved, an open buffer when its text changed.</item>
/// <item>The merged results are rebuilt only when a file's facts changed, and are otherwise the same object, so
/// callers can compare them by reference to see whether anything another document depends on changed.</item>
/// </list>
/// </summary>
public sealed class ProjectIndexes : IDisposable
{
    /// <summary>The per-file parts a merged result was built from, and that result. A class: published with one write.</summary>
    private sealed record Merged<TPart, TResult>(TPart[] Parts, TResult Result);

    private readonly FileFactsCache<List<ResourceEntry>> _resourceFacts;
    private readonly FileFactsCache<ClassIndex.FileFacts> _styleFacts;
    private readonly FileFactsCache<FileWords> _words;
    private readonly FileFactsCache<CodeLiteralFacts> _codeLiterals;
    private readonly ServerMetrics? _metrics;
    private Merged<List<ResourceEntry>, List<ResourceEntry>>? _resources;
    private Merged<ClassIndex.FileFacts, ProjectStyles>? _styles;

    /// <summary>A folder watched with a <see cref="FileSystemWatcher"/>.</summary>
    /// <param name="root">The project folder.</param>
    /// <param name="metrics">Counts files scanned and indexes rebuilt, when given.</param>
    public ProjectIndexes(string root, ServerMetrics? metrics = null)
        : this(Normalize(root), changes: null, TimeProvider.System, metrics)
    {
    }

    /// <param name="root">The project folder.</param>
    /// <param name="changes">Reports file changes below the folder (a watcher when null); the indexes dispose it.</param>
    /// <param name="time">The clock for the file index's fallback refresh.</param>
    /// <param name="metrics">Counts files scanned and indexes rebuilt, when given.</param>
    internal ProjectIndexes(string root, IFileChangeSource? changes, TimeProvider time, ServerMetrics? metrics)
    {
        root = Normalize(root);
        Files = new FileIndex(root, changes ?? new FileSystemChangeSource(root), time);
        _metrics = metrics;
        _resourceFacts = new(ResourceIndex.Scan, ResourceIndex.SameEntries, metrics);
        _styleFacts = new(ClassIndex.ScanFile, (a, b) => a.SameAs(b), metrics);
        _words = new((_, text) => FileWords.Scan(text), (a, b) => a.SameAs(b), metrics);
        _codeLiterals = new(CodeLiteralFacts.Scan, (a, b) => a.SameAs(b), metrics);
    }

    public string Root => Files.Root;

    public FileIndex Files { get; }

    /// <summary>
    /// Every x:Key in the folder's .axaml files; open buffers win over the disk, so a key just typed is offered at once.
    /// The same list object while no file's keys changed.
    /// </summary>
    public List<ResourceEntry> Resources(IReadOnlyDictionary<string, string> openBuffers)
    {
        var parts = _resourceFacts.Collect(Files, Files.WithExtensions(".axaml"), openBuffers, ".axaml");
        var last = Volatile.Read(ref _resources);
        if (last != null && FileFactsCache<List<ResourceEntry>>.SameObjects(last.Parts, parts)) return last.Result;
        var merged = new List<ResourceEntry>(parts.Sum(p => p.Count));
        foreach (var p in parts) merged.AddRange(p);
        Volatile.Write(ref _resources, new(parts, merged));
        _metrics?.IndexRebuilt("resources");
        return merged;
    }

    /// <summary>
    /// Style classes, template parts and code-set keys of the folder's .axaml and .cs files. The same object while no
    /// file's facts changed (typing inside a value, moving lines).
    /// </summary>
    public ProjectStyles Styles(IReadOnlyDictionary<string, string> openBuffers)
    {
        var parts = _styleFacts.Collect(Files, Files.WithExtensions(".axaml", ".cs"), openBuffers, ".axaml", ".cs");
        var last = Volatile.Read(ref _styles);
        if (last != null && FileFactsCache<ClassIndex.FileFacts>.SameObjects(last.Parts, parts)) return last.Result;
        var merged = ClassIndex.Merge(parts);
        Volatile.Write(ref _styles, new(parts, merged));
        _metrics?.IndexRebuilt("styles");
        return merged;
    }

    /// <summary>
    /// Whether the file at <paramref name="path"/> may mention one of <paramref name="names"/>: false only when its
    /// words (cached per file) prove it cannot, so the caller can skip analysing it.
    /// </summary>
    public bool MayMention(string path, IReadOnlyDictionary<string, string> openBuffers, IReadOnlyCollection<string> names) =>
        _words.Get(Files, path, openBuffers)?.MayMention(names) ?? true;

    /// <summary>The style classes and resource keys named by string literals in the folder's .cs files (cached per file).</summary>
    public IReadOnlyList<CodeLiteralFacts> CodeLiterals(IReadOnlyDictionary<string, string> openBuffers) =>
        _codeLiterals.Collect(Files, Files.WithExtensions(".cs"), openBuffers, ".cs");

    private static string Normalize(string root) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    public void Dispose() => Files.Dispose();
}

/// <summary>
/// What an analysis may ask about the rest of the project, given when the analyzer is created. Null members mean
/// "this document only" (tests, the <c>check</c> command, a file outside any project).
/// </summary>
/// <param name="ProjectDirectory">The project's folder, for asset paths; null outside a project.</param>
/// <param name="DocumentPath">The analyzed file.</param>
/// <param name="Resources">The project's x:Key resources.</param>
/// <param name="Styles">The project's style classes and template parts.</param>
/// <param name="Files">Every analyzed .axaml file of the project.</param>
/// <param name="FileIndex">The folder's file list, for asset completion and existence checks.</param>
/// <param name="CodeLiterals">Style classes and resource keys named in the project's C# string literals.</param>
public sealed record ProjectContext(
    string? ProjectDirectory,
    string? DocumentPath,
    Func<IReadOnlyList<ResourceEntry>>? Resources = null,
    Func<ProjectStyles>? Styles = null,
    Func<IReadOnlyList<XamlFile>>? Files = null,
    FileIndex? FileIndex = null,
    Func<IReadOnlyList<CodeLiteralFacts>>? CodeLiterals = null)
{
    public static readonly ProjectContext None = new(null, null);

    /// <summary>
    /// The context of a document in a project folder: the same for open documents and project scans, so they cannot
    /// drift apart.
    /// </summary>
    /// <param name="path">The analyzed file.</param>
    /// <param name="host">Its project, or null for a file outside any project.</param>
    /// <param name="indexes">The indexes of the project folder (or of the file's folder outside a project).</param>
    /// <param name="openBuffers">Unsaved editor contents by path.</param>
    /// <param name="files">Every analyzed .axaml file of the project, when the caller has them.</param>
    public static ProjectContext For(string path, ProjectHost? host, ProjectIndexes indexes,
        Func<IReadOnlyDictionary<string, string>> openBuffers, Func<IReadOnlyList<XamlFile>>? files = null) =>
        new(host?.ProjectDirectory, path, () => indexes.Resources(openBuffers()), () => indexes.Styles(openBuffers()), files, indexes.Files,
            host == null ? null : () => indexes.CodeLiterals(openBuffers()));
}
