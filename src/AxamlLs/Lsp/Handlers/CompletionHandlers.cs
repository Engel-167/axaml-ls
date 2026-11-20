using System.Text.Json.Nodes;
using AxamlLs.Analysis;

namespace AxamlLs.Lsp.Handlers;

/// <summary><c>textDocument/completion</c>: elements, attributes, values, binding paths, selectors and file paths.</summary>
internal sealed class CompletionHandler(Documents documents) : SyncRequestHandler
{
    private const int ColorKind = 16;
    private const int SnippetFormat = 2;

    public override string Method => "textDocument/completion";

    public override void Advertise(JsonObject capabilities) => capabilities["completionProvider"] = new JsonObject
    {
        ["triggerCharacters"] = new JsonArray("<", ".", "{", " ", ":", "$", "#", "=", "\"", ",", "/", "["),
        ["resolveProvider"] = true,
    };

    protected override JsonNode? Handle(JsonObject p)
    {
        if (!documents.TryAnalyzeAt(p, out _, out var analysis, out var offset)) return null;
        var items = new JsonArray();
        var incomplete = false;
        foreach (var c in Completion.At(analysis.Analyzer, offset))
        {
            var item = new JsonObject
            {
                ["label"] = c.Label,
                ["kind"] = (int)c.Kind,
                ["textEdit"] = new JsonObject { ["range"] = Protocol.Range(analysis.Lines, c.ReplaceStart, Math.Max(c.ReplaceEnd, c.ReplaceStart)), ["newText"] = c.InsertText },
                ["filterText"] = c.Label,
            };
            if (c.Detail != null) item["detail"] = c.Detail;
            if (c.ColorHex != null)
            {
                // LSP convention for swatches: kind Color with the color as plain-string documentation.
                item["kind"] = ColorKind;
                item["documentation"] = c.ColorHex;
            }
            else if (c.Documentation != null)
            {
                item["documentation"] = new JsonObject { ["kind"] = "markdown", ["value"] = c.Documentation };
            }
            if (c.IsSnippet) item["insertTextFormat"] = SnippetFormat;
            if (c.SortText != null) item["sortText"] = c.SortText;
            if (c.PreviewPath != null) item["data"] = new JsonObject { ["preview"] = c.PreviewPath, ["root"] = analysis.Analyzer.ProjectDirectory };
            if (c.Kind is CompletionItemKind.File or CompletionItemKind.Folder) incomplete = true;
            items.Add(item);
        }
        // File lists depend on the typed name (deep matches), so the editor asks again as it changes.
        return new JsonObject { ["isIncomplete"] = incomplete, ["items"] = items };
    }
}

/// <summary><c>completionItem/resolve</c>: a file's preview, only for the item the editor highlights (images are big as data: URIs).</summary>
internal sealed class CompletionResolveHandler : SyncRequestHandler
{
    public override string Method => "completionItem/resolve";

    protected override JsonNode? Handle(JsonObject item)
    {
        var result = (JsonObject)item.DeepClone();
        if ((string?)item["data"]?["preview"] is { } path && Assets.PreviewMarkdown(path, (string?)item["data"]?["root"]) is { } markdown)
            result["documentation"] = new JsonObject { ["kind"] = "markdown", ["value"] = markdown };
        return result;
    }
}
