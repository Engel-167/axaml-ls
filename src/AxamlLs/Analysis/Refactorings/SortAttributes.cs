using AxamlLs.Lsp;
using AxamlLs.Workspace;
using AxamlLs.Xaml;

namespace AxamlLs.Analysis.Refactorings;

/// <summary>
/// "Sort attributes" on the tag under the cursor. Each attribute moves as written into the place of another, so the
/// tag's line breaks and indentation stay where they were. The order is a list of patterns
/// (<c>formatting.attributeOrder</c>): exact names, <c>prefix*</c>, <c>*</c> for the rest and <c>@events</c>; an
/// attribute takes the first pattern it matches, and ties sort by name.
/// </summary>
internal sealed class SortAttributes : IRefactoring
{
    public static readonly IReadOnlyList<string> DefaultOrder =
    [
        "x:Class", "xmlns", "xmlns:*", "x:Name", "Name", "x:Key", "x:DataType",
        "Grid.*", "DockPanel.*", "Canvas.*", "RelativePanel.*",
        "Width", "Height", "MinWidth", "MinHeight", "MaxWidth", "MaxHeight", "Margin", "Padding",
        "HorizontalAlignment", "VerticalAlignment", "HorizontalContentAlignment", "VerticalContentAlignment",
        "*", "@events",
    ];

    public string Id => "sortAttributes";

    public string Kind => "refactor.rewrite";

    public string? TitleAt(XamlFile file, int start, int end, RefactoringSettings settings) =>
        Tag(file, start) is { } el && !Sorted(file, el, settings.AttributeOrder).SequenceEqual(el.Attributes) ? "Sort attributes" : null;

    public IReadOnlyList<FileEdit> Edits(XamlFile file, int start, int end, RefactoringSettings settings)
    {
        if (Tag(file, start) is not { } el) return [];
        var sorted = Sorted(file, el, settings.AttributeOrder);
        if (sorted.SequenceEqual(el.Attributes)) return [];
        var text = file.Text;
        var attrs = el.Attributes;
        var result = new System.Text.StringBuilder();
        for (var i = 0; i < attrs.Count; i++)
        {
            if (i > 0) result.Append(text, RefactoringText.AttrEnd(attrs[i - 1]), attrs[i].NameStart - RefactoringText.AttrEnd(attrs[i - 1])); // the gap stays
            result.Append(text, sorted[i].NameStart, RefactoringText.AttrEnd(sorted[i]) - sorted[i].NameStart);
        }
        return [new FileEdit(file.Path, attrs[0].NameStart, RefactoringText.AttrEnd(attrs[^1]), result.ToString())];
    }

    private static XmlElement? Tag(XamlFile file, int offset) =>
        RefactoringText.StartTagAt(file.Analyzer.Result.Doc, offset) is { Attributes.Count: >= 2 } el && RefactoringText.Complete(el) ? el : null;

    internal static List<XmlAttr> Sorted(XamlFile file, XmlElement el, IReadOnlyList<string> order)
    {
        int Rank(XmlAttr a)
        {
            var isEvent = a.Prefix == null && PropertyLookup.TargetProperty(file.Analyzer, el, a) is { } symbol && Members.IsEvent(symbol);
            for (var i = 0; i < order.Count; i++)
            {
                var pattern = order[i];
                var hit = pattern switch
                {
                    "@events" => isEvent,
                    "*" => !isEvent,
                    _ when pattern.EndsWith('*') => a.Name.StartsWith(pattern[..^1], StringComparison.Ordinal),
                    _ => a.Name == pattern,
                };
                if (hit) return i;
            }
            return order.Count;
        }
        return [.. el.Attributes.Select((a, i) => (a, i, rank: Rank(a)))
            .OrderBy(x => x.rank)
            .ThenBy(x => x.a.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.i)
            .Select(x => x.a)];
    }
}
