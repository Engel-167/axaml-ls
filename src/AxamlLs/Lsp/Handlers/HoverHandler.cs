using System.Text.Json.Nodes;
using AxamlLs.Analysis;

namespace AxamlLs.Lsp.Handlers;

/// <summary><c>textDocument/hover</c>: Markdown about the type, member, resource, color or selector part under the cursor.</summary>
internal sealed class HoverHandler(Documents documents) : SyncRequestHandler
{
    public override string Method => "textDocument/hover";

    public override void Advertise(JsonObject capabilities) => capabilities["hoverProvider"] = true;

    protected override JsonNode? Handle(JsonObject p)
    {
        if (!documents.TryAnalyzeAt(p, out _, out var analysis, out var offset)) return null;
        if (Hover.At(analysis.Analyzer, offset) is not { } hover) return null;
        return new JsonObject
        {
            ["contents"] = new JsonObject { ["kind"] = "markdown", ["value"] = hover.Markdown },
            ["range"] = Protocol.Range(analysis.Lines, hover.Start, hover.End),
        };
    }
}
