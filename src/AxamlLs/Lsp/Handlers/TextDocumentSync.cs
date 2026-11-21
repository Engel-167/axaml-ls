using System.Text.Json.Nodes;

namespace AxamlLs.Lsp.Handlers;

/// <summary><c>textDocument/didOpen</c>: start tracking the document and check it right away.</summary>
internal sealed class DidOpenHandler(Documents documents, DiagnosticsPublisher diagnostics) : INotificationHandler
{
    public string Method => "textDocument/didOpen";

    public void Advertise(JsonObject capabilities)
    {
    }

    public void Handle(JsonObject p)
    {
        var td = p["textDocument"]!;
        var doc = documents.Open((string)td["uri"]!, (string?)td["text"] ?? "", (int?)td["version"] ?? 0);
        diagnostics.Schedule(doc, delayMs: 0);
    }
}

/// <summary><c>textDocument/didChange</c>: apply the edits (incremental sync) and check again after a short pause.</summary>
internal sealed class DidChangeHandler(Documents documents, DiagnosticsPublisher diagnostics) : INotificationHandler
{
    public string Method => "textDocument/didChange";

    // Incremental (2): the editor sends only the edited range, not the whole file on every keystroke.
    public void Advertise(JsonObject capabilities) =>
        capabilities["textDocumentSync"] = new JsonObject { ["openClose"] = true, ["change"] = 2 };

    public void Handle(JsonObject p)
    {
        var changes = p["contentChanges"]!.AsArray();
        var doc = documents.Change(Protocol.DocumentUri(p), (int?)p["textDocument"]!["version"], text => Protocol.ApplyContentChanges(text, changes));
        // Other open files are re-checked only if this edit changed what they depend on.
        if (doc != null) diagnostics.Schedule(doc, DiagnosticsPublisher.EditDelayMs, notifyOthers: true);
    }
}

/// <summary><c>textDocument/didClose</c>: stop tracking the document and clear its diagnostics.</summary>
internal sealed class DidCloseHandler(Documents documents, DiagnosticsPublisher diagnostics) : INotificationHandler
{
    public string Method => "textDocument/didClose";

    public void Advertise(JsonObject capabilities)
    {
    }

    public void Handle(JsonObject p)
    {
        var uri = Protocol.DocumentUri(p);
        documents.Close(uri);
        _ = diagnostics.ClearAsync(uri);
    }
}
