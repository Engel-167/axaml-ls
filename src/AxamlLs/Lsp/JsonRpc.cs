using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AxamlLs.Lsp;

/// <summary>LSP base protocol: Content-Length framed JSON-RPC 2.0 over a pair of streams.</summary>
public sealed class JsonRpc(Stream input, Stream output)
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly BufferedStream _input = new(input);

    public async Task<JsonObject?> ReadAsync(CancellationToken ct)
    {
        var length = -1;
        while (true)
        {
            var line = await ReadHeaderLineAsync(ct).ConfigureAwait(false);
            if (line == null) return null;
            if (line.Length == 0)
            {
                if (length >= 0) break;
                continue;
            }
            var colon = line.IndexOf(':');
            if (colon > 0 && line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                length = int.Parse(line[(colon + 1)..].Trim());
        }

        var buffer = new byte[length];
        var read = 0;
        while (read < length)
        {
            var n = await _input.ReadAsync(buffer.AsMemory(read, length - read), ct).ConfigureAwait(false);
            if (n == 0) return null;
            read += n;
        }
        return JsonNode.Parse(buffer) as JsonObject;
    }

    private async Task<string?> ReadHeaderLineAsync(CancellationToken ct)
    {
        var bytes = new List<byte>(64);
        var one = new byte[1];
        while (true)
        {
            var n = await _input.ReadAsync(one, ct).ConfigureAwait(false);
            if (n == 0) return bytes.Count == 0 ? null : Encoding.ASCII.GetString(bytes.ToArray());
            if (one[0] == '\n')
            {
                if (bytes.Count > 0 && bytes[^1] == '\r') bytes.RemoveAt(bytes.Count - 1);
                return Encoding.ASCII.GetString(bytes.ToArray());
            }
            bytes.Add(one[0]);
        }
    }

    public async Task SendAsync(JsonObject message)
    {
        message["jsonrpc"] = "2.0";
        var body = Encoding.UTF8.GetBytes(message.ToJsonString(JsonOptions));
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await output.WriteAsync(header).ConfigureAwait(false);
            await output.WriteAsync(body).ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
}
