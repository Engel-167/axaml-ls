using AxamlLs.Lsp;
using AxamlLs.Workspace;
using AxamlLs.Xaml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AxamlLs.Analysis;

/// <summary>A code action: a title and the edits it makes (possibly in several files).</summary>
public sealed record QuickFix(string Title, List<FileEdit> Edits, bool Preferred = false);

/// <summary>
/// Quick fixes for diagnostics, one method per diagnostic code so each can be tested alone. Everything is recomputed from the
/// analyzer at the diagnostic's range: the client's copy of the message is never parsed.
/// </summary>
public static class QuickFixes
{
    public static IEnumerable<QuickFix> For(XamlAnalyzer analyzer, XamlDiagnostic d) => d.Code switch
    {
        Codes.UnknownProperty => UnknownProperty(analyzer, d),
        Codes.UnknownBindingMember => UnknownBindingMember(analyzer, d),
        Codes.UnknownResource => UnknownResource(analyzer, d),
        Codes.UnknownMarkupExtension => UnknownMarkupExtension(analyzer, d),
        Codes.UnknownType => UnknownType(analyzer, d),
        Codes.MissingDataType => MissingDataType(analyzer, d),
        Codes.UnknownHandler => UnknownHandler(analyzer, d),
        Codes.MissingAsset => MissingFile(analyzer, d),
        _ => [],
    };

    private static string PathOf(XamlAnalyzer a) => a.DocumentPath ?? "";
    private static string Slice(XamlAnalyzer a, XamlDiagnostic d) => a.Result.Doc.Text[d.Start..Math.Min(d.End, a.Result.Doc.Text.Length)];

    // ---------------------------------------------------------------- "Change to 'X'"

    /// <summary>The closest candidates (up to three) as "Change to" fixes replacing the diagnostic's range.</summary>
    private static IEnumerable<QuickFix> ChangeTo(XamlAnalyzer a, XamlDiagnostic d, IEnumerable<string> candidates, string? current = null)
    {
        var word = current ?? Slice(a, d);
        var first = true;
        foreach (var c in Spelling.Closest(word, candidates, 3))
        {
            yield return new QuickFix($"Change to '{c}'", [new FileEdit(PathOf(a), d.Start, d.End, c)], first);
            first = false;
        }
    }

    public static IEnumerable<QuickFix> UnknownProperty(XamlAnalyzer a, XamlDiagnostic d)
    {
        var doc = a.Result.Doc;
        var el = doc.ElementAt(d.Start);
        if (el == null || !a.Result.Elements.TryGetValue(el, out var info)) return [];
        var names = new HashSet<string>(StringComparer.Ordinal);
        void AddAll(ITypeSymbol? type)
        {
            if (type == null) return;
            names.UnionWith(Features.SettableProperties(type).Select(p => p.Name));
            names.UnionWith(Features.AvaloniaProperties(type).Select(p => p.Name));
            names.UnionWith(Members.SelfAndBases(type).SelectMany(t => t.GetMembers()).OfType<IEventSymbol>()
                .Where(e => e.DeclaredAccessibility == Accessibility.Public && !e.IsStatic).Select(e => e.Name));
        }
        var afterDot = d.Start > 0 && doc.Text[d.Start - 1] == '.';
        if (!afterDot)
        {
            AddAll(info.Type);
        }
        else if (d.Start >= el.NameStart && d.Start <= el.NameEnd)
        {
            AddAll(info.OwnerType); // <Grid.Foo>
        }
        else if (el.Attributes.FirstOrDefault(x => x.NameStart <= d.Start && d.Start <= x.NameEnd) is { } attr)
        {
            var dot = attr.Name.LastIndexOf('.');
            var owner = dot > 0 ? a.ResolveTypeName(el, new MappedText(attr.Name[..dot], [.. Enumerable.Range(attr.NameStart, dot + 1)]), report: false) : null;
            AddAll(owner);
        }
        return ChangeTo(a, d, names);
    }

    public static IEnumerable<QuickFix> UnknownBindingMember(XamlAnalyzer a, XamlDiagnostic d)
    {
        if (d.Data is not ITypeSymbol type) return [];
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in Members.Hierarchy(Members.UnwrapNullable(type)))
        {
            if (t.SpecialType == SpecialType.System_Object) continue;
            foreach (var m in t.GetMembers())
            {
                if (m.DeclaredAccessibility != Accessibility.Public || m.IsImplicitlyDeclared || m.IsStatic) continue;
                if (m is IPropertySymbol { IsIndexer: false } || m is IMethodSymbol { MethodKind: MethodKind.Ordinary } method && !method.Name.StartsWith("get_", StringComparison.Ordinal))
                    names.Add(m.Name);
            }
        }
        return ChangeTo(a, d, names);
    }

    public static IEnumerable<QuickFix> UnknownResource(XamlAnalyzer a, XamlDiagnostic d) =>
        ChangeTo(a, d, ResourceCheck.KnownKeys(a));

    public static IEnumerable<QuickFix> UnknownMarkupExtension(XamlAnalyzer a, XamlDiagnostic d)
    {
        var el = a.Result.Doc.ElementAt(d.Start);
        var names = Features.MarkupExtensions.AsEnumerable();
        if (el != null) names = names.Concat(Features.CustomMarkupExtensions(a, el).Select(x => x.Label));
        return ChangeTo(a, d, names);
    }

    // ---------------------------------------------------------------- unknown type: import its namespace

    public static IEnumerable<QuickFix> UnknownType(XamlAnalyzer a, XamlDiagnostic d)
    {
        if (a.Project is not { } p || a.Result.Doc.Root is not { } root) return [];
        var doc = a.Result.Doc;
        var word = Slice(a, d);
        if (word.Length == 0 || word.Contains(':')) return []; // an undeclared prefix is a different problem
        var el = doc.ElementAt(d.Start);
        var inElementName = el != null && d.Start >= el.NameStart && d.Start <= el.NameEnd;
        var fixes = new List<QuickFix>();

        foreach (var type in p.TypesByName[word].Where(t => !t.IsStatic || t.TypeKind != TypeKind.Class).Take(5))
        {
            var clr = type.ContainingNamespace.ToDisplayString();
            if (clr.Length == 0) continue;
            var edits = new List<FileEdit>();
            string title;
            var existing = Features.DeclaredNamespaces(root).FirstOrDefault(n => n.Prefix.Length > 0 && p.ClrNamespacesFor(n.Ns).Contains(clr));
            string prefix;
            if (existing.Prefix != null)
            {
                prefix = existing.Prefix;
                title = $"Change to '{prefix}:{word}'";
            }
            else if (Features.DeclaredNamespaces(root).Any(n => n.Prefix.Length == 0 && p.ClrNamespacesFor(n.Ns).Contains(clr)))
            {
                continue; // visible without a prefix already: the type is not what is missing
            }
            else
            {
                prefix = NewPrefix(root, clr);
                title = $"Import '{clr}' as xmlns:{prefix}";
                edits.Add(RootInsertion(PathOf(a), doc, root, afterXmlns: true, $"xmlns:{prefix}=\"using:{clr}\""));
            }
            edits.Add(new FileEdit(PathOf(a), d.Start, d.Start, prefix + ":"));
            if (inElementName && el!.EndTagStart >= 0) edits.Add(new FileEdit(PathOf(a), el.EndTagStart + 2, el.EndTagStart + 2, prefix + ":"));
            fixes.Add(new QuickFix(title, MergeInsertions(edits), fixes.Count == 0));
        }

        // A typo in a known vocabulary: <TextBlok> → <TextBlock>.
        if (d.Data is string ns)
        {
            var names = p.TypesIn(ns).Where(t => !t.IsAbstract || !inElementName).Select(t => t.Name);
            foreach (var fix in ChangeTo(a, d, names, word)) fixes.Add(fix with { Preferred = fixes.Count == 0 && fix.Preferred });
        }
        return fixes;
    }

    private static string NewPrefix(XmlElement root, string clrNamespace)
    {
        var last = clrNamespace[(clrNamespace.LastIndexOf('.') + 1)..].ToLowerInvariant();
        var clean = new string(last.Where(char.IsLetterOrDigit).ToArray());
        if (clean.Length == 0 || char.IsDigit(clean[0])) clean = "ns" + clean;
        var taken = Features.DeclaredNamespaces(root).Select(n => n.Prefix).ToHashSet(StringComparer.Ordinal);
        var prefix = clean;
        for (var i = 2; taken.Contains(prefix); i++) prefix = clean + i;
        return prefix;
    }

    // ---------------------------------------------------------------- x:DataType

    public static IEnumerable<QuickFix> MissingDataType(XamlAnalyzer a, XamlDiagnostic d)
    {
        if (a.Project is not { } p || a.Result.Doc.Root is not { } root) return [];
        var doc = a.Result.Doc;
        var xPrefix = Features.DeclaredNamespaces(root).FirstOrDefault(n => n.Ns == XamlNamespaces.X).Prefix;
        if (string.IsNullOrEmpty(xPrefix)) return [];

        var viewName = a.Result.ClassType?.Name ?? Path.GetFileNameWithoutExtension(a.DocumentPath ?? "").Split('.')[0];
        var expected = (viewName.EndsWith("View", StringComparison.Ordinal) ? viewName[..^4] : viewName) + "ViewModel";
        var own = p.TypesByName.SelectMany(g => g)
            .Where(t => SymbolEqualityComparer.Default.Equals(t.ContainingAssembly, p.Compilation.Assembly) && t.TypeKind == TypeKind.Class && !t.IsStatic)
            .ToList();
        var candidates = own.Where(t => t.Name == expected)
            .Concat(own.Where(t => t.Name != expected && t.Name.EndsWith("ViewModel", StringComparison.Ordinal) && !t.IsAbstract).OrderBy(t => t.Name, StringComparer.Ordinal))
            .Take(5).ToList();

        var fixes = new List<QuickFix>();
        foreach (var type in candidates)
        {
            var clr = type.ContainingNamespace.ToDisplayString();
            var edits = new List<FileEdit>();
            var existing = Features.DeclaredNamespaces(root).FirstOrDefault(n => n.Prefix.Length > 0 && p.ClrNamespacesFor(n.Ns).Contains(clr));
            string typeText;
            if (existing.Prefix != null) typeText = $"{existing.Prefix}:{type.Name}";
            else if (clr.Length == 0 || Features.DeclaredNamespaces(root).Any(n => n.Prefix.Length == 0 && p.ClrNamespacesFor(n.Ns).Contains(clr))) typeText = type.Name;
            else
            {
                var prefix = NewPrefix(root, clr);
                edits.Add(RootInsertion(PathOf(a), doc, root, afterXmlns: true, $"xmlns:{prefix}=\"using:{clr}\""));
                typeText = $"{prefix}:{type.Name}";
            }
            edits.Add(RootInsertion(PathOf(a), doc, root, afterXmlns: false, $"{xPrefix}:DataType=\"{typeText}\""));
            fixes.Add(new QuickFix($"Add x:DataType=\"{typeText}\"", MergeInsertions(edits), fixes.Count == 0));
        }
        return fixes;
    }

    /// <summary>Inserts an attribute into the root start tag: after the last xmlns attribute, or after the last attribute.</summary>
    private static FileEdit RootInsertion(string path, XmlDoc doc, XmlElement root, bool afterXmlns, string attribute)
    {
        var anchor = (afterXmlns ? root.Attributes.LastOrDefault(x => x.Name.StartsWith("xmlns", StringComparison.Ordinal)) : null) ?? root.Attributes.LastOrDefault();
        if (anchor == null) return new FileEdit(path, root.NameEnd, root.NameEnd, " " + attribute);
        var at = anchor.HasValue ? (anchor.ValueTerminated ? anchor.ValueEnd + 1 : anchor.ValueEnd) : anchor.NameEnd;
        var lineStart = doc.Text.LastIndexOf('\n', Math.Max(anchor.NameStart - 1, 0)) + 1;
        var lead = doc.Text[lineStart..anchor.NameStart];
        var separator = lineStart > 0 && lead.All(char.IsWhiteSpace) ? "\n" + lead : " ";
        return new FileEdit(path, at, at, separator + attribute);
    }

    /// <summary>Insertions at the same offset become one edit (in order), so they cannot be taken for overlapping edits.</summary>
    private static List<FileEdit> MergeInsertions(List<FileEdit> edits)
    {
        var merged = new List<FileEdit>();
        foreach (var e in edits)
        {
            var i = merged.FindIndex(m => m.Start == e.Start && m.End == e.Start && e.End == e.Start && m.Path == e.Path);
            if (i >= 0) merged[i] = merged[i] with { NewText = merged[i].NewText + e.NewText };
            else merged.Add(e);
        }
        return merged;
    }

    // ---------------------------------------------------------------- event handlers

    public static IEnumerable<QuickFix> UnknownHandler(XamlAnalyzer a, XamlDiagnostic d)
    {
        if (a.Project is not { } p || a.Result.ClassType is not { } cls) return [];
        var el = a.Result.Doc.ElementAt(d.Start);
        var attr = el?.Attributes.FirstOrDefault(x => x.HasValue && x.ValueStart <= d.Start && d.End <= x.ValueEnd);
        if (attr == null) return [];
        var name = Slice(a, d);
        if (name.Length == 0 || !Microsoft.CodeAnalysis.CSharp.SyntaxFacts.IsValidIdentifier(name)) return [];

        // The delegate's parameters come from the event the attribute sets.
        var eventSymbol = a.Result.Refs.FirstOrDefault(r => r.Start == attr.NameStart && r.Symbol != null)?.Symbol;
        var parameters = HandlerParameters(eventSymbol);
        if (parameters == null) return [];

        // The code-behind part of the class: the one in the .axaml.cs file, else any real source file.
        var declarations = cls.DeclaringSyntaxReferences
            .Where(r => r.GetSyntax() is ClassDeclarationSyntax && r.SyntaxTree.FilePath is { Length: > 0 } f && File.Exists(f) && !ProjectFiles.IsBuildOutput(f)).ToList();
        var declaration = declarations.FirstOrDefault(r => r.SyntaxTree.FilePath.EndsWith(".axaml.cs", StringComparison.OrdinalIgnoreCase)) ?? declarations.FirstOrDefault();
        if (declaration?.GetSyntax() is not ClassDeclarationSyntax syntax) return [];
        var tree = declaration.SyntaxTree;
        var path = tree.FilePath;
        var sourceText = tree.GetText();
        if (File.ReadAllText(path).Length != sourceText.Length) return []; // changed on disk since Roslyn read it

        var classLine = sourceText.Lines.GetLineFromPosition(syntax.SpanStart);
        var indent = new string(sourceText.ToString(classLine.Span).TakeWhile(char.IsWhiteSpace).ToArray()) + "    ";
        var signature = string.Join(", ", parameters.Select(x => x.Text));
        var method = $"\n{indent}private void {name}({signature})\n{indent}{{\n{indent}}}\n";
        var edits = new List<FileEdit> { new(path, syntax.CloseBraceToken.SpanStart, syntax.CloseBraceToken.SpanStart, method) };

        // A using for parameter types that are not in scope there.
        var model = p.Compilation.GetSemanticModel(tree, ignoreAccessibility: true);
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var t in parameters.SelectMany(x => x.Types).Where(t => t.SpecialType == SpecialType.None && !t.ContainingNamespace.IsGlobalNamespace))
        {
            var inScope = model.LookupNamespacesAndTypes(syntax.SpanStart, name: t.Name).OfType<INamedTypeSymbol>().Any(x => SymbolEqualityComparer.Default.Equals(x.OriginalDefinition, t.OriginalDefinition));
            if (!inScope) missing.Add(t.ContainingNamespace.ToDisplayString());
        }
        if (missing.Count > 0 && tree.GetRoot() is CompilationUnitSyntax unit)
        {
            var text = string.Concat(missing.Select(ns => $"using {ns};\n"));
            if (unit.Usings.Count > 0)
            {
                var end = unit.Usings[^1].Span.End;
                edits.Add(new FileEdit(path, end, end, "\n" + text.TrimEnd('\n')));
            }
            else
            {
                edits.Add(new FileEdit(path, 0, 0, text + "\n"));
            }
        }
        return [new QuickFix($"Create handler '{name}' in {Path.GetFileName(path)}", edits, true)];
    }

    private sealed record HandlerParameter(string Text, IReadOnlyList<INamedTypeSymbol> Types);

    private static readonly SymbolDisplayFormat ParameterFormat = SymbolDisplayFormat.MinimallyQualifiedFormat
        .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    /// <summary>(object? sender, RoutedEventArgs e) for a CLR event or a routed event field; null when the event's shape is unknown.</summary>
    private static List<HandlerParameter>? HandlerParameters(ISymbol? eventSymbol)
    {
        List<HandlerParameter> From(IMethodSymbol invoke) =>
            [.. invoke.Parameters.Select(x => new HandlerParameter($"{x.Type.ToDisplayString(ParameterFormat)} {x.Name}", Named(x.Type)))];

        if (eventSymbol is IEventSymbol { Type: INamedTypeSymbol { DelegateInvokeMethod: { } invoke } }) return From(invoke);
        if (eventSymbol is IFieldSymbol field && Members.IsRoutedEvent(field.Type))
        {
            foreach (var t in Members.SelfAndBases(field.Type))
            {
                if (t is INamedTypeSymbol { Name: "RoutedEvent", IsGenericType: true } g)
                {
                    var args = g.TypeArguments[0];
                    return
                    [
                        new HandlerParameter("object? sender", []),
                        new HandlerParameter($"{args.ToDisplayString(ParameterFormat)} e", Named(args)),
                    ];
                }
            }
        }
        return null;
    }

    private static IReadOnlyList<INamedTypeSymbol> Named(ITypeSymbol type) =>
        type is INamedTypeSymbol n ? [n, .. n.TypeArguments.OfType<INamedTypeSymbol>()] : [];

    // ---------------------------------------------------------------- missing files

    public static IEnumerable<QuickFix> MissingFile(XamlAnalyzer a, XamlDiagnostic d)
    {
        var raw = Slice(a, d);
        var value = raw.Trim();
        if (a.ProjectDirectory == null || Assets.Resolve(a, value) is not { } missing) return [];
        var dir = Path.GetDirectoryName(missing);
        if (dir == null || !Directory.Exists(dir)) return [];
        var wanted = Path.GetFileName(missing);
        var names = Directory.EnumerateFiles(dir).Select(Path.GetFileName).OfType<string>().ToList();

        // Keep how the path was written (avares://…, /Assets/…, ../…): only the file name changes.
        var hash = value.IndexOf('#');
        var pathPart = hash >= 0 ? value[..hash] : value;
        var tail = hash >= 0 ? value[hash..] : "";
        var slash = pathPart.LastIndexOf('/');
        var head = slash >= 0 ? pathPart[..(slash + 1)] : "";
        var lead = raw.IndexOf(value, StringComparison.Ordinal);
        var start = d.Start + lead;
        var fixes = new List<QuickFix>();
        foreach (var c in Spelling.Closest(wanted, names, 3))
            fixes.Add(new QuickFix($"Change to '{head}{c}'", [new FileEdit(PathOf(a), start, start + value.Length, head + c + tail)], fixes.Count == 0));
        return fixes;
    }
}
