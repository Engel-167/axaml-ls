using System.Text.Json.Nodes;
using AxamlLs.Analysis;
using AxamlLs.Text;
using AxamlLs.Xaml;

namespace AxamlLs.Lsp.Handlers;

/// <summary><c>textDocument/documentSymbol</c>: the outline, one nested symbol per element.</summary>
internal sealed class DocumentSymbolHandler(Documents documents) : SyncRequestHandler
{
    public override string Method => "textDocument/documentSymbol";

    public override void Advertise(JsonObject capabilities) => capabilities["documentSymbolProvider"] = true;

    protected override JsonNode? Handle(JsonObject p)
    {
        if (!documents.TryAnalyze(p, out _, out var analysis)) return new JsonArray();
        return Convert(Outline.Build(analysis.Analyzer), analysis.Lines);
    }

    private static JsonArray Convert(IEnumerable<OutlineSymbol> symbols, LineIndex lines)
    {
        var array = new JsonArray();
        foreach (var s in symbols)
        {
            var node = new JsonObject
            {
                ["name"] = s.Name,
                ["kind"] = s.Kind,
                ["range"] = Protocol.Range(lines, s.Start, s.End),
                ["selectionRange"] = Protocol.Range(lines, s.SelectionStart, s.SelectionEnd),
                ["children"] = Convert(s.Children, lines),
            };
            if (s.Detail != null) node["detail"] = s.Detail;
            array.Add(node);
        }
        return array;
    }
}

/// <summary><c>textDocument/foldingRange</c>: elements, start tags, comments and <c>#region</c> blocks.</summary>
internal sealed class FoldingRangeHandler(Documents documents) : SyncRequestHandler
{
    public override string Method => "textDocument/foldingRange";

    public override void Advertise(JsonObject capabilities) => capabilities["foldingRangeProvider"] = true;

    protected override JsonNode? Handle(JsonObject p)
    {
        var array = new JsonArray();
        if (!documents.TryAnalyze(p, out _, out var analysis)) return array;
        foreach (var r in Folding.Compute(analysis.Analyzer, analysis.Lines))
        {
            var node = new JsonObject { ["startLine"] = r.StartLine, ["endLine"] = r.EndLine };
            if (r.Kind != null) node["kind"] = r.Kind;
            array.Add(node);
        }
        return array;
    }
}

/// <summary><c>textDocument/linkedEditingRange</c>: the start and end tag names of the element under the cursor, edited together.</summary>
internal sealed class LinkedEditingRangeHandler(Documents documents) : SyncRequestHandler
{
    public override string Method => "textDocument/linkedEditingRange";

    public override void Advertise(JsonObject capabilities) => capabilities["linkedEditingRangeProvider"] = true;

    protected override JsonNode? Handle(JsonObject p)
    {
        if (!documents.TryGet(Protocol.DocumentUri(p), out var doc)) return null;
        var (text, lines, parsed) = Documents.CurrentText(doc);
        var offset = Protocol.Offset(lines, p["position"]!);
        if (LinkedTags.At(parsed ?? XmlDoc.Parse(text), text, offset) is not var (open, close)) return null;
        return new JsonObject
        {
            ["ranges"] = new JsonArray(Protocol.Range(lines, open.Start, open.End), Protocol.Range(lines, close.Start, close.End)),
            ["wordPattern"] = @"[A-Za-z_][\w.:-]*",
        };
    }
}

/// <summary><c>textDocument/onTypeFormatting</c>: typing '/' before '&gt;' in &lt;Button …&gt;&lt;/Button&gt; removes the now-redundant end tag.</summary>
internal sealed class OnTypeFormattingHandler(Documents documents) : SyncRequestHandler
{
    public override string Method => "textDocument/onTypeFormatting";

    public override void Advertise(JsonObject capabilities) =>
        capabilities["documentOnTypeFormattingProvider"] = new JsonObject { ["firstTriggerCharacter"] = "/" };

    protected override JsonNode? Handle(JsonObject p)
    {
        if ((string?)p["ch"] != "/" || !documents.TryGet(Protocol.DocumentUri(p), out var doc)) return null;
        var (text, lines, _) = Documents.CurrentText(doc);
        var offset = Protocol.Offset(lines, p["position"]!);
        if (Features.SelfClosingEdit(text, offset) is not { } e) return null;
        return new JsonArray(new JsonObject { ["range"] = Protocol.Range(lines, e.Start, e.End), ["newText"] = "" });
    }
}
