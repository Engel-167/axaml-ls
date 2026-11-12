namespace AxamlLs.Analysis;

/// <summary>"Did you mean" support: edit distance and the closest candidates.</summary>
public static class Spelling
{
    /// <summary>Levenshtein distance, ignoring case.</summary>
    public static int Distance(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    /// <summary>Candidates within 2 edits or a third of the word's length, closest first (then alphabetical). The word itself is never returned.</summary>
    public static List<string> Closest(string word, IEnumerable<string> candidates, int max = 3)
    {
        if (word.Length == 0) return [];
        var limit = Math.Max(2, word.Length / 3);
        return candidates.Distinct(StringComparer.Ordinal)
            .Where(c => c != word)
            .Select(c => (Candidate: c, Distance: Distance(word, c)))
            .Where(x => x.Distance <= limit && x.Distance < Math.Max(word.Length, x.Candidate.Length))
            .OrderBy(x => x.Distance).ThenBy(x => x.Candidate, StringComparer.Ordinal)
            .Take(max).Select(x => x.Candidate).ToList();
    }
}
