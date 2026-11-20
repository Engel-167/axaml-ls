using AxamlLs.Xaml;

namespace AxamlLs.Analysis;

/// <summary>A path value that points to a project file.</summary>
/// <param name="Start">The value's start.</param>
/// <param name="End">The value's end.</param>
/// <param name="Path">The file it resolves to.</param>
/// <param name="Tooltip">The file relative to the project folder.</param>
public readonly record struct DocumentLink(int Start, int End, string Path, string Tooltip);

/// <summary>
/// Clickable paths: every value that <see cref="Assets"/> reads as a path (Image Source, Svg Path, StyleInclude Source,
/// <c>{SvgImage /x.svg}</c> …) and that resolves to an existing file of the project. A missing file is not linked
/// (AXN0501 reports it), nor are URLs or <c>avares://</c> paths into other assemblies.
/// </summary>
public static class DocumentLinks
{
    public static IReadOnlyList<DocumentLink> Find(XamlAnalyzer analyzer)
    {
        if (analyzer.ProjectDirectory is not { } root) return [];
        var links = new List<DocumentLink>();
        void Link(string value, int start, int end)
        {
            if (Assets.Resolve(analyzer, value) is not { } file || !File.Exists(file)) return;
            var lead = value.Length - value.TrimStart().Length;
            var trail = value.Length - value.TrimEnd().Length;
            links.Add(new DocumentLink(start + lead, Math.Max(start + lead, end - trail), file, Path.GetRelativePath(root, file).Replace('\\', '/')));
        }

        foreach (var el in analyzer.Result.Doc.AllElements)
        {
            foreach (var attr in el.Attributes)
            {
                if (!attr.HasValue || attr.Value.Trim().Length == 0) continue;
                if (MarkupExtension.IsMarkup(attr.Value))
                {
                    if (MarkupExtension.Parse(MappedText.FromAttribute(attr)) is not { } outer) continue;
                    foreach (var ext in outer.SelfAndNested().Where(x => Assets.AcceptsMarkup(x.LocalName) != null))
                    {
                        foreach (var arg in ext.Args)
                        {
                            if (arg is { Nested: null, Value: { } v } && (arg.Key == null || arg.Key is "Source" or "Path")) Link(v.Text, v.Start, v.End);
                        }
                    }
                }
                else if (Assets.Accepts(el, attr.Prefix == null || attr.Name.Contains('.') ? PropertyLookup.TargetProperty(analyzer, el, attr) : null) != null)
                {
                    Link(attr.Value, attr.ValueStart, attr.ValueEnd);
                }
            }
        }
        return links;
    }
}
