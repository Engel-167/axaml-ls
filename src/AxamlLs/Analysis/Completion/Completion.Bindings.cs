using AxamlLs.Xaml;
using Microsoft.CodeAnalysis;

namespace AxamlLs.Analysis;

public static partial class Completion
{
    private static void CompleteBindingPath(XamlAnalyzer analyzer, XmlElement el, MarkupExtension ext, MappedText value, int offset, List<CompletionItem> list)
    {
        var idx = value.IndexOf(offset);
        var wordStartIdx = idx;
        while (wordStartIdx > 0 && BindingPath.IsIdentPart(value.Text[wordStartIdx - 1])) wordStartIdx--;
        var wordEndIdx = idx;
        while (wordEndIdx < value.Text.Length && BindingPath.IsIdentPart(value.Text[wordEndIdx])) wordEndIdx++;
        var start = value.Abs(wordStartIdx);
        var end = value.Abs(wordEndIdx);
        var before = wordStartIdx > 0 ? value.Text[wordStartIdx - 1] : '\0';

        // $parent[Typ|] or $parent[local:Typ|;2]
        var typeStart = idx;
        while (typeStart > 0 && (BindingPath.IsIdentPart(value.Text[typeStart - 1]) || value.Text[typeStart - 1] == ':')) typeStart--;
        if (typeStart > 0 && value.Text[typeStart - 1] == '[' && value.Text[..(typeStart - 1)].EndsWith("$parent", StringComparison.Ordinal))
        {
            var typeEnd = idx;
            while (typeEnd < value.Text.Length && (BindingPath.IsIdentPart(value.Text[typeEnd]) || value.Text[typeEnd] == ':')) typeEnd++;
            Selectors.CompleteTypes(analyzer, el, value.Abs(typeStart), value.Abs(typeEnd), list, pipe: false);
            return;
        }
        if (before == '#')
        {
            foreach (var name in analyzer.Result.Names.Keys)
                list.Add(new CompletionItem(name, CompletionItemKind.Variable, "<" + analyzer.Result.Names[name].Name + ">", null, name, false, start, end));
            return;
        }
        if (before == '$')
        {
            list.Add(new CompletionItem("parent", CompletionItemKind.Keyword, "$parent[Type] — an ancestor control", null, "parent", false, start, end));
            list.Add(new CompletionItem("self", CompletionItemKind.Keyword, "$self — the target control", null, "self", false, start, end));
            return;
        }

        var prefixText = value.Text[..wordStartIdx];
        var path = BindingPath.Parse(prefixText);
        var s = analyzer.ScopeAt(el);

        // Origin of the binding (ElementName / RelativeSource / DataType / x:DataType).
        ITypeSymbol? origin = s.DataType;
        XmlElement? originElement = null;
        if (ext.Named("ElementName")?.Value is { } en && analyzer.Result.Names.TryGetValue(en.Text.Trim(), out var named))
        {
            originElement = named;
            origin = analyzer.Result.Elements[named].Type;
        }
        else if (ext.Named("DataType") is { } dt)
        {
            var typeText = dt.Nested != null ? (dt.Nested.Named("TypeName") ?? dt.Nested.Positional(0))?.Value : dt.Value;
            origin = typeText != null ? analyzer.ResolveTypeName(el, typeText, report: false) : null;
        }
        else if (ext.Named("RelativeSource")?.Nested is { } rs)
        {
            var mode = (rs.Named("Mode") ?? rs.Positional(0))?.Value?.Text.Trim();
            if (rs.Named("AncestorType")?.Value is { } at) origin = analyzer.ResolveTypeName(el, at, report: false);
            else if (mode == "Self")
            {
                originElement = el;
                origin = analyzer.Result.Elements[el].Type;
            }
            else if (mode == "TemplatedParent") origin = s.TemplateTarget;
        }

        var type = analyzer.Walk(path, value, origin, originElement, el, validate: false, path.Segments.Count);
        if (prefixText.Trim().Length == 0)
        {
            list.Add(new CompletionItem("$parent", CompletionItemKind.Keyword, "an ancestor control: $parent[Type]", null, "\\$parent[$1]$0", true, start, end, "2$parent"));
            list.Add(new CompletionItem("$self", CompletionItemKind.Keyword, "the target control", null, "\\$self", true, start, end, "2$self"));
            foreach (var name in analyzer.Result.Names.Keys)
                list.Add(new CompletionItem("#" + name, CompletionItemKind.Variable, "<" + analyzer.Result.Names[name].Name + ">", null, "#" + name, false, start, end, "3" + name));
        }
        else if (!path.EndsExpectingMember)
        {
            return;
        }
        if (type == null || Members.IsOpaque(type)) return;
        AddBindableMembers(Members.UnwrapNullable(type), start, end, list);
    }

    private static void AddBindableMembers(ITypeSymbol type, int start, int end, List<CompletionItem> list)
    {
        var seen = new HashSet<string>();
        foreach (var t in Members.Hierarchy(type))
        {
            if (t.SpecialType == SpecialType.System_Object) continue;
            foreach (var m in t.GetMembers())
            {
                if (m.DeclaredAccessibility != Accessibility.Public || m.IsImplicitlyDeclared || m.IsStatic) continue;
                switch (m)
                {
                    case IPropertySymbol { IsIndexer: false } prop when seen.Add(prop.Name):
                        list.Add(new CompletionItem(prop.Name, CompletionItemKind.Property, prop.Type.ToDisplayString(), SymbolDocs.Summary(prop), prop.Name, false, start, end, "0" + prop.Name));
                        break;
                    case IMethodSymbol { MethodKind: MethodKind.Ordinary } method when !method.Name.StartsWith("get_", StringComparison.Ordinal) && seen.Add(method.Name):
                        list.Add(new CompletionItem(method.Name, CompletionItemKind.Method, method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), SymbolDocs.Summary(method), method.Name, false, start, end, "1" + method.Name));
                        break;
                }
            }
        }
    }
}
