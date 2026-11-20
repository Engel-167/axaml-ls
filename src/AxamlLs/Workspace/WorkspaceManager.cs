using System.Collections.Concurrent;
using AxamlLs.Telemetry;

namespace AxamlLs.Workspace;

/// <summary>
/// One <see cref="ProjectHost"/> per .csproj, created on first use by an .axaml file inside it, and one
/// <see cref="ProjectIndexes"/> per folder (a project's, or the folder of an .axaml file outside any project).
/// </summary>
/// <param name="metrics">Passed to the indexes it creates, when given.</param>
public sealed class WorkspaceManager(ServerMetrics? metrics = null) : IDisposable
{
    private readonly ConcurrentDictionary<string, ProjectHost> _hosts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<ProjectIndexes>> _indexes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string?> _projectForDir = new(StringComparer.Ordinal);

    public event Action<ProjectHost>? ProjectUpdated;

    /// <summary>Called when a saved .cs file renamed declarations (see <see cref="ProjectHost.CodeRenamed"/>).</summary>
    public Func<ProjectHost, IReadOnlyList<Analysis.CodeRename>, Task>? CodeRenamed { get; set; }

    public ProjectHost? GetHostFor(string axamlPath, bool startLoading = true)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(axamlPath)) ?? "";
        var project = _projectForDir.GetOrAdd(dir, _ => ProjectFiles.FindOwningProject(axamlPath));
        if (project == null) return null;
        var host = _hosts.GetOrAdd(project, p =>
        {
            var h = new ProjectHost(p, IndexesFor(Path.GetDirectoryName(p)!));
            h.Updated += x => ProjectUpdated?.Invoke(x);
            h.CodeRenamed = (x, renames) => CodeRenamed?.Invoke(x, renames) ?? Task.CompletedTask;
            return h;
        });
        if (startLoading) _ = host.EnsureLoadedAsync();
        return host;
    }

    public IEnumerable<ProjectHost> Hosts => _hosts.Values;

    /// <summary>The indexes of a folder, created (and watched) on first use.</summary>
    public ProjectIndexes IndexesFor(string folder)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        // Lazy: GetOrAdd may run the factory twice under a race, and a second instance would start a second watcher.
        return _indexes.GetOrAdd(root, r => new Lazy<ProjectIndexes>(() => new ProjectIndexes(r, metrics))).Value;
    }

    public void Dispose()
    {
        foreach (var h in _hosts.Values) h.Dispose();
        foreach (var i in _indexes.Values.Where(i => i.IsValueCreated)) i.Value.Dispose();
    }
}
