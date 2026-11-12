using AxamlLs.Workspace;
using AxamlLs.Xaml;
using Microsoft.CodeAnalysis;

namespace AxamlLs.Analysis;

public enum SelectorTokenKind
{
    Type,        // Button, local|MyControl
    Class,       // .accent
    Name,        // #PART_ContentPresenter
    PseudoClass, // :pointerover, :not(
    Template,    // /template/
    Nesting,     // ^
    Property,    // [IsEnabled=True]
}

/// <summary>A piece of a style selector, with offsets into the selector text.</summary>
public readonly record struct SelectorToken(SelectorTokenKind Kind, int Start, int End, string Text);

/// <summary>
/// Avalonia style selectors (Style Selector="…"): tokenizing, completion, and the class usage check.
/// Syntax: Type, ns|Type, .class, #name, :pseudo, :not(…), :is(…), :nth-child(…), [Prop=value], ^, " ", "&gt;", ",", /template/.
/// </summary>
public static partial class Selectors
{
    private static bool IsIdent(char c) => char.IsLetterOrDigit(c) || c is '_' or '-';

    public static List<SelectorToken> Tokenize(string s)
    {
        var tokens = new List<SelectorToken>();
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            var start = i;
            switch (c)
            {
                case '.' or '#' or ':':
                    i++;
                    while (i < s.Length && IsIdent(s[i])) i++;
                    var kind = c switch { '.' => SelectorTokenKind.Class, '#' => SelectorTokenKind.Name, _ => SelectorTokenKind.PseudoClass };
                    tokens.Add(new(kind, start, i, s[start..i]));
                    // :nth-child(2n+1): the argument is not a selector.
                    if (c == ':' && i < s.Length && s[i] == '(' && s[start..i] is ":nth-child" or ":nth-last-child")
                    {
                        while (i < s.Length && s[i] != ')') i++;
                    }
                    break;
                case '/':
                    i++;
                    while (i < s.Length && s[i] != '/' && !char.IsWhiteSpace(s[i])) i++;
                    if (i < s.Length && s[i] == '/') i++;
                    if (s[start..i] == "/template/") tokens.Add(new(SelectorTokenKind.Template, start, i, "/template/"));
                    break;
                case '^':
                    tokens.Add(new(SelectorTokenKind.Nesting, i, i + 1, "^"));
                    i++;
                    break;
                case '[':
                    while (i < s.Length && s[i] != ']') i++;
                    if (i < s.Length) i++;
                    tokens.Add(new(SelectorTokenKind.Property, start, i, s[start..i]));
                    break;
                default:
                    if (char.IsLetter(c) || c == '_')
                    {
                        while (i < s.Length && (IsIdent(s[i]) || s[i] == '|')) i++;
                        tokens.Add(new(SelectorTokenKind.Type, start, i, s[start..i]));
                    }
                    else
                    {
                        i++; // whitespace, '>', ',', '(', ')'
                    }
                    break;
            }
        }
        return tokens;
    }

    /// <summary>The selector attribute of a Style element, if any.</summary>
    public static XmlAttr? SelectorAttr(XmlElement el) =>
        el.LocalName == "Style" && el.Attr("Selector") is { HasValue: true } a ? a : null;

    /// <summary>Resolves "Button" or "local|MyControl" as a type.</summary>
    public static INamedTypeSymbol? ResolveType(XamlAnalyzer analyzer, XmlElement el, string text)
    {
        var name = text.Replace('|', ':');
        return name.Length == 0 ? null : analyzer.ResolveTypeName(el, new MappedText(name, [.. Enumerable.Range(0, name.Length + 1)]), report: false);
    }

    // ---------------------------------------------------------------- completion

    public static void Complete(XamlAnalyzer analyzer, XmlElement el, XmlAttr attr, int offset, List<Features.Completion> list, ProjectStyles styles)
    {
        var text = MappedText.FromAttribute(attr);
        var s = text.Text;
        var idx = text.IndexOf(offset);
        var ws = idx;
        while (ws > 0 && IsIdent(s[ws - 1])) ws--;
        var we = idx;
        while (we < s.Length && IsIdent(s[we])) we++;
        var before = ws > 0 ? s[ws - 1] : ' ';
        int A(int i) => text.Abs(i);

        // The compound (Button.accent:pointerover) the cursor is in, up to the cursor.
        var compoundStart = ws;
        while (compoundStart > 0 && !(char.IsWhiteSpace(s[compoundStart - 1]) || s[compoundStart - 1] is '>' or ',' or '(' or ')' || s[compoundStart - 1] == '/')) compoundStart--;
        var compound = Tokenize(s[compoundStart..Math.Max(compoundStart, ws - 1)]);
        INamedTypeSymbol? CompoundType()
        {
            var type = compound.FirstOrDefault(t => t.Kind == SelectorTokenKind.Type);
            if (type.Text != null) return ResolveType(analyzer, el, type.Text);
            // "^:pressed" or ":pointerover" alone: the styled type of the enclosing style, else Control.
            return StyledType(analyzer, el) as INamedTypeSymbol ?? analyzer.Project?.ResolveType(XamlNamespaces.Avalonia, "Control");
        }

        switch (before)
        {
            case '.':
                var classOwner = compound.FirstOrDefault(t => t.Kind == SelectorTokenKind.Type).Text is { } ct ? ResolveType(analyzer, el, ct) : null;
                CompleteClassNames(analyzer, el, classOwner, styles, A(ws), A(we), list, c => c);
                return;
            case ':':
                if (CompoundType() is { } owner)
                {
                    var seenPseudo = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var (pseudo, declaringType) in PseudoClasses(owner))
                    {
                        var name = pseudo.TrimStart(':');
                        if (seenPseudo.Add(name)) list.Add(new(name, Features.ItemKind.Keyword, $"pseudo-class of {declaringType}", null, name, false, A(ws), A(we), "0" + name));
                    }
                    // Pseudo-classes the control's theme styles but the class does not declare (:error, :left …).
                    foreach (var (pseudo, themed) in ThemePseudoClasses(analyzer, owner))
                    {
                        var name = pseudo.TrimStart(':');
                        if (seenPseudo.Add(name)) list.Add(new(name, Features.ItemKind.Keyword, $"pseudo-class used by {themed}'s theme", null, name, false, A(ws), A(we), "0" + name));
                    }
                }
                foreach (var (name, detail) in new[] { ("not", "matches when the selector inside does not"), ("is", "matches the type or a derived type"), ("nth-child", "position among siblings: 2n+1, odd, even"), ("nth-last-child", "position from the end") })
                    list.Add(new(name + "(…)", Features.ItemKind.Function, detail, null, name + "($1)$0", true, A(ws), A(we), "1" + name));
                return;
            case '#':
                CompleteNames(analyzer, el, s, compoundStart, ws, we, text, list, styles);
                return;
            case '/':
                // "/tem|" → "/template/"
                var end = we < s.Length && s[we] == '/' ? we + 1 : we;
                list.Add(new("/template/", Features.ItemKind.Keyword, "match inside the control's template", null, "/template/ ", false, A(ws - 1), A(end)));
                return;
        }

        // A type (start, after a space, '>', ',' or '(').
        if (char.IsWhiteSpace(before) && s[..ws].Trim().Length > 0 && !s[..ws].TrimEnd().EndsWith(',') && !s[..ws].TrimEnd().EndsWith('>'))
            list.Add(new("/template/", Features.ItemKind.Keyword, "match inside the template of the control before", null, "/template/ ", false, A(ws), A(we), "2"));
        CompleteTypes(analyzer, el, A(ws), A(we), list, pipe: true);
    }

    /// <summary>Control types for a selector (prefix|Type) or $parent[prefix:Type].</summary>
    public static void CompleteTypes(XamlAnalyzer analyzer, XmlElement el, int start, int end, List<Features.Completion> list, bool pipe)
    {
        var p = analyzer.Project;
        if (p == null) return;
        foreach (var (prefix, ns) in Features.DeclaredNamespaces(el))
        {
            if (ns is XamlNamespaces.X or XamlNamespaces.Blend or XamlNamespaces.MarkupCompatibility) continue;
            var own = ns.StartsWith("using:", StringComparison.Ordinal) || ns.StartsWith("clr-namespace:", StringComparison.Ordinal);
            foreach (var type in p.TypesIn(ns))
            {
                if (type.IsStatic || type.IsGenericType || type.TypeKind != TypeKind.Class || !Members.InheritsByName(type, "StyledElement")) continue;
                var label = prefix.Length == 0 ? type.Name : $"{prefix}{(pipe ? '|' : ':')}{type.Name}";
                list.Add(new(label, Features.ItemKind.Class, type.ToDisplayString(), null, label, false, start, end, (own ? "3" : "4") + label));
            }
        }
    }

    private static void CompleteNames(XamlAnalyzer analyzer, XmlElement el, string s, int compoundStart, int ws, int we, MappedText text,
        List<Features.Completion> list, ProjectStyles styles)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // After /template/: the named parts of the template of the control before it, of the type written before '#'.
        var templateAt = s.LastIndexOf("/template/", compoundStart, StringComparison.Ordinal);
        if (templateAt >= 0)
        {
            var ownerType = Tokenize(s[..templateAt]).LastOrDefault(t => t.Kind == SelectorTokenKind.Type);
            var owner = ownerType.Text != null ? ResolveType(analyzer, el, ownerType.Text) : StyledType(analyzer, el);
            var partFilterText = Tokenize(s[compoundStart..Math.Max(compoundStart, ws - 1)]).FirstOrDefault(t => t.Kind == SelectorTokenKind.Type).Text;
            var partFilter = partFilterText != null ? ResolveType(analyzer, el, partFilterText) : null;
            if (owner != null)
            {
                foreach (var (part, partType, themed) in TemplateParts(analyzer, el, owner, styles))
                {
                    if (partFilter != null && (partType == null || !Members.IsSameOrDerived(partType, partFilter))) continue;
                    if (!seen.Add(part.Name)) continue;
                    var typeName = partType?.Name ?? part.TypeName.Split('.', ':').Last();
                    list.Add(new(part.Name, Features.ItemKind.Field, $"{typeName} in {themed}'s template", null, part.Name, false, text.Abs(ws), text.Abs(we), "0" + part.Name));
                }
                // Parts the control declares with [TemplatePart] (its contract, even when a theme does not use them).
                foreach (var t in Members.SelfAndBases(owner))
                {
                    foreach (var a in t.GetAttributes().Where(a => a.AttributeClass?.Name == "TemplatePartAttribute"))
                    {
                        if (a.ConstructorArguments.FirstOrDefault().Value is not string name) continue;
                        var partType = a.ConstructorArguments.Length > 1 ? a.ConstructorArguments[1].Value as ITypeSymbol : null;
                        if (partFilter != null && (partType == null || !Members.IsSameOrDerived(partType, partFilter))) continue;
                        if (!seen.Add(name)) continue;
                        list.Add(new(name, Features.ItemKind.Field, $"{partType?.Name ?? "part"} declared by {t.Name}", null, name, false, text.Abs(ws), text.Abs(we), "1" + name));
                    }
                }
            }
            return;
        }
        foreach (var (name, element) in analyzer.Result.Names)
        {
            if (seen.Add(name)) list.Add(new(name, Features.ItemKind.Variable, "<" + element.Name + ">", null, name, false, text.Abs(ws), text.Abs(we), "2" + name));
        }
    }

    /// <summary>
    /// The named parts of a control's template, from the first type in its hierarchy that has a theme
    /// (compiled themes such as Fluent, then the project's own ControlThemes), with the part's resolved type.
    /// </summary>
    public static List<(TemplatePart Part, ITypeSymbol? PartType, string Themed)> TemplateParts(XamlAnalyzer analyzer, XmlElement el, ITypeSymbol owner, ProjectStyles styles)
    {
        var result = new List<(TemplatePart, ITypeSymbol?, string)>();
        if (analyzer.Project is not { } p) return result;
        foreach (var t in Members.SelfAndBases(owner))
        {
            if (p.LibraryThemes.TryGetValue(MetadataName(t), out var facts))
            {
                foreach (var part in facts.Parts.OrderBy(x => x.Name, StringComparer.Ordinal))
                    result.Add((part, p.Compilation.GetTypeByMetadataName(part.TypeName), t.Name));
            }
            foreach (var part in styles.TemplateParts)
            {
                if (!SymbolEqualityComparer.Default.Equals(ResolveType(analyzer, el, part.TargetText.Replace(':', '|')), t)) continue;
                result.Add((new TemplatePart(part.Name, part.TypeText), ResolveType(analyzer, el, part.TypeText.Replace(':', '|')), t.Name));
            }
            if (result.Count > 0) break;
        }
        return result;
    }

    private static IEnumerable<(string Pseudo, string Themed)> ThemePseudoClasses(XamlAnalyzer analyzer, ITypeSymbol owner)
    {
        if (analyzer.Project is not { } p) yield break;
        foreach (var t in Members.SelfAndBases(owner))
        {
            if (!p.LibraryThemes.TryGetValue(MetadataName(t), out var facts)) continue;
            foreach (var pseudo in facts.PseudoClasses.Order(StringComparer.Ordinal)) yield return (pseudo, t.Name);
        }
    }

    /// <summary>
    /// Style class names, best first: classes styled for this element's type (by the project's selectors or its theme,
    /// e.g. Fluent's Button.accent), then classes styled without a type, then the other classes the project uses.
    /// </summary>
    public static void CompleteClassNames(XamlAnalyzer analyzer, XmlElement el, ITypeSymbol? elementType, ProjectStyles styles, int start, int end,
        List<Features.Completion> list, Func<string, string> insert, Func<string, string>? label = null, bool snippet = false, ISet<string>? exclude = null)
    {
        var best = new Dictionary<string, (string Rank, string Detail)>(StringComparer.Ordinal);
        void Offer(string cls, string rank, string detail)
        {
            if (exclude?.Contains(cls) == true) return;
            if (!best.TryGetValue(cls, out var current) || string.CompareOrdinal(rank, current.Rank) < 0) best[cls] = (rank, detail);
        }
        var bases = elementType != null ? Members.SelfAndBases(elementType).ToList() : [];
        foreach (var styled in styles.StyledClasses)
        {
            if (styled.TypeText == null)
            {
                Offer(styled.Class, "1", "styled for any control");
                continue;
            }
            var type = ResolveType(analyzer, el, styled.TypeText);
            if (type != null && bases.Any(b => SymbolEqualityComparer.Default.Equals(b, type))) Offer(styled.Class, "0", $"styled for {type.Name}");
            else if (elementType == null) Offer(styled.Class, "1", $"styled for {styled.TypeText.Replace('|', ':')}");
            else Offer(styled.Class, "2", $"styled for {styled.TypeText.Replace('|', ':')}");
        }
        if (analyzer.Project is { } p)
        {
            foreach (var b in bases)
            {
                if (!p.LibraryThemes.TryGetValue(MetadataName(b), out var facts)) continue;
                foreach (var cls in facts.Classes) Offer(cls, "0", $"styled by {b.Name}'s theme");
            }
        }
        foreach (var cls in styles.UsedClasses) Offer(cls, "3", "set elsewhere in the project");
        foreach (var (cls, (rank, detail)) in best)
        {
            var text = label?.Invoke(cls) ?? cls;
            list.Add(new(text, Features.ItemKind.EnumMember, detail, null, insert(cls), snippet, start, end, rank + cls));
        }
    }

    /// <summary>Metadata name as used in compiled IL: Namespace.Outer+Inner.</summary>
    public static string MetadataName(ITypeSymbol type)
    {
        if (type.ContainingType != null) return MetadataName(type.ContainingType) + "+" + type.MetadataName;
        var ns = type.ContainingNamespace;
        return ns == null || ns.IsGlobalNamespace ? type.MetadataName : ns.ToDisplayString() + "." + type.MetadataName;
    }

    /// <summary>
    /// The control each comma-separated branch of a selector styles: the type of its last compound
    /// ("RadioButton.segment:checked" → RadioButton). Null when that compound names no type; Nested tells a
    /// "^…" branch, whose type is the enclosing style's. Types inside :is(…)/:not(…) are arguments, not targets.
    /// </summary>
    public static List<(string? TypeText, bool Nested)> TargetTypes(string selector)
    {
        var result = new List<(string?, bool)>();
        string? type = null;
        var nested = false;
        var depth = 0;
        var previousEnd = 0;
        void EndBranch() { result.Add((type, nested)); type = null; nested = false; }
        foreach (var t in Tokenize(selector))
        {
            foreach (var c in selector[previousEnd..t.Start])
            {
                if (c == ')') depth = Math.Max(0, depth - 1);
                else if (c == ',' && depth == 0) EndBranch();
                else if ((char.IsWhiteSpace(c) || c == '>') && depth == 0) { type = null; nested = false; }
            }
            previousEnd = t.End;
            if (depth == 0)
            {
                if (t.Kind == SelectorTokenKind.Template) { type = null; nested = false; }
                else if (t.Kind == SelectorTokenKind.Type) type = t.Text;
                else if (t.Kind == SelectorTokenKind.Nesting) nested = true;
            }
            if (t.Kind == SelectorTokenKind.PseudoClass && t.End < selector.Length && selector[t.End] == '(' && t.Text is not (":nth-child" or ":nth-last-child"))
            {
                depth++;
                previousEnd = t.End + 1;
            }
        }
        EndBranch();
        return result;
    }

    /// <summary>The classes in a selector, each with the type of its compound ("Button.a /template/ Border.b" → (Button, a), (Border, b)).</summary>
    public static IEnumerable<(string? TypeText, string Class)> ClassesWithTypes(string selector)
    {
        string? type = null;
        var previousEnd = 0;
        foreach (var t in Tokenize(selector))
        {
            var gap = selector[previousEnd..t.Start];
            if (gap.Any(c => char.IsWhiteSpace(c) || c is '>' or ',' or '(' or ')') || t.Kind is SelectorTokenKind.Template or SelectorTokenKind.Nesting) type = null;
            previousEnd = t.End;
            switch (t.Kind)
            {
                case SelectorTokenKind.Type:
                    type = t.Text;
                    break;
                case SelectorTokenKind.Class when t.Text.Length > 1:
                    yield return (type, t.Text[1..]);
                    break;
            }
        }
    }

    /// <summary>The pseudo-classes a control declares with [PseudoClasses(":pressed", …)], its own first.</summary>
    public static IEnumerable<(string Name, string DeclaringType)> PseudoClasses(ITypeSymbol type)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in Members.SelfAndBases(type))
        {
            foreach (var a in t.GetAttributes().Where(a => a.AttributeClass?.Name == "PseudoClassesAttribute"))
            {
                foreach (var arg in a.ConstructorArguments)
                {
                    var values = arg.Kind == TypedConstantKind.Array ? arg.Values.Select(v => v.Value) : [arg.Value];
                    foreach (var v in values.OfType<string>())
                    {
                        if (seen.Add(v)) yield return (v, t.Name);
                    }
                }
            }
        }
    }

    /// <summary>The type a Style styles, taken from the selector of the nearest enclosing Style or ControlTheme.</summary>
    public static ITypeSymbol? StyledType(XamlAnalyzer analyzer, XmlElement inner)
    {
        for (var e = inner.Parent; e != null; e = e.Parent)
        {
            if (e.LocalName == "ControlTheme" && e.Attr("TargetType") is { HasValue: true } tt)
                return analyzer.ResolveTypeName(e, MappedText.FromAttribute(tt), report: false);
            if (SelectorAttr(e) is not { } sel) continue;
            // "Button.Card:pointerover /template/ ContentPresenter#PART_ContentPresenter" → ContentPresenter; "^:pressed" → outer style.
            var last = sel.Value.Split(',').Last();
            var tokens = Tokenize(last);
            var lastCompoundStart = tokens.FindLastIndex(t => t.Kind is SelectorTokenKind.Type or SelectorTokenKind.Nesting or SelectorTokenKind.Template);
            if (lastCompoundStart < 0) return null;
            var token = tokens[lastCompoundStart];
            if (token.Kind == SelectorTokenKind.Nesting) continue;
            if (token.Kind == SelectorTokenKind.Template) return null;
            return ResolveType(analyzer, e, token.Text);
        }
        return null;
    }

    // ---------------------------------------------------------------- semantic tokens & class usage

    public sealed record ClassUse(int Start, int End, string Name);

    /// <summary>Every token of every selector in the document, in absolute offsets.</summary>
    public static IEnumerable<(SelectorToken Token, int Start, int End)> DocumentTokens(XmlDoc doc)
    {
        foreach (var el in doc.AllElements)
        {
            if (SelectorAttr(el) is not { } attr) continue;
            var text = MappedText.FromAttribute(attr);
            foreach (var t in Tokenize(text.Text)) yield return (t, text.Abs(t.Start), text.Abs(t.End));
        }
    }

    /// <summary>Hints (faded in the editor) for selector classes that no element in the project sets.</summary>
    public static IEnumerable<XamlDiagnostic> UnusedClasses(XmlDoc doc, IReadOnlySet<string> used)
    {
        foreach (var (t, start, end) in DocumentTokens(doc))
        {
            if (t.Kind != SelectorTokenKind.Class || t.Text.Length < 2) continue;
            var name = t.Text[1..];
            if (!used.Contains(name))
                yield return new XamlDiagnostic(start + 1, end, Severity.Hint, Codes.UnusedClass, $"No element in the project sets the class '{name}'.");
        }
    }
}
