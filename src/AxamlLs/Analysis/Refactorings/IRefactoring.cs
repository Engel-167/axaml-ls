using AxamlLs.Lsp;
using AxamlLs.Workspace;

namespace AxamlLs.Analysis.Refactorings;

/// <summary>
/// One refactoring (features plan F10). Listing actions only asks <see cref="TitleAt"/>, which must stay cheap; the
/// edits are computed when the user picks the action (<c>codeAction/resolve</c>).
/// </summary>
internal interface IRefactoring
{
    /// <summary>Stable id, carried in the action's data between listing and resolving.</summary>
    string Id { get; }

    /// <summary>The LSP code action kind: <c>refactor.rewrite</c> or <c>refactor.extract</c>.</summary>
    string Kind { get; }

    /// <summary>The action's title when it applies at the selection, else null. No edits computed here.</summary>
    string? TitleAt(XamlFile file, int start, int end, RefactoringSettings settings);

    /// <summary>The edits; empty when it no longer applies (the document changed).</summary>
    IReadOnlyList<FileEdit> Edits(XamlFile file, int start, int end, RefactoringSettings settings);
}

/// <summary>Settings refactorings read: the attribute order (shared with formatting's options).</summary>
internal sealed record RefactoringSettings(IReadOnlyList<string> AttributeOrder)
{
    public static readonly RefactoringSettings Default = new(SortAttributes.DefaultOrder);
}

/// <summary>Every refactoring, in the order the editor lists them.</summary>
internal static class Refactorings
{
    public static readonly IReadOnlyList<IRefactoring> All =
    [
        new SortAttributes(),
        .. WrapIn.Containers.Select(c => new WrapIn(c)),
        new ExtractToResource(),
        new ExtractToStyle(),
        new ConvertToCompiledBinding(),
    ];

    public static IRefactoring? ById(string id) => All.FirstOrDefault(r => r.Id == id);
}
