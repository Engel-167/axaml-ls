using System.Text.Json.Nodes;
using AxamlLs.Analysis;
using AxamlLs.Workspace;

namespace AxamlLs.Lsp.Handlers;

/// <summary><c>textDocument/documentHighlight</c>: the other uses of the name under the cursor in this file.</summary>
internal sealed class DocumentHighlightHandler(Documents documents) : SyncRequestHandler
{
    public override string Method => "textDocument/documentHighlight";

    public override void Advertise(JsonObject capabilities) => capabilities["documentHighlightProvider"] = true;

    protected override JsonNode? Handle(JsonObject p)
    {
        var array = new JsonArray();
        if (!documents.TryAnalyzeAt(p, out var doc, out var analysis, out var offset)) return array;
        var file = new XamlFile(doc.Path, analysis.Analyzer.Result.Doc.Text, analysis.Analyzer, analysis.Lines);
        foreach (var h in DocumentHighlights.For(file, offset))
            array.Add(new JsonObject { ["range"] = Protocol.Range(analysis.Lines, h.Start, h.End), ["kind"] = (int)h.Kind });
        return array;
    }
}
