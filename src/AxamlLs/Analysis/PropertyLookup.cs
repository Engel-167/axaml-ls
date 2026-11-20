using AxamlLs.Workspace;
using AxamlLs.Xaml;
using Microsoft.CodeAnalysis;

namespace AxamlLs.Analysis;

/// <summary>Which C# property an attribute (or a Setter) sets, and which properties a type offers to XAML.</summary>
internal static class PropertyLookup
{
    /// <summary>
    /// The property an attribute value is for: the attribute's own property, or for &lt;Setter Property="X" Value="…"/&gt;
    /// property X of the styled type (Style Selector / ControlTheme TargetType).
    /// </summary>
    internal static ISymbol? TargetProperty(XamlAnalyzer analyzer, XmlElement el, XmlAttr attr)
    {
        var info = analyzer.Result.Elements.GetValueOrDefault(el);
        if (el.LocalName == "Setter" && attr.LocalName == "Value")
        {
            var propName = el.Attr("Property")?.Value.Trim();
            if (string.IsNullOrEmpty(propName)) return null;
            var dot = propName.LastIndexOf('.');
            if (dot > 0)
            {
                var owner = analyzer.ResolveTypeName(el, new MappedText(propName[..dot], [.. Enumerable.Range(0, dot + 1)]), report: false);
                return owner == null ? null : Members.FindAttached(owner, propName[(dot + 1)..]) ?? Members.FindSettable(owner, propName[(dot + 1)..]);
            }
            var target = Selectors.StyledType(analyzer, el) ?? analyzer.Project?.ResolveType(XamlNamespaces.Avalonia, "Control");
            return target == null ? null : Members.FindSettable(target, propName);
        }
        if (info?.Type == null) return null;
        var local = attr.LocalName;
        var d = local.IndexOf('.');
        if (d > 0)
        {
            var owner = analyzer.ResolveTypeName(el, new MappedText(local[..d], [.. Enumerable.Range(0, d + 1)]), report: false);
            return owner == null ? null : Members.FindAttached(owner, local[(d + 1)..]) ?? Members.FindSettable(owner, local[(d + 1)..]);
        }
        return Members.FindSettable(info.Type, local);
    }

    /// <summary>The XxxProperty fields of a type and its bases: (Xxx, field, value type).</summary>
    internal static IEnumerable<(string Name, IFieldSymbol Field, ITypeSymbol? ValueType)> AvaloniaProperties(ITypeSymbol type)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in Members.SelfAndBases(type))
        {
            foreach (var f in t.GetMembers().OfType<IFieldSymbol>())
            {
                if (!f.IsStatic || f.DeclaredAccessibility != Accessibility.Public || !f.Name.EndsWith("Property", StringComparison.Ordinal) || f.Name.Length <= 8) continue;
                if (!Members.IsAvaloniaProperty(f.Type)) continue;
                var name = f.Name[..^8];
                if (!seen.Add(name)) continue;
                var valueType = f.Type is INamedTypeSymbol { TypeArguments.Length: > 0 } g ? g.TypeArguments[^1] : null;
                yield return (name, f, valueType);
            }
        }
    }

    internal static IEnumerable<IPropertySymbol> SettableProperties(ITypeSymbol type) =>
        Members.SelfAndBases(type).SelectMany(t => t.GetMembers()).OfType<IPropertySymbol>()
            .Where(p => p.DeclaredAccessibility == Accessibility.Public && !p.IsStatic && !p.IsIndexer &&
                        (p.SetMethod is { DeclaredAccessibility: Accessibility.Public } || Members.ElementType(p.Type) != null))
            .DistinctBy(p => p.Name);
}
