using System.Text.Json.Nodes;
using AxamlLs.Analysis;
using AxamlLs.Xaml;

namespace AxamlLs.Lsp.Handlers;

/// <summary><c>textDocument/selectionRange</c>: expand and shrink selection, for each position of the request.</summary>
internal sealed class SelectionRangeHandler(Documents documents) : SyncRequestHandler
{
    public override string Method => "textDocument/selectionRange";

    public override void Advertise(JsonObject capabilities) => capabilities["selectionRangeProvider"] = true;

    protected override JsonNode? Handle(JsonObject p)
    {
        var result = new JsonArray();
        if (!documents.TryGet(Protocol.DocumentUri(p), out var doc)) return result;
        var (text, lines, parsed) = Documents.CurrentText(doc);
        var xml = parsed ?? XmlDoc.Parse(text);
        foreach (var position in p["positions"] as JsonArray ?? [])
        {
            JsonObject? node = null;
            // Built outside in: each range's parent is the next larger one.
            foreach (var (start, end) in SelectionRanges.At(xml, Protocol.Offset(lines, position!)).Reverse())
            {
                var next = new JsonObject { ["range"] = Protocol.Range(lines, start, end) };
                if (node != null) next["parent"] = node;
                node = next;
            }
            result.Add(node ?? new JsonObject { ["range"] = Protocol.Range(lines, 0, 0) });
        }
        return result;
    }
}
