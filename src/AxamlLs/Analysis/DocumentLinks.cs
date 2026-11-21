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
        return [.. AssetReferences.Find(analyzer)
            .Where(r => File.Exists(r.Resolved))
            .Select(r => new DocumentLink(r.Start, r.End, r.Resolved, Path.GetRelativePath(root, r.Resolved).Replace('\\', '/')))];
    }
}
