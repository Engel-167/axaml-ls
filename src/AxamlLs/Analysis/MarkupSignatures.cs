using AxamlLs.Workspace;
using AxamlLs.Xaml;
using Microsoft.CodeAnalysis;

namespace AxamlLs.Analysis;

/// <summary>A markup extension argument: positional (<c>{StaticResource ResourceKey}</c>) or named (<c>Mode=</c>).</summary>
public sealed record MarkupParameter(string Name, bool Named, string? Type = null, string? Doc = null);

/// <summary>One way to write an extension: its positional parameters, then the properties it can set by name.</summary>
public sealed record MarkupSignature(string Extension, IReadOnlyList<MarkupParameter> Parameters, string? Doc = null)
{
    public int PositionalCount => Parameters.Count(p => !p.Named);

    /// <summary>
    /// <c>{Binding Path, Mode=…, Converter=…}</c>, with each parameter's span in the label (LSP parameter labels as
    /// offsets, since "Path" appears both positionally and as a name).
    /// </summary>
    public (string Label, (int Start, int End)[] Spans) Render()
    {
        var label = new System.Text.StringBuilder("{").Append(Extension);
        var spans = new (int, int)[Parameters.Count];
        for (var i = 0; i < Parameters.Count; i++)
        {
            label.Append(i == 0 ? " " : ", ");
            var start = label.Length;
            label.Append(Parameters[i].Name);
            spans[i] = (start, label.Length);
            if (Parameters[i].Named) label.Append("=…");
        }
        return (label.Append('}').ToString(), spans);
    }
}

/// <summary>The signatures to show, which one applies and which parameter the cursor is in (-1: none).</summary>
public sealed record MarkupSignatureHelp(IReadOnlyList<MarkupSignature> Signatures, int ActiveSignature, int ActiveParameter);

/// <summary>
/// Argument hints inside <c>{Ext …}</c> (features plan F6): the built-in extensions Avalonia knows, described here, and
/// custom ones read from the project (each public constructor, plus the settable properties as named arguments, with
/// their C# doc comments). In a nested extension the innermost one answers.
/// </summary>
public static class MarkupSignatures
{
    private static MarkupParameter P(string name, string doc) => new(name, Named: false, Doc: doc);

    private static MarkupParameter[] Named(params string[] names) => [.. names.Select(n => new MarkupParameter(n, Named: true))];

    private static readonly MarkupParameter[] BindingNamed = Named(Completion.BindingKeys);

    private static readonly Dictionary<string, MarkupSignature[]> BuiltIn = new(StringComparer.Ordinal)
    {
        ["Binding"] = Binding("Binding"),
        ["CompiledBinding"] = Binding("CompiledBinding"),
        ["ReflectionBinding"] = Binding("ReflectionBinding"),
        ["StaticResource"] = [new("StaticResource", [P("ResourceKey", "The x:Key of a resource, looked up once when the view loads.")])],
        ["DynamicResource"] = [new("DynamicResource", [P("ResourceKey", "The x:Key of a resource; the value follows changes (theme variants).")])],
        ["TemplateBinding"] = [new("TemplateBinding", [P("Property", "A property of the templated control."), .. Named("Converter", "ConverterParameter", "Mode")])],
        ["RelativeSource"] = [new("RelativeSource", [P("Mode", "Self, TemplatedParent, FindAncestor or DataContext."), .. Named("Mode", "AncestorType", "AncestorLevel", "Tree")])],
        ["OnPlatform"] = [new("OnPlatform", [P("Default", "The value on platforms not listed."), .. Named("Default", "Windows", "macOS", "Linux", "Android", "iOS", "Browser")])],
        ["OnFormFactor"] = [new("OnFormFactor", [P("Default", "The value on form factors not listed."), .. Named("Default", "Desktop", "Mobile")])],
        ["x:Static"] = [new("x:Static", [P("Member", "A static field or property: prefix:Type.Member.")])],
        ["x:Type"] = [new("x:Type", [P("TypeName", "A type: prefix:Type.")])],
    };

    private static MarkupSignature[] Binding(string name) =>
    [
        new(name, BindingNamed, "Binds to the data context."),
        new(name, [P("Path", "The path from the data context: Title, Items[0].Name, #Element.Text, $parent[Type].Prop."), .. BindingNamed], "Binds to the data context."),
    ];

    public static MarkupSignatureHelp? At(XamlAnalyzer analyzer, int offset)
    {
        var el = analyzer.Result.Doc.ElementAt(offset);
        if (el == null || el.StartTagEnd > 0 && offset >= el.StartTagEnd) return null;
        var attr = el.Attributes.FirstOrDefault(a => a.HasValue && a.ValueStart <= offset && offset <= a.ValueEnd);
        if (attr == null || !MarkupExtension.IsMarkup(attr.Value) || MarkupExtension.Parse(MappedText.FromAttribute(attr)) is not { } outer) return null;
        var ext = outer.SelfAndNested()
            .Where(x => x.Start < offset && (offset < x.End || !x.Closed && offset <= x.End))
            .MinBy(x => x.End - x.Start);
        if (ext == null || offset <= ext.NameEnd) return null;

        var signatures = SignaturesOf(analyzer, el, ext);
        if (signatures.Length == 0) return null;

        // The argument the cursor is in; between arguments (after a comma) it is the next positional one.
        var current = ext.Args.LastOrDefault(a => a.Start <= offset && offset <= Math.Max(a.End, a.ValueEnd));
        var positionalBefore = ext.Args.Count(a => a.Key == null && a.End < offset && a != current);
        var position = current is { Key: null } ? ext.Args.Where(a => a.Key == null).ToList().IndexOf(current) : positionalBefore;
        var wanted = current?.Key != null ? ext.Args.Count(a => a.Key == null) : Math.Max(position + 1, ext.Args.Count(a => a.Key == null));
        var active = Array.FindIndex(signatures, s => s.PositionalCount >= wanted);
        if (active < 0) active = signatures.Length - 1;
        var signature = signatures[active];
        int parameter;
        if (current?.Key is { } key)
        {
            parameter = -1;
            for (var i = 0; i < signature.Parameters.Count; i++)
            {
                if (signature.Parameters[i] is { Named: true } p && string.Equals(p.Name, key, StringComparison.Ordinal)) parameter = i;
            }
        }
        else
        {
            parameter = position < signature.PositionalCount ? position : -1;
        }
        return new MarkupSignatureHelp(signatures, active, parameter);
    }

    private static MarkupSignature[] SignaturesOf(XamlAnalyzer analyzer, XmlElement el, MarkupExtension ext)
    {
        if (ext.Prefix == null && BuiltIn.TryGetValue(ext.LocalName, out var builtIn)) return builtIn;
        if (ext.Prefix is { } prefix && el.LookupNamespace(prefix) == XamlNamespaces.X) return BuiltIn.TryGetValue("x:" + ext.LocalName, out var x) ? x : [];
        if (ext.Prefix is not { } custom || el.LookupNamespace(custom) is not { } ns || analyzer.Project is not { } project) return [];
        if ((project.ResolveType(ns, ext.LocalName + "Extension") ?? project.ResolveType(ns, ext.LocalName)) is not INamedTypeSymbol type) return [];

        var named = PropertyLookup.SettableProperties(type)
            .Select(p => new MarkupParameter(p.Name, Named: true, p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), SymbolDocs.Summary(p)))
            .ToList();
        var typeDoc = SymbolDocs.Summary(type);
        var constructors = type.InstanceConstructors.Where(c => c.DeclaredAccessibility == Accessibility.Public).OrderBy(c => c.Parameters.Length).ToList();
        if (constructors.Count == 0) return [new MarkupSignature(ext.Name, named, typeDoc)];
        return [.. constructors.Select(c => new MarkupSignature(ext.Name,
            [.. c.Parameters.Select(p => new MarkupParameter(p.Name, Named: false, p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), ParameterDoc(type, p))), .. named],
            SymbolDocs.Summary(c) ?? typeDoc))];
    }

    /// <summary>A constructor parameter is usually stored in the property of the same name (primary constructors): its doc.</summary>
    private static string? ParameterDoc(INamedTypeSymbol type, IParameterSymbol parameter) =>
        PropertyLookup.SettableProperties(type).FirstOrDefault(p => string.Equals(p.Name, parameter.Name, StringComparison.OrdinalIgnoreCase)) is { } prop
            ? SymbolDocs.Summary(prop)
            : null;
}
