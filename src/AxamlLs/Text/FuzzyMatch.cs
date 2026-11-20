namespace AxamlLs.Text;

/// <summary>
/// Fuzzy matching of a typed pattern against a name or path (asset completion, symbol search): the pattern's characters
/// in order, case-insensitively, scored higher for matches at word starts and runs of consecutive characters.
/// </summary>
public static class FuzzyMatch
{
    /// <summary>
    /// fzf's scoring (v1, the greedy-then-shortest-window variant): the typed letters must appear in order; each match
    /// scores, more at the start of a word or path segment and when consecutive, and gaps cost. Null when it doesn't match.
    /// </summary>
    public static int? Score(string text, string pattern)
    {
        if (pattern.Length == 0) return 0;
        static bool Eq(char a, char b) => char.ToLowerInvariant(a) == char.ToLowerInvariant(b);

        // Forward: the first position where the whole pattern has been seen.
        int p = 0, end = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (!Eq(text[i], pattern[p])) continue;
            if (++p == pattern.Length)
            {
                end = i;
                break;
            }
        }
        if (end < 0) return null;
        // Backward from there: the shortest window that still holds the pattern.
        var start = 0;
        p = pattern.Length - 1;
        for (var i = end; i >= 0; i--)
        {
            if (!Eq(text[i], pattern[p])) continue;
            if (--p < 0)
            {
                start = i;
                break;
            }
        }

        const int Match = 16, GapStart = -3, GapExtension = -1, Consecutive = 4;
        int score = 0, consecutive = 0;
        var inGap = false;
        p = 0;
        for (var i = start; i <= end; i++)
        {
            if (p < pattern.Length && Eq(text[i], pattern[p]))
            {
                var bonus = Bonus(text, i);
                score += Match + (p == 0 ? bonus * 2 : bonus);
                if (consecutive > 0) score += Math.Max(bonus, Consecutive);
                consecutive++;
                inGap = false;
                p++;
            }
            else
            {
                score += inGap ? GapExtension : GapStart;
                inGap = true;
                consecutive = 0;
            }
        }
        return score;

        static int Bonus(string t, int i)
        {
            if (i == 0) return 10;
            var prev = t[i - 1];
            var c = t[i];
            if (prev is '/' or '\\') return 9;
            if (prev is '_' or '-' or '.' or ' ') return 8;
            if (char.IsLower(prev) && char.IsUpper(c)) return 7;
            if (!char.IsDigit(prev) && char.IsDigit(c)) return 7;
            return 0;
        }
    }
}
