using System.Text;

namespace AxamlLs.Xaml;

/// <summary>An attribute of a start tag. All offsets are absolute offsets into the document text.</summary>
public sealed class XmlAttr
{
    public required XmlElement Owner { get; init; }
    public required string Name { get; init; }
    public required int NameStart { get; init; }
    public int NameEnd => NameStart + Name.Length;
    public string? Prefix => Name.IndexOf(':') is var i and > 0 ? Name[..i] : null;
    public string LocalName => Name.IndexOf(':') is var i and >= 0 ? Name[(i + 1)..] : Name;

    public bool HasValue { get; set; }
    /// <summary>Offset of the first character inside the quotes.</summary>
    public int ValueStart { get; set; }
    /// <summary>Offset just past the last character inside the quotes.</summary>
    public int ValueEnd { get; set; }
    public bool ValueTerminated { get; set; }

    /// <summary>Value with XML entities decoded.</summary>
    public string Value { get; set; } = "";
    /// <summary>Map[i] = absolute offset of decoded character i; Map[Value.Length] = ValueEnd.</summary>
    public int[] Map { get; set; } = [];
}

public sealed class XmlElement
{
    public required string Name { get; init; }
    /// <summary>Offset of '&lt;'.</summary>
    public required int Start { get; init; }
    public int NameStart => Start + 1;
    public int NameEnd => NameStart + Name.Length;
    public string? Prefix => Name.IndexOf(':') is var i and > 0 ? Name[..i] : null;
    public string LocalName => Name.IndexOf(':') is var i and >= 0 ? Name[(i + 1)..] : Name;

    public XmlElement? Parent { get; init; }
    public List<XmlAttr> Attributes { get; } = [];
    public List<XmlElement> Children { get; } = [];

    /// <summary>Offset just past the '&gt;' of the start tag, or -1 when the tag is not finished.</summary>
    public int StartTagEnd { get; set; } = -1;
    public bool SelfClosing { get; set; }
    /// <summary>Offset of '&lt;/' of the end tag, if any.</summary>
    public int EndTagStart { get; set; } = -1;
    public int End { get; set; }

    public Dictionary<string, string>? NamespaceDeclarations { get; set; }

    public XmlAttr? Attr(string name)
    {
        foreach (var a in Attributes)
        {
            if (a.Name == name) return a;
        }
        return null;
    }

    /// <summary>Resolves an XML namespace prefix ("" = default namespace) against this element and its ancestors.</summary>
    public string? LookupNamespace(string prefix)
    {
        if (prefix == "xml") return "http://www.w3.org/XML/1998/namespace";
        for (var e = this; e != null; e = e.Parent)
        {
            if (e.NamespaceDeclarations != null && e.NamespaceDeclarations.TryGetValue(prefix, out var ns)) return ns;
        }
        return prefix.Length == 0 ? "" : null;
    }

    public IEnumerable<XmlElement> SelfAndAncestors()
    {
        for (var e = this; e != null; e = e.Parent) yield return e;
    }

    public IEnumerable<XmlElement> Descendants()
    {
        foreach (var c in Children)
        {
            yield return c;
            foreach (var d in c.Descendants()) yield return d;
        }
    }

    public override string ToString() => $"<{Name}>@{Start}";
}

/// <summary>
/// A forgiving XML parse of an AXAML document. It never throws: unfinished tags, missing quotes and
/// mismatched end tags still produce a usable tree, which is what completion needs while typing.
/// Well-formedness errors are reported separately by <see cref="WellFormedness"/>.
/// </summary>
public sealed class XmlDoc
{
    public string Text { get; }
    public List<XmlElement> TopLevel { get; } = [];
    public XmlElement? Root => TopLevel.Count > 0 ? TopLevel[0] : null;
    public List<XmlElement> AllElements { get; } = [];

    private XmlDoc(string text) => Text = text;

    public static XmlDoc Parse(string text)
    {
        var doc = new XmlDoc(text);
        var stack = new List<XmlElement>();
        var n = text.Length;
        var i = 0;

        while (i < n)
        {
            var lt = text.IndexOf('<', i);
            if (lt < 0) break;
            i = lt;

            if (At(text, i, "<!--")) { i = SkipPast(text, i + 4, "-->"); continue; }
            if (At(text, i, "<![CDATA[")) { i = SkipPast(text, i + 9, "]]>"); continue; }
            if (At(text, i, "<?")) { i = SkipPast(text, i + 2, "?>"); continue; }
            if (At(text, i, "<!")) { i = SkipPast(text, i + 2, ">"); continue; }

            if (At(text, i, "</"))
            {
                var ns = i + 2;
                var ne = ReadName(text, ns);
                var name = text[ns..ne];
                var gt = IndexOfEither(text, ne, '>', '<');
                var end = gt < 0 ? n : text[gt] == '>' ? gt + 1 : gt;
                var idx = stack.FindLastIndex(e => e.Name == name);
                if (idx >= 0)
                {
                    for (var k = stack.Count - 1; k > idx; k--)
                    {
                        stack[k].End = i;
                        stack.RemoveAt(k);
                    }
                    var el = stack[idx];
                    el.EndTagStart = i;
                    el.End = end;
                    stack.RemoveAt(idx);
                }
                i = end;
                continue;
            }

            var nameStart = i + 1;
            var nameEnd = ReadName(text, nameStart);
            var parent = stack.Count > 0 ? stack[^1] : null;
            var element = new XmlElement { Name = text[nameStart..nameEnd], Start = i, Parent = parent };
            if (parent != null) parent.Children.Add(element); else doc.TopLevel.Add(element);
            doc.AllElements.Add(element);

            var p = nameEnd;
            var open = false;
            while (true)
            {
                p = SkipWhitespace(text, p);
                if (p >= n) { element.End = n; open = true; break; }
                var c = text[p];
                if (c == '>') { element.StartTagEnd = p + 1; open = true; break; }
                if (c == '/' && p + 1 < n && text[p + 1] == '>')
                {
                    element.StartTagEnd = p + 2;
                    element.SelfClosing = true;
                    element.End = p + 2;
                    break;
                }
                if (c == '<')
                {
                    // Unfinished tag (the user is typing). Treat it as self-closing at this point.
                    element.SelfClosing = true;
                    element.End = p;
                    break;
                }

                var an = p;
                var ae = ReadName(text, p);
                if (ae == an) { p++; continue; }
                var attr = new XmlAttr { Owner = element, Name = text[an..ae], NameStart = an };
                p = SkipWhitespace(text, ae);
                if (p < n && text[p] == '=')
                {
                    p = SkipWhitespace(text, p + 1);
                    if (p < n && (text[p] == '"' || text[p] == '\''))
                    {
                        var q = text[p];
                        var vs = p + 1;
                        var ve = IndexOfEither(text, vs, q, '<');
                        attr.HasValue = true;
                        attr.ValueStart = vs;
                        if (ve >= 0 && text[ve] == q)
                        {
                            attr.ValueEnd = ve;
                            attr.ValueTerminated = true;
                            p = ve + 1;
                        }
                        else
                        {
                            attr.ValueEnd = ve < 0 ? n : ve;
                            p = attr.ValueEnd;
                        }
                        Decode(text, attr);
                    }
                    else
                    {
                        // Unquoted value: take it up to whitespace or the end of the tag.
                        var vs = p;
                        while (p < n && !char.IsWhiteSpace(text[p]) && text[p] != '>' && text[p] != '<' && !(text[p] == '/' && p + 1 < n && text[p + 1] == '>')) p++;
                        attr.HasValue = true;
                        attr.ValueStart = vs;
                        attr.ValueEnd = p;
                        Decode(text, attr);
                    }
                }
                element.Attributes.Add(attr);

                if (attr.Name == "xmlns" || attr.Name.StartsWith("xmlns:", StringComparison.Ordinal))
                {
                    element.NamespaceDeclarations ??= new Dictionary<string, string>();
                    element.NamespaceDeclarations[attr.Name == "xmlns" ? "" : attr.Name[6..]] = attr.Value;
                }
            }

            i = element.StartTagEnd > 0 ? element.StartTagEnd : p;
            if (open && element.StartTagEnd > 0) stack.Add(element);
            if (i <= lt) i = lt + 1;
        }

        foreach (var e in stack) e.End = n;
        foreach (var e in doc.AllElements)
        {
            if (e.End == 0) e.End = e.StartTagEnd > 0 ? e.StartTagEnd : n;
        }
        return doc;
    }

    /// <summary>The innermost element whose span contains <paramref name="offset"/>.</summary>
    public XmlElement? ElementAt(int offset)
    {
        XmlElement? best = null;
        foreach (var e in AllElements)
        {
            if (e.Start <= offset && offset <= e.End && (best == null || e.Start >= best.Start)) best = e;
        }
        return best;
    }

    public static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '_' or ':' or '.' or '-';

    private static int ReadName(string text, int start)
    {
        var p = start;
        while (p < text.Length && IsNameChar(text[p])) p++;
        return p;
    }

    private static int SkipWhitespace(string text, int p)
    {
        while (p < text.Length && char.IsWhiteSpace(text[p])) p++;
        return p;
    }

    private static bool At(string text, int i, string s) => string.CompareOrdinal(text, i, s, 0, s.Length) == 0;

    private static int SkipPast(string text, int from, string terminator)
    {
        var e = text.IndexOf(terminator, from, StringComparison.Ordinal);
        return e < 0 ? text.Length : e + terminator.Length;
    }

    private static int IndexOfEither(string text, int from, char a, char b)
    {
        for (var k = from; k < text.Length; k++)
        {
            if (text[k] == a || text[k] == b) return k;
        }
        return -1;
    }

    private static void Decode(string text, XmlAttr attr)
    {
        var sb = new StringBuilder(attr.ValueEnd - attr.ValueStart);
        var map = new List<int>(attr.ValueEnd - attr.ValueStart + 1);
        var k = attr.ValueStart;
        while (k < attr.ValueEnd)
        {
            if (text[k] == '&')
            {
                var semi = text.IndexOf(';', k);
                if (semi > k && semi < attr.ValueEnd && semi - k <= 10)
                {
                    var entity = text[(k + 1)..semi];
                    string? decoded = entity switch
                    {
                        "lt" => "<",
                        "gt" => ">",
                        "amp" => "&",
                        "quot" => "\"",
                        "apos" => "'",
                        _ when entity.StartsWith("#x", StringComparison.OrdinalIgnoreCase) &&
                               int.TryParse(entity[2..], System.Globalization.NumberStyles.HexNumber, null, out var hx) => char.ConvertFromUtf32(hx),
                        _ when entity.StartsWith('#') && int.TryParse(entity[1..], out var dc) => char.ConvertFromUtf32(dc),
                        _ => null,
                    };
                    if (decoded != null)
                    {
                        foreach (var ch in decoded)
                        {
                            sb.Append(ch);
                            map.Add(k);
                        }
                        k = semi + 1;
                        continue;
                    }
                }
            }
            sb.Append(text[k]);
            map.Add(k);
            k++;
        }
        map.Add(attr.ValueEnd);
        attr.Value = sb.ToString();
        attr.Map = map.ToArray();
    }
}
