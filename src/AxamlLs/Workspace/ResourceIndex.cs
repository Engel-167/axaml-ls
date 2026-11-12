using System.Collections.Concurrent;
using AxamlLs.Text;
using AxamlLs.Xaml;

namespace AxamlLs.Workspace;

/// <summary>A resource declared with x:Key somewhere in the project.</summary>
public sealed record ResourceEntry(string Key, string ElementName, string FilePath, int Line, int Character, string? ValuePreview)
{
    /// <summary>Set for keys found in a compiled theme/library (no source file to open).</summary>
    public string? Assembly { get; init; }
}

/// <summary>
/// Every x:Key in the project's .axaml files, read straight from the files (no build needed). Files are
/// re-read when their timestamp changes, and open editor buffers win over the disk, so a resource you
/// just typed is offered immediately.
/// </summary>
public static class ResourceIndex
{
    private static readonly ConcurrentDictionary<string, (DateTime Stamp, List<ResourceEntry> Entries)> Cache = new(StringComparer.Ordinal);

    public static List<ResourceEntry> ForDirectory(string directory, IReadOnlyDictionary<string, string> openBuffers)
    {
        var result = new List<ResourceEntry>();
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*.axaml", SearchOption.AllDirectories).Where(f => !ProjectFiles.IsBuildOutput(f)).ToList();
        }
        catch (Exception)
        {
            files = [];
        }
        foreach (var file in files.Concat(openBuffers.Keys.Where(k => k.StartsWith(directory, StringComparison.Ordinal))).Distinct())
        {
            if (openBuffers.TryGetValue(file, out var text))
            {
                result.AddRange(Scan(file, text));
                continue;
            }
            try
            {
                var stamp = File.GetLastWriteTimeUtc(file);
                if (!Cache.TryGetValue(file, out var cached) || cached.Stamp != stamp)
                {
                    cached = (stamp, Scan(file, File.ReadAllText(file)));
                    Cache[file] = cached;
                }
                result.AddRange(cached.Entries);
            }
            catch (IOException)
            {
            }
        }
        return result;
    }

    public static List<ResourceEntry> Scan(string file, string text)
    {
        var doc = XmlDoc.Parse(text);
        LineIndex? lines = null;
        var entries = new List<ResourceEntry>();
        foreach (var el in doc.AllElements)
        {
            foreach (var a in el.Attributes)
            {
                if (a.LocalName != "Key" || a.Prefix == null || !a.HasValue || el.LookupNamespace(a.Prefix) != XamlNamespaces.X) continue;
                lines ??= new LineIndex(text);
                var (line, ch) = lines.GetPosition(a.ValueStart);
                entries.Add(new ResourceEntry(a.Value, el.Name, file, line, ch, Preview(el, text)));
            }
        }
        return entries;
    }

    /// <summary>A short hint of the value: Color="#FF0000", the element text, etc.</summary>
    private static string? Preview(XmlElement el, string text)
    {
        // <x:Double x:Key="FontSize.Body">14</x:Double>
        if (el.Children.Count == 0 && el.StartTagEnd > 0 && el.EndTagStart > el.StartTagEnd)
        {
            var inner = text[el.StartTagEnd..el.EndTagStart].Trim();
            if (inner.Length is > 0 and <= 60 && !inner.Contains('<')) return inner;
        }
        foreach (var name in new[] { "Color", "Value", "Source", "Data" })
        {
            if (el.Attr(name) is { HasValue: true } a) return $"{name}=\"{(a.Value.Length > 40 ? a.Value[..40] + "…" : a.Value)}\"";
        }
        return null;
    }
}
