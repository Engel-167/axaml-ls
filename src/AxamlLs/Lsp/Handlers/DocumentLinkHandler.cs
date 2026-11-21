using System.Text.Json.Nodes;
using AxamlLs.Analysis;

namespace AxamlLs.Lsp.Handlers;

/// <summary><c>textDocument/documentLink</c>: path values that open their file.</summary>
internal sealed class DocumentLinkHandler(Documents documents) : SyncRequestHandler
{
    public override string Method => "textDocument/documentLink";

    public override void Advertise(JsonObject capabilities) => capabilities["documentLinkProvider"] = new JsonObject { ["resolveProvider"] = false };

    protected override JsonNode? Handle(JsonObject p)
    {
        var array = new JsonArray();
        if (!documents.TryAnalyze(p, out _, out var analysis)) return array;
        foreach (var link in DocumentLinks.Find(analysis.Analyzer))
        {
            array.Add(new JsonObject
            {
                ["range"] = Protocol.Range(analysis.Lines, link.Start, link.End),
                ["target"] = Protocol.PathToUri(link.Path),
                ["tooltip"] = link.Tooltip,
            });
        }
        return array;
    }
}
