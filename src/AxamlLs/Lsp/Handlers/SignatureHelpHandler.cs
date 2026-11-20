using System.Text.Json.Nodes;
using AxamlLs.Analysis;

namespace AxamlLs.Lsp.Handlers;

/// <summary><c>textDocument/signatureHelp</c>: the arguments of the markup extension under the cursor.</summary>
internal sealed class SignatureHelpHandler(Documents documents) : SyncRequestHandler
{
    public override string Method => "textDocument/signatureHelp";

    public override void Advertise(JsonObject capabilities) => capabilities["signatureHelpProvider"] = new JsonObject
    {
        ["triggerCharacters"] = new JsonArray("{", ",", " "),
        ["retriggerCharacters"] = new JsonArray("="),
    };

    protected override JsonNode? Handle(JsonObject p)
    {
        if (!documents.TryAnalyzeAt(p, out _, out var analysis, out var offset)) return null;
        if (MarkupSignatures.At(analysis.Analyzer, offset) is not { } help) return null;
        var signatures = new JsonArray();
        foreach (var s in help.Signatures)
        {
            var (label, spans) = s.Render();
            var parameters = new JsonArray();
            for (var i = 0; i < s.Parameters.Count; i++)
            {
                var parameter = s.Parameters[i];
                var item = new JsonObject { ["label"] = new JsonArray(spans[i].Start, spans[i].End) };
                var doc = string.Join("\n\n", new[] { parameter.Type is { } t ? $"`{t}`" : null, parameter.Doc }.Where(x => x != null));
                if (doc.Length > 0) item["documentation"] = new JsonObject { ["kind"] = "markdown", ["value"] = doc };
                parameters.Add(item);
            }
            var signature = new JsonObject { ["label"] = label, ["parameters"] = parameters };
            if (s.Doc != null) signature["documentation"] = new JsonObject { ["kind"] = "markdown", ["value"] = s.Doc };
            signatures.Add(signature);
        }
        return new JsonObject
        {
            ["signatures"] = signatures,
            ["activeSignature"] = help.ActiveSignature,
            // Out of range means "no parameter" (a key the extension doesn't have).
            ["activeParameter"] = help.ActiveParameter < 0 ? help.Signatures[help.ActiveSignature].Parameters.Count : help.ActiveParameter,
        };
    }
}
