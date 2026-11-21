using System.Text.Json.Nodes;
using AxamlLs.Analysis.Formatting;

namespace AxamlLs.Lsp.Handlers;

/// <summary>
/// <c>textDocument/formatting</c> and <c>textDocument/rangeFormatting</c>: whitespace-only layout (see
/// <see cref="XamlLayout"/>). A document that is not well formed gets no edits.
/// </summary>
internal sealed class FormattingHandler(Documents documents, Session session, bool range) : SyncRequestHandler
{
    public override string Method => range ? "textDocument/rangeFormatting" : "textDocument/formatting";

    public override void Advertise(JsonObject capabilities) =>
        capabilities[range ? "documentRangeFormattingProvider" : "documentFormattingProvider"] = true;

    protected override JsonNode? Handle(JsonObject p)
    {
        var edits = new JsonArray();
        if (!documents.TryAnalyze(p, out var doc, out var analysis)) return edits;
        if (analysis.Analyzer.WellFormed.Error is { } error)
        {
            Log.Debug($"Not formatting {doc.Path}: not well formed ({error.Message})");
            return edits;
        }
        var options = new FormattingOptions(
            (int?)p["options"]?["tabSize"] ?? 4,
            (bool?)p["options"]?["insertSpaces"] ?? true,
            session.Options.Formatting);
        var (start, end) = range ? Protocol.Offsets(analysis.Lines, p["range"]!) : (0, int.MaxValue);
        foreach (var change in XamlLayout.Format(analysis.Analyzer.Result.Doc, options, start, end))
            edits.Add(new JsonObject { ["range"] = Protocol.Range(analysis.Lines, change.Start, change.End), ["newText"] = change.NewText });
        return edits;
    }
}
