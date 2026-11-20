using System.Collections.Frozen;

namespace AxamlLs.Workspace;

/// <summary>
/// The words a file contains, for a cheap "can this file mention X?" check before analysing it (references, rename).
/// It may answer "maybe" for a file that turns out not to mention X, never "no" for one that does.
/// </summary>
/// <param name="Words">Every run of letters, digits, '_', '.' and '-', and its parts split at '.' and '-'
/// (<c>Grid.Row</c> gives <c>Grid.Row</c>, <c>Grid</c> and <c>Row</c>).</param>
/// <param name="Anything">The file writes characters as references (<c>&amp;#65;</c>), so any name may hide in it.</param>
internal sealed record FileWords(FrozenSet<string> Words, bool Anything)
{
    /// <summary>Whether the file may mention any of <paramref name="names"/>.</summary>
    public bool MayMention(IEnumerable<string> names)
    {
        if (Anything) return true;
        foreach (var name in names)
        {
            // A name that is not one word (a key with a space or a colon) is not in the set: no filtering for it.
            if (!IsWord(name) || Words.Contains(name)) return true;
        }
        return false;
    }

    public bool SameAs(FileWords other) => Anything == other.Anything && Words.SetEquals(other.Words);

    /// <summary>One pass over the text, no regex.</summary>
    public static FileWords Scan(string text)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        var i = 0;
        while (i < text.Length)
        {
            if (!IsWordChar(text[i]))
            {
                i++;
                continue;
            }
            var start = i;
            while (i < text.Length && IsWordChar(text[i])) i++;
            Add(words, text.AsSpan(start, i - start));
        }
        return new FileWords(words.ToFrozenSet(StringComparer.Ordinal), text.Contains("&#", StringComparison.Ordinal));
    }

    private static void Add(HashSet<string> words, ReadOnlySpan<char> run)
    {
        words.Add(run.ToString());
        if (run.IndexOfAny('.', '-') < 0) return;
        // Parts at '.' keep their hyphens (".btn-primary" → "btn-primary"); parts at both separators cover the rest.
        foreach (var range in run.Split('.'))
        {
            if (!run[range].IsEmpty) words.Add(run[range].ToString());
        }
        foreach (var range in run.SplitAny('.', '-'))
        {
            if (!run[range].IsEmpty) words.Add(run[range].ToString());
        }
    }

    private static bool IsWord(string name)
    {
        if (name.Length == 0) return false;
        foreach (var c in name)
        {
            if (!IsWordChar(c)) return false;
        }
        return true;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '.' or '-';
}

/// <summary>The C# string literals of one file that name XAML things (see <see cref="CodeLiterals"/>).</summary>
/// <param name="Path">The .cs file.</param>
/// <param name="Classes">Style classes: <c>Classes.Add("a")</c> and the like.</param>
/// <param name="ResourceKeys">Resource keys: <c>Resources["k"]</c> and the like.</param>
public sealed record CodeLiteralFacts(string Path, CodeLiteral[] Classes, CodeLiteral[] ResourceKeys)
{
    internal static CodeLiteralFacts Scan(string path, string text) =>
        new(path, [.. CodeLiterals.Classes(text)], [.. CodeLiterals.ResourceKeys(text)]);

    internal bool SameAs(CodeLiteralFacts other) =>
        Path == other.Path && Classes.AsSpan().SequenceEqual(other.Classes) && ResourceKeys.AsSpan().SequenceEqual(other.ResourceKeys);
}
