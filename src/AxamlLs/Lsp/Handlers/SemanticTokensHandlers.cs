using System.Text.Json.Nodes;
using AxamlLs.Analysis;

namespace AxamlLs.Lsp.Handlers;

/// <summary>
/// The last token array sent for each document, so a delta request can answer with the change only. Keyed by URI and
/// replaced by every full or delta answer; 64 documents at most (LRU). A request naming an older or unknown result
/// gets the full array.
/// </summary>
internal sealed class SemanticTokensResults
{
    private readonly LruCache<string, (string Id, int[] Data)> _last = new(maxEntries: 64, maxWeight: long.MaxValue);
    private long _next;

    public string Remember(string uri, int[] data)
    {
        var id = Interlocked.Increment(ref _next).ToString(System.Globalization.CultureInfo.InvariantCulture);
        _last.Add(uri, (id, data), 1);
        return id;
    }

    public int[]? Previous(string uri, string? id) => id != null && _last.TryGet(uri, out var last) && last.Id == id ? last.Data : null;
}

/// <summary><c>textDocument/semanticTokens/full</c>: style selectors colored by part; unused classes get their own token type.</summary>
internal sealed class SemanticTokensHandler(Documents documents, SemanticTokensResults results) : SyncRequestHandler
{
    public override string Method => "textDocument/semanticTokens/full";

    public override void Advertise(JsonObject capabilities) => capabilities["semanticTokensProvider"] = new JsonObject
    {
        ["legend"] = new JsonObject
        {
            ["tokenTypes"] = new JsonArray([.. SemanticTokens.TokenTypes.Select(t => (JsonNode)t)]),
            ["tokenModifiers"] = new JsonArray(),
        },
        ["full"] = new JsonObject { ["delta"] = true },
        ["range"] = true,
    };

    protected override JsonNode? Handle(JsonObject p)
    {
        if (!documents.TryAnalyze(p, out _, out var analysis)) return null;
        var data = SemanticTokens.Encode(analysis.Analyzer, analysis.Lines);
        return new JsonObject { ["resultId"] = results.Remember(Protocol.DocumentUri(p), data), ["data"] = Array(data) };
    }

    internal static JsonArray Array(int[] data) => new([.. data.Select(n => (JsonNode)n)]);
}

/// <summary><c>textDocument/semanticTokens/full/delta</c>: the change since the result the editor names.</summary>
internal sealed class SemanticTokensDeltaHandler(Documents documents, SemanticTokensResults results) : SyncRequestHandler
{
    public override string Method => "textDocument/semanticTokens/full/delta";

    protected override JsonNode? Handle(JsonObject p)
    {
        if (!documents.TryAnalyze(p, out _, out var analysis)) return null;
        var uri = Protocol.DocumentUri(p);
        var data = SemanticTokens.Encode(analysis.Analyzer, analysis.Lines);
        var previous = results.Previous(uri, (string?)p["previousResultId"]);
        var id = results.Remember(uri, data);
        if (previous == null) return new JsonObject { ["resultId"] = id, ["data"] = SemanticTokensHandler.Array(data) };
        var edits = new JsonArray();
        foreach (var e in SemanticTokens.Diff(previous, data))
            edits.Add(new JsonObject { ["start"] = e.Start, ["deleteCount"] = e.DeleteCount, ["data"] = SemanticTokensHandler.Array(e.Data) });
        return new JsonObject { ["resultId"] = id, ["edits"] = edits };
    }
}

/// <summary><c>textDocument/semanticTokens/range</c>: the tokens of the visible range only.</summary>
internal sealed class SemanticTokensRangeHandler(Documents documents) : SyncRequestHandler
{
    public override string Method => "textDocument/semanticTokens/range";

    protected override JsonNode? Handle(JsonObject p)
    {
        if (!documents.TryAnalyze(p, out _, out var analysis)) return null;
        var (start, end) = Protocol.Offsets(analysis.Lines, p["range"]!);
        return new JsonObject { ["data"] = SemanticTokensHandler.Array(SemanticTokens.Encode(analysis.Analyzer, analysis.Lines, start, end)) };
    }
}
