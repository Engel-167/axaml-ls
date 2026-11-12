namespace AxamlLs.Xaml;

public enum PathSegmentKind
{
    /// <summary><c>!</c> boolean negation (prefix).</summary>
    Not,
    /// <summary><c>$self</c>.</summary>
    Self,
    /// <summary><c>$parent</c>, <c>$parent[Type]</c>, <c>$parent[Type;2]</c>.</summary>
    Parent,
    /// <summary><c>#name</c>.</summary>
    Element,
    /// <summary>A property (or method) name.</summary>
    Property,
    /// <summary><c>[0]</c>, <c>["key"]</c>.</summary>
    Indexer,
    /// <summary><c>(Grid.Row)</c>.</summary>
    Attached,
    /// <summary><c>((vm:T)Expr)</c> or <c>(vm:T)</c>: the current value is cast to TypeName.</summary>
    Cast,
    /// <summary><c>^</c>: the value of a Task or IObservable.</summary>
    Stream,
}

/// <summary>One step of a binding path. Offsets are indices into the parsed text.</summary>
public sealed class PathSegment
{
    public required PathSegmentKind Kind { get; init; }
    public string Name { get; init; } = "";
    public int Start { get; init; }
    public int End { get; init; }
    /// <summary>For Parent/Cast/Attached: the type name and where it starts.</summary>
    public string? TypeName { get; init; }
    public int TypeStart { get; init; }
    /// <summary>For Attached: the property name and where it starts.</summary>
    public int NameStart { get; init; }
    /// <summary>For Parent: the ancestor level (1 = nearest).</summary>
    public int Level { get; init; } = 1;
}

/// <summary>A parsed Avalonia binding path (the subset that compiled bindings understand).</summary>
public sealed class BindingPath
{
    public List<PathSegment> Segments { get; } = [];
    public List<(int Start, int End, string Message)> Errors { get; } = [];
    /// <summary>True when the text ends right after a '.', '!' or is empty — i.e. a new member name is expected.</summary>
    public bool EndsExpectingMember { get; private set; }

    public static BindingPath Parse(string text)
    {
        var path = new BindingPath();
        var p = 0;
        var n = text.Length;
        path.ParseInto(text, ref p, n, topLevel: true);
        return path;
    }

    private void ParseInto(string text, ref int p, int end, bool topLevel)
    {
        while (p < end && char.IsWhiteSpace(text[p])) p++;
        while (p < end && text[p] == '!')
        {
            Segments.Add(new PathSegment { Kind = PathSegmentKind.Not, Start = p, End = p + 1 });
            p++;
        }

        var expectMember = true;
        if (Match(text, p, "$self"))
        {
            Segments.Add(new PathSegment { Kind = PathSegmentKind.Self, Name = "$self", Start = p, End = p + 5 });
            p += 5;
            expectMember = false;
        }
        else if (Match(text, p, "$parent"))
        {
            var start = p;
            p += 7;
            string? typeName = null;
            var typeStart = 0;
            var level = 1;
            if (p < end && text[p] == '[')
            {
                var close = text.IndexOf(']', p);
                if (close < 0 || close > end)
                {
                    Errors.Add((p, end, "Missing ']' after $parent["));
                    close = end;
                }
                var inner = text[(p + 1)..close];
                var parts = inner.Split(';');
                var t = parts[0].Trim();
                if (int.TryParse(t, out var lv)) level = lv;
                else if (t.Length > 0)
                {
                    typeName = t;
                    typeStart = p + 1 + parts[0].IndexOf(t, StringComparison.Ordinal);
                }
                if (parts.Length > 1 && int.TryParse(parts[1].Trim(), out var lv2)) level = lv2;
                p = Math.Min(close + 1, end);
            }
            Segments.Add(new PathSegment { Kind = PathSegmentKind.Parent, Name = "$parent", Start = start, End = p, TypeName = typeName, TypeStart = typeStart, Level = level });
            expectMember = false;
        }
        else if (p < end && text[p] == '#')
        {
            var s = p + 1;
            var e = ReadIdent(text, s, end);
            if (e == s) Errors.Add((p, p + 1, "Element name expected after '#'"));
            Segments.Add(new PathSegment { Kind = PathSegmentKind.Element, Name = text[s..e], Start = p, End = e, NameStart = s });
            p = e;
            expectMember = false;
        }

        while (p < end)
        {
            var c = text[p];
            if (c == '.')
            {
                if (expectMember && Segments.Count > 0 && Segments[^1].Kind != PathSegmentKind.Not)
                    Errors.Add((p, p + 1, "Unexpected '.' in binding path"));
                p++;
                expectMember = true;
                continue;
            }
            if (c == '[')
            {
                var close = FindClose(text, p, end, '[', ']');
                if (close < 0)
                {
                    Errors.Add((p, end, "Missing ']' in binding path"));
                    close = end;
                }
                Segments.Add(new PathSegment { Kind = PathSegmentKind.Indexer, Name = text[(p + 1)..Math.Min(close, end)], Start = p, End = Math.Min(close + 1, end) });
                p = Math.Min(close + 1, end);
                expectMember = false;
                continue;
            }
            if (c == '^')
            {
                Segments.Add(new PathSegment { Kind = PathSegmentKind.Stream, Start = p, End = p + 1 });
                p++;
                expectMember = false;
                continue;
            }
            if (c == '(')
            {
                var close = FindClose(text, p, end, '(', ')');
                if (close < 0)
                {
                    Errors.Add((p, end, "Missing ')' in binding path"));
                    close = end;
                }
                var innerStart = p + 1;
                var innerEnd = close;
                var q = innerStart;
                while (q < innerEnd && char.IsWhiteSpace(text[q])) q++;
                if (q < innerEnd && text[q] == '(')
                {
                    // ((Type)Expr): parse Expr, then cast the result.
                    var castClose = FindClose(text, q, innerEnd, '(', ')');
                    if (castClose < 0)
                    {
                        Errors.Add((q, innerEnd, "Missing ')' in cast"));
                        castClose = innerEnd;
                    }
                    var typeName = text[(q + 1)..castClose].Trim();
                    var typeStart = q + 1 + text[(q + 1)..castClose].IndexOf(typeName, StringComparison.Ordinal);
                    var ep = castClose + 1;
                    ParseInto(text, ref ep, innerEnd, topLevel: false);
                    Segments.Add(new PathSegment { Kind = PathSegmentKind.Cast, Start = q, End = castClose + 1, TypeName = typeName, TypeStart = typeStart });
                }
                else
                {
                    var inner = text[innerStart..innerEnd].Trim();
                    var innerOffset = innerStart + text[innerStart..innerEnd].IndexOf(inner, StringComparison.Ordinal);
                    var after = close + 1;
                    var followedByIdent = after < end && IsIdentStart(text[after]);
                    var lastDot = inner.LastIndexOf('.');
                    if (followedByIdent || lastDot < 0)
                    {
                        // (Type) cast of the current value.
                        Segments.Add(new PathSegment { Kind = PathSegmentKind.Cast, Start = p, End = Math.Min(close + 1, end), TypeName = inner, TypeStart = innerOffset });
                        expectMember = true;
                        p = Math.Min(close + 1, end);
                        continue;
                    }
                    // (Owner.Property) attached property.
                    Segments.Add(new PathSegment
                    {
                        Kind = PathSegmentKind.Attached,
                        Start = p,
                        End = Math.Min(close + 1, end),
                        TypeName = inner[..lastDot],
                        TypeStart = innerOffset,
                        Name = inner[(lastDot + 1)..],
                        NameStart = innerOffset + lastDot + 1,
                    });
                }
                p = Math.Min(close + 1, end);
                expectMember = false;
                continue;
            }
            if (IsIdentStart(c))
            {
                if (!expectMember) Errors.Add((p, p + 1, "Expected '.' before member name"));
                var s = p;
                var e = ReadIdent(text, s, end);
                Segments.Add(new PathSegment { Kind = PathSegmentKind.Property, Name = text[s..e], Start = s, End = e, NameStart = s });
                p = e;
                expectMember = false;
                continue;
            }
            if (char.IsWhiteSpace(c) && !topLevel)
            {
                p++;
                continue;
            }
            Errors.Add((p, p + 1, $"Unexpected character '{c}' in binding path"));
            p = end;
        }

        if (topLevel) EndsExpectingMember = expectMember;
    }

    public static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';
    public static bool IsIdentPart(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static int ReadIdent(string text, int p, int end)
    {
        while (p < end && IsIdentPart(text[p])) p++;
        return p;
    }

    private static bool Match(string text, int p, string s) =>
        p + s.Length <= text.Length && string.CompareOrdinal(text, p, s, 0, s.Length) == 0 &&
        (p + s.Length == text.Length || !IsIdentPart(text[p + s.Length]));

    private static int FindClose(string text, int open, int end, char o, char c)
    {
        var depth = 0;
        for (var i = open; i < end; i++)
        {
            if (text[i] == o) depth++;
            else if (text[i] == c && --depth == 0) return i;
        }
        return -1;
    }
}
