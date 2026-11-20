using AxamlLs.Xaml;

namespace AxamlLs.Analysis;

/// <summary>
/// "Expand selection": the ranges around a position, innermost first. The word, then inside a markup extension the
/// argument value, the argument and the extension (and the same for each enclosing extension), then the attribute value
/// and the attribute, then the tag, the element, the parent's content, the parent, and so on up to the whole document.
/// A comment, a tag name or text content take the place of the attribute. Ranges that don't grow are skipped.
/// </summary>
public static class SelectionRanges
{
    public static IReadOnlyList<(int Start, int End)> At(XmlDoc doc, int offset)
    {
        var text = doc.Text;
        var chain = new Chain();
        var el = doc.ElementAt(offset);
        if (Comment(text, offset) is { } comment)
        {
            chain.Add(Word(text, offset));
            chain.Add(comment);
        }
        else if (el != null && el.StartTagEnd > 0 && offset < el.StartTagEnd)
        {
            chain.Add(Word(text, offset));
            if (el.Attributes.FirstOrDefault(a => a.NameStart <= offset && offset <= AttrEnd(a)) is { } attr)
            {
                if (attr.HasValue && offset >= attr.ValueStart && offset <= attr.ValueEnd && MarkupExtension.IsMarkup(attr.Value) &&
                    MarkupExtension.Parse(MappedText.FromAttribute(attr)) is { } ext)
                    Markup(ext, offset, chain);
                if (attr.HasValue) chain.Add((attr.ValueStart, attr.ValueEnd));
                chain.Add((attr.NameStart, AttrEnd(attr)));
            }
            chain.Add((el.Start, el.StartTagEnd));
        }
        else if (el != null && el.EndTagStart >= 0 && offset >= el.EndTagStart)
        {
            chain.Add(Word(text, offset));
            chain.Add((el.EndTagStart, el.End));
        }
        else if (el != null && el.StartTagEnd > 0 && el.EndTagStart >= 0)
        {
            chain.Add(Word(text, offset));
            chain.Add(Trimmed(text, el.StartTagEnd, el.EndTagStart));
            chain.Add((el.StartTagEnd, el.EndTagStart));
        }
        else
        {
            chain.Add(Word(text, offset));
        }

        for (var e = el; e != null; e = e.Parent)
        {
            chain.Add((e.Start, e.End));
            if (e.Parent is { StartTagEnd: > 0, EndTagStart: >= 0 } parent)
            {
                chain.Add(Trimmed(text, parent.StartTagEnd, parent.EndTagStart));
                chain.Add((parent.StartTagEnd, parent.EndTagStart));
            }
        }
        chain.Add((0, text.Length));
        return chain.Ranges;
    }

    /// <summary>The extensions around the offset, innermost first, each with the argument the offset is in.</summary>
    private static void Markup(MarkupExtension outer, int offset, Chain chain)
    {
        var around = outer.SelfAndNested().Where(x => x.Start <= offset && offset <= x.End).OrderBy(x => x.End - x.Start).ToList();
        foreach (var ext in around)
        {
            if (ext.Args.FirstOrDefault(a => a.Start <= offset && offset <= a.End) is { } arg)
            {
                if (arg.Nested == null && arg.ValueStart <= offset && offset <= arg.ValueEnd) chain.Add((arg.ValueStart, arg.ValueEnd));
                chain.Add((arg.Start, arg.End));
            }
            chain.Add((ext.Start, ext.End));
        }
    }

    private static int AttrEnd(XmlAttr a) => a.HasValue ? (a.ValueTerminated ? a.ValueEnd + 1 : a.ValueEnd) : a.NameEnd;

    private static (int, int)? Comment(string text, int offset)
    {
        var open = text.LastIndexOf("<!--", Math.Min(offset, text.Length - 1), StringComparison.Ordinal);
        if (open < 0) return null;
        var close = text.IndexOf("-->", open + 4, StringComparison.Ordinal);
        return close >= 0 && offset <= close + 3 ? (open, close + 3) : null;
    }

    private static (int, int)? Word(string text, int offset)
    {
        static bool IsWord(char c) => char.IsLetterOrDigit(c) || c is '_' or '.' or ':' or '-' or '#';
        var a = offset;
        while (a > 0 && IsWord(text[a - 1])) a--;
        var b = offset;
        while (b < text.Length && IsWord(text[b])) b++;
        return b > a ? (a, b) : null;
    }

    /// <summary>The content without its leading and trailing whitespace (what a person means by "the children").</summary>
    private static (int, int) Trimmed(string text, int start, int end)
    {
        while (start < end && char.IsWhiteSpace(text[start])) start++;
        while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
        return (start, end);
    }

    /// <summary>Ranges innermost first; one is added only when it contains the last and is larger.</summary>
    private sealed class Chain
    {
        public List<(int Start, int End)> Ranges { get; } = [];

        public void Add((int Start, int End)? range)
        {
            if (range is not { } r || r.End < r.Start) return;
            if (Ranges.Count > 0)
            {
                var last = Ranges[^1];
                if (r.Start > last.Start || r.End < last.End || r == last) return;
            }
            Ranges.Add(r);
        }
    }
}
