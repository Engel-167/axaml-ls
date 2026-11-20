using AxamlLs.Workspace;
using AxamlLs.Xaml;
using Microsoft.CodeAnalysis;

namespace AxamlLs.Analysis;

public static partial class Completion
{
    private static void CompleteAttributeValue(XamlAnalyzer analyzer, XmlElement el, XmlAttr attr, int offset, List<CompletionItem> list)
    {
        var p = analyzer.Project;
        var text = MappedText.FromAttribute(attr);
        var info = analyzer.Result.Elements.GetValueOrDefault(el);
        var isX = attr.Prefix != null && el.LookupNamespace(attr.Prefix) == XamlNamespaces.X;
        var prop = !isX && p != null ? PropertyLookup.TargetProperty(analyzer, el, attr) : null;
        var valueType = prop != null && !Members.IsEvent(prop) ? Members.ValueTypeOf(prop) is { } vt ? Members.UnwrapNullable(vt) : null : null;

        if (Selectors.SelectorAttr(el) == attr)
        {
            Selectors.Complete(analyzer, el, attr, offset, list, analyzer.Styles());
            return;
        }
        // Classes="accent big|": class names (those styled for this control first), without the ones already there.
        if (attr.Name == "Classes" && !MarkupExtension.IsMarkup(attr.Value))
        {
            var (cs, ce) = ClassWord(text, offset);
            var present = attr.Value.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
            present.Remove(text.Text[text.IndexOf(cs)..text.IndexOf(ce)]);
            Selectors.CompleteClassNames(analyzer, el, info?.Type, analyzer.Styles(), cs, ce, list, c => c, exclude: present);
            return;
        }
        if (el.LocalName == "Setter" && attr.LocalName == "Property" && !MarkupExtension.IsMarkup(attr.Value))
        {
            CompleteSetterProperty(analyzer, el, text, offset, list);
            return;
        }
        if (MarkupExtension.IsMarkup(attr.Value))
        {
            var ext = MarkupExtension.Parse(text);
            if (ext != null) CompleteInMarkup(analyzer, el, ext, offset, list, valueType);
            return;
        }
        if (!isX && Assets.Accepts(el, prop) is { } accepts)
        {
            Assets.Complete(analyzer, text, offset, accepts, list);
            return;
        }

        var (wordStart, wordEnd) = Word(text, offset, allowColon: true);
        if (isX && attr.LocalName == "DataType" || !isX && attr.LocalName is "DataType" or "TargetType" or "AncestorType")
        {
            CompleteTypeNames(analyzer, el, wordStart, wordEnd, list);
            return;
        }
        if (p == null || prop == null) return;

        if (Members.IsEvent(prop))
        {
            if (analyzer.Result.ClassType == null) return;
            foreach (var m in Members.SelfAndBases(analyzer.Result.ClassType).SelectMany(t => t.GetMembers()).OfType<IMethodSymbol>()
                         .Where(m => m.MethodKind == MethodKind.Ordinary && m.Parameters.Length == 2).DistinctBy(m => m.Name))
            {
                list.Add(new CompletionItem(m.Name, CompletionItemKind.Method, m.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), null, m.Name, false, wordStart, wordEnd));
            }
            return;
        }
        if (valueType == null) return;
        CompleteValuesOfType(p, valueType, wordStart, wordEnd, list, attr.ValueStart, attr.ValueEnd);
        AddMarkupOpeners(analyzer, el, valueType, attr.ValueStart, attr.ValueEnd, list);
    }

    /// <summary>
    /// Markup extensions that can supply this value, listed after the literal values. Each one inserts
    /// "{Name |}" and the completion inside it takes over (binding paths, resource keys…).
    /// </summary>
    private static void AddMarkupOpeners(XamlAnalyzer analyzer, XmlElement el, ITypeSymbol valueType, int start, int end, List<CompletionItem> list)
    {
        void Add(string name, string detail, string sort, bool argument = true) =>
            list.Add(new CompletionItem($"{{{name}{(argument ? " …" : "")}}}", CompletionItemKind.Snippet, detail, null,
                argument ? $"{{{name} $1}}$0" : $"{{{name}}}$0", true, start, end, "9" + sort));

        Add("Binding", "bind to a member of the data context (x:DataType)", "a");
        if (ResourceValues.KindFor(valueType) is { } kind && analyzer.Resources().Any(r => ResourceValues.Fits(r, kind)))
        {
            Add("DynamicResource", "resource that follows theme/runtime changes", "b");
            Add("StaticResource", "resource resolved once at load", "c");
        }
        if (analyzer.ScopeAt(el).TemplateTarget != null) Add("TemplateBinding", "property of the templated control", "d");
        if (valueType.IsReferenceType || valueType is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T })
            Add("x:Null", "null", "e", argument: false);
    }

    /// <summary>
    /// Setter Property="|": the Avalonia properties of the styled type (styled and attached ones; direct properties
    /// cannot be set by styles), or "Grid.|" → the attached properties of Grid.
    /// </summary>
    private static void CompleteSetterProperty(XamlAnalyzer analyzer, XmlElement setter, MappedText text, int offset, List<CompletionItem> list)
    {
        var (ws, we) = Word(text, offset, allowColon: true);
        var typed = text.Text[text.IndexOf(ws)..text.IndexOf(offset)];
        var dot = typed.LastIndexOf('.');
        if (dot > 0)
        {
            var ownerText = typed[..dot];
            var owner = analyzer.ResolveTypeName(setter, new MappedText(ownerText, [.. Enumerable.Range(0, ownerText.Length + 1)]), report: false);
            if (owner == null) return;
            foreach (var (name, field, valueType) in PropertyLookup.AvaloniaProperties(owner).Where(x => x.Field.Type.Name == "AttachedProperty"))
                list.Add(new CompletionItem($"{ownerText}.{name}", CompletionItemKind.Property, Members.Display(valueType), SymbolDocs.Summary(field), $"{ownerText}.{name}", false, ws, we, "0" + name));
            return;
        }
        var target = Selectors.StyledType(analyzer, setter) ?? analyzer.Project?.ResolveType(XamlNamespaces.Avalonia, "Control");
        if (target == null) return;
        foreach (var (name, field, valueType) in PropertyLookup.AvaloniaProperties(target))
        {
            if (field.Type.Name == "DirectPropertyBase" || field.Type.Name.StartsWith("DirectProperty", StringComparison.Ordinal)) continue;
            var docs = Members.FindSettable(target, name) is { } clr ? SymbolDocs.Summary(clr) : SymbolDocs.Summary(field);
            var own = SymbolEqualityComparer.Default.Equals(field.ContainingType, target);
            list.Add(new CompletionItem(name, CompletionItemKind.Property, Members.Display(valueType), docs, name, false, ws, we, (own ? "0" : "1") + name));
        }
    }

    private static void CompleteValuesOfType(ProjectSnapshot p, ITypeSymbol type, int start, int end, List<CompletionItem> list, int valueStart = -1, int valueEnd = -1)
    {
        if (type.SpecialType == SpecialType.System_Boolean)
        {
            list.Add(new CompletionItem("True", CompletionItemKind.Keyword, "bool", null, "True", false, start, end));
            list.Add(new CompletionItem("False", CompletionItemKind.Keyword, "bool", null, "False", false, start, end));
            return;
        }
        if (type.TypeKind == TypeKind.Enum)
        {
            foreach (var f in type.GetMembers().OfType<IFieldSymbol>().Where(f => f.IsConst))
            {
                list.Add(new CompletionItem(f.Name, CompletionItemKind.EnumMember, type.Name, SymbolDocs.Summary(f), f.Name, false, start, end));
            }
            return;
        }
        // Brushes and colors accept the named colors (Background="Red").
        if (ResourceValues.KindFor(type) is "Brush" or "Color" && p.Compilation.GetTypeByMetadataName("Avalonia.Media.Colors") is { } colors)
        {
            var known = ColorSupport.KnownColors(p);
            foreach (var c in colors.GetMembers().OfType<IPropertySymbol>().Where(c => c.IsStatic))
            {
                var hex = known.TryGetValue(c.Name, out var argb) ? ColorSupport.Hex(argb) : null;
                list.Add(new CompletionItem(c.Name, hex != null ? CompletionItemKind.Color : CompletionItemKind.Value, hex ?? "named color", null, c.Name, false, start, end, "0" + c.Name)
                {
                    ColorHex = hex,
                });
            }
            // Zeroed templates for each syntax Avalonia parses, after the names and before the {…} openers.
            // A color literal is the whole value, so they replace all of it ("#", "rgb(" … are not word characters).
            if (valueStart >= 0) (start, end) = (valueStart, valueEnd);
            for (var i = 0; i < ColorSupport.Templates.Length; i++)
            {
                var (label, snippet, detail) = ColorSupport.Templates[i];
                list.Add(new CompletionItem(label, CompletionItemKind.Snippet, detail, null, snippet, true, start, end, $"8{i:D2}"));
            }
            return;
        }
        // Types with well-known static values of themselves: FontWeight.Bold, Cursor…
        foreach (var m in Members.SelfAndBases(type).Take(1).SelectMany(t => t.GetMembers()))
        {
            if (m is IPropertySymbol { IsStatic: true } sp && SymbolEqualityComparer.Default.Equals(sp.Type, type) ||
                m is IFieldSymbol { IsStatic: true, DeclaredAccessibility: Accessibility.Public } sf && SymbolEqualityComparer.Default.Equals(sf.Type, type))
            {
                list.Add(new CompletionItem(m.Name, CompletionItemKind.Value, type.Name, SymbolDocs.Summary(m), m.Name, false, start, end, "0" + m.Name));
            }
        }
    }

    private static void CompleteTemplateProperties(XamlAnalyzer analyzer, XmlElement el, int start, int end, List<CompletionItem> list)
    {
        if (analyzer.ScopeAt(el).TemplateTarget is not { } target) return;
        foreach (var prop in PropertyLookup.SettableProperties(target))
            list.Add(new CompletionItem(prop.Name, CompletionItemKind.Property, prop.Type.ToDisplayString(), SymbolDocs.Summary(prop), prop.Name, false, start, end));
    }
}
