using AxamlLs.Lsp;
using AxamlLs.Workspace;
using AxamlLs.Xaml;

namespace AxamlLs.Analysis.Refactorings;

/// <summary>
/// "Convert to compiled binding": <c>{ReflectionBinding X}</c> becomes <c>{Binding X}</c> (or <c>{CompiledBinding X}</c>
/// where bindings aren't compiled by default). Offered only when the binding would check: the scope has an
/// <c>x:DataType</c> and analysing the converted text reports nothing in that binding (the analyzer's own check, P7).
/// That analysis runs when the cursor is on a ReflectionBinding, which is rare enough for the lightbulb.
/// </summary>
internal sealed class ConvertToCompiledBinding : IRefactoring
{
    public string Id => "convertToCompiledBinding";

    public string Kind => "refactor.rewrite";

    public string? TitleAt(XamlFile file, int start, int end, RefactoringSettings settings) => Find(file, start) != null ? "Convert to compiled binding" : null;

    public IReadOnlyList<FileEdit> Edits(XamlFile file, int start, int end, RefactoringSettings settings) =>
        Find(file, start) is var (ext, name) ? [new FileEdit(file.Path, ext.NameStart, ext.NameEnd, name)] : [];

    private static (MarkupExtension Ext, string NewName)? Find(XamlFile file, int offset)
    {
        var analyzer = file.Analyzer;
        var el = RefactoringText.StartTagAt(analyzer.Result.Doc, offset);
        var attr = el?.Attributes.FirstOrDefault(a => a.HasValue && a.ValueStart <= offset && offset <= a.ValueEnd);
        if (el == null || attr == null || !MarkupExtension.IsMarkup(attr.Value) || MarkupExtension.Parse(MappedText.FromAttribute(attr)) is not { } outer) return null;
        var ext = outer.SelfAndNested().Where(x => x.Start <= offset && offset <= x.End).MinBy(x => x.End - x.Start);
        if (ext is not { LocalName: "ReflectionBinding", Prefix: null, Closed: true }) return null;
        var scope = analyzer.ScopeAt(el);
        if (scope.DataType == null) return null;
        var name = scope.Compiled ? "Binding" : "CompiledBinding";

        var converted = file.Text[..ext.NameStart] + name + file.Text[ext.NameEnd..];
        var shift = name.Length - ext.Name.Length;
        var after = XamlAnalyzer.Analyze(converted, analyzer.Project, analyzer.CompiledDefault, analyzer.Context);
        return after.Result.Diagnostics.Any(d => d.End >= ext.Start && d.Start <= ext.End + shift) ? null : (ext, name);
    }
}
