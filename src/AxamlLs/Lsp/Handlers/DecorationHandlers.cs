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
        foreach (var h in Features.InlayHintsIn(analysis.Analyzer, start, end, session.Options.InlayHints))
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

/// <summary><c>textDocument/semanticTokens/full</c>: style selectors colored by part; unused classes get their own token type.</summary>
internal sealed class SemanticTokensHandler(Documents documents) : SyncRequestHandler
{
    /// <summary>Custom token types; the Zed extension's semantic_token_rules.json maps them to theme styles. The index is the type's number.</summary>
    private static readonly string[] TokenTypes = ["selectorType", "styleClass", "styleClassUnused", "pseudoClass", "templateSelector", "selectorName", "nestingSelector"];

    public override string Method => "textDocument/semanticTokens/full";

    public override void Advertise(JsonObject capabilities) => capabilities["semanticTokensProvider"] = new JsonObject
    {
        ["legend"] = new JsonObject
        {
            ["tokenTypes"] = new JsonArray([.. TokenTypes.Select(t => (JsonNode)t)]),
            ["tokenModifiers"] = new JsonArray(),
        },
        ["full"] = true,
    };

    protected override JsonNode? Handle(JsonObject p)
    {
        if (!documents.TryAnalyze(p, out _, out var analysis)) return null;
        var lines = analysis.Lines;
        var used = analysis.Analyzer.StyleProvider?.Invoke()?.UsedClasses;
        var data = new JsonArray();
        var (prevLine, prevChar) = (0, 0);
        foreach (var (token, start, end) in Selectors.DocumentTokens(analysis.Analyzer.Result.Doc).OrderBy(t => t.Start))
        {
            var type = token.Kind switch
            {
                SelectorTokenKind.Type => 0,
                SelectorTokenKind.Class => used == null || used.Contains(token.Text[1..]) ? 1 : 2,
                SelectorTokenKind.PseudoClass => 3,
                SelectorTokenKind.Template => 4,
                SelectorTokenKind.Name => 5,
                SelectorTokenKind.Nesting => 6,
                _ => -1,
            };
            if (type < 0) continue;
            var (line, ch) = lines.GetPosition(start);
            var (endLine, endCh) = lines.GetPosition(end);
            if (endLine != line || endCh <= ch) continue;
            // LSP encoding: delta line, delta start (relative on the same line), length, type, modifiers.
            data.Add(line - prevLine);
            data.Add(line == prevLine ? ch - prevChar : ch);
            data.Add(endCh - ch);
            data.Add(type);
            data.Add(0);
            (prevLine, prevChar) = (line, ch);
        }
        return new JsonObject { ["data"] = data };
    }
}
