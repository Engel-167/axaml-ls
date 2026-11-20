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
    private static readonly FileFactsCache<List<ResourceEntry>> Facts = new(Scan, SameEntries);
    private static readonly ConcurrentDictionary<string, (List<ResourceEntry>[] Parts, List<ResourceEntry> Merged)> Merged = new(StringComparer.Ordinal);

    /// <summary>
    /// Every x:Key of the project. Repeated calls with nothing changed return the same list without touching the disk
    /// (see <see cref="FileIndex"/>); an edit that leaves a buffer's entries as they were keeps the same list too, so callers
    /// can compare by reference to see whether anything changed.
    /// </summary>
    public static List<ResourceEntry> ForDirectory(string directory, IReadOnlyDictionary<string, string> openBuffers)
    {
        var index = FileIndex.For(directory);
        var parts = Facts.Collect(index, index.WithExtensions(".axaml"), openBuffers, ".axaml");
        if (Merged.TryGetValue(index.Root, out var last) && FileFactsCache<List<ResourceEntry>>.SameObjects(last.Parts, parts)) return last.Merged;
        var merged = new List<ResourceEntry>(parts.Sum(p => p.Count));
        foreach (var p in parts) merged.AddRange(p);
        Merged[index.Root] = (parts, merged);
        return merged;
    }

    private static bool SameEntries(List<ResourceEntry> a, List<ResourceEntry> b) => a.SequenceEqual(b);

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
