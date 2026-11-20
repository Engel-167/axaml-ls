using AxamlLs.Workspace;
using AxamlLs.Xaml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace AxamlLs.Analysis;

/// <summary>Where else a target occurs: the project's AXAML, C# string literals, and Roslyn's references.</summary>
public static partial class Targets
{
    /// <summary>
    /// Every place a target occurs. Element names stay within the origin file (names are per name scope, which is close enough);
    /// keys, classes and symbols are searched in all the project's .axaml files and its C# code.
    /// </summary>
    public static async Task<List<Occurrence>> OccurrencesAsync(Target target, XamlFile origin, IReadOnlyList<XamlFile> files, ProjectHost? host)
    {
        var result = TextOccurrences(target, origin, files);
        if (target is SymbolTarget s && host?.Solution != null)
        {
            await CodeSymbolOccurrencesAsync(s.Symbol, host, result).ConfigureAwait(false);
            result = Ordered(result);
        }
        return result;
    }

    /// <summary>
    /// The occurrences found by reading text: everything for element names, resource keys and style classes (XAML plus
    /// string literals in C#), and only the XAML uses of a C# symbol (its C# references need Roslyn, see
    /// <see cref="OccurrencesAsync"/>). Synchronous, so hover can use it without blocking on a task.
    /// The C# literals come from the origin's project context (none outside a project).
    /// </summary>
    public static List<Occurrence> TextOccurrences(Target target, XamlFile origin, IReadOnlyList<XamlFile> files)
    {
        var result = new List<Occurrence>();
        var code = origin.Analyzer.Context.CodeLiterals?.Invoke() ?? [];
        switch (target)
        {
            case ElementNameTarget n:
                ElementNameOccurrences(n.Name, origin, result);
                break;
            case ResourceKeyTarget k:
                foreach (var f in files) ResourceKeyOccurrences(k.Key, f, result);
                foreach (var file in code)
                {
                    foreach (var lit in file.ResourceKeys)
                    {
                        if (lit.Name == k.Key) result.Add(new Occurrence(file.Path, lit.Start, lit.End));
                    }
                }
                break;
            case StyleClassTarget c:
                foreach (var f in files) ClassOccurrences(c.Class, f, result);
                foreach (var file in code)
                {
                    foreach (var lit in file.Classes)
                    {
                        if (lit.Name == c.Class) result.Add(new Occurrence(file.Path, lit.Start, lit.End));
                    }
                }
                break;
            case SymbolTarget s:
                foreach (var f in files) SymbolOccurrencesIn(s.Symbol, f, result);
                break;
        }
        return Ordered(result);
    }

    /// <summary>
    /// The words that make an .axaml file a candidate for the target's occurrences (see <see cref="FileWords"/>):
    /// none for an element name (it is only searched in its own file), the key or class, and for a symbol the name as
    /// written under the cursor plus the spellings XAML uses for it (<c>FooExtension</c> as <c>{x:Foo}</c>,
    /// <c>RowProperty</c> and <c>GetRow</c> as <c>Grid.Row</c>, <c>ClickEvent</c> as <c>Click</c>).
    /// </summary>
    /// <param name="found">The target and its span in the origin.</param>
    /// <param name="originText">The text of the file the target was found in.</param>
    public static IReadOnlyCollection<string> CandidateNames(TargetAt found, string originText)
    {
        switch (found.Target)
        {
            case ElementNameTarget:
                return [];
            case ResourceKeyTarget k:
                return [k.Key];
            case StyleClassTarget c:
                return [c.Class];
            case SymbolTarget s:
                var names = new HashSet<string>(StringComparer.Ordinal) { s.Symbol.Name };
                if (found.Start >= 0 && found.End <= originText.Length && found.End > found.Start) names.Add(originText[found.Start..found.End]);
                foreach (var suffix in (string[])["Extension", "Property", "Event"])
                {
                    if (s.Symbol.Name.Length > suffix.Length && s.Symbol.Name.EndsWith(suffix, StringComparison.Ordinal)) names.Add(s.Symbol.Name[..^suffix.Length]);
                }
                if (s.Symbol is IMethodSymbol && s.Symbol.Name.Length > 3 && (s.Symbol.Name.StartsWith("Get", StringComparison.Ordinal) || s.Symbol.Name.StartsWith("Set", StringComparison.Ordinal)))
                    names.Add(s.Symbol.Name[3..]);
                return names;
            default:
                return [found.Target.ToString()!];
        }
    }

    private static List<Occurrence> Ordered(List<Occurrence> found) =>
        [.. found.DistinctBy(o => (o.Path, o.Start, o.End)).OrderBy(o => o.Path, StringComparer.Ordinal).ThenBy(o => o.Start)];

    private static void ElementNameOccurrences(string name, XamlFile file, List<Occurrence> result)
    {
        var doc = file.Analyzer.Result.Doc;
        foreach (var el in doc.AllElements)
        {
            if (file.Analyzer.Result.Elements.TryGetValue(el, out var info) && info.Ignored) continue;
            var attr = XamlAnalyzer.XAttr(el, "Name") ?? el.Attr("Name");
            if (attr is { HasValue: true } && attr.Value.Trim() == name && Trimmed(attr) is { } t)
                result.Add(new Occurrence(file.Path, t.Start, t.End, IsDeclaration: true));
            if (Selectors.SelectorAttr(el) is { } sel)
            {
                var text = MappedText.FromAttribute(sel);
                foreach (var tk in Selectors.Tokenize(text.Text))
                {
                    if (tk.Kind == SelectorTokenKind.Name && tk.Text[1..] == name && !AfterTemplate(text.Text, tk.Start))
                        result.Add(new Occurrence(file.Path, text.Abs(tk.Start + 1), text.Abs(tk.End)));
                }
            }
        }
        foreach (var r in file.Analyzer.Result.Refs)
        {
            if (r.TargetElement == null || ElementName(r.TargetElement) != name) continue;
            var start = file.Text.Length > r.Start && file.Text[r.Start] == '#' ? r.Start + 1 : r.Start;
            result.Add(new Occurrence(file.Path, start, r.End));
        }
    }

    private static void ResourceKeyOccurrences(string key, XamlFile file, List<Occurrence> result)
    {
        foreach (var el in file.Analyzer.Result.Doc.AllElements)
        {
            foreach (var attr in el.Attributes)
            {
                if (IsXAttr(el, attr, "Key") && attr.HasValue && !MarkupExtension.IsMarkup(attr.Value) && attr.Value.Trim() == key && Trimmed(attr) is { } t)
                    result.Add(new Occurrence(file.Path, t.Start, t.End, IsDeclaration: true));
            }
        }
        foreach (var use in ResourceCheck.Uses(file.Analyzer))
        {
            if (use.Key == key) result.Add(new Occurrence(file.Path, use.Start, use.End));
        }
    }

    private static void ClassOccurrences(string cls, XamlFile file, List<Occurrence> result)
    {
        foreach (var (token, start, end) in Selectors.DocumentTokens(file.Analyzer.Result.Doc))
        {
            if (token.Kind == SelectorTokenKind.Class && token.Text.Length > 1 && token.Text[1..] == cls) result.Add(new Occurrence(file.Path, start + 1, end) { IsSelector = true });
        }
        foreach (var el in file.Analyzer.Result.Doc.AllElements)
        {
            var isClassSetter = el.LocalName == "Setter" && el.Attr("Property")?.Value.Trim() == "Classes";
            foreach (var attr in el.Attributes)
            {
                if (attr.Name.StartsWith("Classes.", StringComparison.Ordinal) && attr.Name[8..] == cls)
                {
                    result.Add(new Occurrence(file.Path, attr.NameStart + 8, attr.NameEnd));
                }
                else if ((attr.Name == "Classes" || isClassSetter && attr.Name == "Value") && attr.HasValue && !MarkupExtension.IsMarkup(attr.Value))
                {
                    var s = attr.Value;
                    for (var i = 0; i < s.Length;)
                    {
                        while (i < s.Length && IsSeparator(s[i])) i++;
                        var a = i;
                        while (i < s.Length && !IsSeparator(s[i])) i++;
                        if (i > a && s[a..i] == cls) result.Add(new Occurrence(file.Path, attr.Map[a], attr.Map[i]));
                    }
                }
            }
        }
    }

    public static void SymbolOccurrencesIn(ISymbol symbol, XamlFile file, List<Occurrence> result)
    {
        foreach (var r in file.Analyzer.Result.Refs)
        {
            if (r.Symbol == null || r.TargetElement != null || !SameSymbol(r.Symbol, symbol)) continue;
            var (s, e) = NameSpan(file.Text, r);
            result.Add(new Occurrence(file.Path, s, e));
        }
    }

    /// <summary>Symbols from different compilations never compare equal, so fall back to kind, owner and name.</summary>
    public static bool SameSymbol(ISymbol a, ISymbol b)
    {
        if (SymbolEqualityComparer.Default.Equals(a.OriginalDefinition, b.OriginalDefinition)) return true;
        return a.Kind == b.Kind && a.Name == b.Name &&
               (a.ContainingType?.ToDisplayString() ?? a.ContainingNamespace?.ToDisplayString()) == (b.ContainingType?.ToDisplayString() ?? b.ContainingNamespace?.ToDisplayString()) &&
               (a is not IMethodSymbol ma || b is not IMethodSymbol mb || ma.Parameters.Length == mb.Parameters.Length);
    }

    /// <summary>
    /// References and declarations in the project's own C# files. Members generated by CommunityToolkit are searched through
    /// the user-written field, partial property or method they come from.
    /// </summary>
    private static async Task CodeSymbolOccurrencesAsync(ISymbol symbol, ProjectHost host, List<Occurrence> result)
    {
        var solution = host.Solution!;
        // The user-written field/method behind a generated member is only searched for when the symbol itself is generated:
        // an ordinary property must not pick up an unrelated field that happens to be named like it.
        var candidates = IsGenerated(symbol) ? Definition.Symbols(symbol) : [symbol];
        var usable = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var candidate in candidates.Distinct(SymbolEqualityComparer.Default))
        {
            IEnumerable<ReferencedSymbol> found;
            try
            {
                found = await SymbolFinder.FindReferencesAsync(candidate, solution).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error($"Finding references of {candidate.Name} failed", ex);
                continue;
            }
            foreach (var rs in found)
            {
                foreach (var loc in rs.Definition.Locations) AddLocation(loc, host, result, declaration: true, usable);
                foreach (var rl in rs.Locations) AddLocation(rl.Location, host, result, declaration: false, usable);
            }
        }
    }

    /// <summary>True when the symbol has no declaration in a real source file (a source-generator output).</summary>
    public static bool IsGenerated(ISymbol symbol) => !symbol.Locations.Any(IsRealSource);

    private static bool IsRealSource(Location loc) =>
        loc.IsInSource && loc.SourceTree?.FilePath is { Length: > 0 } p && File.Exists(p) && !ProjectFiles.IsBuildOutput(p);

    /// <summary>C# references (not declarations) of exactly this symbol, in the project's own files.</summary>
    public static async Task<List<Occurrence>> CodeReferencesOfAsync(ISymbol symbol, ProjectHost host)
    {
        var result = new List<Occurrence>();
        if (host.Solution is not { } solution) return result;
        var usable = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var rs in await SymbolFinder.FindReferencesAsync(symbol, solution).ConfigureAwait(false))
        {
            foreach (var rl in rs.Locations) AddLocation(rl.Location, host, result, declaration: false, usable);
        }
        return result;
    }

    /// <summary>
    /// Adds a Roslyn location when its file is usable. <paramref name="usable"/> remembers the verdict per path for the
    /// request, so a file with many references is checked and read once.
    /// </summary>
    private static void AddLocation(Location loc, ProjectHost host, List<Occurrence> result, bool declaration, Dictionary<string, bool> usable)
    {
        if (!loc.IsInSource || loc.SourceTree?.FilePath is not { Length: > 0 } path) return;
        if (!usable.TryGetValue(path, out var ok)) usable[path] = ok = Usable(loc.SourceTree, path, host);
        if (ok) result.Add(new Occurrence(path, loc.SourceSpan.Start, loc.SourceSpan.End, declaration));
    }

    /// <summary>
    /// A project file outside the build output whose text on disk is still what Roslyn saw (its offsets are Roslyn's,
    /// so they only apply to that text).
    /// </summary>
    private static bool Usable(SyntaxTree tree, string path, ProjectHost host)
    {
        if (!File.Exists(path) || ProjectFiles.IsBuildOutput(path) || !path.StartsWith(host.ProjectDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return false;
        try
        {
            return new FileInfo(path).Length == 0 || tree.Length == File.ReadAllText(path).Length;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
