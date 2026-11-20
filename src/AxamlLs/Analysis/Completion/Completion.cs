using AxamlLs.Xaml;

namespace AxamlLs.Analysis;

/// <summary>
/// Completion at an offset: dispatches on where the cursor is (tag name, attribute name, attribute value, markup
/// extension, binding path) to the partial files beside this one.
/// </summary>
public static partial class Completion
{
    internal static readonly string[] BindingKeys =
        ["Path", "Mode", "Converter", "ConverterParameter", "ConverterCulture", "StringFormat", "FallbackValue", "TargetNullValue", "ElementName", "RelativeSource", "Source", "DataType", "Priority", "UpdateSourceTrigger", "Delay"];

    internal static readonly string[] MarkupExtensions =
        ["Binding", "CompiledBinding", "ReflectionBinding", "TemplateBinding", "StaticResource", "DynamicResource", "RelativeSource", "OnPlatform", "OnFormFactor", "x:Static", "x:Type", "x:Null", "x:True", "x:False"];

    public static List<CompletionItem> At(XamlAnalyzer analyzer, int offset)
    {
        var doc = analyzer.Result.Doc;
        var list = new List<CompletionItem>();
        var el = StartTagAt(doc, offset);
        if (el == null) return list;

        if (offset <= el.NameEnd)
        {
            CompleteElementNames(analyzer, el, el.NameStart, el.NameEnd, list);
            return list;
        }

        foreach (var attr in el.Attributes)
        {
            if (attr.HasValue && attr.ValueStart <= offset && offset <= attr.ValueEnd)
            {
                CompleteAttributeValue(analyzer, el, attr, offset, list);
                return list;
            }
            if (attr.NameStart <= offset && offset <= attr.NameEnd)
            {
                // Classes.accent="{Binding IsSpecial}": a class switched on and off by a bool.
                if (attr.Name.StartsWith("Classes.", StringComparison.Ordinal) && offset >= attr.NameStart + 8)
                {
                    var type = analyzer.Result.Elements.GetValueOrDefault(el)?.Type;
                    var present = el.Attributes.Where(a => a != attr && a.Name.StartsWith("Classes.", StringComparison.Ordinal)).Select(a => a.Name[8..]).ToHashSet(StringComparer.Ordinal);
                    Selectors.CompleteClassNames(analyzer, el, type, analyzer.Styles(), attr.NameStart, attr.NameEnd, list,
                        c => attr.HasValue ? "Classes." + c : $"Classes.{c}=\"{{Binding $1}}\"$0", c => "Classes." + c, snippet: !attr.HasValue, exclude: present);
                    return list;
                }
                CompleteAttributeNames(analyzer, el, attr.NameStart, attr.NameEnd, list);
                return list;
            }
        }
        // Whitespace inside the tag: a new attribute.
        if (offset > 0 && char.IsWhiteSpace(doc.Text[offset - 1]) || offset < doc.Text.Length && doc.Text[offset] is '>' or '/')
            CompleteAttributeNames(analyzer, el, offset, offset, list);
        return list;
    }

    private static XmlElement? StartTagAt(XmlDoc doc, int offset)
    {
        XmlElement? best = null;
        foreach (var e in doc.AllElements)
        {
            var tagEnd = e.StartTagEnd > 0 ? e.StartTagEnd - 1 : e.End;
            if (e.Start < offset && offset <= tagEnd && (best == null || e.Start > best.Start)) best = e;
        }
        return best;
    }

    private static void AddWords(List<CompletionItem> list, string[] words, string detail, int start, int end)
    {
        foreach (var w in words) list.Add(new CompletionItem(w, CompletionItemKind.EnumMember, detail, null, w, false, start, end));
    }

    /// <summary>The space-separated class name around the cursor in Classes="a b|c".</summary>
    private static (int Start, int End) ClassWord(MappedText text, int offset)
    {
        var i = text.IndexOf(offset);
        var s = i;
        while (s > 0 && !char.IsWhiteSpace(text.Text[s - 1])) s--;
        var e = i;
        while (e < text.Text.Length && !char.IsWhiteSpace(text.Text[e])) e++;
        return (text.Abs(s), text.Abs(e));
    }

    private static (int Start, int End) Word(MappedText text, int offset, bool allowColon)
    {
        bool Part(char c) => BindingPath.IsIdentPart(c) || allowColon && c is ':' or '.';
        var i = text.IndexOf(offset);
        var s = i;
        while (s > 0 && Part(text.Text[s - 1])) s--;
        var e = i;
        while (e < text.Text.Length && Part(text.Text[e])) e++;
        return (text.Abs(s), text.Abs(e));
    }
}
