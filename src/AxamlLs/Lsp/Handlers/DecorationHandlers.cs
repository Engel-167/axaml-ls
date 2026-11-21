using System.Text.Json.Nodes;
using AxamlLs.Analysis;

namespace AxamlLs.Lsp.Handlers;

/// <summary><c>textDocument/inlayHint</c>: names of the parts of compact values (Thickness, CornerRadius, BoxShadow), implicit markup keys, binding sources.</summary>
internal sealed class InlayHintHandler(Documents documents, Session session) : SyncRequestHandler
{
    private const int TypeKind = 1, ParameterKind = 2;

    public override string Method => "textDocument/inlayHint";

    public override void Advertise(JsonObject capabilities) => capabilities["inlayHintProvider"] = true;

    protected override JsonNode? Handle(JsonObject p)
    {
        var array = new JsonArray();
        if (!documents.TryAnalyze(p, out _, out var analysis)) return array;
        var (start, end) = Protocol.Offsets(analysis.Lines, p["range"]!);
        foreach (var h in InlayHints.InRange(analysis.Analyzer, start, end, session.Options.InlayHints))
        {
            array.Add(new JsonObject
            {
                ["position"] = Protocol.Position(analysis.Lines, h.Offset),
                ["label"] = h.Label,
                ["kind"] = h.IsType ? TypeKind : ParameterKind,
                ["paddingRight"] = h.PadRight,
                ["paddingLeft"] = h.PadLeft,
            });
        }
        return array;
    }
}
