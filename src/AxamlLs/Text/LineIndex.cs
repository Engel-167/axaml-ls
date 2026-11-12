namespace AxamlLs.Text;

/// <summary>Maps between string offsets and LSP positions (0-based line, UTF-16 column).</summary>
public sealed class LineIndex
{
    private readonly int[] _starts;
    private readonly int _length;

    public LineIndex(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n') starts.Add(i + 1);
        }
        _starts = starts.ToArray();
        _length = text.Length;
    }

    public (int Line, int Character) GetPosition(int offset)
    {
        offset = Math.Clamp(offset, 0, _length);
        var line = Array.BinarySearch(_starts, offset);
        if (line < 0) line = ~line - 1;
        return (line, offset - _starts[line]);
    }

    public int GetOffset(int line, int character)
    {
        if (line < 0) return 0;
        if (line >= _starts.Length) return _length;
        var lineEnd = line + 1 < _starts.Length ? _starts[line + 1] : _length;
        return Math.Min(_starts[line] + Math.Max(character, 0), lineEnd);
    }
}
