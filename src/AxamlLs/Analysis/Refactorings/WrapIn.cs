using AxamlLs.Lsp;
using AxamlLs.Workspace;
using AxamlLs.Xaml;

namespace AxamlLs.Analysis.Refactorings;

/// <summary>
/// "Wrap in Border/Grid/…" for one selected control (the whole element selected, or the cursor on its tag name).
/// The element is indented one level deeper as written. Attached layout (<c>Grid.Row</c>, <c>DockPanel.Dock</c>, …)
/// moves to the new container, since it places the container now. Not offered for the root, for non-controls (a
/// <c>RowDefinition</c>, a <c>Setter</c>), or where whitespace is preserved (P7).
/// </summary>
internal sealed class WrapIn(string container) : IRefactoring
{
    public static readonly string[] Containers = ["Border", "Grid", "StackPanel", "Panel", "ScrollViewer"];

    private static readonly string[] AttachedLayout = ["Grid.", "DockPanel.", "Canvas.", "RelativePanel."];

    public string Id => "wrapIn." + container;

    public string Kind => "refactor.rewrite";

    public string? TitleAt(XamlFile file, int start, int end, RefactoringSettings settings) => Target(file, start, end) != null ? $"Wrap in {container}" : null;

    public IReadOnlyList<FileEdit> Edits(XamlFile file, int start, int end, RefactoringSettings settings)
    {
        if (Target(file, start, end) is not { } el) return [];
        var text = file.Text;
        var nl = RefactoringText.Newline(text);
        var unit = RefactoringText.Unit(text);
        var indent = RefactoringText.LineIndent(text, el.Start);

        var moved = el.Attributes.Where(a => AttachedLayout.Any(p => a.Name.StartsWith(p, StringComparison.Ordinal))).ToList();
        var inner = text[el.Start..el.End];
        // Remove the moved attributes with the whitespace before each, from the end so offsets stay valid.
        var attrs = el.Attributes;
        for (var i = attrs.Count - 1; i >= 0; i--)
        {
            if (!moved.Contains(attrs[i])) continue;
            var from = (i == 0 ? el.NameEnd : RefactoringText.AttrEnd(attrs[i - 1])) - el.Start;
            inner = inner[..from] + inner[(RefactoringText.AttrEnd(attrs[i]) - el.Start)..];
        }
        var shifted = inner.Replace("\n", "\n" + unit, StringComparison.Ordinal);
        var movedText = string.Concat(moved.Select(a => " " + text[a.NameStart..RefactoringText.AttrEnd(a)]));
        var wrapped = $"<{container}{movedText}>{nl}{indent}{unit}{shifted}{nl}{indent}</{container}>";
        return [new FileEdit(file.Path, el.Start, el.End, wrapped)];
    }

    private static XmlElement? Target(XamlFile file, int start, int end)
    {
        var doc = file.Analyzer.Result.Doc;
        var text = file.Text;
        XmlElement? el;
        if (end > start)
        {
            while (start < end && char.IsWhiteSpace(text[start])) start++;
            while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
            el = doc.ElementAt(start)?.SelfAndAncestors().FirstOrDefault(e => e.Start == start && e.End == end);
        }
        else
        {
            el = doc.ElementAt(start) is { } at && at.NameStart <= start && start <= at.NameEnd ? at : null;
        }
        if (el?.Parent == null || el.LocalName.Contains('.') || !RefactoringText.Complete(el) || el.End <= el.Start) return null;
        if (el.Descendants().Prepend(el).Any(e => e.Attr("xml:space")?.Value == "preserve")) return null;
        var type = file.Analyzer.Result.Elements.GetValueOrDefault(el)?.Type;
        return type != null && Members.InheritsByName(type, "Control") ? el : null;
    }
}
