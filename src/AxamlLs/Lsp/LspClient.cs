using System.Text.Json.Nodes;

namespace AxamlLs.Lsp;

/// <summary>
/// The editor, as the server talks to it: responses, notifications and the few requests the server makes
/// (apply an edit, open a document, create a progress token). Responses to the server's requests are not awaited:
/// the read loop drops them, so nothing here can wait on the editor.
/// </summary>
internal sealed class LspClient(JsonRpc rpc)
{
    private int _nextRequestId;

    public Task RespondAsync(JsonNode id, JsonNode? result, JsonObject? error)
    {
        var response = new JsonObject { ["id"] = id };
        if (error != null) response["error"] = error;
        else response["result"] = result;
        return rpc.SendAsync(response);
    }

    public Task NotifyAsync(string method, JsonObject parameters) =>
        rpc.SendAsync(new JsonObject { ["method"] = method, ["params"] = parameters });

    /// <summary>Sends a request to the editor without waiting for its answer.</summary>
    public Task RequestAsync(string method, JsonObject parameters) =>
        rpc.SendAsync(new JsonObject { ["id"] = $"axaml-{Interlocked.Increment(ref _nextRequestId)}", ["method"] = method, ["params"] = parameters });

    public Task PublishDiagnosticsAsync(string uri, int? version, JsonArray diagnostics)
    {
        var p = new JsonObject { ["uri"] = uri, ["diagnostics"] = diagnostics };
        if (version != null) p["version"] = version;
        return NotifyAsync("textDocument/publishDiagnostics", p);
    }

    public Task ApplyEditAsync(string label, JsonObject workspaceEdit) =>
        RequestAsync("workspace/applyEdit", new JsonObject { ["label"] = label, ["edit"] = workspaceEdit });

    /// <param name="type">LSP MessageType: 1 error, 2 warning, 3 info, 4 log.</param>
    /// <param name="message">The text the editor shows.</param>
    public Task ShowMessageAsync(int type, string message) =>
        NotifyAsync("window/showMessage", new JsonObject { ["type"] = type, ["message"] = message });

    public Task ShowDocumentAsync(string uri, int line, int character)
    {
        var position = new JsonObject { ["line"] = line, ["character"] = character };
        return RequestAsync("window/showDocument", new JsonObject
        {
            ["uri"] = uri,
            ["takeFocus"] = true,
            ["selection"] = new JsonObject { ["start"] = position.DeepClone(), ["end"] = position.DeepClone() },
        });
    }

    public Task CreateProgressAsync(string token) =>
        RequestAsync("window/workDoneProgress/create", new JsonObject { ["token"] = token });

    public Task ReportProgressAsync(string token, JsonObject value) =>
        NotifyAsync("$/progress", new JsonObject { ["token"] = token, ["value"] = value });
}
