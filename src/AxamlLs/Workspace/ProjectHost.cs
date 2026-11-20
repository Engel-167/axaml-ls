using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

namespace AxamlLs.Workspace;

/// <summary>
/// Loads one .csproj with Roslyn's MSBuildWorkspace (source generators included, so CommunityToolkit
/// [ObservableProperty]/[RelayCommand] members exist) and keeps it current while .cs files change on disk.
/// </summary>
public sealed class ProjectHost : IDisposable
{
    /// <summary>Serializes loads and updates of the Roslyn solution (async: held across awaits, never with a lock).</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _loadLock = new();
    private readonly ConcurrentDictionary<string, byte> _pendingFiles = new(StringComparer.Ordinal);
    private readonly Timer _debounce;
    private MSBuildWorkspace? _workspace;
    private Solution? _solution;
    private ProjectId? _projectId;
    private ProjectSnapshot? _current;
    private Task? _loadTask;
    private FileIndex? _files;
    private volatile bool _reloadRequested;

    public string ProjectPath { get; }
    public string ProjectDirectory { get; }

    /// <summary>The project folder's file index, resources and style facts.</summary>
    public ProjectIndexes Indexes { get; }
    public bool CompiledBindingsDefault { get; }
    public ProjectSnapshot? Current => Volatile.Read(ref _current);
    public string? LoadError { get; private set; }
    /// <summary>The Roslyn solution (source generators included), for symbol search and rename. Null until loaded.</summary>
    public Solution? Solution => Volatile.Read(ref _solution);
    public ProjectId? ProjectId => _projectId;

    /// <summary>Raised (on a background thread) after a new snapshot is available.</summary>
    public event Action<ProjectHost>? Updated;

    /// <summary>
    /// Called when saved .cs files rename declarations (see <see cref="Analysis.CodeRenames"/>), before the new code
    /// becomes <see cref="Current"/>: AXAML analyzed against the current snapshot still resolves the old names.
    /// </summary>
    public Func<ProjectHost, IReadOnlyList<Analysis.CodeRename>, Task>? CodeRenamed { get; set; }

    /// <param name="projectPath">The .csproj file.</param>
    /// <param name="indexes">The indexes of the project's folder (owned by the caller, shared with the analyses).</param>
    public ProjectHost(string projectPath, ProjectIndexes indexes)
    {
        ProjectPath = projectPath;
        ProjectDirectory = Path.GetDirectoryName(projectPath)!;
        Indexes = indexes;
        CompiledBindingsDefault = ProjectFiles.DetectCompiledBindingsDefault(projectPath);
        _debounce = new Timer(_ => _ = ApplyPendingAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Starts loading the project if nobody did, and waits for it.</summary>
    /// <param name="ct">Ends the wait only: the load keeps going for the next caller.</param>
    public Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        lock (_loadLock)
        {
            _loadTask ??= Task.Run(LoadAsync, CancellationToken.None); // the load serves every caller, not just this one
            return ct.CanBeCanceled ? _loadTask.WaitAsync(ct) : _loadTask;
        }
    }

    private async Task LoadAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await LoadCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
        StartWatching();
    }

    private async Task LoadCoreAsync()
    {
        var sw = Stopwatch.StartNew();
        Log.Info($"Loading project {ProjectPath}");
        try
        {
            _workspace?.Dispose();
            _workspace = MSBuildWorkspace.Create(new Dictionary<string, string>
            {
                ["Configuration"] = "Debug",
            });
            _workspace.RegisterWorkspaceFailedHandler(e =>
            {
                if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure) Log.Info($"[msbuild] {e.Diagnostic.Message}");
            });
            var project = await _workspace.OpenProjectAsync(ProjectPath).ConfigureAwait(false);
            _solution = project.Solution;
            _projectId = project.Id;
            await RebuildSnapshotAsync().ConfigureAwait(false);
            LoadError = null;
            Log.Info($"Loaded {Path.GetFileName(ProjectPath)} in {sw.ElapsedMilliseconds} ms ({project.DocumentIds.Count} documents)");
        }
        catch (Exception ex)
        {
            LoadError = ex.Message;
            Log.Error($"Could not load {ProjectPath}", ex);
        }
    }

    private async Task RebuildSnapshotAsync()
    {
        var project = _solution?.GetProject(_projectId!);
        if (project == null) return;
        var compilation = await project.GetCompilationAsync().ConfigureAwait(false);
        if (compilation == null) return;
        Volatile.Write(ref _current, new ProjectSnapshot(ProjectPath, compilation, CompiledBindingsDefault, Current));
        try
        {
            Updated?.Invoke(this);
        }
        catch (Exception ex)
        {
            Log.Error("Updated handler failed", ex);
        }
    }

    /// <summary>Follows .cs and project file changes through the folder's shared <see cref="FileIndex"/>.</summary>
    private void StartWatching()
    {
        if (_files != null) return;
        _files = Indexes.Files;
        _files.Changed += OnFileChanges;
        _files.Overflowed += OnFilesOverflowed;
    }

    private void OnFileChanges(IReadOnlyList<FileChange> changes)
    {
        var relevant = false;
        foreach (var (path, _) in changes)
        {
            if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                _pendingFiles[path] = 0;
                relevant = true;
            }
            else if (path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".props", StringComparison.OrdinalIgnoreCase) ||
                     path.EndsWith(".targets", StringComparison.OrdinalIgnoreCase))
            {
                _reloadRequested = true;
                relevant = true;
            }
        }
        if (relevant) _debounce.Change(_reloadRequested ? 1500 : 350, Timeout.Infinite);
    }

    /// <summary>C# changes may have been lost, so the loaded solution may be stale: reload it.</summary>
    private void OnFilesOverflowed()
    {
        _reloadRequested = true;
        _debounce.Change(1500, Timeout.Infinite);
    }

    /// <summary>Lets the language server push an unsaved .cs buffer, if the editor sends one.</summary>
    public void NotifyFileChanged(string path)
    {
        _pendingFiles[path] = 0;
        _debounce.Change(350, Timeout.Infinite);
    }

    private async Task ApplyPendingAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_reloadRequested)
            {
                _reloadRequested = false;
                _pendingFiles.Clear();
                await LoadCoreAsync().ConfigureAwait(false);
                return;
            }
            if (_solution == null || _projectId == null) return;

            var changed = false;
            var before = _solution;
            var edited = new List<(DocumentId Id, SourceText Text)>();
            foreach (var path in _pendingFiles.Keys.ToList())
            {
                _pendingFiles.TryRemove(path, out _);
                var ids = _solution.GetDocumentIdsWithFilePath(path);
                if (File.Exists(path))
                {
                    string text;
                    try
                    {
                        text = await File.ReadAllTextAsync(path).ConfigureAwait(false);
                    }
                    catch (IOException)
                    {
                        _pendingFiles[path] = 0; // Still being written; try again.
                        _debounce.Change(300, Timeout.Infinite);
                        continue;
                    }
                    var source = SourceText.From(text);
                    if (ids.Length > 0)
                    {
                        foreach (var id in ids) _solution = _solution.WithDocumentText(id, source);
                        edited.Add((ids[0], source));
                    }
                    else if (path.StartsWith(ProjectDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    {
                        var docId = DocumentId.CreateNewId(_projectId);
                        _solution = _solution.AddDocument(docId, Path.GetFileName(path), source, filePath: path);
                    }
                    else
                    {
                        continue;
                    }
                    changed = true;
                }
                else if (ids.Length > 0)
                {
                    _solution = _solution.RemoveDocuments(ids);
                    changed = true;
                }
            }
            if (changed && CodeRenamed is { } onRenamed) await ReportRenamesAsync(before, edited, onRenamed).ConfigureAwait(false);
            if (changed) await RebuildSnapshotAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("Updating the project failed", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ReportRenamesAsync(Solution before, List<(DocumentId Id, SourceText Text)> edited, Func<ProjectHost, IReadOnlyList<Analysis.CodeRename>, Task> onRenamed)
    {
        var renames = new List<Analysis.CodeRename>();
        foreach (var (id, text) in edited)
        {
            if (before.GetDocument(id) is not { } document) continue;
            try
            {
                renames.AddRange(await Analysis.CodeRenames.DetectAsync(document, text).ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                Log.Error($"Looking for renames in {document.FilePath} failed", ex);
            }
        }
        if (renames.Count == 0) return;
        try
        {
            await onRenamed(this, renames).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("Following a C# rename in AXAML failed", ex);
        }
    }

    public void Dispose()
    {
        if (_files != null)
        {
            _files.Changed -= OnFileChanges;
            _files.Overflowed -= OnFilesOverflowed;
        }
        _debounce.Dispose();
        _workspace?.Dispose();
    }
}
