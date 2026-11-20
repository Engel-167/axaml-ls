using AxamlLs.Xaml;
using AxamlLs.Workspace;
using Microsoft.CodeAnalysis;

namespace AxamlLs.Analysis;

/// <summary>
/// File paths in property values: Svg Path, Image Source, Window Icon, StyleInclude/ResourceInclude Source, any Uri.
/// Avalonia accepts "/Assets/x.png" (from the project root), "avares://Assembly/Assets/x.png", and paths relative to
/// the .axaml file ("../Assets/x.png"). Completion walks the folders, and once a name is being typed it also finds
/// matching files deeper down; missing files are reported; go-to-definition opens the file.
/// </summary>
public static class Assets
{
    private static readonly string[] Images = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".ico"];
    private static readonly HashSet<string> SkippedDirs = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".git", ".vs", ".idea", "node_modules" };
    private const int MaxDeepMatches = 150;
    /// <summary>A folder with thousands of icons is cut to this many entries (the editor asks again as you type).</summary>
    private const int MaxEntries = 400;

    /// <summary>File extensions a property accepts (empty = any file), or null when the property is not a path.</summary>
    public static string[]? Accepts(XmlElement el, ISymbol? prop)
    {
        if (prop == null) return null;
        var type = Members.ValueTypeOf(prop) is { } t ? Members.UnwrapNullable(t) : null;
        if (type == null) return null;
        var owner = prop.ContainingType?.Name ?? "";
        if (el.LocalName is "StyleInclude" or "ResourceInclude" or "MergeResourceInclude" && prop.Name == "Source") return [".axaml", ".xaml"];
        if (type.SpecialType == SpecialType.System_String && prop.Name is "Path" or "Source" && owner.Contains("Svg", StringComparison.Ordinal)) return [".svg"];
        return type.Name switch
        {
            "IImage" or "Bitmap" or "IBitmap" or "IImageBrushSource" => owner.Contains("Svg", StringComparison.Ordinal) ? [".svg", .. Images] : Images,
            "WindowIcon" => [".ico", ".png"],
            "Uri" => [],
            _ => null,
        };
    }

    /// <summary>Extensions for a markup extension's positional path argument ({SvgImage /Assets/x.svg}), or null.</summary>
    public static string[]? AcceptsMarkup(string extensionName) => extensionName switch
    {
        "SvgImage" or "SvgSource" => [".svg"],
        "Bitmap" or "BitmapExtension" => Images,
        _ => null,
    };

    /// <summary>The file a path value points to (null when it can't be resolved to a project file: other assemblies, http, …).</summary>
    public static string? Resolve(XamlAnalyzer analyzer, string value)
    {
        var v = value.Trim();
        var hash = v.IndexOf('#');
        if (hash >= 0) v = v[..hash];
        if (v.Length == 0 || analyzer.ProjectDirectory is not { } root) return null;
        string? relativeToRoot = null;
        if (v.StartsWith("avares://", StringComparison.OrdinalIgnoreCase))
        {
            var rest = v[9..];
            var slash = rest.IndexOf('/');
            var assembly = slash < 0 ? rest : rest[..slash];
            if (analyzer.Project?.Compilation.AssemblyName is { } own && !string.Equals(assembly, own, StringComparison.OrdinalIgnoreCase)) return null;
            relativeToRoot = slash < 0 ? "" : rest[(slash + 1)..];
        }
        else if (v.Contains("://", StringComparison.Ordinal))
        {
            return null;
        }
        else if (v.StartsWith('/'))
        {
            relativeToRoot = v[1..];
        }
        string full;
        if (relativeToRoot != null) full = Path.Combine(root, relativeToRoot);
        else if (analyzer.DocumentPath is { } doc) full = Path.Combine(Path.GetDirectoryName(doc)!, v);
        else return null;
        return Path.GetFullPath(Uri.UnescapeDataString(full));
    }

    public static void Complete(XamlAnalyzer analyzer, MappedText value, int offset, string[] accepts, List<CompletionItem> list)
    {
        if (analyzer.ProjectDirectory is not { } root) return;
        var idx = value.IndexOf(offset);
        var typed = value.Text[..idx];
        var assembly = analyzer.Project?.Compilation.AssemblyName ?? Path.GetFileName(root.TrimEnd('/'));

        // Typing the scheme: offer this project's avares:// root.
        if (typed.Length > 0 && "avares://".StartsWith(typed, StringComparison.OrdinalIgnoreCase) || typed.Equals("avares://", StringComparison.OrdinalIgnoreCase))
        {
            list.Add(new($"avares://{assembly}/", CompletionItemKind.Folder, "this project's resources", null, $"avares://{assembly}", false,
                value.Abs(0), value.Abs(SegmentEnd(value.Text, idx))));
            return;
        }

        var lastSlash = typed.LastIndexOf('/');
        var dirPart = lastSlash >= 0 ? typed[..(lastSlash + 1)] : "";
        var filter = typed[(lastSlash + 1)..];
        string? dir;
        if (dirPart.Length == 0) dir = root; // nothing typed yet: offer the project root as "/…"
        else dir = Resolve(analyzer, dirPart.Length == 1 ? "/" : dirPart) is { } d ? d : null;
        if (dir == null || !Directory.Exists(dir)) return;
        var prefix = dirPart.Length == 0 ? "/" : ""; // an empty value becomes an absolute path
        var start = value.Abs(lastSlash + 1);
        var end = value.Abs(SegmentEnd(value.Text, idx));

        IEnumerable<string> subdirs, files;
        try
        {
            subdirs = Directory.EnumerateDirectories(dir).Where(d => !Skipped(d) && Matches(Path.GetFileName(d), filter)).Order(StringComparer.Ordinal).ToList();
            files = Directory.EnumerateFiles(dir).Where(f => Fits(f, accepts) && Matches(Path.GetFileName(f), filter))
                .OrderBy(f => !Path.GetFileName(f).StartsWith(filter, StringComparison.OrdinalIgnoreCase)).ThenBy(f => f, StringComparer.Ordinal)
                .Take(MaxEntries).ToList();
        }
        catch (Exception)
        {
            return;
        }
        // Folders insert their name without the slash: Zed never reopens the menu after a completion, but typing the
        // "/" yourself is a trigger character, so the folder's contents show up right away.
        foreach (var d in subdirs)
        {
            var name = Path.GetFileName(d);
            list.Add(new(prefix + name + "/", CompletionItemKind.Folder, "folder — type / to open", null, prefix + name, false, start, end, "0" + name));
        }
        foreach (var f in files)
        {
            var name = Path.GetFileName(f);
            list.Add(new(prefix + name, CompletionItemKind.File, AssetPreview.Describe(f), null, prefix + name, false, start, end, "1" + name) { PreviewPath = f });
        }

        // A name is being typed: also find matching files in the folders below, so a whole path is one completion.
        if (filter.Length == 0) return;
        var deep = new List<(int Rank, int Score, string Rel, string File)>();
        foreach (var f in Deep(dir, analyzer.Context.FileIndex))
        {
            if (!Fits(f, accepts)) continue;
            var rel = Path.GetRelativePath(dir, f).Replace('\\', '/');
            if (!rel.Contains('/')) continue; // already listed
            var fileName = Path.GetFileName(f);
            // Tiers first (name starts with / contains the text / the path matches it fuzzily), then fzf's score.
            if (FuzzyScore(rel, filter) is not { } score) continue;
            var rank = fileName.StartsWith(filter, StringComparison.OrdinalIgnoreCase) ? 0
                : fileName.Contains(filter, StringComparison.OrdinalIgnoreCase) ? 1 : 2;
            deep.Add((rank, score, rel, f));
        }
        foreach (var (rank, _, rel, f) in deep.OrderBy(x => x.Rank).ThenByDescending(x => x.Score).ThenBy(x => x.Rel.Length)
                     .ThenBy(x => x.Rel, StringComparer.Ordinal).Take(MaxDeepMatches))
            list.Add(new(prefix + rel, CompletionItemKind.File, AssetPreview.Describe(f), null, prefix + rel, false, start, end, $"2{rank}{rel}") { PreviewPath = f });
    }

    /// <summary>Warnings for path values that point to a file in this project that does not exist.</summary>
    public static IEnumerable<XamlDiagnostic> Missing(XamlAnalyzer analyzer, Func<XmlElement, XmlAttr, ISymbol?> targetProperty)
    {
        if (analyzer.ProjectDirectory == null) yield break;
        foreach (var el in analyzer.Result.Doc.AllElements)
        {
            foreach (var attr in el.Attributes)
            {
                if (!attr.HasValue || attr.Value.Trim().Length == 0 || MarkupExtension.IsMarkup(attr.Value)) continue;
                if (Accepts(el, targetProperty(el, attr)) == null) continue;
                if (Resolve(analyzer, attr.Value) is not { } file || Exists(analyzer.Context.FileIndex, file)) continue;
                yield return new XamlDiagnostic(attr.ValueStart, attr.ValueEnd, Severity.Warning, Codes.MissingAsset, $"File not found: {attr.Value.Trim()} ({Path.GetRelativePath(analyzer.ProjectDirectory, file)})");
            }
        }
    }

    /// <summary>The typed letters appear in the name in order (the editor's fuzzy match), ignoring case.</summary>
    private static bool Matches(string name, string filter)
    {
        var i = 0;
        foreach (var c in name)
        {
            if (i < filter.Length && char.ToLowerInvariant(c) == char.ToLowerInvariant(filter[i])) i++;
        }
        return i == filter.Length;
    }

    private static int SegmentEnd(string text, int idx)
    {
        var e = idx;
        while (e < text.Length && text[e] != '/') e++;
        return e;
    }

    private static bool Fits(string file, string[] accepts) =>
        accepts.Length == 0
            ? !file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            : accepts.Any(ext => file.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    private static bool Skipped(string dir) => Path.GetFileName(dir) is var n && (SkippedDirs.Contains(n) || n.StartsWith('.'));

    /// <summary>
    /// Every file below a folder. Inside the project this is a range of the project's sorted <see cref="FileIndex"/>
    /// (found by binary search, no disk access), so typing in a path value does not walk the tree on each keystroke.
    /// </summary>
    private static IEnumerable<string> Deep(string dir, FileIndex? index)
    {
        var prefix = Path.TrimEndingDirectorySeparator(dir) + Path.DirectorySeparatorChar;
        if (index != null && (string.Equals(Path.TrimEndingDirectorySeparator(dir), index.Root, StringComparison.Ordinal) ||
            prefix.StartsWith(index.Root + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !index.IsPruned(prefix + "x")))
        {
            var files = index.Files;
            var lo = LowerBound(files, prefix);
            for (var i = lo; i < files.Count && files[i].StartsWith(prefix, StringComparison.Ordinal); i++) yield return files[i];
            yield break;
        }

        // Outside the project (a path relative to the file that leaves it), or no index: walk the disk.
        var pending = new Stack<string>();
        pending.Push(dir);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            string[] found, dirs;
            try
            {
                found = Directory.GetFiles(current);
                dirs = Directory.GetDirectories(current);
            }
            catch (Exception)
            {
                continue;
            }
            foreach (var f in found.Order(StringComparer.Ordinal)) yield return f;
            foreach (var d in dirs.Where(d => !Skipped(d)).OrderDescending(StringComparer.Ordinal)) pending.Push(d);
        }
    }

    private static int LowerBound(IReadOnlyList<string> sorted, string value)
    {
        int lo = 0, hi = sorted.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (string.CompareOrdinal(sorted[mid], value) < 0) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    /// <summary>Whether a resolved path exists: the project's file index answers for its files, the disk for the rest.</summary>
    private static bool Exists(FileIndex? index, string path) =>
        index?.Contains(path) == true || File.Exists(path) || Directory.Exists(path);

    /// <summary>
    /// fzf's scoring (v1, the greedy-then-shortest-window variant): the typed letters must appear in order; each match
    /// scores, more at the start of a word or path segment and when consecutive, and gaps cost. Null when it doesn't match.
    /// </summary>
    internal static int? FuzzyScore(string text, string pattern)
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

    /// <summary>The project file a path value under the cursor points to (Svg Path, Image Source …).</summary>
    public static string? At(XamlAnalyzer analyzer, int offset)
    {
        var el = analyzer.Result.Doc.ElementAt(offset);
        var attr = el?.Attributes.FirstOrDefault(a => a.HasValue && a.ValueStart <= offset && offset <= a.ValueEnd);
        if (el == null || attr == null || MarkupExtension.IsMarkup(attr.Value) || Assets.Accepts(el, PropertyLookup.TargetProperty(analyzer, el, attr)) == null) return null;
        return Assets.Resolve(analyzer, attr.Value) is { } file && File.Exists(file) ? file : null;
    }

    /// <summary>Paths that point to files that do not exist (AXN diagnostics).</summary>
    public static IEnumerable<XamlDiagnostic> Diagnostics(XamlAnalyzer analyzer) =>
        Assets.Missing(analyzer, (el, attr) => attr.Prefix == null || attr.Name.Contains('.') ? PropertyLookup.TargetProperty(analyzer, el, attr) : null);
}
