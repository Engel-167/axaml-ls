using AxamlLs.Text;
using AxamlLs.Workspace;
using AxamlLs.Xaml;

namespace AxamlLs.Analysis;

public static partial class Selectors
{
    /// <summary>What the common pseudo-classes mean, for hover.</summary>
    private static readonly Dictionary<string, string> PseudoMeanings = new(StringComparer.Ordinal)
    {
        [":pointerover"] = "the pointer is over the control",
        [":pressed"] = "the control is being pressed (pointer button or key)",
        [":disabled"] = "the control is disabled (`IsEnabled` is false)",
        [":focus"] = "the control has keyboard focus",
        [":focus-visible"] = "the control has keyboard focus that came from keyboard navigation",
        [":focus-within"] = "the control or one of its descendants has focus",
        [":selected"] = "the item is selected",
        [":checked"] = "the control is checked (`IsChecked` is true)",
        [":unchecked"] = "the control is unchecked (`IsChecked` is false)",
        [":indeterminate"] = "the control is neither checked nor unchecked",
        [":empty"] = "the control has no content or items",
        [":flyout-open"] = "the control's flyout is open",
        [":error"] = "data validation reports an error",
        [":expanded"] = "the control is expanded",
        [":open"] = "the drop-down or popup is open",
        [":dragging"] = "the control is being dragged",
        [":inlineholder"] = "the text box shows its watermark inline",
        [":vertical"] = "the control is laid out vertically",
        [":horizontal"] = "the control is laid out horizontally",
        [":readonly"] = "the control is read-only",
        [":not"] = "matches when the selector inside does not",
        [":is"] = "matches the type, or a type derived from it",
        [":nth-child"] = "matches by position among siblings: `2n+1`, `odd`, `even`",
        [":nth-last-child"] = "matches by position counted from the end",
    };

    /// <summary>Hover for the selector token under the cursor, or null when the offset is not in a selector.</summary>
    public static HoverInfo? Hover(XamlAnalyzer analyzer, int offset)
    {
        foreach (var el in analyzer.Result.Doc.AllElements)
        {
            if (SelectorAttr(el) is not { } attr || offset < attr.ValueStart || offset > attr.ValueEnd) continue;
            var text = MappedText.FromAttribute(attr);
            var tokens = Tokenize(text.Text);
            SelectorToken? hit = tokens.Cast<SelectorToken?>().FirstOrDefault(t => text.Abs(t!.Value.Start) <= offset && offset < text.Abs(t.Value.End))
                                 ?? tokens.Cast<SelectorToken?>().FirstOrDefault(t => offset == text.Abs(t!.Value.End));
            if (hit is not { } token) return null;
            var compound = Compounds(text.Text).FirstOrDefault(c => c.Tokens.Contains(token));
            var markdown = TokenHover(analyzer, el, text, token, compound);
            return markdown == null ? null : new HoverInfo(text.Abs(token.Start), text.Abs(token.End), markdown);
        }
        return null;
    }

    private static string? TokenHover(XamlAnalyzer analyzer, XmlElement el, MappedText text, SelectorToken token, Compound? compound)
    {
        switch (token.Kind)
        {
            case SelectorTokenKind.Type:
                if (ResolveType(analyzer, el, token.Text) is not { } type) return null;
                var summary = Features.Summary(type);
                return $"```csharp\n{Features.Describe(type)}\n```" + (summary != null ? "\n\n" + summary : "");

            case SelectorTokenKind.Class when token.Text.Length > 1:
                return ClassHover(analyzer, token.Text[1..]);

            case SelectorTokenKind.PseudoClass when token.Text.Length > 1:
                return PseudoHover(analyzer, el, token.Text, compound);

            case SelectorTokenKind.Template:
                return "`/template/` matches elements inside the template of the control before it.";

            case SelectorTokenKind.Name when token.Text.Length > 1:
                var name = token.Text[1..];
                if (compound is { InTemplate: true })
                {
                    var owner = compound.TemplateOwnerText != null ? ResolveType(analyzer, el, compound.TemplateOwnerText) : StyledType(analyzer, el);
                    if (owner != null && TemplateParts(analyzer, el, owner, analyzer.Styles()).FirstOrDefault(x => x.Part.Name == name) is { Part: { } part } found)
                    {
                        var partType = found.PartType?.Name ?? part.TypeName.Split('.', ':').Last();
                        return $"Named part `{name}` ({partType}) in the template of `{found.Themed}`.";
                    }
                    return $"Named part `{name}` in the template of the control before `/template/`.";
                }
                return analyzer.Result.Names.TryGetValue(name, out var named)
                    ? $"Element `<{named.Name}>` named `{name}` in this document" + (analyzer.Result.Elements[named].Type is { } t ? $" (type `{t.ToDisplayString()}`)." : ".")
                    : $"No element named `{name}` in this document.";

            case SelectorTokenKind.Nesting:
                for (var e = el.Parent; e != null; e = e.Parent)
                {
                    if (SelectorAttr(e) is { } outer) return $"`^` is the selector of the enclosing style: `{outer.Value.Trim()}`.";
                }
                return "`^` is the selector of the enclosing style (this style is not nested in one).";
        }
        return null;
    }

    private static string ClassHover(XamlAnalyzer analyzer, string cls)
    {
        var origin = new XamlFile(analyzer.DocumentPath ?? "", analyzer.Result.Doc.Text, analyzer, new LineIndex(analyzer.Result.Doc.Text));
        var files = analyzer.ProjectFiles?.Invoke() ?? [origin];
        var uses = Targets.OccurrencesAsync(new StyleClassTarget(cls), origin, files, null).GetAwaiter().GetResult().Where(o => !o.IsSelector).ToList();
        if (uses.Count == 0) return $"Style class `{cls}`: no element in the project sets it.";
        var lines = uses.Take(5).Select(o =>
        {
            var text = files.FirstOrDefault(f => f.Path == o.Path)?.Text ?? File.ReadAllText(o.Path);
            var line = 1 + text.AsSpan(0, Math.Min(o.Start, text.Length)).Count('\n');
            return $"- `{Path.GetFileName(o.Path)}` line {line}";
        });
        return $"Style class `{cls}`: set on {uses.Count} element{(uses.Count == 1 ? "" : "s")}\n\n{string.Join("\n", lines)}" + (uses.Count > 5 ? $"\n- … and {uses.Count - 5} more" : "");
    }

    private static string? PseudoHover(XamlAnalyzer analyzer, XmlElement el, string pseudo, Compound? compound)
    {
        var type = compound?.Type is { } tt ? ResolveType(analyzer, el, tt.Text) : compound is { InTemplate: false } ? StyledType(analyzer, el) as Microsoft.CodeAnalysis.INamedTypeSymbol : null;
        string? origin = null;
        if (type != null)
        {
            if (PseudoClasses(type).FirstOrDefault(x => x.Name == pseudo) is { Name: not null } declared) origin = $"Declared by `{declared.DeclaringType}`.";
            else if (ThemePseudoClasses(analyzer, type).FirstOrDefault(x => x.Pseudo == pseudo) is { Pseudo: not null } themed) origin = $"Used by the theme of `{themed.Themed}`.";
            else if (analyzer.Styles().CodePseudoClasses.Contains(pseudo)) origin = "Set by code in this project.";
        }
        PseudoMeanings.TryGetValue(pseudo, out var meaning);
        if (meaning == null && origin == null) return null;
        return $"Pseudo-class `{pseudo}`" + (meaning != null ? $": {meaning}." : "") + (origin != null ? $"\n\n{origin}" : "");
    }
}
