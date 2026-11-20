using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AxamlLs.Lsp;

/// <summary>LSP base protocol: Content-Length framed JSON-RPC 2.0 over a pair of streams.</summary>
public sealed class JsonRpc(Stream input, Stream output) : IDisposable
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
                length = int.TryParse(line[(colon + 1)..].Trim(), out var parsed) && parsed >= 0 ? parsed : -1;
        }

        var buffer = new byte[length];
        var read = 0;
        while (read < length)
        {
            var n = await _input.ReadAsync(buffer.AsMemory(read, length - read), ct).ConfigureAwait(false);
            if (n == 0) return null;
            read += n;
        }
        try
        {
            return JsonNode.Parse(buffer) as JsonObject ?? [];
        }
        catch (JsonException ex)
        {
            // One malformed message must not end the session: skip it (an object without "method" is ignored).
            Log.Error("Ignoring a malformed message", ex);
            return [];
        }
    }

    private readonly byte[] _one = new byte[1];
    private readonly List<byte> _line = new(64);

    /// <summary>One header line (ASCII, CRLF stripped); null at end of stream. Reads are buffered, so byte-by-byte is cheap.</summary>
    private async Task<string?> ReadHeaderLineAsync(CancellationToken ct)
    {
        _line.Clear();
        while (true)
        {
            var n = await _input.ReadAsync(_one, ct).ConfigureAwait(false);
            if (n == 0) return _line.Count == 0 ? null : Encoding.ASCII.GetString(CollectionsMarshal.AsSpan(_line));
            if (_one[0] == '\n')
            {
                var span = CollectionsMarshal.AsSpan(_line);
                if (span.Length > 0 && span[^1] == '\r') span = span[..^1];
                return Encoding.ASCII.GetString(span);
            }
            _line.Add(_one[0]);
        }
    }

    /// <summary>Sends one message. Header and body go out in a single write, serialized straight to UTF-8.</summary>
    public async Task SendAsync(JsonObject message)
    {
        message["jsonrpc"] = "2.0";
        var buffer = new ArrayBufferWriter<byte>(4096);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            message.WriteTo(writer, JsonOptions);
        }
        var header = Encoding.ASCII.GetBytes($"Content-Length: {buffer.WrittenCount}\r\n\r\n");
        var frame = new byte[header.Length + buffer.WrittenCount];
        header.CopyTo(frame, 0);
        buffer.WrittenSpan.CopyTo(frame.AsSpan(header.Length));
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await output.WriteAsync(frame).ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose()
    {
        _input.Dispose();
        _writeLock.Dispose();
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
}
