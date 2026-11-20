using System.Text.Json.Nodes;
using AxamlLs.Analysis;

namespace AxamlLs.Lsp.Handlers;

/// <summary>
/// <c>textDocument/codeAction</c>: quick fixes for the diagnostics in the requested range, recomputed here rather than
/// taken from the client's copies (so they match the current text).
/// </summary>
internal sealed class CodeActionHandler(Documents documents) : SyncRequestHandler
{
    public override string Method => "textDocument/codeAction";

    public override void Advertise(JsonObject capabilities) =>
        capabilities["codeActionProvider"] = new JsonObject { ["codeActionKinds"] = new JsonArray("quickfix") };

    protected override JsonNode? Handle(JsonObject p)
    {
        var actions = new JsonArray();
        if (!documents.TryAnalyze(p, out _, out var analysis)) return actions;
        var (start, end) = Protocol.Offsets(analysis.Lines, p["range"]!);
        var wanted = (p["context"]?["diagnostics"] as JsonArray)?.Select(d => (string?)d?["code"]).OfType<string>().ToHashSet(StringComparer.Ordinal) ?? [];
        var textOf = documents.TextProvider();
        foreach (var (d, _) in DocumentDiagnostics.Collect(analysis.Analyzer))
        {
            if (d.Start > end || d.End < start || wanted.Count > 0 && !wanted.Contains(d.Code)) continue;
            foreach (var fix in QuickFixes.For(analysis.Analyzer, d))
            {
                try
                {
                    var action = new JsonObject
                    {
                        ["title"] = fix.Title,
                        ["kind"] = "quickfix",
                        ["diagnostics"] = new JsonArray(Protocol.Diagnostic(d, analysis.Lines)),
                        ["edit"] = Edits.ToWorkspaceEdit(fix.Edits, textOf),
                    };
                    if (fix.Preferred) action["isPreferred"] = true;
                    actions.Add(action);
                }
                catch (IOException ex)
                {
                    Log.Error($"Quick fix '{fix.Title}' skipped", ex);
                }
            }
        }
        return actions;
    }
}
