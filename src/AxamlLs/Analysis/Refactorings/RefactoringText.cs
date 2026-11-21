using AxamlLs.Lsp;
using AxamlLs.Xaml;

namespace AxamlLs.Analysis.Refactorings;

/// <summary>Text helpers the refactorings share: the document's indentation and line ending, and inserting children.</summary>
internal static class RefactoringText
{
    public static string Newline(string text) => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    /// <summary>The document's indent unit: a tab if lines are indented with tabs, else the smallest indent in spaces (4 if none).</summary>
    public static string Unit(string text)
    {
        var smallest = int.MaxValue;
        var lines = 0;
        for (var i = text.IndexOf('\n'); i >= 0 && i + 1 < text.Length && lines < 200; i = text.IndexOf('\n', i + 1), lines++)
        {
            if (text[i + 1] == '\t') return "\t";
            var n = 0;
            while (i + 1 + n < text.Length && text[i + 1 + n] == ' ') n++;
            if (n > 0 && i + 1 + n < text.Length && text[i + 1 + n] is not ('\r' or '\n')) smallest = Math.Min(smallest, n);
        }
        return new string(' ', smallest == int.MaxValue ? 4 : smallest);
    }

    /// <summary>The whitespace at the start of the line holding <paramref name="offset"/>.</summary>
    public static string LineIndent(string text, int offset)
    {
        var start = offset == 0 ? 0 : text.LastIndexOf('\n', offset - 1) + 1;
        var end = start;
        while (end < text.Length && text[end] is ' ' or '\t') end++;
        return text[start..end];
    }

    /// <summary>The end of an attribute, after its closing quote.</summary>
    public static int AttrEnd(XmlAttr a) => a.HasValue ? (a.ValueTerminated ? a.ValueEnd + 1 : a.ValueEnd) : a.NameEnd;

    /// <summary>Every attribute has a quoted, terminated value: safe to move or delete as text.</summary>
    public static bool Complete(XmlElement el) => el.StartTagEnd > 0 && el.Attributes.All(a => a.HasValue && a.ValueTerminated);

    /// <summary>
    /// Adds an element as the last child of <paramref name="container"/>, on its own line, indented like its siblings.
    /// <paramref name="xml"/> gets the indent of its first line, for its own inner lines. Null when the container is
    /// self-closing.
    /// </summary>
    public static FileEdit? AppendChild(string path, string text, XmlElement container, Func<string, string> xml)
    {
        if (container.SelfClosing || container.EndTagStart < 0) return null;
        var nl = Newline(text);
        var unit = Unit(text);
        var containerIndent = LineIndent(text, container.Start);
        if (container.Children.LastOrDefault() is { } last)
        {
            // The last child's indent, unless it shares its line with something else.
            var lineStart = last.Start == 0 ? 0 : text.LastIndexOf('\n', last.Start - 1) + 1;
            var indent = string.IsNullOrWhiteSpace(text[lineStart..last.Start]) ? text[lineStart..last.Start] : containerIndent + unit;
            return new FileEdit(path, last.End, last.End, nl + indent + xml(indent));
        }
        var inner = containerIndent + unit;
        return new FileEdit(path, container.StartTagEnd, container.EndTagStart, nl + inner + xml(inner) + nl + containerIndent);
    }

    /// <summary>A new property element (<c>&lt;Root.Resources&gt;</c>) holding one child, as the root's first child.</summary>
    public static FileEdit? NewPropertyElement(string path, string text, XmlElement root, string property, Func<string, string> xml)
    {
        if (root.SelfClosing || root.StartTagEnd <= 0) return null;
        var nl = Newline(text);
        var unit = Unit(text);
        var indent = LineIndent(text, root.Start) + unit;
        var name = root.Name + "." + property;
        return new FileEdit(path, root.StartTagEnd, root.StartTagEnd,
            nl + indent + "<" + name + ">" + nl + indent + unit + xml(indent + unit) + nl + indent + "</" + name + ">");
    }

    /// <summary>The prefix the document uses for a namespace ("" for the default one); null when it isn't declared.</summary>
    public static string? PrefixOf(XmlElement el, string ns)
    {
        foreach (var e in el.SelfAndAncestors())
        {
            foreach (var (prefix, uri) in e.DeclaredNamespaces())
            {
                if (uri == ns && el.LookupNamespace(prefix) == ns) return prefix;
            }
        }
        return null;
    }

    public static string Qualified(string? prefix, string name) => string.IsNullOrEmpty(prefix) ? name : prefix + ":" + name;

    /// <summary>The element whose start tag holds <paramref name="offset"/>.</summary>
    public static XmlElement? StartTagAt(XmlDoc doc, int offset) =>
        doc.ElementAt(offset) is { StartTagEnd: > 0 } el && el.Start <= offset && offset <= el.StartTagEnd ? el : null;
}
