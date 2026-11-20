using AxamlLs.Text;
using AxamlLs.Xaml;

namespace AxamlLs.Analysis;

/// <summary>One change to a token array: replace <c>DeleteCount</c> numbers at <c>Start</c> with <c>Data</c>.</summary>
public readonly record struct TokenEdit(int Start, int DeleteCount, int[] Data);

/// <summary>
/// Semantic tokens for style selectors, in the LSP encoding: five numbers per token (delta line, delta start, length,
/// type, modifiers). The type is the index in <see cref="TokenTypes"/>; unused classes get their own type.
/// </summary>
public static class SemanticTokens
{
    /// <summary>The legend. The Zed extension's semantic_token_rules.json maps these names to theme styles.</summary>
    public static readonly string[] TokenTypes = ["selectorType", "styleClass", "styleClassUnused", "pseudoClass", "templateSelector", "selectorName", "nestingSelector"];

    /// <summary>The encoded tokens of the document, or only those overlapping <paramref name="rangeStart"/>..<paramref name="rangeEnd"/>.</summary>
    public static int[] Encode(XamlAnalyzer analyzer, LineIndex lines, int rangeStart = 0, int rangeEnd = int.MaxValue)
    {
        var used = analyzer.StyleProvider?.Invoke()?.UsedClasses;
        var data = new List<int>();
        var (prevLine, prevChar) = (0, 0);
        foreach (var (token, start, end) in Selectors.DocumentTokens(analyzer.Result.Doc).OrderBy(t => t.Start))
        {
            if (end <= rangeStart || start >= rangeEnd) continue;
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
            data.Add(line - prevLine);
            data.Add(line == prevLine ? ch - prevChar : ch);
            data.Add(endCh - ch);
            data.Add(type);
            data.Add(0);
            (prevLine, prevChar) = (line, ch);
        }
        return [.. data];
    }

    /// <summary>
    /// The one edit that turns <paramref name="old"/> into <paramref name="now"/>: everything between their common prefix and
    /// common suffix, both cut at whole tokens. None when they are equal.
    /// </summary>
    public static TokenEdit[] Diff(int[] old, int[] now)
    {
        var prefix = 0;
        var max = Math.Min(old.Length, now.Length);
        while (prefix < max && old[prefix] == now[prefix]) prefix++;
        if (prefix == old.Length && prefix == now.Length) return [];
        prefix -= prefix % 5;
        var suffix = 0;
        while (suffix < max - prefix && old[^(suffix + 1)] == now[^(suffix + 1)]) suffix++;
        suffix -= suffix % 5;
        return [new TokenEdit(prefix, old.Length - prefix - suffix, now[prefix..(now.Length - suffix)])];
    }

    /// <summary>Applies edits to a token array (what the editor does with a delta).</summary>
    public static int[] Apply(int[] old, IEnumerable<TokenEdit> edits)
    {
        var result = old.ToList();
        foreach (var e in edits.OrderByDescending(e => e.Start))
        {
            result.RemoveRange(e.Start, e.DeleteCount);
            result.InsertRange(e.Start, e.Data);
        }
        return [.. result];
    }
}
