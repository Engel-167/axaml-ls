using AxamlLs.Xaml;

namespace AxamlLs.Analysis;

/// <summary>An entry of the document outline (LSP DocumentSymbol), in absolute offsets.</summary>
public sealed record OutlineSymbol(string Name, string? Detail, int Kind, int Start, int End, int SelectionStart, int SelectionEnd, List<OutlineSymbol> Children);

/// <summary>The document outline: one symbol per element, nested, named by what identifies it (name, key, selector, classes).</summary>
public static class Outline
{
    // LSP SymbolKind values.
    public const int Namespace = 3, Class = 5, Property = 7, Field = 8, Constant = 14, Object = 19;

    public static List<OutlineSymbol> Build(XamlAnalyzer analyzer)
    {
        var result = new List<OutlineSymbol>();
        foreach (var top in analyzer.Result.Doc.TopLevel) Add(analyzer, top, result, isRoot: true);
        return result;
    }

    private static void Add(XamlAnalyzer analyzer, XmlElement el, List<OutlineSymbol> into, bool isRoot)
    {
        var info = analyzer.Result.Elements.GetValueOrDefault(el);
        if (info is { Ignored: true }) return; // d: design-time elements and undeclared prefixes

        // <Grid.ColumnDefinitions> is not a symbol of its own, but what it contains is part of the Grid.
        if (info is { IsPropertyElement: true } || el.LocalName.Contains('.'))
        {
            foreach (var child in el.Children) Add(analyzer, child, into, isRoot: false);
            return;
        }

        var (name, kind) = Describe(analyzer, el, isRoot);
        var symbol = new OutlineSymbol(name, info?.Type?.ToDisplayString(), kind, el.Start, Math.Max(el.End, el.NameEnd), el.NameStart, el.NameEnd, []);
        into.Add(symbol);
        foreach (var child in el.Children) Add(analyzer, child, symbol.Children, isRoot: false);
    }

    private static (string Name, int Kind) Describe(XamlAnalyzer analyzer, XmlElement el, bool isRoot)
    {
        var key = XamlAnalyzer.XAttr(el, "Key") is { HasValue: true } k && !MarkupExtension.IsMarkup(k.Value) ? k.Value.Trim() : null;
        var elementName = Targets.ElementName(el);

        if (el.LocalName == "Style")
        {
            var selector = Selectors.SelectorAttr(el)?.Value.Trim();
            return (selector is { Length: > 0 } ? $"{el.Name} {selector}" : el.Name, Namespace);
        }
        if (el.LocalName == "ControlTheme")
        {
            var target = el.Attr("TargetType") is { HasValue: true } t ? TypeText(t.Value) : null;
            var id = key ?? target;
            return (id != null ? $"{el.Name} [{id}]" : el.Name, Namespace);
        }
        if (el.LocalName == "Setter")
        {
            var property = el.Attr("Property")?.Value.Trim();
            return (string.IsNullOrEmpty(property) ? el.Name : $"{el.Name} {property}", Property);
        }

        if (isRoot)
        {
            var cls = XamlAnalyzer.XAttr(el, "Class")?.Value.Trim();
            var simple = cls?[(cls.LastIndexOf('.') + 1)..];
            return (string.IsNullOrEmpty(simple) ? el.Name : $"{el.Name} {simple}", Class);
        }

        if (key != null) return ($"{el.Name} [{key}]", Constant);

        var parts = new List<string> { el.Name };
        if (elementName != null) parts.Add("#" + elementName);
        if (el.Attr("Classes") is { HasValue: true } classes && !MarkupExtension.IsMarkup(classes.Value))
            parts.AddRange(classes.Value.Split([' ', '\t', '\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries).Select(c => "." + c));
        parts.AddRange(el.Attributes.Where(a => a.Name.StartsWith("Classes.", StringComparison.Ordinal) && a.Name.Length > 8).Select(a => "." + a.Name[8..]));
        return (string.Join(' ', parts), elementName != null ? Field : Object);
    }

    /// <summary>"{x:Type Button}", "local:Foo" or "Button" → the type as written.</summary>
    private static string TypeText(string value)
    {
        var v = value.Trim();
        if (v.StartsWith('{') && v.EndsWith('}'))
        {
            var inner = v[1..^1].Trim();
            var space = inner.IndexOf(' ');
            v = space > 0 ? inner[(space + 1)..].Trim() : inner;
            if (v.StartsWith("TypeName=", StringComparison.Ordinal)) v = v[9..];
        }
        return v;
    }
}
