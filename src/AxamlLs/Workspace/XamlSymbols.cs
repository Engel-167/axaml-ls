using AxamlLs.Analysis;
using AxamlLs.Text;
using AxamlLs.Xaml;

namespace AxamlLs.Workspace;

/// <summary>What a workspace symbol names.</summary>
public enum XamlSymbolKind
{
    /// <summary>A view: the root's <c>x:Class</c>.</summary>
    View,
    ResourceKey,
    ElementName,
    /// <summary>A <c>&lt;Style Selector="…"&gt;</c>, named by its selector.</summary>
    Style,
    ControlTheme,
    /// <summary>A <c>&lt;DataTemplate DataType="…"&gt;</c>, named by its data type.</summary>
    DataTemplate,
}

/// <summary>A name declared in an .axaml file, for workspace symbol search.</summary>
/// <param name="Name">What the user searches for.</param>
/// <param name="Kind">What it names.</param>
/// <param name="Path">The file.</param>
/// <param name="Start">The declaring value's start.</param>
/// <param name="End">Its end.</param>
/// <param name="Line">The 0-based line of <paramref name="Start"/>.</param>
/// <param name="Character">The UTF-16 column of <paramref name="Start"/>.</param>
/// <param name="EndLine">The line of <paramref name="End"/>.</param>
/// <param name="EndCharacter">The column of <paramref name="End"/>.</param>
/// <param name="Container">Where it lives: the element type for keys and names, the namespace for a view.</param>
public sealed record XamlSymbol(string Name, XamlSymbolKind Kind, string Path, int Start, int End, int Line, int Character, int EndLine, int EndCharacter, string? Container);

/// <summary>Reads the symbols of one file from its parse tree only (no Roslyn), so it is cheap to cache per file.</summary>
public static class XamlSymbols
{
    public static XamlSymbol[] Scan(string path, string text) => Scan(path, XmlDoc.Parse(text));

    /// <summary>The symbols of an already parsed file (the open document's analysis).</summary>
    public static XamlSymbol[] Scan(string path, XmlDoc doc)
    {
        var text = doc.Text;
        LineIndex? lines = null;
        var found = new List<XamlSymbol>();
        void Add(string name, XamlSymbolKind kind, XmlAttr attr, string? container)
        {
            if (name.Length == 0 || MarkupExtension.IsMarkup(name)) return;
            lines ??= new LineIndex(text);
            var lead = attr.Value.Length - attr.Value.TrimStart().Length;
            var start = attr.Map[Math.Min(lead, attr.Map.Length - 1)];
            var end = attr.Map[Math.Min(lead + name.Length, attr.Map.Length - 1)];
            var (line, character) = lines.GetPosition(start);
            var (endLine, endCharacter) = lines.GetPosition(end);
            found.Add(new XamlSymbol(name, kind, path, start, end, line, character, endLine, endCharacter, container));
        }

        if (doc.Root is { } root && XamlAnalyzer.XAttr(root, "Class") is { HasValue: true } cls)
        {
            var full = cls.Value.Trim();
            var dot = full.LastIndexOf('.');
            var simple = full[(dot + 1)..];
            // The span covers the simple name only: that is what the search matched.
            Add(simple, XamlSymbolKind.View, Shift(cls, dot + 1), dot < 0 ? null : full[..dot]);
        }
        foreach (var el in doc.AllElements)
        {
            if (XamlAnalyzer.XAttr(el, "Key") is { HasValue: true } key) Add(key.Value.Trim(), XamlSymbolKind.ResourceKey, key, el.LocalName);
            if ((XamlAnalyzer.XAttr(el, "Name") ?? el.Attr("Name")) is { HasValue: true } name) Add(name.Value.Trim(), XamlSymbolKind.ElementName, name, el.LocalName);
            switch (el.LocalName)
            {
                case "Style" when el.Attr("Selector") is { HasValue: true } selector:
                    Add(selector.Value.Trim(), XamlSymbolKind.Style, selector, null);
                    break;
                case "ControlTheme" when (XamlAnalyzer.XAttr(el, "Key") ?? el.Attr("TargetType")) is { HasValue: true } theme:
                    Add(theme.Value.Trim(), XamlSymbolKind.ControlTheme, theme, el.Attr("TargetType")?.Value.Trim());
                    break;
                case "DataTemplate" when (el.Attr("DataType") ?? XamlAnalyzer.XAttr(el, "DataType")) is { HasValue: true } type:
                    Add(type.Value.Trim(), XamlSymbolKind.DataTemplate, type, null);
                    break;
            }
        }
        return [.. found];
    }

    /// <summary>The attribute seen from <paramref name="by"/> characters into its trimmed value (for x:Class's simple name).</summary>
    private static XmlAttr Shift(XmlAttr attr, int by)
    {
        if (by <= 0) return attr;
        var lead = attr.Value.Length - attr.Value.TrimStart().Length;
        var from = Math.Min(lead + by, attr.Value.Length);
        return new XmlAttr
        {
            Owner = attr.Owner,
            Name = attr.Name,
            NameStart = attr.NameStart,
            HasValue = true,
            ValueStart = attr.Map[from],
            ValueEnd = attr.ValueEnd,
            Value = attr.Value[from..],
            Map = attr.Map[from..],
        };
    }

    /// <summary>
    /// The best matches for a query across the given symbols, ranked by fuzzy score, then shorter names, then path
    /// and position (deterministic). An empty query lists everything by name.
    /// </summary>
    public static IReadOnlyList<XamlSymbol> Search(IEnumerable<XamlSymbol> symbols, string query, int limit)
    {
        var scored = new List<(XamlSymbol Symbol, int Score)>();
        foreach (var s in symbols)
        {
            if (FuzzyMatch.Score(s.Name, query) is { } score) scored.Add((s, score));
        }
        return [.. scored
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Symbol.Name.Length)
            .ThenBy(x => x.Symbol.Name, StringComparer.Ordinal)
            .ThenBy(x => x.Symbol.Path, StringComparer.Ordinal)
            .ThenBy(x => x.Symbol.Start)
            .Take(limit)
            .Select(x => x.Symbol)];
    }
}
