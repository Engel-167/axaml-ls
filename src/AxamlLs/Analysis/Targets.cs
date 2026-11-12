using AxamlLs.Workspace;
using AxamlLs.Xaml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace AxamlLs.Analysis;

/// <summary>What the cursor is on, for find-references and rename.</summary>
public abstract record Target;
/// <summary>x:Name="Foo" / Name="Foo", #Foo in a binding path or selector, ElementName=Foo.</summary>
public sealed record ElementNameTarget(string Name) : Target;
/// <summary>x:Key="Foo" or {StaticResource Foo}.</summary>
public sealed record ResourceKeyTarget(string Key) : Target;
/// <summary>.Foo in a selector, Classes="… Foo …", Classes.Foo=.</summary>
public sealed record StyleClassTarget(string Class) : Target;
/// <summary>A C# member or type (binding path segment, property, type, event handler).</summary>
public sealed record SymbolTarget(ISymbol Symbol) : Target;

/// <summary>A target and the span of its name under the cursor (without '.', '#', quotes or the Classes. prefix).</summary>
public sealed record TargetAt(Target Target, int Start, int End);

/// <summary>A place a target occurs. <see cref="IsDeclaration"/> marks the x:Name / x:Key value or the C# declaration.</summary>
public sealed record Occurrence(string Path, int Start, int End, bool IsDeclaration = false)
{
    /// <summary>A class used inside a style selector (".Card"), as opposed to set on an element.</summary>
    public bool IsSelector { get; init; }
}

public static class Targets
{
    public static Target? At(XamlAnalyzer analyzer, int offset) => Find(analyzer, offset)?.Target;

    /// <summary>Recognizes, in order: a selector token, a Classes value or name, an x:Key or resource use, an element name, a symbol.</summary>
    public static TargetAt? Find(XamlAnalyzer analyzer, int offset)
    {
        var doc = analyzer.Result.Doc;

        // 1. Selector tokens.
        foreach (var el in doc.AllElements)
        {
            if (Selectors.SelectorAttr(el) is not { } sel || offset < sel.ValueStart || offset > sel.ValueEnd) continue;
            var text = MappedText.FromAttribute(sel);
            var tokens = Selectors.Tokenize(text.Text);
            var token = tokens.Cast<SelectorToken?>().FirstOrDefault(t => text.Abs(t!.Value.Start) <= offset && offset < text.Abs(t.Value.End))
                        ?? tokens.Cast<SelectorToken?>().FirstOrDefault(t => offset == text.Abs(t!.Value.End));
            if (token is not { } tk || tk.Text.Length < 2) break;
            if (tk.Kind == SelectorTokenKind.Class) return new TargetAt(new StyleClassTarget(tk.Text[1..]), text.Abs(tk.Start + 1), text.Abs(tk.End));
            if (tk.Kind == SelectorTokenKind.Name && !AfterTemplate(text.Text, tk.Start))
                return new TargetAt(new ElementNameTarget(tk.Text[1..]), text.Abs(tk.Start + 1), text.Abs(tk.End));
            break;
        }

        var element = doc.ElementAt(offset);
        if (element != null)
        {
            foreach (var attr in element.Attributes)
            {
                // 2. Classes="a b" / Classes.a="…".
                if (attr.Name == "Classes" && attr.HasValue && !MarkupExtension.IsMarkup(attr.Value) && attr.ValueStart <= offset && offset <= attr.ValueEnd)
                {
                    if (WordAt(attr, offset) is { } w) return new TargetAt(new StyleClassTarget(w.Word), w.Start, w.End);
                }
                else if (attr.Name.StartsWith("Classes.", StringComparison.Ordinal) && attr.Name.Length > 8 && offset >= attr.NameStart + 8 && offset <= attr.NameEnd)
                {
                    return new TargetAt(new StyleClassTarget(attr.Name[8..]), attr.NameStart + 8, attr.NameEnd);
                }
                else if (element.LocalName == "Setter" && attr.Name == "Value" && element.Attr("Property")?.Value.Trim() == "Classes" &&
                         attr.HasValue && !MarkupExtension.IsMarkup(attr.Value) && attr.ValueStart <= offset && offset <= attr.ValueEnd)
                {
                    if (WordAt(attr, offset) is { } w) return new TargetAt(new StyleClassTarget(w.Word), w.Start, w.End);
                }

                // 3. x:Key="Foo".
                if (IsXAttr(element, attr, "Key") && attr.HasValue && !MarkupExtension.IsMarkup(attr.Value) && attr.ValueStart <= offset && offset <= attr.ValueEnd)
                {
                    if (Trimmed(attr) is { } t) return new TargetAt(new ResourceKeyTarget(t.Text), t.Start, t.End);
                }
                // 4. x:Name="Foo" / Name="Foo".
                if ((IsXAttr(element, attr, "Name") || attr.Name == "Name") && attr.HasValue && !MarkupExtension.IsMarkup(attr.Value) && attr.ValueStart <= offset && offset <= attr.ValueEnd)
                {
                    if (Trimmed(attr) is { } t) return new TargetAt(new ElementNameTarget(t.Text), t.Start, t.End);
                }
            }
        }

        if (Features.ResourceKeyAt(analyzer, offset) is { } key && key.Key.Length > 0)
            return new TargetAt(new ResourceKeyTarget(key.Key), key.Start, key.Start + key.Key.Length);

        var r = analyzer.Result.RefAt(offset);
        if (r == null) return null;
        if (r.TargetElement != null)
        {
            var name = ElementName(r.TargetElement);
            if (name == null) return null;
            var start = doc.Text.Length > r.Start && doc.Text[r.Start] == '#' ? r.Start + 1 : r.Start;
            return new TargetAt(new ElementNameTarget(name), start, r.End);
        }
        if (r.Symbol == null) return null;
        var (s, e) = NameSpan(doc.Text, r);
        return new TargetAt(new SymbolTarget(r.Symbol), s, e);
    }

    /// <summary>The name part of a symbol reference: "vm:MainViewModel" → "MainViewModel", "SampleApp.Views.MainView" → "MainView".</summary>
    private static (int Start, int End) NameSpan(string text, SymbolRef r)
    {
        var start = r.Start;
        if (r.Symbol is ITypeSymbol && r.End <= text.Length)
        {
            var slice = text[r.Start..r.End];
            start = r.Start + Math.Max(slice.LastIndexOf(':'), slice.LastIndexOf('.')) + 1;
        }
        return (start, r.End);
    }

    private static bool AfterTemplate(string selector, int tokenStart)
    {
        var before = selector[..tokenStart];
        var comma = before.LastIndexOf(',');
        return before[(comma + 1)..].Contains("/template/", StringComparison.Ordinal);
    }

    private static bool IsXAttr(XmlElement el, XmlAttr attr, string local) =>
        attr.Prefix != null && attr.LocalName == local && el.LookupNamespace(attr.Prefix) == XamlNamespaces.X;

    public static string? ElementName(XmlElement el)
    {
        var attr = XamlAnalyzer.XAttr(el, "Name") ?? el.Attr("Name");
        return attr is { HasValue: true } && attr.Value.Trim().Length > 0 ? attr.Value.Trim() : null;
    }

    private static (string Text, int Start, int End)? Trimmed(XmlAttr attr)
    {
        var text = attr.Value.Trim();
        if (text.Length == 0) return null;
        var lead = attr.Value.IndexOf(text, StringComparison.Ordinal);
        return (text, attr.Map[lead], attr.Map[lead + text.Length]);
    }

    private static (string Word, int Start, int End)? WordAt(XmlAttr attr, int offset)
    {
        var s = attr.Value;
        var idx = 0;
        while (idx < s.Length && attr.Map[idx] < offset) idx++;
        var from = Math.Min(idx, s.Length);
        var a = from;
        while (a > 0 && !IsSeparator(s[a - 1])) a--;
        var b = from;
        while (b < s.Length && !IsSeparator(s[b])) b++;
        return b > a ? (s[a..b], attr.Map[a], attr.Map[b]) : null;
    }

    private static bool IsSeparator(char c) => char.IsWhiteSpace(c) || c == ',';

    // ---------------------------------------------------------------- where else

    /// <summary>
    /// Every place a target occurs. Element names stay within the origin file (names are per name scope, which is close enough);
    /// keys, classes and symbols are searched in all the project's .axaml files and its C# code.
    /// </summary>
    public static async Task<List<Occurrence>> OccurrencesAsync(Target target, XamlFile origin, IReadOnlyList<XamlFile> files, ProjectHost? host)
    {
        var result = new List<Occurrence>();
        var projectDir = host?.ProjectDirectory ?? origin.Analyzer.ProjectDirectory;
        switch (target)
        {
            case ElementNameTarget n:
                ElementNameOccurrences(n.Name, origin, result);
                break;
            case ResourceKeyTarget k:
                foreach (var f in files) ResourceKeyOccurrences(k.Key, f, result);
                foreach (var (path, text) in CodeFiles(projectDir))
                {
                    foreach (var lit in CodeLiterals.ResourceKeys(text))
                    {
                        if (lit.Name == k.Key) result.Add(new Occurrence(path, lit.Start, lit.End));
                    }
                }
                break;
            case StyleClassTarget c:
                foreach (var f in files) ClassOccurrences(c.Class, f, result);
                foreach (var (path, text) in CodeFiles(projectDir))
                {
                    foreach (var lit in CodeLiterals.Classes(text))
                    {
                        if (lit.Name == c.Class) result.Add(new Occurrence(path, lit.Start, lit.End));
                    }
                }
                break;
            case SymbolTarget s:
                foreach (var f in files) SymbolOccurrencesIn(s.Symbol, f, result);
                if (host?.Solution != null) await CodeSymbolOccurrencesAsync(s.Symbol, host, result).ConfigureAwait(false);
                break;
        }
        return result.DistinctBy(o => (o.Path, o.Start, o.End)).OrderBy(o => o.Path, StringComparer.Ordinal).ThenBy(o => o.Start).ToList();
    }

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
        var candidates = IsGenerated(symbol) ? Features.DefinitionSymbols(symbol) : [symbol];
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
                foreach (var loc in rs.Definition.Locations) AddLocation(loc, host, result, declaration: true);
                foreach (var rl in rs.Locations) AddLocation(rl.Location, host, result, declaration: false);
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
        foreach (var rs in await SymbolFinder.FindReferencesAsync(symbol, solution).ConfigureAwait(false))
        {
            foreach (var rl in rs.Locations) AddLocation(rl.Location, host, result, declaration: false);
        }
        return result;
    }

    private static void AddLocation(Location loc, ProjectHost host, List<Occurrence> result, bool declaration)
    {
        if (!loc.IsInSource || loc.SourceTree?.FilePath is not { Length: > 0 } path) return;
        if (!File.Exists(path) || ProjectFiles.IsBuildOutput(path) || !path.StartsWith(host.ProjectDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return;
        // The offsets are Roslyn's; they only apply to the file if it still has the text Roslyn saw.
        if (new FileInfo(path).Length > 0 && loc.SourceTree.Length != File.ReadAllText(path).Length) return;
        result.Add(new Occurrence(path, loc.SourceSpan.Start, loc.SourceSpan.End, declaration));
    }

    /// <summary>The project's C# files (path, text) read from disk.</summary>
    public static IEnumerable<(string Path, string Text)> CodeFiles(string? projectDir)
    {
        if (projectDir == null) yield break;
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(projectDir, "*.cs", SearchOption.AllDirectories).Where(f => !ProjectFiles.IsBuildOutput(f)).ToList();
        }
        catch (Exception)
        {
            yield break;
        }
        foreach (var f in files)
        {
            string text;
            try
            {
                text = File.ReadAllText(f);
            }
            catch (IOException)
            {
                continue;
            }
            yield return (f, text);
        }
    }
}
