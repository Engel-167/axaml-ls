using System.Text.Json.Nodes;
using AxamlLs.Analysis;

namespace AxamlLs.Lsp.Handlers;

/// <summary><c>textDocument/documentColor</c>: color literals and named colors, for the editor's swatches.</summary>
internal sealed class DocumentColorHandler(Documents documents) : SyncRequestHandler
{
    public override string Method => "textDocument/documentColor";

    public override void Advertise(JsonObject capabilities) => capabilities["colorProvider"] = true;

    protected override JsonNode? Handle(JsonObject p)
    {
        var array = new JsonArray();
        if (!documents.TryAnalyze(p, out _, out var analysis)) return array;
        foreach (var c in Features.DocumentColors(analysis.Analyzer))
        {
            array.Add(new JsonObject
            {
                ["range"] = Protocol.Range(analysis.Lines, c.Start, c.End),
                ["color"] = new JsonObject
                {
                    ["red"] = (c.Argb >> 16 & 0xFF) / 255.0,
                    ["green"] = (c.Argb >> 8 & 0xFF) / 255.0,
                    ["blue"] = (c.Argb & 0xFF) / 255.0,
                    ["alpha"] = (c.Argb >> 24 & 0xFF) / 255.0,
                },
            });
        }
        return array;
    }
}

/// <summary><c>textDocument/colorPresentation</c>: how a picked color is written back (hex, rgb()/rgba() or hsl()/hsla()).</summary>
internal sealed class ColorPresentationHandler : SyncRequestHandler
{
    public override string Method => "textDocument/colorPresentation";

    protected override JsonNode? Handle(JsonObject p)
    {
        var c = p["color"]!;
        static uint Channel(JsonNode? v) => (uint)Math.Clamp(Math.Round(((double?)v ?? 0) * 255), 0, 255);
        var argb = Channel(c["alpha"]) << 24 | Channel(c["red"]) << 16 | Channel(c["green"]) << 8 | Channel(c["blue"]);
        var array = new JsonArray();
        foreach (var text in ColorSupport.Presentations(argb))
            array.Add(new JsonObject { ["label"] = text, ["textEdit"] = new JsonObject { ["range"] = p["range"]!.DeepClone(), ["newText"] = text } });
        return array;
    }
}
