using System.Text.Json.Nodes;

namespace AxamlLs.Lsp.Handlers;

/// <summary><c>initialized</c>: the editor is ready for server requests, so file watchers can be registered.</summary>
internal sealed class InitializedHandler(EditorFileWatcher watcher) : INotificationHandler
{
    public string Method => "initialized";

    public void Advertise(JsonObject capabilities)
    {
    }

    public void Handle(JsonObject p) => watcher.OnInitialized();
}

/// <summary><c>workspace/didChangeWatchedFiles</c>: files changed that the registered watchers cover (registered at runtime, so nothing to advertise).</summary>
internal sealed class DidChangeWatchedFilesHandler(EditorFileWatcher watcher) : INotificationHandler
{
    public string Method => EditorFileWatcher.Method;

    public void Advertise(JsonObject capabilities)
    {
    }

    public void Handle(JsonObject p) => watcher.OnDidChangeWatchedFiles(p);
}
