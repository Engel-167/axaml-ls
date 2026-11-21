using System.Text.RegularExpressions;

namespace AxamlLs.Analysis;

/// <summary>
/// The documentation shown for an asset in completion: the image itself as a <c>data:</c> URI (Zed renders those, not
/// <c>file://</c> links), then a line with its path, pixel size and file size. Images over
/// <see cref="MaxPreviewBytes"/> get the line only: the editor would otherwise parse megabytes of JSON per item.
/// </summary>
public static partial class AssetPreview
{
    /// <summary>The largest image embedded, in source bytes (about 350 KB once base64-encoded).</summary>
    public const long MaxPreviewBytes = 256 * 1024;

    private static readonly Dictionary<string, string> Mime = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif",
        [".webp"] = "image/webp", [".bmp"] = "image/bmp", [".ico"] = "image/ico", [".svg"] = "image/svg+xml",
    };

    /// <summary>
    /// Built previews, global across projects because the key is the file itself: path, root, timestamp and size. A
    /// changed file has a new key; old entries fall out by LRU (128 entries, 24 MB of markdown at most). The editor
    /// resolves the same item again as the selection moves up and down.
    /// </summary>
    private static readonly LruCache<(string, string?, DateTime, long), string> Previews = new(maxEntries: 128, maxWeight: 24 * 1024 * 1024);

    /// <summary>The preview markdown for <paramref name="file"/>; null when it does not exist.</summary>
    /// <param name="file">The asset.</param>
    /// <param name="root">The project folder, to show the path as Avalonia writes it ("/Assets/logo.png"); null shows the full path.</param>
    public static string? Markdown(string file, string? root)
    {
        FileInfo fi;
        try
        {
            fi = new FileInfo(file);
            if (!fi.Exists) return null;
        }
        catch (Exception)
        {
            return null;
        }
        var key = (file, root, fi.LastWriteTimeUtc, fi.Length);
        if (Previews.TryGet(key, out var cached)) return cached;
        var markdown = Build(fi, root);
        Previews.Add(key, markdown, markdown.Length);
        return markdown;
    }

    /// <summary>A file's size for people: "512 B", "3.4 KB", "1.2 MB".</summary>
    internal static string Describe(string file)
    {
        try
        {
            return Size(new FileInfo(file).Length);
        }
        catch (Exception)
        {
            return "file";
        }
    }

    private static string Size(long bytes) =>
        bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.#} KB" : $"{bytes / 1024.0 / 1024.0:0.#} MB";

    private static string Build(FileInfo fi, string? root)
    {
        var file = fi.FullName;
        var rel = root != null ? "/" + Path.GetRelativePath(root, file).Replace('\\', '/') : file;
        var isSvg = Path.GetExtension(file).Equals(".svg", StringComparison.OrdinalIgnoreCase);
        var pixels = isSvg ? null : ImageInfo.Read(file);
        var info = $"`{rel}` · " + (pixels is { } p ? $"{p} · " : "") + Size(fi.Length);
        if (!Mime.TryGetValue(Path.GetExtension(file), out var mime)) return info;
        try
        {
            byte[] bytes;
            if (isSvg)
            {
                bytes = System.Text.Encoding.UTF8.GetBytes(PreviewSvg(Minify(File.ReadAllText(file))));
                if (bytes.Length > MaxPreviewBytes) return info;
            }
            else
            {
                if (fi.Length > MaxPreviewBytes) return info;
                bytes = File.ReadAllBytes(file);
            }
            return $"![{Path.GetFileName(file)}](data:{mime};base64,{Convert.ToBase64String(bytes)})\n\n{info}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return info;
        }
    }

    /// <summary>
    /// Drops what an SVG carries that does not draw anything: comments, <c>&lt;metadata&gt;</c>, Inkscape and Sodipodi
    /// elements and attributes, and whitespace between tags. Attribute values (path data) are left byte-identical.
    /// </summary>
    internal static string Minify(string svg)
    {
        svg = Comment().Replace(svg, "");
        svg = Metadata().Replace(svg, "");
        svg = EditorElement().Replace(svg, "");
        svg = EditorAttribute().Replace(svg, "");
        return BetweenTags().Replace(svg, "><").Trim();
    }

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comment();

    [GeneratedRegex(@"<metadata\b[^>]*/>|<metadata\b.*?</metadata\s*>", RegexOptions.Singleline)]
    private static partial Regex Metadata();

    [GeneratedRegex(@"<(inkscape|sodipodi):([\w.-]+)\b[^>]*?(/>|>.*?</\1:\2\s*>)", RegexOptions.Singleline)]
    private static partial Regex EditorElement();

    [GeneratedRegex(@"\s(xmlns:)?(inkscape|sodipodi)(:[\w.-]+)?\s*=\s*(""[^""]*""|'[^']*')")]
    private static partial Regex EditorAttribute();

    [GeneratedRegex(@">\s+<")]
    private static partial Regex BetweenTags();

    /// <summary>
    /// SVG icons that leave their color unset would draw black on a dark theme, so the preview gets a neutral fill and
    /// a readable size.
    /// </summary>
    private static string PreviewSvg(string svg)
    {
        var open = svg.IndexOf("<svg", StringComparison.Ordinal);
        if (open < 0) return svg;
        var close = svg.IndexOf('>', open);
        if (close < 0) return svg;
        var tag = svg[open..close];
        string? Attr(string name)
        {
            var m = Regex.Match(tag, $@"\s{name}\s*=\s*[""']([^""']*)[""']");
            return m.Success ? m.Groups[1].Value : null;
        }
        var newTag = tag;
        if (Attr("viewBox") == null && double.TryParse(Attr("width")?.TrimEnd('p', 'x'), out var w) && double.TryParse(Attr("height")?.TrimEnd('p', 'x'), out var h))
            newTag += $" viewBox=\"0 0 {w} {h}\"";
        newTag = Regex.Replace(newTag, @"\s(width|height)\s*=\s*[""'][^""']*[""']", "");
        newTag += " width=\"96\" height=\"96\"";
        if (Attr("fill") == null) newTag += " fill=\"#9a9a9a\"";
        // currentColor has no surrounding text color in a preview and renders black.
        return (svg[..open] + newTag + svg[close..]).Replace("currentColor", "#9a9a9a", StringComparison.OrdinalIgnoreCase);
    }
}
