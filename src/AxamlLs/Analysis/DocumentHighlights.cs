using AxamlLs.Workspace;

namespace AxamlLs.Analysis;

/// <summary>How a highlighted place uses the target (LSP DocumentHighlightKind).</summary>
public enum HighlightKind
{
    Text = 1,
    Read = 2,
    Write = 3,
}

/// <summary>One place to highlight.</summary>
public readonly record struct Highlight(int Start, int End, HighlightKind Kind);

/// <summary>
/// The other uses, in the same file, of what the cursor is on: an element name, resource key, style class or C# symbol
/// (the same search as find-references, limited to the file), or the matching start and end tag names.
/// </summary>
public static class DocumentHighlights
{
    /// <summary>
    /// Declarations (<c>x:Name</c>, <c>x:Key</c>, a class in a selector, which defines its style) are writes, other uses
    /// reads. On a tag name, the start and end tag names are text highlights.
    /// </summary>
    public static IReadOnlyList<Highlight> For(XamlFile file, int offset)
    {
        if (LinkedTags.At(file.Analyzer.Result.Doc, file.Text, offset) is var (open, close))
            return [new Highlight(open.Start, open.End, HighlightKind.Text), new Highlight(close.Start, close.End, HighlightKind.Text)];
        if (Targets.Find(file.Analyzer, offset) is not { } found) return [];
        return [.. Targets.TextOccurrences(found.Target, file, [file])
            .Where(o => o.Path == file.Path)
            .Select(o => new Highlight(o.Start, o.End, o.IsDeclaration || o.IsSelector ? HighlightKind.Write : HighlightKind.Read))];
    }
}
