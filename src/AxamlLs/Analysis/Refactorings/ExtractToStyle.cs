using AxamlLs.Lsp;
using AxamlLs.Workspace;
using AxamlLs.Xaml;

namespace AxamlLs.Analysis.Refactorings;

/// <summary>
/// "Extract to style": the element's styled-property attributes become a <c>&lt;Style Selector="Type.newClass"&gt;</c>
/// with one Setter each, in the nearest <c>Styles</c> (an ancestor's, the root <c>Styles</c>, or a new
/// <c>&lt;Root.Styles&gt;</c>), and the element gets the class. Bindings, attached properties, names, events and plain
/// CLR properties stay on the element, since a Setter can't hold them.
/// </summary>
internal sealed class ExtractToStyle : IRefactoring
{
    private static readonly HashSet<string> Bindings = new(StringComparer.Ordinal) { "Binding", "CompiledBinding", "ReflectionBinding", "TemplateBinding" };

    public string Id => "extractToStyle";

    public string Kind => "refactor.extract";

    public string? TitleAt(XamlFile file, int start, int end, RefactoringSettings settings) => Find(file, start) != null ? "Extract to style" : null;

    public IReadOnlyList<FileEdit> Edits(XamlFile file, int start, int end, RefactoringSettings settings)
    {
        if (Find(file, start) is not var (el, moved)) return [];
        var text = file.Text;
        var nl = RefactoringText.Newline(text);
        var unit = RefactoringText.Unit(text);
        var cls = UniqueClass(file);
        var selector = el.Name.Replace(':', '|') + "." + cls;
        string Style(string indent) =>
            $"<Style Selector=\"{selector}\">" +
            string.Concat(moved.Select(a => $"{nl}{indent}{unit}<Setter Property=\"{a.Name}\" Value={text[(a.ValueStart - 1)..RefactoringText.AttrEnd(a)]} />")) +
            $"{nl}{indent}</Style>";

        var root = file.Analyzer.Result.Doc.Root!;
        var insert = Container(root, el) is { } container
            ? RefactoringText.AppendChild(file.Path, text, container, Style)
            : RefactoringText.NewPropertyElement(file.Path, text, root, "Styles", Style);
        if (insert == null) return [];

        var edits = new List<FileEdit> { insert };
        var attrs = el.Attributes;
        for (var i = 0; i < attrs.Count; i++)
        {
            if (moved.Contains(attrs[i])) edits.Add(new FileEdit(file.Path, i == 0 ? el.NameEnd : RefactoringText.AttrEnd(attrs[i - 1]), RefactoringText.AttrEnd(attrs[i]), ""));
        }
        edits.Add(el.Attr("Classes") is { } classes
            ? new FileEdit(file.Path, classes.ValueEnd, classes.ValueEnd, (classes.Value.Trim().Length > 0 ? " " : "") + cls)
            : new FileEdit(file.Path, el.NameEnd, el.NameEnd, $" Classes=\"{cls}\""));
        return edits;
    }

    private static (XmlElement El, List<XmlAttr> Moved)? Find(XamlFile file, int offset)
    {
        var el = RefactoringText.StartTagAt(file.Analyzer.Result.Doc, offset);
        if (el?.Parent == null || el.LocalName.Contains('.') || !RefactoringText.Complete(el)) return null;
        if (offset > el.NameEnd || offset < el.NameStart) return null; // on the tag name: the element as a whole
        if (el.Attr("Classes") is { } classes && MarkupExtension.IsMarkup(classes.Value)) return null;
        if (file.Analyzer.Result.Elements.GetValueOrDefault(el)?.Type is not { } type || !Members.InheritsByName(type, "StyledElement")) return null;
        var styled = PropertyLookup.AvaloniaProperties(type).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var moved = el.Attributes.Where(a =>
            a.Prefix == null && !a.Name.Contains('.') && a.Name is not ("Name" or "Classes") && styled.Contains(a.Name)
            && !(MarkupExtension.IsMarkup(a.Value) && MarkupExtension.Parse(MappedText.FromAttribute(a)) is { } ext && Bindings.Contains(ext.LocalName))).ToList();
        if (moved.Count == 0) return null;
        var root = file.Analyzer.Result.Doc.Root!;
        if (Container(root, el) == null && root.LocalName is "ResourceDictionary") return null;
        return (el, moved);
    }

    /// <summary>The nearest ancestor's Styles, or the root when it is a Styles; null: make one on the root.</summary>
    private static XmlElement? Container(XmlElement root, XmlElement el)
    {
        if (root.LocalName == "Styles") return root;
        for (var a = el.Parent; a != null; a = a.Parent)
        {
            if (a.LocalName.Contains('.')) continue;
            if (a.Children.FirstOrDefault(c => c.Name == a.Name + ".Styles") is { } styles) return styles;
        }
        return null;
    }

    private static string UniqueClass(XamlFile file)
    {
        // Used in Classes, and styled by a selector: reusing either would merge with styles already there.
        var taken = ClassIndex.Scan(file.Path, file.Text).ToHashSet(StringComparer.Ordinal);
        foreach (var (token, _, _) in Selectors.DocumentTokens(file.Analyzer.Result.Doc))
        {
            if (token.Kind == SelectorTokenKind.Class) taken.Add(token.Text[1..]);
        }
        if (!taken.Contains("newClass")) return "newClass";
        for (var i = 2; ; i++)
        {
            if (!taken.Contains("newClass" + i)) return "newClass" + i;
        }
    }
}
