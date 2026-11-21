using AxamlLs.Workspace;
using AxamlLs.Xaml;
using Microsoft.CodeAnalysis;

namespace AxamlLs.Analysis;

public static partial class Completion
{
    private static void CompleteInMarkup(XamlAnalyzer analyzer, XmlElement el, MarkupExtension ext, int offset, List<CompletionItem> list, ITypeSymbol? expected = null)
    {
        // Extension name: {Bin|
        if (offset <= ext.NameEnd)
        {
            foreach (var name in MarkupExtensions)
            {
                list.Add(new CompletionItem(name, CompletionItemKind.Keyword, "markup extension", null, name, false, ext.NameStart, ext.NameEnd, "0" + name));
            }
            foreach (var (label, type) in CustomMarkupExtensions(analyzer, el))
            {
                list.Add(new CompletionItem(label, CompletionItemKind.Class, type.ToDisplayString(), SymbolDocs.Summary(type), label, false, ext.NameStart, ext.NameEnd, "1" + label));
            }
            return;
        }

        MarkupArg? current = null;
        foreach (var arg in ext.Args)
        {
            if (arg.Start <= offset && offset <= Math.Max(arg.End, arg.ValueEnd)) current = arg;
        }
        if (current?.Nested != null && current.Nested.Start < offset && offset <= current.Nested.End && (!current.Nested.Closed || offset < current.Nested.End))
        {
            CompleteInMarkup(analyzer, el, current.Nested, offset, list);
            return;
        }

        var isBinding = ext.LocalName is "Binding" or "CompiledBinding" or "ReflectionBinding" && ext.Prefix == null;
        var isResource = ext.LocalName is "StaticResource" or "DynamicResource";

        // Cursor on a key ("Mo|de=") or in a blank argument slot ("{Binding Foo, |"): offer keys / positional values.
        if (current == null || current.Key != null && offset <= current.KeyEnd)
        {
            var start = current?.KeyStart ?? offset;
            var end = current?.KeyEnd ?? offset;
            if (isBinding)
            {
                foreach (var key in BindingKeys)
                {
                    if (ext.Named(key) != null) continue;
                    list.Add(new CompletionItem(key, CompletionItemKind.Property, "Binding." + key, null, key + "=", false, start, end, "1" + key));
                }
                if (ext.Positional(0) == null && ext.Named("Path") == null)
                    CompleteBindingPath(analyzer, el, ext, new MappedText("", [offset]), offset, list);
            }
            else if (isResource)
            {
                CompleteResourceKeys(analyzer, start, end, list, expected);
            }
            else if (ext.LocalName == "TemplateBinding")
            {
                CompleteTemplateProperties(analyzer, el, start, end, list);
            }
            else if (ext.Prefix is { } prefix && el.LookupNamespace(prefix) is { } extNs && extNs != XamlNamespaces.X && analyzer.Project is { } p)
            {
                // A custom extension: its settable properties as Key=.
                var type = p.ResolveType(extNs, ext.LocalName + "Extension") ?? p.ResolveType(extNs, ext.LocalName);
                if (type != null)
                {
                    foreach (var prop in PropertyLookup.SettableProperties(type).Where(pr => ext.Named(pr.Name) == null))
                        list.Add(new CompletionItem(prop.Name, CompletionItemKind.Property, prop.Type.ToDisplayString(), SymbolDocs.Summary(prop), prop.Name + "=", false, start, end, "1" + prop.Name));
                }
            }
            return;
        }

        var value = current.Value;
        if (value == null) return;
        var (ws, we) = Word(value, offset, allowColon: true);
        if (current.Key == null)
        {
            // Positional value.
            if (isBinding && current == ext.Positional(0)) CompleteBindingPath(analyzer, el, ext, value, offset, list);
            else if (isResource) CompleteResourceKeys(analyzer, value.Start, value.End, list, expected);
            else if (ext.LocalName == "TemplateBinding") CompleteTemplateProperties(analyzer, el, ws, we, list);
            else if (ext.LocalName == "RelativeSource") AddWords(list, ["Self", "TemplatedParent", "FindAncestor", "DataContext"], "RelativeSourceMode", ws, we);
            else if (ext.LocalName is "Type" or "Static") CompleteTypeNames(analyzer, el, ws, we, list);
            else if (Assets.AcceptsMarkup(ext.LocalName) is { } accepts) Assets.Complete(analyzer, value, offset, accepts, list);
            return;
        }
        switch (current.Key)
        {
            case "Path" when isBinding:
                CompleteBindingPath(analyzer, el, ext, value, offset, list);
                break;
            case "Mode" when isBinding:
                AddWords(list, ["Default", "OneWay", "TwoWay", "OneTime", "OneWayToSource"], "BindingMode", ws, we);
                break;
            case "Mode" when ext.LocalName == "RelativeSource":
                AddWords(list, ["Self", "TemplatedParent", "FindAncestor", "DataContext"], "RelativeSourceMode", ws, we);
                break;
            case "UpdateSourceTrigger":
                AddWords(list, ["Default", "PropertyChanged", "LostFocus", "Explicit"], "UpdateSourceTrigger", ws, we);
                break;
            case "ElementName":
                foreach (var name in analyzer.Result.Names.Keys)
                    list.Add(new CompletionItem(name, CompletionItemKind.Variable, "<" + analyzer.Result.Names[name].Name + ">", null, name, false, ws, we));
                break;
            case "DataType":
            case "AncestorType":
                CompleteTypeNames(analyzer, el, ws, we, list);
                break;
            case "Converter":
                CompleteResourceKeys(analyzer, value.Start, value.End, list);
                break;
        }
    }

    /// <summary>Markup extensions from the document's own xmlns prefixes: {localization:Localize …}.</summary>
    internal static IEnumerable<(string Label, INamedTypeSymbol Type)> CustomMarkupExtensions(XamlAnalyzer analyzer, XmlElement el)
    {
        if (analyzer.Project is not { } p) yield break;
        foreach (var (prefix, ns) in el.DeclaredNamespaces())
        {
            if (prefix.Length == 0 || ns is XamlNamespaces.X or XamlNamespaces.Blend or XamlNamespaces.MarkupCompatibility or XamlNamespaces.Avalonia) continue;
            foreach (var type in p.TypesIn(ns))
            {
                if (type.IsAbstract || type.IsStatic || type.TypeKind != TypeKind.Class) continue;
                var isExtension = Members.InheritsByName(type, "MarkupExtension") ||
                                  Members.SelfAndBases(type).Any(t => t.GetMembers("ProvideValue").OfType<IMethodSymbol>().Any(m => m.DeclaredAccessibility == Accessibility.Public));
                if (!isExtension) continue;
                var name = type.Name.EndsWith("Extension", StringComparison.Ordinal) && type.Name.Length > 9 ? type.Name[..^9] : type.Name;
                yield return ($"{prefix}:{name}", type);
            }
        }
    }

    private static void CompleteResourceKeys(XamlAnalyzer analyzer, int start, int end, List<CompletionItem> list, ITypeSymbol? expected = null)
    {
        var want = expected != null ? ResourceValues.KindFor(expected) : null;
        var seen = new HashSet<string>();
        foreach (var r in analyzer.Resources())
        {
            if (!seen.Add(r.Key)) continue;
            var fits = want != null && ResourceValues.Fits(r, want);
            // Own resources first, then theme ones; within each, those that fit the property first.
            var rank = (r.Assembly == null ? 0 : 2) + (want == null || fits ? 0 : 1);
            var hex = ResourceValues.Hex(analyzer, r);
            list.Add(new CompletionItem(r.Key, hex != null ? CompletionItemKind.Color : CompletionItemKind.Value, ResourceDetail(r), ResourceDoc(r), r.Key, false, start, end, $"{rank}{r.Key}")
            {
                ColorHex = hex,
            });
        }
    }

    private static string ResourceDetail(ResourceEntry r) =>
        r.Assembly != null ? $"{r.ElementName} · {r.Assembly}"
        : r.ValuePreview != null ? $"<{r.ElementName}> {r.ValuePreview}" : $"<{r.ElementName}>";

    private static string? ResourceDoc(ResourceEntry r) =>
        r.Assembly != null ? $"From the compiled theme/library `{r.Assembly}`."
        : r.FilePath.Length > 0 ? $"Declared in `{Path.GetFileName(r.FilePath)}`" : null;
}
