using System.Text.Json.Nodes;
using AxamlLs.Analysis;
using AxamlLs.Analysis.Refactorings;
using AxamlLs.Workspace;

namespace AxamlLs.Lsp.Handlers;

/// <summary>
/// <c>textDocument/codeAction</c>: quick fixes for the diagnostics in the requested range, recomputed here rather than
/// taken from the client's copies (so they match the current text), then the refactorings that apply at the selection
/// (F10). A refactoring is listed by title only; its edit is computed by <c>codeAction/resolve</c>, or right away for
/// editors that can't resolve.
/// </summary>
internal sealed class CodeActionHandler(Documents documents, Session session) : SyncRequestHandler
{
    public override string Method => "textDocument/codeAction";

    public override void Advertise(JsonObject capabilities) =>
        capabilities["codeActionProvider"] = new JsonObject
        {
            ["codeActionKinds"] = new JsonArray("quickfix", "refactor.rewrite", "refactor.extract"),
            ["resolveProvider"] = true,
        };

    protected override JsonNode? Handle(JsonObject p)
    {
        var actions = new JsonArray();
        if (!documents.TryAnalyze(p, out var doc, out var analysis)) return actions;
        var (start, end) = Protocol.Offsets(analysis.Lines, p["range"]!);
        var only = (p["context"]?["only"] as JsonArray)?.Select(k => (string?)k).OfType<string>().ToList() ?? [];
        bool Wanted(string kind) => only.Count == 0 || only.Any(k => kind == k || kind.StartsWith(k + ".", StringComparison.Ordinal));
        var textOf = documents.TextProvider();
        if (Wanted("quickfix")) QuickFixActions(p, analysis, start, end, textOf, actions);

        var file = new XamlFile(doc.Path, analysis.Analyzer.Result.Doc.Text, analysis.Analyzer, analysis.Lines);
        foreach (var refactoring in Refactorings.All)
        {
            if (!Wanted(refactoring.Kind)) continue;
            try
            {
                if (refactoring.TitleAt(file, start, end, CodeActionResolveHandler.Settings(session)) is not { } title) continue;
                var action = new JsonObject { ["title"] = title, ["kind"] = refactoring.Kind };
                if (session.Client.CodeActionResolve)
                {
                    action["data"] = new JsonObject { ["id"] = refactoring.Id, ["uri"] = doc.Uri, ["version"] = analysis.Version, ["start"] = start, ["end"] = end };
                }
                else
                {
                    var edits = refactoring.Edits(file, start, end, CodeActionResolveHandler.Settings(session));
                    if (edits.Count == 0) continue;
                    action["edit"] = Edits.ToWorkspaceEdit(edits, textOf);
                }
                actions.Add(action);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Error($"Refactoring {refactoring.Id} failed", ex);
            }
        }
        return actions;
    }

    private static void QuickFixActions(JsonObject p, DocumentAnalysis analysis, int start, int end, Func<string, (string Text, AxamlLs.Text.LineIndex Lines)> textOf, JsonArray actions)
    {
        var wanted = (p["context"]?["diagnostics"] as JsonArray)?.Select(d => (string?)d?["code"]).OfType<string>().ToHashSet(StringComparer.Ordinal) ?? [];
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
    }
}

/// <summary>
/// <c>codeAction/resolve</c>: computes a listed refactoring's edit. A document that changed since the listing answers
/// "content modified": the offsets in the action no longer fit.
/// </summary>
internal sealed class CodeActionResolveHandler(Documents documents, Session session) : SyncRequestHandler
{
    public override string Method => "codeAction/resolve";

    internal static RefactoringSettings Settings(Session session) =>
        new(session.Options.Formatting.AttributeOrder ?? SortAttributes.DefaultOrder);

    protected override JsonNode? Handle(JsonObject parameters)
    {
        // A copy: the parameters belong to the request message.
        var action = parameters.DeepClone().AsObject();
        if (action["edit"] != null || action["data"] is not JsonObject data) return action;
        if (Refactorings.ById((string?)data["id"] ?? "") is not { } refactoring) return action;
        if (!documents.TryGet((string?)data["uri"] ?? "", out var doc)) return action;
        var analysis = documents.Analyze(doc);
        if (analysis.Version != (int?)data["version"]) throw new ContentModifiedException();
        var file = new XamlFile(doc.Path, analysis.Analyzer.Result.Doc.Text, analysis.Analyzer, analysis.Lines);
        var edits = refactoring.Edits(file, (int?)data["start"] ?? 0, (int?)data["end"] ?? 0, Settings(session));
        action["edit"] = Edits.ToWorkspaceEdit(edits, documents.TextProvider());
        return action;
    }
}
