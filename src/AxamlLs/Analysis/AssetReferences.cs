using AxamlLs.Xaml;

namespace AxamlLs.Analysis;

/// <summary>How a path value is written; a rewritten path keeps its form.</summary>
public enum PathForm
{
    /// <summary><c>/Assets/logo.png</c>: from the project folder.</summary>
    Absolute,
    /// <summary><c>avares://SampleApp/Assets/logo.png</c>: from the project folder, through the assembly name.</summary>
    Avares,
    /// <summary><c>../Assets/logo.png</c>: from the file's folder.</summary>
    Relative,
}

/// <summary>A path value in a file, and the file it resolves to (which may not exist).</summary>
/// <param name="Start">Start of the value, trimmed.</param>
/// <param name="End">End of the value, trimmed.</param>
/// <param name="Written">The value as written, trimmed, with any <c>#fragment</c>.</param>
/// <param name="Form">How it is written.</param>
/// <param name="Resolved">The absolute path it names.</param>
public sealed record AssetReference(int Start, int End, string Written, PathForm Form, string Resolved);

/// <summary>
/// Every value <see cref="Assets"/> reads as a path (Image Source, Svg Path, StyleInclude Source, the path argument of
/// <c>{SvgImage …}</c>-like extensions) that resolves inside this project. URLs and <c>avares://</c> paths into other
/// assemblies are not included. Used by clickable paths and by following file moves.
/// </summary>
public static class AssetReferences
{
    public static IReadOnlyList<AssetReference> Find(XamlAnalyzer analyzer)
    {
        if (analyzer.ProjectDirectory == null) return [];
        var found = new List<AssetReference>();
        void Add(string value, int start, int end)
        {
            if (Assets.Resolve(analyzer, value) is not { } file) return;
            var lead = value.Length - value.TrimStart().Length;
            var trail = value.Length - value.TrimEnd().Length;
            var written = value.Trim();
            var form = written.StartsWith("avares://", StringComparison.OrdinalIgnoreCase) ? PathForm.Avares
                : written.StartsWith('/') ? PathForm.Absolute
                : PathForm.Relative;
            found.Add(new AssetReference(start + lead, Math.Max(start + lead, end - trail), written, form, file));
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
                            if (arg is { Nested: null, Value: { } v } && (arg.Key == null || arg.Key is "Source" or "Path")) Add(v.Text, v.Start, v.End);
                        }
                    }
                }
                else if (Assets.Accepts(el, attr.Prefix == null || attr.Name.Contains('.') ? PropertyLookup.TargetProperty(analyzer, el, attr) : null) != null)
                {
                    Add(attr.Value, attr.ValueStart, attr.ValueEnd);
                }
            }
        }
        return found;
    }
}
