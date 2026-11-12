using System.Collections.Concurrent;

namespace AxamlLs.Workspace;

/// <summary>One <see cref="ProjectHost"/> per .csproj, created on first use by an .axaml file inside it.</summary>
public sealed class WorkspaceManager : IDisposable
{
    private readonly ConcurrentDictionary<string, ProjectHost> _hosts = new(StringComparer.Ordinal);
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
            var h = new ProjectHost(p);
            h.Updated += x => ProjectUpdated?.Invoke(x);
            h.CodeRenamed = (x, renames) => CodeRenamed?.Invoke(x, renames) ?? Task.CompletedTask;
            return h;
        });
        if (startLoading) _ = host.EnsureLoadedAsync();
        return host;
    }

    public IEnumerable<ProjectHost> Hosts => _hosts.Values;

    public void Dispose()
    {
        foreach (var h in _hosts.Values) h.Dispose();
    }
}
