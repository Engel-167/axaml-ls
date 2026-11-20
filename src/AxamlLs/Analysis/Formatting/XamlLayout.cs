using System.Text;
using AxamlLs.Xaml;

namespace AxamlLs.Analysis.Formatting;

/// <summary>Replace <c>Start..End</c> of the text with <c>NewText</c>.</summary>
public readonly record struct TextChange(int Start, int End, string NewText);

/// <summary>
/// A whitespace-only formatter: it changes nothing but the whitespace between tokens (indentation, line breaks between
/// attributes and elements, the space before <c>/&gt;</c>), so formatting cannot change what a document means.
/// <list type="bullet">
/// <item>Attribute values, names and order, comment text, CDATA and processing instructions are never touched.</item>
/// <item>Content that holds text (<c>&lt;TextBlock&gt;Hello  world&lt;/TextBlock&gt;</c>), <c>xml:space="preserve"</c>
/// content and inline containers (<c>TextBlock</c>, <c>Span</c>, anything with a <c>Run</c>) are left as they are,
/// including everything inside them: whitespace there can be shown.</item>
/// <item>A comment on the same line as what comes before it stays on that line.</item>
/// <item>Empty content (<c>&lt;Grid&gt;&lt;/Grid&gt;</c>) and anything outside the root element stay as they are.</item>
/// </list>
/// Only call it for a well-formed document: the tolerant parse of broken XML must not drive edits. It returns one
/// change per gap whose whitespace differs, so the editor keeps the cursor and undo history.
/// </summary>
public static class XamlLayout
{
    /// <summary>Inline elements: whitespace between them is shown as a space.</summary>
    private static readonly HashSet<string> Inlines = new(StringComparer.Ordinal)
    {
        "Run", "Span", "Bold", "Italic", "Underline", "Hyperlink", "LineBreak", "InlineUIContainer",
    };

    /// <summary>Elements whose content is inlines (or text).</summary>
    private static readonly HashSet<string> InlineContainers = new(StringComparer.Ordinal)
    {
        "TextBlock", "SelectableTextBlock", "Span", "Bold", "Italic", "Underline", "Hyperlink",
    };

    /// <param name="doc">The parsed document (well formed).</param>
    /// <param name="options">Indentation and layout settings.</param>
    /// <param name="rangeStart">With <paramref name="rangeEnd"/>: only changes touching this range are returned. Indentation
    /// is still computed from the whole document.</param>
    /// <param name="rangeEnd">The end of the range.</param>
    public static IReadOnlyList<TextChange> Format(XmlDoc doc, FormattingOptions options, int rangeStart = 0, int rangeEnd = int.MaxValue)
    {
        if (doc.Root is not { } root || !Complete(doc)) return [];
        var layout = new Layout(doc.Text, options, rangeStart, rangeEnd);
        layout.Element(root, LeadingIndent(doc.Text, root.Start));
        return layout.Changes;
    }

    /// <summary>Every tag finished and every attribute value quoted: what the layout relies on.</summary>
    private static bool Complete(XmlDoc doc) =>
        doc.AllElements.All(e => e.StartTagEnd > 0 && (e.SelfClosing || e.EndTagStart >= 0) && e.Attributes.All(a => a.HasValue && a.ValueTerminated && a.ValueStart > 0 && doc.Text[a.ValueStart - 1] is '"' or '\''));

    /// <summary>The whitespace before <paramref name="offset"/> on its line, when only whitespace precedes it there.</summary>
    private static string LeadingIndent(string text, int offset)
    {
        var lineStart = text.LastIndexOf('\n', Math.Max(offset - 1, 0)) + 1;
        if (offset == 0) lineStart = 0;
        var before = text[lineStart..offset];
        return string.IsNullOrWhiteSpace(before) ? before : "";
    }

    private sealed class Layout(string text, FormattingOptions options, int rangeStart, int rangeEnd)
    {
        private readonly FormattingSettings _s = options.Settings;
        private readonly string _unit = options.IndentUnit;
        private readonly string _newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        public List<TextChange> Changes { get; } = [];

        public void Element(XmlElement el, string indent)
        {
            StartTag(el, indent);
            if (el.SelfClosing) return;
            var endName = el.EndTagStart + 2 + el.Name.Length;
            if (el.End > endName && text[el.End - 1] == '>') Gap(endName, el.End - 1, ""); // </Grid  >
            if (KeepsContent(el) || ContentItems(el) is not { Count: > 0 } items) return;

            var inner = indent + _unit;
            var previousEnd = el.StartTagEnd;
            var first = true;
            foreach (var item in items)
            {
                var gap = text.AsSpan(previousEnd, item.Start - previousEnd);
                if (!(item.Element == null && !gap.Contains('\n')))
                {
                    var blank = first ? 0 : Math.Min(Math.Max(gap.Count('\n') - 1, 0), Math.Max(_s.KeepBlankLines, 0));
                    Gap(previousEnd, item.Start, Repeat(_newline, 1 + blank) + inner);
                }
                if (item.Element != null) Element(item.Element, inner);
                previousEnd = item.End;
                first = false;
            }
            Gap(previousEnd, el.EndTagStart, _newline + indent);
        }

        private void StartTag(XmlElement el, string indent)
        {
            var close = el.SelfClosing ? el.StartTagEnd - 2 : el.StartTagEnd - 1;
            var closeSpace = el.SelfClosing && _s.SpaceBeforeSelfClosingSlash ? " " : "";
            var attrs = el.Attributes;
            foreach (var a in attrs)
            {
                // Name = "value" → Name="value"
                var eq = a.NameEnd;
                while (eq < a.ValueStart && text[eq] != '=') eq++;
                if (eq >= a.ValueStart) continue;
                Gap(a.NameEnd, eq, "");
                Gap(eq + 1, a.ValueStart - 1, "");
            }
            if (attrs.Count == 0)
            {
                Gap(el.NameEnd, close, closeSpace);
                return;
            }

            var separate = SeparateLines(el, indent);
            var attrIndent = _s.IndentAttributesBy == AttributeIndent.AlignWithFirst && _s.FirstAttributeOnSameLine
                ? indent + new string(' ', el.Name.Length + 2)
                : indent + _unit;
            Gap(el.NameEnd, attrs[0].NameStart, separate && !_s.FirstAttributeOnSameLine ? _newline + attrIndent : " ");
            for (var i = 1; i < attrs.Count; i++) Gap(attrs[i - 1].ValueEnd + 1, attrs[i].NameStart, separate ? _newline + attrIndent : " ");
            Gap(attrs[^1].ValueEnd + 1, close, closeSpace);
        }

        private bool SeparateLines(XmlElement el, string indent)
        {
            var attrs = el.Attributes;
            switch (_s.AttributesOnSeparateLines)
            {
                case AttributeLayout.Never:
                    return false;
                case AttributeLayout.Always:
                    return attrs.Count > 1;
            }
            if (attrs.Count <= 1) return false;
            if (attrs.Count > _s.AttributesOnSeparateLinesThreshold) return true;
            if (attrs.Any(a => text.AsSpan(a.ValueStart, a.ValueEnd - a.ValueStart).Contains('\n'))) return true;
            var length = Width(indent) + 1 + el.Name.Length + (el.SelfClosing ? (_s.SpaceBeforeSelfClosingSlash ? 3 : 2) : 1);
            foreach (var a in attrs) length += 1 + a.Name.Length + 2 + (a.ValueEnd - a.ValueStart) + 1;
            return length > _s.MaxLineLength;
        }

        private int Width(string indent)
        {
            var width = 0;
            foreach (var c in indent) width += c == '\t' ? Math.Max(options.TabSize, 1) : 1;
            return width;
        }

        /// <summary>Content whose whitespace may be shown or is asked to be kept: left exactly as written.</summary>
        private static bool KeepsContent(XmlElement el) =>
            el.Attr("xml:space")?.Value == "preserve"
            || InlineContainers.Contains(el.LocalName)
            || el.LocalName.EndsWith(".Inlines", StringComparison.Ordinal)
            || el.Children.Any(c => Inlines.Contains(c.LocalName));

        /// <summary>The child elements and comments of an element, in order; null when its content holds anything else (text, CDATA…).</summary>
        private List<Item>? ContentItems(XmlElement el)
        {
            var items = new List<Item>();
            var p = el.StartTagEnd;
            foreach (var child in el.Children)
            {
                if (!Between(p, child.Start, items)) return null;
                items.Add(new Item(child.Start, child.End, child));
                p = child.End;
            }
            return Between(p, el.EndTagStart, items) ? items : null;
        }

        private bool Between(int from, int to, List<Item> items)
        {
            var p = from;
            while (p < to)
            {
                if (char.IsWhiteSpace(text[p]))
                {
                    p++;
                    continue;
                }
                if (string.CompareOrdinal(text, p, "<!--", 0, 4) != 0) return false;
                var end = text.IndexOf("-->", p + 4, StringComparison.Ordinal);
                if (end < 0 || end + 3 > to) return false;
                items.Add(new Item(p, end + 3, null));
                p = end + 3;
            }
            return true;
        }

        /// <summary>Replaces the whitespace between two tokens, when it differs and touches the range.</summary>
        private void Gap(int start, int end, string desired)
        {
            if (end < start) return;
            var current = text.AsSpan(start, end - start);
            if (current.SequenceEqual(desired)) return;
            foreach (var c in current)
            {
                if (!char.IsWhiteSpace(c)) return; // not a gap: never touch tokens
            }
            if (start > rangeEnd || end < rangeStart) return;
            Changes.Add(new TextChange(start, end, desired));
        }

        private static string Repeat(string s, int count)
        {
            var sb = new StringBuilder(s.Length * count);
            for (var i = 0; i < count; i++) sb.Append(s);
            return sb.ToString();
        }
    }

    /// <summary>A child element, or a comment when <see cref="Element"/> is null.</summary>
    private readonly record struct Item(int Start, int End, XmlElement? Element);
}
