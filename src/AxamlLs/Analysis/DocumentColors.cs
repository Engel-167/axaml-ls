using AxamlLs.Xaml;

namespace AxamlLs.Analysis;

public sealed record ColorSpan(int Start, int End, uint Argb);

/// <summary>Color literals in the document, for the editor's swatches and color picker.</summary>
public static class DocumentColors
{
    /// <summary>Color literals in attribute values of Brush/Color properties: Background="#FF6600", Color="Red".</summary>
    public static List<ColorSpan> Find(XamlAnalyzer analyzer)
    {
        var result = new List<ColorSpan>();
        var known = ColorSupport.KnownColors(analyzer.Project);
        foreach (var el in analyzer.Result.Doc.AllElements)
        {
            foreach (var attr in el.Attributes)
            {
                if (!attr.HasValue || attr.Value.Length == 0 || MarkupExtension.IsMarkup(attr.Value) || attr.Name.StartsWith("xmlns", StringComparison.Ordinal)) continue;
                // BoxShadow="1 2 4 0 #66000000": a swatch for the color piece of each shadow.
                if (analyzer.Project != null && Members.ValueTypeOf(PropertyLookup.TargetProperty(analyzer, el, attr)) is { Name: "BoxShadows" or "BoxShadow" })
                {
                    foreach (var (s, e) in InlayHints.ShadowColors(attr.Value))
                    {
                        if (ColorSupport.TryParse(attr.Value[s..e], known, out var shadowColor))
                            result.Add(new ColorSpan(attr.Map[s], attr.Map[e], shadowColor));
                    }
                    continue;
                }
                if (!ColorSupport.TryParse(attr.Value, known, out var argb)) continue;
                var isHex = attr.Value.TrimStart().StartsWith('#');
                if (analyzer.Project != null)
                {
                    // Only where a color is meant ("Red" is also a valid string for Text="Red").
                    var prop = PropertyLookup.TargetProperty(analyzer, el, attr);
                    var type = Members.ValueTypeOf(prop);
                    if (type == null ? !isHex : ResourceValues.KindFor(Members.UnwrapNullable(type)) is not ("Brush" or "Color")) continue;
                }
                else if (!isHex)
                {
                    continue;
                }
                var trimmed = attr.Value.Trim();
                var lead = attr.Value.IndexOf(trimmed, StringComparison.Ordinal);
                result.Add(new ColorSpan(attr.Map[lead], attr.Map[lead + trimmed.Length], argb));
            }
        }
        return result;
    }
}
