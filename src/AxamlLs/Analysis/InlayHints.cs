using AxamlLs.Xaml;
using Microsoft.CodeAnalysis;

namespace AxamlLs.Analysis;

/// <summary>A label shown before a piece of a value: Padding="[l:]7 [t:]2 [r:]7 [b:]2".</summary>
public readonly record struct InlayHint(int Offset, string Label, bool PadRight, bool PadLeft = false, bool IsType = false);

/// <summary>Which hints are shown (the client can switch groups off through initializationOptions.inlayHints).</summary>
public sealed record InlayHintOptions(bool BindingSource = true)
{
    public static readonly InlayHintOptions Default = new();
}

/// <summary>
/// Inlay hints that name the parts of compact values, the way Rider does:
///   Thickness     "7" → all · "7 2" → lr, tb · "7 2 7 2" → l, t, r, b
///   CornerRadius  "9" → all · "9 4" → top, bottom · "1 2 3 4" → tl, tr, br, bl
///   BoxShadows    "[inset] x y [blur [spread]] color", several separated by commas
///   Grid ColumnDefinitions / RowDefinitions   "Auto,*,2*" → "0:" "1:" "2:" before each entry, and → * after Grid.Column="1"
///   {Binding Path}   the type the path starts from: (MainViewModel).Path
/// and the implicit key of a markup extension's first argument: {Binding [Path=]Name}, {StaticResource [ResourceKey=]Key}.
/// </summary>
public static class InlayHints
{
    private static readonly Dictionary<string, string> PositionalKeys = new(StringComparer.Ordinal)
    {
        ["Binding"] = "Path", ["CompiledBinding"] = "Path", ["ReflectionBinding"] = "Path",
        ["StaticResource"] = "ResourceKey", ["DynamicResource"] = "ResourceKey",
        ["TemplateBinding"] = "Property", ["RelativeSource"] = "Mode",
        ["x:Static"] = "Member", ["x:Type"] = "TypeName",
    };

    public static List<InlayHint> Compute(XamlAnalyzer analyzer, int start, int end, Func<XmlElement, XmlAttr, ISymbol?> targetProperty, InlayHintOptions? options = null)
    {
        options ??= InlayHintOptions.Default;
        var hints = new List<InlayHint>();
        foreach (var el in analyzer.Result.Doc.AllElements)
        {
            if (el.End < start || el.Start > end) continue;
            foreach (var attr in el.Attributes)
            {
                if (!attr.HasValue || attr.ValueEnd < start || attr.ValueStart > end || attr.Value.Trim().Length == 0) continue;
                var text = MappedText.FromAttribute(attr);
                if (MarkupExtension.IsMarkup(attr.Value))
                {
                    if (MarkupExtension.Parse(text) is { } ext) MarkupHints(analyzer, ext, hints, options);
                    continue;
                }
                if (attr.Name.StartsWith("xmlns", StringComparison.Ordinal)) continue;
                if (GridIndexHint(el, attr) is { } gridHint) hints.Add(gridHint);
                var type = Members.ValueTypeOf(targetProperty(el, attr)) is { } t ? Members.UnwrapNullable(t) : null;
                foreach (var part in Parts(type?.Name, text))
                {
                    if (part.Short != null) hints.Add(new InlayHint(part.Start, part.Short + ":", true));
                }
            }
        }
        return hints.Where(h => h.Offset >= start && h.Offset <= end).OrderBy(h => h.Offset).ToList();
    }

    private static void MarkupHints(XamlAnalyzer analyzer, MarkupExtension ext, List<InlayHint> hints, InlayHintOptions options)
    {
        var name = ext.Prefix == "x" ? "x:" + ext.LocalName : ext.Prefix == null ? ext.LocalName : null;
        if (name != null && PositionalKeys.TryGetValue(name, out var key) && ext.Positional(0) is { } first && first.ValueStart < first.ValueEnd)
            hints.Add(new InlayHint(first.ValueStart, key + "=", false));
        if (options.BindingSource && ext.Prefix == null && ext.LocalName is "Binding" or "CompiledBinding" or "ReflectionBinding" &&
            (ext.Named("Path") ?? ext.Positional(0))?.Value is { } path && path.Text.Trim() is { Length: > 0 } pathText && !"$#!(".Contains(pathText[0]) &&
            analyzer.BindingOriginAt(ext.NameStart) is { } origin && !Members.IsOpaque(origin))
        {
            // Added after the "Path=" hint, which sits at the same offset.
            hints.Add(new InlayHint(path.Start, $"({Members.Display(Members.UnwrapNullable(origin))}).", false, IsType: true));
        }
        foreach (var arg in ext.Args)
        {
            if (arg.Nested != null) MarkupHints(analyzer, arg.Nested, hints, options);
        }
    }

    /// <summary>
    /// One piece of a compact value: where it is, its text, the short label hints show (null = no hint),
    /// the full name hover shows, and which shadow it belongs to (BoxShadows only).
    /// </summary>
    public readonly record struct ValuePart(int Start, int End, string Text, string? Short, string Full, int Group = 0);

    private static readonly string[][] ThicknessShort = [["all"], ["lr", "tb"], ["l", "t", "r", "b"]];
    private static readonly string[][] CornerShort = [["all"], ["top", "bottom"], ["tl", "tr", "br", "bl"]];

    /// <summary>
    /// The pieces of a Thickness, CornerRadius or BoxShadows value. Inlay hints and hover both read this, so they always agree.
    /// A shape Avalonia does not accept (three numbers…) gives no parts: no guessing.
    /// </summary>
    public static List<ValuePart> Parts(string? typeName, MappedText text)
    {
        var parts = new List<ValuePart>();
        switch (typeName)
        {
            case "Thickness":
                Label(Numbers(text), parts, ThicknessShort, ["Left", "Top", "Right", "Bottom"]);
                break;
            case "CornerRadius":
                Label(Numbers(text), parts, CornerShort, ["TopLeft", "TopRight", "BottomRight", "BottomLeft"]);
                break;
            case "BoxShadows" or "BoxShadow":
                BoxShadowParts(text, parts);
                break;
            case "ColumnDefinitions" or "RowDefinitions":
                DefinitionParts(typeName == "ColumnDefinitions" ? "Column" : "Row", text, parts);
                break;
        }
        return parts;
    }

    private static void Label(List<(int Start, int End, string Text)> pieces, List<ValuePart> parts, string[][] shortByCount, string[] full)
    {
        var labels = shortByCount.FirstOrDefault(l => l.Length == pieces.Count);
        if (labels == null) return;
        for (var i = 0; i < pieces.Count; i++) parts.Add(new ValuePart(pieces[i].Start, pieces[i].End, pieces[i].Text, labels[i], full[i]));
    }

    /// <summary>What every side means: "Left 7 · Top 2 · Right 7 · Bottom 2" for "7 2", "tl…" for corners, one line per shadow.</summary>
    public static List<string> Expand(string? typeName, List<ValuePart> parts)
    {
        string[] sides = typeName switch { "Thickness" => ["Left", "Top", "Right", "Bottom"], "CornerRadius" => ["TopLeft", "TopRight", "BottomRight", "BottomLeft"], _ => [] };
        if (sides.Length > 0)
        {
            string[] v = parts.Count switch
            {
                1 => [parts[0].Text, parts[0].Text, parts[0].Text, parts[0].Text],
                2 when typeName == "Thickness" => [parts[0].Text, parts[1].Text, parts[0].Text, parts[1].Text],
                2 => [parts[0].Text, parts[0].Text, parts[1].Text, parts[1].Text],
                4 => [.. parts.Select(x => x.Text)],
                _ => [],
            };
            return v.Length == 4 ? [string.Join(" · ", sides.Select((n, i) => $"{n} {v[i]}"))] : [];
        }
        return [.. parts.GroupBy(x => x.Group).Select(g => string.Join(" · ", g.Select(x => $"{x.Full} {x.Text}".Trim())))];
    }

    /// <summary>The space- or comma-separated pieces of a value, with absolute offsets.</summary>
    private static List<(int Start, int End, string Text)> Numbers(MappedText text) => Split(text, 0, text.Text.Length, c => char.IsWhiteSpace(c) || c == ',');

    /// <summary>"Auto,*,2*": one part per entry, labelled with its index.</summary>
    private static void DefinitionParts(string what, MappedText text, List<ValuePart> parts)
    {
        var entries = Split(text, 0, text.Text.Length, c => c == ',');
        for (var i = 0; i < entries.Count; i++)
        {
            var (start, end, entry) = entries[i];
            var trimmed = entry.Trim();
            if (trimmed.Length == 0) continue;
            var lead = entry.IndexOf(trimmed, StringComparison.Ordinal);
            parts.Add(new ValuePart(start + lead, start + lead + trimmed.Length, trimmed, i.ToString(), $"{what} {i}"));
        }
    }

    /// <summary>Grid.Column="2" → "→ 2*": the definition of that column, when the grid lists its definitions as an attribute.</summary>
    private static InlayHint? GridIndexHint(XmlElement el, XmlAttr attr)
    {
        if (attr.Name is not ("Grid.Column" or "Grid.Row") || el.Parent is not { LocalName: "Grid" } grid || !int.TryParse(attr.Value.Trim(), out var index) || index < 0) return null;
        var isColumn = attr.Name == "Grid.Column";
        var definitions = grid.Attr(isColumn ? "ColumnDefinitions" : "RowDefinitions");
        if (definitions is not { HasValue: true } || MarkupExtension.IsMarkup(definitions.Value)) return null;
        var parts = Parts(isColumn ? "ColumnDefinitions" : "RowDefinitions", MappedText.FromAttribute(definitions));
        return index < parts.Count ? new InlayHint(attr.ValueEnd + 1, "→ " + parts[index].Text, false, PadLeft: true) : null;
    }

    private static void BoxShadowParts(MappedText text, List<ValuePart> parts)
    {
        // Shadows are separated by commas outside parentheses (a color can be rgb(…)).
        var depth = 0;
        var shadowStart = 0;
        var group = 0;
        var labels = new[] { "x", "y", "blur", "spread" };
        for (var i = 0; i <= text.Text.Length; i++)
        {
            var c = i < text.Text.Length ? text.Text[i] : ',';
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == ',' && depth <= 0)
            {
                var n = 0;
                var seenColor = false;
                foreach (var (start, end, part) in Split(text, shadowStart, i, char.IsWhiteSpace))
                {
                    if (part.Equals("inset", StringComparison.OrdinalIgnoreCase)) parts.Add(new ValuePart(start, end, part, null, "inset", group));
                    else if (!seenColor && double.TryParse(part, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
                    {
                        if (n < labels.Length) parts.Add(new ValuePart(start, end, part, labels[n], labels[n], group));
                        n++;
                    }
                    else
                    {
                        parts.Add(new ValuePart(start, end, part, null, "color", group)); // the color
                        seenColor = true;
                    }
                }
                shadowStart = i + 1;
                group++;
            }
        }
    }

    /// <summary>The color piece of each shadow in a BoxShadows value ("1 2 4 Red, inset 0 0 8 #66000000"), as indexes into the text.</summary>
    public static IEnumerable<(int Start, int End)> ShadowColors(string text)
    {
        var depth = 0;
        var shadowStart = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            var c = i < text.Length ? text[i] : ',';
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == ',' && depth <= 0)
            {
                // Pieces of this shadow: the first that is neither "inset" nor a number is the color.
                var j = shadowStart;
                while (j < i)
                {
                    while (j < i && char.IsWhiteSpace(text[j])) j++;
                    if (j >= i) break;
                    var s = j;
                    var d = 0;
                    while (j < i && (d > 0 || !char.IsWhiteSpace(text[j])))
                    {
                        if (text[j] == '(') d++;
                        else if (text[j] == ')') d--;
                        j++;
                    }
                    var piece = text[s..j];
                    if (piece.Equals("inset", StringComparison.OrdinalIgnoreCase) ||
                        double.TryParse(piece, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)) continue;
                    yield return (s, j);
                    break;
                }
                shadowStart = i + 1;
            }
        }
    }

    private static List<(int Start, int End, string Text)> Split(MappedText text, int from, int to, Func<char, bool> separator)
    {
        var parts = new List<(int, int, string)>();
        var depth = 0;
        var i = from;
        while (i < to)
        {
            while (i < to && separator(text.Text[i])) i++;
            if (i >= to) break;
            var s = i;
            while (i < to && (depth > 0 || !separator(text.Text[i])))
            {
                if (text.Text[i] == '(') depth++;
                else if (text.Text[i] == ')') depth--;
                i++;
            }
            parts.Add((text.Abs(s), text.Abs(i), text.Text[s..i]));
        }
        return parts;
    }
}
