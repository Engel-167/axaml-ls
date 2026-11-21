using AxamlLs.Workspace;
using AxamlLs.Xaml;
using Microsoft.CodeAnalysis;

namespace AxamlLs.Analysis;

/// <summary>Resource keys under the cursor, the kind of resource a property takes, and the color a resource holds.</summary>
public static class ResourceValues
{
    /// <summary>The resource key under the cursor in {StaticResource Key} / {DynamicResource Key}.</summary>
    public static (string Key, int Start, int End)? KeyAt(XamlAnalyzer analyzer, int offset)
    {
        var doc = analyzer.Result.Doc;
        foreach (var el in doc.AllElements)
        {
            foreach (var attr in el.Attributes)
            {
                if (!attr.HasValue || offset < attr.ValueStart || offset > attr.ValueEnd || !MarkupExtension.IsMarkup(attr.Value)) continue;
                var ext = MarkupExtension.Parse(MappedText.FromAttribute(attr));
                if (ext == null) return null;
                foreach (var e in ext.SelfAndNested())
                {
                    if (e.LocalName is not ("StaticResource" or "DynamicResource")) continue;
                    var arg = (e.Named("ResourceKey") ?? e.Positional(0))?.Value;
                    if (arg != null && arg.Start <= offset && offset <= arg.End) return (arg.Text.Trim(), arg.Start, arg.End);
                }
                return null;
            }
        }
        return null;
    }

    /// <summary>The resource "kind" a property type takes: Brush, Color, Thickness, double… (null = no idea).</summary>
    internal static string? KindFor(ITypeSymbol type)
    {
        var name = type.Name;
        if (name is "IBrush" or "IImmutableBrush" || name.EndsWith("Brush", StringComparison.Ordinal)) return "Brush";
        if (type.SpecialType == SpecialType.System_Double) return "Double";
        return name is "Color" or "Thickness" or "CornerRadius" or "FontFamily" or "FontWeight" or "Geometry" or "StreamGeometry" or "IImage" or "TimeSpan"
            ? name.Replace("Stream", "").Replace("IImage", "Image") : name;
    }

    internal static bool Fits(ResourceEntry r, string want)
    {
        if (r.Assembly != null)
        {
            return want == "Double" ? r.ElementName is "FontSize" or "Opacity" : r.ElementName == want ||
                   want == "Geometry" && r.ElementName == "Glyph" || want == "TimeSpan" && r.ElementName == "Duration";
        }
        var local = r.ElementName[(r.ElementName.IndexOf(':') + 1)..];
        return want switch
        {
            "Brush" => local.EndsWith("Brush", StringComparison.Ordinal),
            "Double" => local == "Double",
            "Geometry" => local.EndsWith("Geometry", StringComparison.Ordinal),
            "Image" => local is "Bitmap" or "DrawingImage" or "SvgImage" || local.EndsWith("Image", StringComparison.Ordinal),
            _ => local == want,
        };
    }

    /// <summary>The color of a project resource such as &lt;SolidColorBrush Color="#FF6600"/&gt; or &lt;Color&gt;Red&lt;/Color&gt;.</summary>
    internal static string? Hex(XamlAnalyzer analyzer, ResourceEntry r) =>
        ResourceValues.Argb(analyzer, r) is { } argb ? ColorSupport.Hex(argb) : null;

    internal static uint? Argb(XamlAnalyzer analyzer, ResourceEntry r)
    {
        if (r.Assembly != null || r.ValuePreview == null) return null;
        var local = r.ElementName[(r.ElementName.IndexOf(':') + 1)..];
        if (local is not ("SolidColorBrush" or "Color")) return null;
        var value = r.ValuePreview.StartsWith("Color=\"", StringComparison.Ordinal) ? r.ValuePreview[7..^1] : r.ValuePreview;
        return ColorSupport.TryParse(value, ColorSupport.KnownColors(analyzer.Project), out var argb) ? argb : null;
    }
}
