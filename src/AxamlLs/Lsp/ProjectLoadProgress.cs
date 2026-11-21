using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using AxamlLs.Workspace;

namespace AxamlLs.Lsp;

/// <summary>Shows "Loading MyApp.csproj" in the editor's status bar until a project's first load finishes, once per project.</summary>
internal sealed class ProjectLoadProgress(LspClient client, Session session)
{
    private readonly ConcurrentDictionary<ProjectHost, byte> _announced = new();

    public void Announce(ProjectHost host)
    {
        if (!_announced.TryAdd(host, 0) || host.Current != null || !session.Client.WorkDoneProgress) return;
        var token = $"axaml-load-{Path.GetFileNameWithoutExtension(host.ProjectPath)}";
        _ = Task.Run(async () =>
        {
            try
            {
                await client.CreateProgressAsync(token).ConfigureAwait(false);
                await client.ReportProgressAsync(token, new JsonObject { ["kind"] = "begin", ["title"] = $"Loading {Path.GetFileName(host.ProjectPath)}" }).ConfigureAwait(false);
                await host.EnsureLoadedAsync().ConfigureAwait(false);
                var end = host.LoadError == null ? "Project loaded" : $"Project failed to load: {host.LoadError}";
                await client.ReportProgressAsync(token, new JsonObject { ["kind"] = "end", ["message"] = end }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error("Progress reporting failed", ex);
            }
        });
    }
}
