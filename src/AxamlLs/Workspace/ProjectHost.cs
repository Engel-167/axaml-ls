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
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _pendingFiles = new(StringComparer.Ordinal);
    private readonly Timer _debounce;
    private MSBuildWorkspace? _workspace;
    private Solution? _solution;
    private ProjectId? _projectId;
    private ProjectSnapshot? _current;
    private Task? _loadTask;
    private FileSystemWatcher? _watcher;
    private volatile bool _reloadRequested;

    public string ProjectPath { get; }
    public string ProjectDirectory { get; }
    public bool CompiledBindingsDefault { get; }
    public ProjectSnapshot? Current => Volatile.Read(ref _current);
    public string? LoadError { get; private set; }
    /// <summary>The Roslyn solution (source generators included), for symbol search and rename. Null until loaded.</summary>
    public Solution? Solution => Volatile.Read(ref _solution);
    public ProjectId? ProjectId => _projectId;

    /// <summary>Raised (on a background thread) after a new snapshot is available.</summary>
    public event Action<ProjectHost>? Updated;

    public ProjectHost(string projectPath)
    {
        ProjectPath = projectPath;
        ProjectDirectory = Path.GetDirectoryName(projectPath)!;
        CompiledBindingsDefault = ProjectFiles.DetectCompiledBindingsDefault(projectPath);
        _debounce = new Timer(_ => _ = ApplyPendingAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public Task EnsureLoadedAsync()
    {
        lock (_gate)
        {
            _loadTask ??= Task.Run(LoadAsync);
            return _loadTask;
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
        Volatile.Write(ref _current, new ProjectSnapshot(ProjectPath, compilation, CompiledBindingsDefault));
        try
        {
            Updated?.Invoke(this);
        }
        catch (Exception ex)
        {
            Log.Error("Updated handler failed", ex);
        }
    }

    private void StartWatching()
    {
        if (_watcher != null) return;
        try
        {
            _watcher = new FileSystemWatcher(ProjectDirectory)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            _watcher.Changed += OnFileEvent;
            _watcher.Created += OnFileEvent;
            _watcher.Deleted += OnFileEvent;
            _watcher.Renamed += (s, e) =>
            {
                OnFileEvent(s, new FileSystemEventArgs(WatcherChangeTypes.Deleted, Path.GetDirectoryName(e.OldFullPath)!, e.OldName));
                OnFileEvent(s, e);
            };
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            Log.Error("File watching is unavailable; C# changes will need a restart of the server", ex);
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        var path = e.FullPath;
        if (ProjectFiles.IsBuildOutput(path)) return;
        if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            _pendingFiles[path] = 0;
        }
        else if (path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".props", StringComparison.OrdinalIgnoreCase) ||
                 path.EndsWith(".targets", StringComparison.OrdinalIgnoreCase))
        {
            _reloadRequested = true;
        }
        else
        {
            return;
        }
        _debounce.Change(_reloadRequested ? 1500 : 350, Timeout.Infinite);
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

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce.Dispose();
        _workspace?.Dispose();
    }
}
