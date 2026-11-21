using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace AxamlLs.Lsp;

/// <summary>
/// The editor, as the server talks to it: responses, notifications and the few requests the server makes
/// (apply an edit, open a document, create a progress token, register file watchers). Most requests are sent without
/// waiting; <see cref="CallAsync"/> waits for the answer, which the read loop hands to <see cref="OnResponse"/>.
/// </summary>
internal sealed class LspClient(JsonRpc rpc)
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _calls = new(StringComparer.Ordinal);
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
        rpc.SendAsync(new JsonObject { ["id"] = NextId(), ["method"] = method, ["params"] = parameters });

    /// <summary>
    /// Sends a request and waits for the editor's response message (with <c>result</c> or <c>error</c>). Throws
    /// <see cref="OperationCanceledException"/> when <paramref name="ct"/> ends the wait first.
    /// </summary>
    /// <param name="method">The request method.</param>
    /// <param name="parameters">Its params.</param>
    /// <param name="ct">Ends the wait (a timeout, shutdown); the editor may still answer later, which is then dropped.</param>
    public async Task<JsonObject> CallAsync(string method, JsonObject parameters, CancellationToken ct)
    {
        var id = NextId();
        var answer = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _calls[id] = answer;
        try
        {
            await rpc.SendAsync(new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters }).ConfigureAwait(false);
            return await answer.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _calls.TryRemove(id, out _);
        }
    }

    /// <summary>A response from the editor: completes the <see cref="CallAsync"/> waiting for it, if any.</summary>
    public void OnResponse(JsonObject response)
    {
        if ((string?)response["id"] is { } id && _calls.TryRemove(id, out var answer)) answer.TrySetResult(response);
    }

    private string NextId() => $"axaml-{Interlocked.Increment(ref _nextRequestId)}";

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
