using AxamlLs.Workspace;
using AxamlLs.Xaml;
using Microsoft.CodeAnalysis;

namespace AxamlLs.Analysis;

/// <summary>Checks of style selectors against the project's types, themes and code (AXN0402–AXN0406).</summary>
public static partial class Selectors
{
    /// <summary>A run of selector tokens with no separator between them: Button.accent:pointerover.</summary>
    private sealed class Compound
    {
        public List<SelectorToken> Tokens { get; } = [];
        public SelectorToken? Type => Tokens.Cast<SelectorToken?>().FirstOrDefault(t => t!.Value.Kind == SelectorTokenKind.Type);
        public bool Nesting => Tokens.Any(t => t.Kind == SelectorTokenKind.Nesting);
        /// <summary>Set when this compound is inside a template (after /template/ in its comma-separated branch).</summary>
        public bool InTemplate { get; set; }
        /// <summary>For compounds in a template: the type text of the control whose template it is (null = the styled type).</summary>
        public string? TemplateOwnerText { get; set; }
    }

    private static readonly HashSet<string> SelectorFunctions = [":not", ":is", ":nth-child", ":nth-last-child"];

    /// <summary>Pseudo-classes every control can have, set by Avalonia's input and styling base classes.</summary>
    private static readonly HashSet<string> UniversalPseudoClasses = [":disabled", ":focus", ":focus-visible", ":focus-within", ":pointerover", ":empty"];

    public static IEnumerable<XamlDiagnostic> Validate(XamlAnalyzer analyzer)
    {
        foreach (var el in analyzer.Result.Doc.AllElements)
        {
            if (SelectorAttr(el) is not { } attr) continue;
            var text = MappedText.FromAttribute(attr);
            foreach (var d in Syntax(text)) yield return d;
            if (analyzer.Project == null) continue;
            var styles = analyzer.Styles();
            foreach (var compound in Compounds(text.Text))
            {
                foreach (var d in CheckCompound(analyzer, el, text, compound, styles)) yield return d;
            }
        }
    }

    private static List<Compound> Compounds(string s)
    {
        var compounds = new List<Compound>();
        Compound? current = null;
        var previousEnd = 0;
        var inTemplate = false;
        string? ownerText = null;
        string? lastTypeText = null;
        foreach (var t in Tokenize(s))
        {
            var gap = s[previousEnd..t.Start];
            if (gap.Contains(','))
            {
                inTemplate = false;
                ownerText = null;
                lastTypeText = null;
            }
            if (current == null || gap.Any(c => char.IsWhiteSpace(c) || c is '>' or ',' or '(' or ')'))
            {
                current = new Compound { InTemplate = inTemplate, TemplateOwnerText = ownerText };
                compounds.Add(current);
            }
            previousEnd = t.End;
            current.Tokens.Add(t);
            if (t.Kind == SelectorTokenKind.Type) lastTypeText = t.Text;
            if (t.Kind == SelectorTokenKind.Template)
            {
                inTemplate = true;
                ownerText = lastTypeText;
            }
        }
        return compounds;
    }

    private static IEnumerable<XamlDiagnostic> CheckCompound(XamlAnalyzer analyzer, XmlElement el, MappedText text, Compound c, ProjectStyles styles)
    {
        var p = analyzer.Project!;
        INamedTypeSymbol? type = null;
        if (c.Type is { } typeToken)
        {
            type = ResolveType(analyzer, el, typeToken.Text);
            if (type == null)
            {
                var bar = typeToken.Text.IndexOf('|');
                var prefix = bar > 0 ? typeToken.Text[..bar] : "";
                var ns = el.LookupNamespace(prefix);
                if (ns == null && prefix.Length > 0)
                    yield return new XamlDiagnostic(text.Abs(typeToken.Start), text.Abs(typeToken.End), Severity.Error, Codes.UnknownSelectorType, $"Undeclared namespace prefix '{prefix}' in the selector.");
                else if (p.IsCheckable(ns))
                    yield return new XamlDiagnostic(text.Abs(typeToken.Start), text.Abs(typeToken.End), Severity.Error, Codes.UnknownSelectorType,
                        $"Unable to resolve type '{typeToken.Text[(bar + 1)..]}' from namespace '{ns}'.");
            }
        }
        else
        {
            // A compound without a type (^:pressed, .accent:pointerover) belongs to the enclosing style's type.
            type = c.InTemplate ? null : StyledType(analyzer, el) as INamedTypeSymbol;
        }

        foreach (var t in c.Tokens)
        {
            switch (t.Kind)
            {
                case SelectorTokenKind.PseudoClass when type != null && !SelectorFunctions.Contains(t.Text) && t.Text.Length > 1:
                    if (!PseudoClassKnown(analyzer, type, t.Text, styles))
                        yield return new XamlDiagnostic(text.Abs(t.Start + 1), text.Abs(t.End), Severity.Warning, Codes.UnknownPseudoClass,
                            $"The pseudo-class '{t.Text}' is not declared by '{type.Name}' or its theme, and no code in the project sets it.");
                    break;

                case SelectorTokenKind.Name when c.InTemplate && t.Text.Length > 1:
                    if (TemplatePartUnknown(analyzer, el, c, t.Text[1..], styles) is { } owner)
                        yield return new XamlDiagnostic(text.Abs(t.Start + 1), text.Abs(t.End), Severity.Warning, Codes.UnknownTemplatePart,
                            $"'{t.Text[1..]}' is not a named part in the template of '{owner.Name}'.");
                    break;

                case SelectorTokenKind.Property:
                    if (PropertyUnknown(analyzer, el, type, t, text) is { } d) yield return d;
                    break;
            }
        }
    }

    private static bool PseudoClassKnown(XamlAnalyzer analyzer, INamedTypeSymbol type, string pseudo, ProjectStyles styles)
    {
        // When part of the hierarchy did not resolve, the pseudo-class may be declared there.
        if (Members.HasUnknownBase(type) || UniversalPseudoClasses.Contains(pseudo) || styles.CodePseudoClasses.Contains(pseudo)) return true;
        if (PseudoClasses(type).Any(x => x.Name == pseudo)) return true;
        return ThemePseudoClasses(analyzer, type).Any(x => x.Pseudo == pseudo);
    }

    /// <summary>The control whose template lacks the part, or null when the part exists or there is nothing to check against.</summary>
    private static ITypeSymbol? TemplatePartUnknown(XamlAnalyzer analyzer, XmlElement el, Compound c, string name, ProjectStyles styles)
    {
        var owner = c.TemplateOwnerText != null ? ResolveType(analyzer, el, c.TemplateOwnerText) : StyledType(analyzer, el);
        if (owner == null || Members.HasUnknownBase(owner)) return null;
        var parts = TemplateParts(analyzer, el, owner, styles);
        if (parts.Count == 0) return null; // no known theme for this control: its template is unknown
        if (parts.Any(x => x.Part.Name == name)) return null;
        foreach (var t in Members.SelfAndBases(owner))
        {
            foreach (var a in t.GetAttributes().Where(a => a.AttributeClass?.Name == "TemplatePartAttribute"))
            {
                if (a.ConstructorArguments.FirstOrDefault().Value as string == name) return null;
            }
        }
        return owner;
    }

    /// <summary>[Prop=value] or [(Owner.Prop)=value]: the property name must exist on the compound's type.</summary>
    private static XamlDiagnostic? PropertyUnknown(XamlAnalyzer analyzer, XmlElement el, INamedTypeSymbol? type, SelectorToken token, MappedText text)
    {
        var inner = token.Text.Trim('[', ']');
        var end = inner.IndexOfAny(['=', '!', '<', '>', ' ']);
        var name = end < 0 ? inner : inner[..end];
        if (name.Length == 0) return null;
        var nameStart = token.Start + 1;
        ISymbol? found;
        string where;
        if (name.StartsWith('(') && name.EndsWith(')'))
        {
            var dotted = name[1..^1];
            var dot = dotted.LastIndexOf('.');
            if (dot <= 0) return null;
            var owner = ResolveType(analyzer, el, dotted[..dot]);
            if (owner == null) return null; // an unresolved owner is not this check's business
            found = Members.FindAttached(owner, dotted[(dot + 1)..]) ?? Members.FindSettable(owner, dotted[(dot + 1)..]);
            if (found != null || Members.HasUnknownBase(owner)) return null;
            nameStart += 1 + dot + 1;
            name = dotted[(dot + 1)..];
            where = owner.Name;
        }
        else
        {
            if (type == null || Members.HasUnknownBase(type)) return null;
            found = Members.FindSettable(type, name) ?? Members.FindAvaloniaPropertyField(type, name);
            if (found != null) return null;
            where = type.Name;
        }
        return new XamlDiagnostic(text.Abs(nameStart), text.Abs(nameStart + name.Length), Severity.Warning, Codes.UnknownSelectorProperty,
            $"Property '{name}' not found on type '{where}'.");
    }

    // ---------------------------------------------------------------- syntax

    private static IEnumerable<XamlDiagnostic> Syntax(MappedText text)
    {
        var s = text.Text;
        // Parentheses outside [..] sections.
        var open = new Stack<int>();
        var inBracket = false;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '[') inBracket = true;
            else if (c == ']') inBracket = false;
            else if (inBracket) continue;
            else if (c == '(') open.Push(i);
            else if (c == ')')
            {
                if (open.Count > 0) open.Pop();
                else yield return new XamlDiagnostic(text.Abs(i), text.Abs(i + 1), Severity.Error, Codes.SelectorSyntax, "Unmatched ')' in the selector.");
            }
        }
        foreach (var i in open)
            yield return new XamlDiagnostic(text.Abs(i), text.Abs(i + 1), Severity.Error, Codes.SelectorSyntax, "Unclosed '(' in the selector.");

        // An empty compound after '>' (at the end, before ',' or another '>'), and /template/ with nothing after it.
        var tokens = Tokenize(s);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] != '>') continue;
            var j = i + 1;
            while (j < s.Length && char.IsWhiteSpace(s[j])) j++;
            if (j >= s.Length || s[j] is '>' or ',' or ')')
                yield return new XamlDiagnostic(text.Abs(i), text.Abs(i + 1), Severity.Error, Codes.SelectorSyntax, "Expected a selector after '>'.");
        }
        if (tokens.Count > 0 && tokens[^1].Kind == SelectorTokenKind.Template)
            yield return new XamlDiagnostic(text.Abs(tokens[^1].Start), text.Abs(tokens[^1].End), Severity.Error, Codes.SelectorSyntax, "Expected a selector after '/template/'.");
        else
        {
            // "Button /template/ , Border" : /template/ right before a comma.
            foreach (var t in tokens.Where(t => t.Kind == SelectorTokenKind.Template))
            {
                var j = t.End;
                while (j < s.Length && char.IsWhiteSpace(s[j])) j++;
                if (j < s.Length && s[j] == ',')
                    yield return new XamlDiagnostic(text.Abs(t.Start), text.Abs(t.End), Severity.Error, Codes.SelectorSyntax, "Expected a selector after '/template/'.");
            }
        }
    }
}
