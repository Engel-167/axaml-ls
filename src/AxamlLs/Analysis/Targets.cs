using AxamlLs.Workspace;
using AxamlLs.Xaml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace AxamlLs.Analysis;

/// <summary>What the cursor is on, for find-references and rename.</summary>
public abstract record Target;
/// <summary>x:Name="Foo" / Name="Foo", #Foo in a binding path or selector, ElementName=Foo.</summary>
public sealed record ElementNameTarget(string Name) : Target;
/// <summary>x:Key="Foo" or {StaticResource Foo}.</summary>
public sealed record ResourceKeyTarget(string Key) : Target;
/// <summary>.Foo in a selector, Classes="… Foo …", Classes.Foo=.</summary>
public sealed record StyleClassTarget(string Class) : Target;
/// <summary>A C# member or type (binding path segment, property, type, event handler).</summary>
public sealed record SymbolTarget(ISymbol Symbol) : Target;

/// <summary>A target and the span of its name under the cursor (without '.', '#', quotes or the Classes. prefix).</summary>
public sealed record TargetAt(Target Target, int Start, int End);

/// <summary>A place a target occurs. <see cref="IsDeclaration"/> marks the x:Name / x:Key value or the C# declaration.</summary>
public sealed record Occurrence(string Path, int Start, int End, bool IsDeclaration = false)
{
    /// <summary>A class used inside a style selector (".Card"), as opposed to set on an element.</summary>
    public bool IsSelector { get; init; }
}

public static partial class Targets
{
    public static Target? At(XamlAnalyzer analyzer, int offset) => Find(analyzer, offset)?.Target;

    /// <summary>Recognizes, in order: a selector token, a Classes value or name, an x:Key or resource use, an element name, a symbol.</summary>
    public static TargetAt? Find(XamlAnalyzer analyzer, int offset)
    {
        var doc = analyzer.Result.Doc;

        // 1. Selector tokens.
        foreach (var el in doc.AllElements)
        {
            if (Selectors.SelectorAttr(el) is not { } sel || offset < sel.ValueStart || offset > sel.ValueEnd) continue;
            var text = MappedText.FromAttribute(sel);
            var tokens = Selectors.Tokenize(text.Text);
            var token = tokens.Cast<SelectorToken?>().FirstOrDefault(t => text.Abs(t!.Value.Start) <= offset && offset < text.Abs(t.Value.End))
                        ?? tokens.Cast<SelectorToken?>().FirstOrDefault(t => offset == text.Abs(t!.Value.End));
            if (token is not { } tk || tk.Text.Length < 2) break;
            if (tk.Kind == SelectorTokenKind.Class) return new TargetAt(new StyleClassTarget(tk.Text[1..]), text.Abs(tk.Start + 1), text.Abs(tk.End));
            if (tk.Kind == SelectorTokenKind.Name && !AfterTemplate(text.Text, tk.Start))
                return new TargetAt(new ElementNameTarget(tk.Text[1..]), text.Abs(tk.Start + 1), text.Abs(tk.End));
            break;
        }

        var element = doc.ElementAt(offset);
        if (element != null)
        {
            foreach (var attr in element.Attributes)
            {
                // 2. Classes="a b" / Classes.a="…".
                if (attr.Name == "Classes" && attr.HasValue && !MarkupExtension.IsMarkup(attr.Value) && attr.ValueStart <= offset && offset <= attr.ValueEnd)
                {
                    if (WordAt(attr, offset) is { } w) return new TargetAt(new StyleClassTarget(w.Word), w.Start, w.End);
                }
                else if (attr.Name.StartsWith("Classes.", StringComparison.Ordinal) && attr.Name.Length > 8 && offset >= attr.NameStart + 8 && offset <= attr.NameEnd)
                {
                    return new TargetAt(new StyleClassTarget(attr.Name[8..]), attr.NameStart + 8, attr.NameEnd);
                }
                else if (element.LocalName == "Setter" && attr.Name == "Value" && element.Attr("Property")?.Value.Trim() == "Classes" &&
                         attr.HasValue && !MarkupExtension.IsMarkup(attr.Value) && attr.ValueStart <= offset && offset <= attr.ValueEnd)
                {
                    if (WordAt(attr, offset) is { } w) return new TargetAt(new StyleClassTarget(w.Word), w.Start, w.End);
                }

                // 3. x:Key="Foo".
                if (IsXAttr(element, attr, "Key") && attr.HasValue && !MarkupExtension.IsMarkup(attr.Value) && attr.ValueStart <= offset && offset <= attr.ValueEnd)
                {
                    if (Trimmed(attr) is { } t) return new TargetAt(new ResourceKeyTarget(t.Text), t.Start, t.End);
                }
                // 4. x:Name="Foo" / Name="Foo".
                if ((IsXAttr(element, attr, "Name") || attr.Name == "Name") && attr.HasValue && !MarkupExtension.IsMarkup(attr.Value) && attr.ValueStart <= offset && offset <= attr.ValueEnd)
                {
                    if (Trimmed(attr) is { } t) return new TargetAt(new ElementNameTarget(t.Text), t.Start, t.End);
                }
            }
        }

        if (ResourceValues.KeyAt(analyzer, offset) is { } key && key.Key.Length > 0)
            return new TargetAt(new ResourceKeyTarget(key.Key), key.Start, key.Start + key.Key.Length);

        var r = analyzer.Result.RefAt(offset);
        if (r == null) return null;
        if (r.TargetElement != null)
        {
            var name = ElementName(r.TargetElement);
            if (name == null) return null;
            var start = doc.Text.Length > r.Start && doc.Text[r.Start] == '#' ? r.Start + 1 : r.Start;
            return new TargetAt(new ElementNameTarget(name), start, r.End);
        }
        if (r.Symbol == null) return null;
        var (s, e) = NameSpan(doc.Text, r);
        return new TargetAt(new SymbolTarget(r.Symbol), s, e);
    }

    /// <summary>The name part of a symbol reference: "vm:MainViewModel" → "MainViewModel", "SampleApp.Views.MainView" → "MainView".</summary>
    private static (int Start, int End) NameSpan(string text, SymbolRef r)
    {
        var start = r.Start;
        if (r.Symbol is ITypeSymbol && r.End <= text.Length)
        {
            var slice = text[r.Start..r.End];
            start = r.Start + Math.Max(slice.LastIndexOf(':'), slice.LastIndexOf('.')) + 1;
        }
        return (start, r.End);
    }

    private static bool AfterTemplate(string selector, int tokenStart)
    {
        var before = selector[..tokenStart];
        var comma = before.LastIndexOf(',');
        return before[(comma + 1)..].Contains("/template/", StringComparison.Ordinal);
    }

    private static bool IsXAttr(XmlElement el, XmlAttr attr, string local) =>
        attr.Prefix != null && attr.LocalName == local && el.LookupNamespace(attr.Prefix) == XamlNamespaces.X;

    public static string? ElementName(XmlElement el)
    {
        var attr = XamlAnalyzer.XAttr(el, "Name") ?? el.Attr("Name");
        return attr is { HasValue: true } && attr.Value.Trim().Length > 0 ? attr.Value.Trim() : null;
    }

    private static (string Text, int Start, int End)? Trimmed(XmlAttr attr)
    {
        var text = attr.Value.Trim();
        if (text.Length == 0) return null;
        var lead = attr.Value.IndexOf(text, StringComparison.Ordinal);
        return (text, attr.Map[lead], attr.Map[lead + text.Length]);
    }

    private static (string Word, int Start, int End)? WordAt(XmlAttr attr, int offset)
    {
        var s = attr.Value;
        var idx = 0;
        while (idx < s.Length && attr.Map[idx] < offset) idx++;
        var from = Math.Min(idx, s.Length);
        var a = from;
        while (a > 0 && !IsSeparator(s[a - 1])) a--;
        var b = from;
        while (b < s.Length && !IsSeparator(s[b])) b++;
        return b > a ? (s[a..b], attr.Map[a], attr.Map[b]) : null;
    }

    private static bool IsSeparator(char c) => char.IsWhiteSpace(c) || c == ',';
}
