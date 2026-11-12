using System.Text;

namespace AxamlLs.Xaml;

/// <summary>A piece of text with a map from each character back to its absolute offset in the document.</summary>
public sealed class MappedText
{
    public string Text { get; }
    /// <summary>Map[i] = absolute offset of Text[i]; Map[Text.Length] = absolute end.</summary>
    public int[] Map { get; }

    public MappedText(string text, int[] map)
    {
        Text = text;
        Map = map;
    }

    public int Start => Map[0];
    public int End => Map[^1];
    public int Abs(int index) => Map[Math.Clamp(index, 0, Map.Length - 1)];

    /// <summary>The index in <see cref="Text"/> that corresponds to an absolute offset (clamped).</summary>
    public int IndexOf(int absolute)
    {
        for (var i = 0; i < Map.Length; i++)
        {
            if (Map[i] >= absolute) return i;
        }
        return Text.Length;
    }

    public static MappedText FromAttribute(XmlAttr attr) => new(attr.Value, attr.Map);
}

public sealed class MarkupArg
{
    public string? Key { get; init; }
    public int KeyStart { get; init; }
    public int KeyEnd { get; init; }
    /// <summary>The literal value (unquoted, unescaped). Null when the value is a nested extension.</summary>
    public MappedText? Value { get; init; }
    public MarkupExtension? Nested { get; init; }
    /// <summary>Absolute span of the whole argument, key included.</summary>
    public int Start { get; init; }
    public int End { get; init; }
    public int ValueStart => Nested?.Start ?? Value?.Start ?? End;
    public int ValueEnd => Nested?.End ?? Value?.End ?? End;
}

/// <summary>A parsed <c>{Name arg, Key=value, Key={Nested ...}}</c> markup extension.</summary>
public sealed class MarkupExtension
{
    public required string Name { get; init; }
    public required int NameStart { get; init; }
    public int NameEnd => NameStart + Name.Length;
    public string? Prefix => Name.IndexOf(':') is var i and > 0 ? Name[..i] : null;
    public string LocalName => Name.IndexOf(':') is var i and >= 0 ? Name[(i + 1)..] : Name;
    /// <summary>Absolute offset of '{'.</summary>
    public required int Start { get; init; }
    /// <summary>Absolute offset just past '}' (or the end of the text when unclosed).</summary>
    public int End { get; set; }
    public bool Closed { get; set; }
    public List<MarkupArg> Args { get; } = [];
    public List<(int Start, int End, string Message)> Errors { get; } = [];

    public MarkupArg? Positional(int index)
    {
        var i = 0;
        foreach (var a in Args)
        {
            if (a.Key != null) continue;
            if (i++ == index) return a;
        }
        return null;
    }

    public MarkupArg? Named(string key)
    {
        foreach (var a in Args)
        {
            if (string.Equals(a.Key, key, StringComparison.Ordinal)) return a;
        }
        return null;
    }

    public IEnumerable<MarkupExtension> SelfAndNested()
    {
        yield return this;
        foreach (var a in Args)
        {
            if (a.Nested == null) continue;
            foreach (var n in a.Nested.SelfAndNested()) yield return n;
        }
    }

    /// <summary>True when the text is a markup extension (starts with '{' and is not the '{}' escape).</summary>
    public static bool IsMarkup(string text)
    {
        var t = text.TrimStart();
        return t.StartsWith('{') && !t.StartsWith("{}", StringComparison.Ordinal);
    }

    public static MarkupExtension? Parse(MappedText source)
    {
        var s = source.Text;
        var p = 0;
        while (p < s.Length && char.IsWhiteSpace(s[p])) p++;
        if (p >= s.Length || s[p] != '{' || (p + 1 < s.Length && s[p + 1] == '}')) return null;
        return new Parser(source).ParseExtension(ref p);
    }

    private sealed class Parser(MappedText source)
    {
        private readonly string _s = source.Text;

        public MarkupExtension ParseExtension(ref int p)
        {
            var open = p;
            p++;
            SkipWs(ref p);
            var ns = p;
            while (p < _s.Length && !char.IsWhiteSpace(_s[p]) && _s[p] is not (',' or '}' or '{')) p++;
            var ext = new MarkupExtension { Name = _s[ns..p], NameStart = source.Abs(ns), Start = source.Abs(open) };
            if (ext.Name.Length == 0) ext.Errors.Add((ext.Start, source.Abs(p), "Markup extension name expected"));

            while (true)
            {
                SkipWs(ref p);
                if (p >= _s.Length)
                {
                    ext.Errors.Add((ext.Start, source.Abs(p), "Markup extension is missing its closing '}'"));
                    break;
                }
                if (_s[p] == '}')
                {
                    p++;
                    ext.Closed = true;
                    break;
                }
                if (_s[p] == ',')
                {
                    p++;
                    continue;
                }
                ext.Args.Add(ParseArg(ref p));
            }
            ext.End = source.Abs(p);
            return ext;
        }

        private MarkupArg ParseArg(ref int p)
        {
            var argStart = p;
            string? key = null;
            int keyStart = 0, keyEnd = 0;

            // A key is an identifier (possibly dotted/prefixed) followed by '='.
            var k = p;
            while (k < _s.Length && (char.IsLetterOrDigit(_s[k]) || _s[k] is '_' or '.' or ':')) k++;
            var afterIdent = k;
            while (k < _s.Length && char.IsWhiteSpace(_s[k])) k++;
            if (afterIdent > p && k < _s.Length && _s[k] == '=')
            {
                key = _s[p..afterIdent];
                keyStart = source.Abs(p);
                keyEnd = source.Abs(afterIdent);
                p = k + 1;
                SkipWs(ref p);
            }

            if (p < _s.Length && _s[p] == '{' && !(p + 1 < _s.Length && _s[p + 1] == '}'))
            {
                var nested = ParseExtension(ref p);
                return new MarkupArg { Key = key, KeyStart = keyStart, KeyEnd = keyEnd, Nested = nested, Start = source.Abs(argStart), End = source.Abs(p) };
            }

            var text = new StringBuilder();
            var map = new List<int>();
            if (p < _s.Length && _s[p] is '\'' or '"')
            {
                var q = _s[p++];
                while (p < _s.Length && _s[p] != q)
                {
                    if (_s[p] == '\\' && p + 1 < _s.Length) p++;
                    text.Append(_s[p]);
                    map.Add(source.Abs(p));
                    p++;
                }
                map.Add(source.Abs(p));
                if (p < _s.Length) p++;
            }
            else
            {
                var depth = 0;
                while (p < _s.Length)
                {
                    var c = _s[p];
                    if (depth == 0 && c is ',' or '}') break;
                    if (c is '[' or '(') depth++;
                    else if (c is ']' or ')') depth = Math.Max(0, depth - 1);
                    if (c == '\\' && p + 1 < _s.Length)
                    {
                        p++;
                        c = _s[p];
                    }
                    text.Append(c);
                    map.Add(source.Abs(p));
                    p++;
                }
                // Trim trailing whitespace.
                var len = text.Length;
                while (len > 0 && char.IsWhiteSpace(text[len - 1])) len--;
                map.Add(len < map.Count ? map[len] : source.Abs(p));
                text.Length = len;
                map.RemoveRange(len + 1, map.Count - len - 1);
            }
            var value = new MappedText(text.ToString(), map.ToArray());
            return new MarkupArg { Key = key, KeyStart = keyStart, KeyEnd = keyEnd, Value = value, Start = source.Abs(argStart), End = source.Abs(p) };
        }

        private void SkipWs(ref int p)
        {
            while (p < _s.Length && char.IsWhiteSpace(_s[p])) p++;
        }
    }
}
