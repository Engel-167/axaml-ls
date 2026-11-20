using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json.Nodes;
using AxamlLs.Lsp;
using AxamlLs.Telemetry;

namespace AxamlLs.Tests;

/// <summary>
/// Talks to an in-process <see cref="LspServer"/> over in-memory pipes, framed like stdio. Answers the server's own
/// requests (progress tokens) with null and keeps its notifications for inspection.
/// </summary>
internal sealed class LspTestClient : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly Pipe _toServer = new();
    private readonly Pipe _toClient = new();
    private readonly Stream _output;
    private readonly LspServer _server;
    private readonly Task _serverLoop;
    private readonly Task _readLoop;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private int _nextId;

    public LspTestClient(ServerMetrics metrics)
    {
        _server = new LspServer(_toServer.Reader.AsStream(), _toClient.Writer.AsStream(), metrics);
        _output = _toServer.Writer.AsStream();
        _serverLoop = Task.Run(_server.RunAsync);
        _readLoop = Task.Run(ReadLoopAsync);
    }

    /// <summary>Notifications the server sent, in order.</summary>
    public ConcurrentQueue<JsonObject> Notifications { get; } = new();

    public async Task<JsonObject> RequestAsync(string method, JsonNode? parameters)
    {
        var id = Interlocked.Increment(ref _nextId);
        var answer = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id.ToString(System.Globalization.CultureInfo.InvariantCulture)] = answer;
        await SendAsync(new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters }).ConfigureAwait(false);
        return await answer.Task.WaitAsync(Timeout).ConfigureAwait(false);
    }

    public Task NotifyAsync(string method, JsonNode? parameters) =>
        SendAsync(new JsonObject { ["method"] = method, ["params"] = parameters });

    /// <summary>Initializes and opens one document.</summary>
    public async Task OpenAsync(string uri, string text)
    {
        await RequestAsync("initialize", new JsonObject { ["processId"] = null, ["capabilities"] = new JsonObject() }).ConfigureAwait(false);
        await NotifyAsync("initialized", new JsonObject()).ConfigureAwait(false);
        await NotifyAsync("textDocument/didOpen", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri, ["languageId"] = "axaml", ["version"] = 1, ["text"] = text },
        }).ConfigureAwait(false);
    }

    private async Task SendAsync(JsonObject message)
    {
        message["jsonrpc"] = "2.0";
        var body = Encoding.UTF8.GetBytes(message.ToJsonString());
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _output.WriteAsync(header).ConfigureAwait(false);
            await _output.WriteAsync(body).ConfigureAwait(false);
            await _output.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        var reader = _toClient.Reader;
        while (true)
        {
            var read = await reader.ReadAsync().ConfigureAwait(false);
            var buffer = read.Buffer;
            while (TryReadMessage(ref buffer, out var message)) await DispatchAsync(message).ConfigureAwait(false);
            reader.AdvanceTo(buffer.Start, buffer.End);
            if (read.IsCompleted) return;
        }
    }

    private static bool TryReadMessage(ref ReadOnlySequence<byte> buffer, out JsonObject message)
    {
        message = null!;
        var text = Encoding.ASCII.GetString(buffer.Slice(0, Math.Min(buffer.Length, 256)).ToArray());
        var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (headerEnd < 0) return false;
        var lengthLine = text[..headerEnd].Split("\r\n").First(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
        var length = int.Parse(lengthLine["Content-Length:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
        var start = headerEnd + 4;
        if (buffer.Length < start + length) return false;
        message = (JsonObject)JsonNode.Parse(buffer.Slice(start, length).ToArray())!;
        buffer = buffer.Slice(start + length);
        return true;
    }

    private async Task DispatchAsync(JsonObject message)
    {
        var id = message["id"]?.ToJsonString().Trim('"');
        if (message["method"] is null && id != null && _pending.TryRemove(id, out var answer))
            answer.SetResult(message);
        else if (message["method"] is not null && message["id"] is { } requestId)
            await SendAsync(new JsonObject { ["id"] = requestId.DeepClone(), ["result"] = null }).ConfigureAwait(false);
        else if (message["method"] is not null)
            Notifications.Enqueue(message);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await RequestAsync("shutdown", null).ConfigureAwait(false);
            await NotifyAsync("exit", null).ConfigureAwait(false);
            await _serverLoop.WaitAsync(Timeout).ConfigureAwait(false);
        }
        finally
        {
            await _toClient.Writer.CompleteAsync().ConfigureAwait(false);
            await _readLoop.WaitAsync(Timeout).ConfigureAwait(false);
            _server.Dispose();
            _writeGate.Dispose();
        }
    }
}
