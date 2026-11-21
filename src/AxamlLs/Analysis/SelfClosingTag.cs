
namespace AxamlLs.Analysis;

/// <summary>On-type formatting for '/': turning &lt;X …&gt;&lt;/X&gt; into &lt;X …/&gt;.</summary>
public static class SelfClosingTag
{
    /// <summary>
    /// After typing '/' just before the '>' of a start tag (cursor after the '/'), the span of a directly following,
    /// empty matching end tag: &lt;Button A="1"/&gt;&lt;/Button&gt; → remove "&lt;/Button&gt;". Null when there is nothing to remove.
    /// </summary>
    public static (int Start, int End)? Edit(string text, int offset)
    {
        if (offset < 2 || offset >= text.Length || text[offset - 1] != '/' || text[offset] != '>') return null;
        var lt = text.LastIndexOf('<', offset - 2);
        if (lt < 0 || text.IndexOf('>', lt, offset - lt) >= 0 && InsideTagEnds(text, lt, offset)) return null;
        var nameEnd = lt + 1;
        while (nameEnd < text.Length && (char.IsLetterOrDigit(text[nameEnd]) || text[nameEnd] is '_' or ':' or '.' or '-')) nameEnd++;
        var name = text[(lt + 1)..nameEnd];
        if (name.Length == 0) return null;
        var i = offset + 1;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        var close = "</" + name;
        if (string.CompareOrdinal(text, i, close, 0, close.Length) != 0) return null;
        var j = i + close.Length;
        while (j < text.Length && char.IsWhiteSpace(text[j])) j++;
        if (j >= text.Length || text[j] != '>') return null;
        return (offset + 1, j + 1);
    }

    /// <summary>True when a '>' outside quotes ends the tag that starts at <paramref name="lt"/> before <paramref name="end"/>.</summary>
    private static bool InsideTagEnds(string text, int lt, int end)
    {
        char? quote = null;
        for (var i = lt + 1; i < end - 1; i++)
        {
            var c = text[i];
            if (quote != null)
            {
                if (c == quote) quote = null;
            }
            else if (c is '"' or '\'') quote = c;
            else if (c == '>') return true;
        }
        return false;
    }
}
