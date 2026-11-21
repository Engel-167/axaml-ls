using System.Text;
using AxamlLs.Xaml;
using Microsoft.CodeAnalysis;

namespace AxamlLs.Analysis;

public sealed record HoverInfo(int Start, int End, string Markdown);

/// <summary>Hover text for the symbol, element name, resource key, selector, compact value or color under the cursor.</summary>
public static class Hover
{
    public static HoverInfo? At(XamlAnalyzer analyzer, int offset)
    {
        var r = analyzer.Result.RefAt(offset);
        if (r != null) return SymbolHover(r);
        return ResourceHover(analyzer, offset) ?? Selectors.Hover(analyzer, offset) ?? CompactValueHover(analyzer, offset) ?? ColorHover(analyzer, offset);
    }

    private static HoverInfo? SymbolHover(SymbolRef r)
    {
        var sb = new StringBuilder();
        if (r.TargetElement != null)
        {
            var nameAttr = XamlAnalyzer.XAttr(r.TargetElement, "Name") ?? r.TargetElement.Attr("Name");
            sb.Append("```xml\n<").Append(r.TargetElement.Name);
            if (nameAttr != null) sb.Append(' ').Append(nameAttr.Name).Append("=\"").Append(nameAttr.Value).Append('"');
            sb.Append(" />\n```\n");
            if (r.Type != null) sb.Append("Element of type `").Append(Members.FullDisplay(r.Type)).Append('`');
            return new HoverInfo(r.Start, r.End, sb.ToString());
        }
        if (r.Symbol == null) return null;

        sb.Append("```csharp\n").Append(SymbolDocs.Describe(r.Symbol)).Append("\n```");
        if (r.Symbol is not ITypeSymbol && r.Type != null && !SymbolEqualityComparer.Default.Equals(r.Type, Members.ValueTypeOf(r.Symbol)))
            sb.Append("\n\nResolved type: `").Append(Members.FullDisplay(r.Type)).Append('`');
        var doc = SymbolDocs.Summary(r.Symbol);
        if (doc != null) sb.Append("\n\n").Append(doc);
        return new HoverInfo(r.Start, r.End, sb.ToString());
    }

    /// <summary>A resource key in a use or in its x:Key: where it is declared, and a swatch when it is a color.</summary>
    private static HoverInfo? ResourceHover(XamlAnalyzer analyzer, int offset)
    {
        var found = ResourceValues.KeyAt(analyzer, offset) is { } use ? (use.Key, use.Start, use.End) : ((string, int, int)?)null;
        if (found == null && Targets.Find(analyzer, offset) is { Target: ResourceKeyTarget decl } at) found = (decl.Key, at.Start, at.End);
        if (found is not var (key, start, end)) return null;

        var entries = analyzer.Resources().Where(e => e.Key == key).ToList();
        if (entries.Count == 0)
            return new HoverInfo(start, end, $"Resource `{key}` is not declared in this project's .axaml files (it may come from a theme or a library).");
        var blocks = new List<string>();
        foreach (var e in entries)
        {
            if (e.Assembly != null)
            {
                blocks.Add($"Resource `{e.Key}` ({e.ElementName}) from the compiled theme/library `{e.Assembly}`");
                continue;
            }
            var block = $"```xml\n<{e.ElementName} x:Key=\"{e.Key}\"{(e.ValuePreview != null ? " " + e.ValuePreview : "")} />\n```\n`{Path.GetFileName(e.FilePath)}` line {e.Line + 1}";
            if (ResourceValues.Argb(analyzer, e) is { } argb) block = $"{ColorSupport.Swatch(argb)} `{ColorSupport.Hex(argb)}`\n\n{block}";
            blocks.Add(block);
        }
        return new HoverInfo(start, end, string.Join("\n\n", blocks));
    }

    /// <summary>Thickness, CornerRadius and BoxShadows values as a table of what every side means.</summary>
    private static HoverInfo? CompactValueHover(XamlAnalyzer analyzer, int offset)
    {
        if (analyzer.Project == null || analyzer.Result.Doc.ElementAt(offset) is not { } el) return null;
        var attr = el.Attributes.FirstOrDefault(a => a.HasValue && a.ValueStart <= offset && offset <= a.ValueEnd);
        if (attr == null || MarkupExtension.IsMarkup(attr.Value) || attr.Name.StartsWith("xmlns", StringComparison.Ordinal)) return null;
        var type = Members.ValueTypeOf(PropertyLookup.TargetProperty(analyzer, el, attr)) is { } t ? Members.UnwrapNullable(t) : null;
        var parts = InlayHints.Parts(type?.Name, MappedText.FromAttribute(attr));
        var lines = InlayHints.Expand(type?.Name, parts);
        if (lines.Count == 0) return null;
        var text = string.Join("\n\n", lines.Select(l => "`" + l + "`"));
        return new HoverInfo(attr.ValueStart, attr.ValueEnd, $"**{type!.Name}**\n\n{text}");
    }

    /// <summary>A color literal: a swatch and the same color as hex, rgb() and hsl().</summary>
    private static HoverInfo? ColorHover(XamlAnalyzer analyzer, int offset)
    {
        foreach (var c in DocumentColors.Find(analyzer))
        {
            if (offset < c.Start || offset > c.End) continue;
            return new HoverInfo(c.Start, c.End, $"{ColorSupport.Swatch(c.Argb)} " + string.Join(" · ", ColorSupport.Presentations(c.Argb).Select(p => $"`{p}`")));
        }
        return null;
    }
}
