using AxamlLs.Workspace;
using AxamlLs.Xaml;
using Microsoft.CodeAnalysis;

namespace AxamlLs.Analysis;

public static partial class Completion
{
    private static void CompleteElementNames(XamlAnalyzer analyzer, XmlElement el, int start, int end, List<CompletionItem> list)
    {
        var p = analyzer.Project;
        if (p == null) return;
        // A tag being typed from scratch gets its closing tag too:  <Button |>⏎</Button>
        // (an existing tag being renamed keeps only the name).
        var fresh = el.Attributes.Count == 0 && el.StartTagEnd < 0 && el.EndTagStart < 0;
        string Insert(string label, bool isPropertyElement) =>
            !fresh ? label : isPropertyElement ? $"{label}>$0\n</{label}>" : $"{label} $1>\n</{label}>";
        // Property elements of the parent: <Grid.RowDefinitions>.
        if (el.Parent != null && analyzer.Result.Elements.TryGetValue(el.Parent, out var parentInfo) && parentInfo.Type != null)
        {
            var prefix = el.Parent.Prefix != null ? el.Parent.Prefix + ":" : "";
            foreach (var prop in PropertyLookup.SettableProperties(parentInfo.Type))
            {
                var label = $"{prefix}{el.Parent.LocalName}.{prop.Name}";
                list.Add(new CompletionItem(label, CompletionItemKind.Property, prop.Type.ToDisplayString(), null, Insert(label, true), fresh, start, end, "0" + label));
            }
        }
        foreach (var (prefix, ns) in el.DeclaredNamespaces())
        {
            if (ns is XamlNamespaces.X or XamlNamespaces.Blend or XamlNamespaces.MarkupCompatibility) continue;
            foreach (var type in p.TypesIn(ns))
            {
                if (type.IsStatic || type.TypeKind is not (TypeKind.Class or TypeKind.Struct) || type.IsGenericType) continue;
                if (type.IsAbstract) continue;
                var label = prefix.Length == 0 ? type.Name : $"{prefix}:{type.Name}";
                list.Add(new CompletionItem(label, CompletionItemKind.Class, type.ContainingNamespace?.ToDisplayString(), SymbolDocs.Summary(type), Insert(label, false), fresh, start, end, "1" + label));
            }
        }
    }

    private static void CompleteAttributeNames(XamlAnalyzer analyzer, XmlElement el, int start, int end, List<CompletionItem> list)
    {
        var info = analyzer.Result.Elements.GetValueOrDefault(el);
        var present = el.Attributes.Select(a => a.Name).ToHashSet();
        var xPrefix = el.DeclaredNamespaces().FirstOrDefault(n => n.Ns == XamlNamespaces.X).Prefix ?? "x";
        void Add(string name, CompletionItemKind kind, string? detail, string? docs, string sort)
        {
            if (present.Contains(name)) return;
            list.Add(new CompletionItem(name, kind, detail, docs, name + "=\"$1\"$0", true, start, end, sort + name));
        }

        if (info?.Type != null)
        {
            foreach (var prop in PropertyLookup.SettableProperties(info.Type))
            {
                Add(prop.Name, CompletionItemKind.Property, prop.Type.ToDisplayString(), SymbolDocs.Summary(prop), "1");
            }
            foreach (var ev in Members.SelfAndBases(info.Type).SelectMany(t => t.GetMembers()).OfType<IEventSymbol>()
                         .Where(e => e.DeclaredAccessibility == Accessibility.Public && !e.IsStatic).DistinctBy(e => e.Name))
            {
                Add(ev.Name, CompletionItemKind.Event, ev.Type.ToDisplayString(), SymbolDocs.Summary(ev), "2");
            }
        }
        Add($"{xPrefix}:Name", CompletionItemKind.Keyword, "x:Name directive", null, "3");
        Add($"{xPrefix}:DataType", CompletionItemKind.Keyword, "x:DataType directive — the type compiled bindings resolve against", null, "3");
        Add($"{xPrefix}:CompileBindings", CompletionItemKind.Keyword, "x:CompileBindings directive", null, "3");
        Add($"{xPrefix}:Key", CompletionItemKind.Keyword, "x:Key directive", null, "3");
        if (el.Parent == null) Add($"{xPrefix}:Class", CompletionItemKind.Keyword, "x:Class directive", null, "3");
    }

    private static void CompleteTypeNames(XamlAnalyzer analyzer, XmlElement el, int start, int end, List<CompletionItem> list)
    {
        var p = analyzer.Project;
        if (p == null) return;
        foreach (var (prefix, ns) in el.DeclaredNamespaces())
        {
            if (ns is XamlNamespaces.X or XamlNamespaces.Blend or XamlNamespaces.MarkupCompatibility) continue;
            var own = ns.StartsWith("using:", StringComparison.Ordinal) || ns.StartsWith("clr-namespace:", StringComparison.Ordinal);
            foreach (var type in p.TypesIn(ns))
            {
                if (type.IsStatic || type.IsGenericType) continue;
                var label = prefix.Length == 0 ? type.Name : $"{prefix}:{type.Name}";
                list.Add(new CompletionItem(label, CompletionItemKind.Class, type.ToDisplayString(), SymbolDocs.Summary(type), label, false, start, end, (own ? "0" : "1") + label));
            }
        }
    }
}
