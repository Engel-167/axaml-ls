using System.Text.Json.Nodes;
using AxamlLs.Analysis;
using AxamlLs.Text;

namespace AxamlLs.Lsp;

/// <summary>
/// Conversions between LSP JSON and the server's own terms: URIs ↔ paths, positions and ranges ↔ absolute UTF-16
/// offsets, diagnostics. Everything inside the server works in offsets; only this boundary knows lines and characters.
/// </summary>
internal static class Protocol
{
    public static string UriToPath(string uri) => new Uri(uri).LocalPath;

    public static string PathToUri(string path) => new Uri(path).AbsoluteUri;

    /// <summary>The <c>textDocument.uri</c> of a request or notification.</summary>
    public static string DocumentUri(JsonObject p) => (string)p["textDocument"]!["uri"]!;

    /// <summary>An LSP <c>Position</c> as an offset into the text.</summary>
    public static int Offset(LineIndex lines, JsonNode position) =>
        lines.GetOffset((int)position["line"]!, (int)position["character"]!);

    /// <summary>An LSP <c>Range</c> as start and end offsets.</summary>
    public static (int Start, int End) Offsets(LineIndex lines, JsonNode range) =>
        (Offset(lines, range["start"]!), Offset(lines, range["end"]!));

    public static JsonObject Position(LineIndex lines, int offset)
    {
        var (line, character) = lines.GetPosition(offset);
        return new JsonObject { ["line"] = line, ["character"] = character };
    }

    public static JsonObject Range(LineIndex lines, int start, int end) =>
        new() { ["start"] = Position(lines, start), ["end"] = Position(lines, end) };

    /// <summary>An LSP <c>Diagnostic</c>; <paramref name="unnecessary"/> adds the tag that makes editors fade the range.</summary>
    public static JsonObject Diagnostic(XamlDiagnostic d, LineIndex lines, bool unnecessary = false)
    {
        var json = new JsonObject
        {
            ["range"] = Range(lines, d.Start, d.End),
            ["severity"] = (int)d.Severity,
            ["code"] = d.Code,
            ["source"] = "axaml",
            ["message"] = d.Message,
        };
        if (unnecessary) json["tags"] = new JsonArray(1);
        return json;
    }

    /// <summary>A location: in the requesting document (by offset) or in another file (by line and character).</summary>
    public static JsonObject Location(DefinitionTarget target, string documentUri, LineIndex lines)
    {
        if (target.DocumentOffset is { } offset) return new JsonObject { ["uri"] = documentUri, ["range"] = Range(lines, offset, offset) };
        return new JsonObject
        {
            ["uri"] = PathToUri(target.FilePath!),
            ["range"] = new JsonObject
            {
                ["start"] = new JsonObject { ["line"] = target.StartLine, ["character"] = target.StartCharacter },
                ["end"] = new JsonObject { ["line"] = target.EndLine, ["character"] = target.EndCharacter },
            },
        };
    }

    /// <summary>
    /// Applies <c>didChange</c> content changes in order: a change with a range replaces that range (incremental sync),
    /// one without replaces the whole text.
    /// </summary>
    public static string ApplyContentChanges(string text, JsonArray changes)
    {
        foreach (var change in changes)
        {
            if (change == null) continue;
            var newText = (string?)change["text"] ?? "";
            if (change["range"] is not JsonObject range)
            {
                text = newText;
                continue;
            }
            var lines = new LineIndex(text);
            var (start, end) = Offsets(lines, range);
            end = Math.Max(start, end);
            text = string.Concat(text.AsSpan(0, start), newText, text.AsSpan(end));
        }
        return text;
    }
}

/// <summary>JSON-RPC and LSP error codes the server answers with.</summary>
internal static class ErrorCodes
{
    public const int InvalidParams = -32602;
    public const int MethodNotFound = -32601;
    public const int InternalError = -32603;
    public const int RequestCancelled = -32800;
    public const int ContentModified = -32801;

    public static JsonObject Error(int code, string message) => new() { ["code"] = code, ["message"] = message };
}

/// <summary>The request was understood but its arguments cannot be used (an invalid new name): answered as -32602.</summary>
internal sealed class InvalidParamsException(string message) : Exception(message);

/// <summary>The document changed while the answer was computed, so the answer is stale: answered as -32801.</summary>
internal sealed class ContentModifiedException() : Exception("Content modified");
