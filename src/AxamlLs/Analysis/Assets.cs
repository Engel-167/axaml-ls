using AxamlLs.Xaml;
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

    public static void Complete(XamlAnalyzer analyzer, MappedText value, int offset, string[] accepts, List<Features.Completion> list)
    {
        if (analyzer.ProjectDirectory is not { } root) return;
        var idx = value.IndexOf(offset);
        var typed = value.Text[..idx];
        var assembly = analyzer.Project?.Compilation.AssemblyName ?? Path.GetFileName(root.TrimEnd('/'));

        // Typing the scheme: offer this project's avares:// root.
        if (typed.Length > 0 && "avares://".StartsWith(typed, StringComparison.OrdinalIgnoreCase) || typed.Equals("avares://", StringComparison.OrdinalIgnoreCase))
        {
            list.Add(new($"avares://{assembly}/", Features.ItemKind.Folder, "this project's resources", null, $"avares://{assembly}", false,
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
            list.Add(new(prefix + name + "/", Features.ItemKind.Folder, "folder — type / to open", null, prefix + name, false, start, end, "0" + name));
        }
        foreach (var f in files)
        {
            var name = Path.GetFileName(f);
            list.Add(new(prefix + name, Features.ItemKind.File, Describe(f), null, prefix + name, false, start, end, "1" + name) { PreviewPath = f });
        }

        // A name is being typed: also find matching files in the folders below, so a whole path is one completion.
        if (filter.Length == 0) return;
        var deep = new List<(int Rank, string Rel, string File)>();
        foreach (var f in Deep(dir))
        {
            if (!Fits(f, accepts)) continue;
            var rel = Path.GetRelativePath(dir, f).Replace('\\', '/');
            if (!rel.Contains('/')) continue; // already listed
            var fileName = Path.GetFileName(f);
            var rank = fileName.StartsWith(filter, StringComparison.OrdinalIgnoreCase) ? 0
                : fileName.Contains(filter, StringComparison.OrdinalIgnoreCase) ? 1
                : Matches(rel, filter) ? 2 : -1;
            if (rank >= 0) deep.Add((rank, rel, f));
        }
        foreach (var (rank, rel, f) in deep.OrderBy(x => x.Rank).ThenBy(x => x.Rel.Length).ThenBy(x => x.Rel, StringComparer.Ordinal).Take(MaxDeepMatches))
            list.Add(new(prefix + rel, Features.ItemKind.File, Describe(f), null, prefix + rel, false, start, end, $"2{rank}{rel}") { PreviewPath = f });
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
                if (Resolve(analyzer, attr.Value) is not { } file || File.Exists(file) || Directory.Exists(file)) continue;
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

    private static IEnumerable<string> Deep(string dir)
    {
        var pending = new Stack<string>();
        pending.Push(dir);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            string[] files, dirs;
            try
            {
                files = Directory.GetFiles(current);
                dirs = Directory.GetDirectories(current);
            }
            catch (Exception)
            {
                continue;
            }
            foreach (var f in files.Order(StringComparer.Ordinal)) yield return f;
            foreach (var d in dirs.Where(d => !Skipped(d)).OrderDescending(StringComparer.Ordinal)) pending.Push(d);
        }
    }

    private static string Describe(string file)
    {
        try
        {
            var size = new FileInfo(file).Length;
            return size < 1024 ? $"{size} B" : size < 1024 * 1024 ? $"{size / 1024.0:0.#} KB" : $"{size / 1024.0 / 1024.0:0.#} MB";
        }
        catch (Exception)
        {
            return "file";
        }
    }

    private static readonly Dictionary<string, string> Mime = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif",
        [".webp"] = "image/webp", [".bmp"] = "image/bmp", [".ico"] = "image/ico", [".svg"] = "image/svg+xml",
    };
    private const long MaxPreviewBytes = 2 * 1024 * 1024;

    /// <summary>
    /// Markdown for the documentation panel: the image embedded as a data: URI (Zed renders those, not file:// links),
    /// then the path and size. SVG icons that leave their color unset would draw black on a dark theme, so the
    /// preview gets a neutral fill and a readable size.
    /// </summary>
    public static string? PreviewMarkdown(string file, string? root)
    {
        if (!File.Exists(file)) return null;
        var rel = root != null ? "/" + Path.GetRelativePath(root, file).Replace('\\', '/') : file;
        var info = $"`{rel}` · {Describe(file)}";
        if (!Mime.TryGetValue(Path.GetExtension(file), out var mime) || new FileInfo(file).Length > MaxPreviewBytes) return info;
        try
        {
            byte[] bytes;
            if (mime == "image/svg+xml") bytes = System.Text.Encoding.UTF8.GetBytes(PreviewSvg(File.ReadAllText(file)));
            else bytes = File.ReadAllBytes(file);
            return $"![{Path.GetFileName(file)}](data:{mime};base64,{Convert.ToBase64String(bytes)})\n\n{info}";
        }
        catch (Exception)
        {
            return info;
        }
    }

    private static string PreviewSvg(string svg)
    {
        var open = svg.IndexOf("<svg", StringComparison.Ordinal);
        if (open < 0) return svg;
        var close = svg.IndexOf('>', open);
        if (close < 0) return svg;
        var tag = svg[open..close];
        string? Attr(string name)
        {
            var m = System.Text.RegularExpressions.Regex.Match(tag, $@"\s{name}\s*=\s*[""']([^""']*)[""']");
            return m.Success ? m.Groups[1].Value : null;
        }
        var newTag = tag;
        if (Attr("viewBox") == null && double.TryParse(Attr("width")?.TrimEnd('p', 'x'), out var w) && double.TryParse(Attr("height")?.TrimEnd('p', 'x'), out var h))
            newTag += $" viewBox=\"0 0 {w} {h}\"";
        newTag = System.Text.RegularExpressions.Regex.Replace(newTag, @"\s(width|height)\s*=\s*[""'][^""']*[""']", "");
        newTag += " width=\"96\" height=\"96\"";
        if (Attr("fill") == null) newTag += " fill=\"#9a9a9a\"";
        // currentColor has no surrounding text color in a preview and renders black.
        return (svg[..open] + newTag + svg[close..]).Replace("currentColor", "#9a9a9a", StringComparison.OrdinalIgnoreCase);
    }
}
