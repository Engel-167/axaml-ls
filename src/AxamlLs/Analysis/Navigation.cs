using AxamlLs.Text;
using AxamlLs.Workspace;
using Microsoft.CodeAnalysis;

namespace AxamlLs.Analysis;

/// <summary>Go to type definition (the view model), go to implementation (the code-behind) and the code lenses that offer both.</summary>
public static class Navigation
{
    /// <summary>
    /// On a symbol: the declaration of its type. Anywhere else: the x:DataType in scope there, i.e. the view model class.
    /// A type that is not declared in the project falls back to its item type (a collection of view models).
    /// </summary>
    public static List<DefinitionTarget> TypeDefinition(XamlAnalyzer analyzer, int offset)
    {
        if (analyzer.Result.RefAt(offset) is { } r && (r.Symbol != null || r.Type != null))
        {
            var type = r.Symbol as ITypeSymbol ?? r.Type ?? Members.ValueTypeOf(r.Symbol);
            var found = DeclarationsOf(type);
            if (found.Count == 0 && type != null && Members.ElementType(type) is { } item) found = DeclarationsOf(item);
            if (found.Count > 0) return found;
        }
        var el = analyzer.Result.Doc.ElementAt(offset);
        return el == null ? [] : DeclarationsOf(analyzer.ScopeAt(el).DataType);
    }

    /// <summary>On an event handler: the method. Anywhere else: the x:Class type in its code-behind file.</summary>
    public static List<DefinitionTarget> Implementation(XamlAnalyzer analyzer, int offset)
    {
        if (analyzer.Result.RefAt(offset) is { Symbol: IMethodSymbol handler })
        {
            var found = Declarations(handler);
            if (found.Count > 0) return found;
        }
        return CodeBehind(analyzer);
    }

    /// <summary>The class named by x:Class, in the .axaml.cs file if it has a part there.</summary>
    public static List<DefinitionTarget> CodeBehind(XamlAnalyzer analyzer)
    {
        if (analyzer.Result.ClassType is not { } cls) return [];
        var all = Declarations(cls);
        var behind = all.Where(t => t.FilePath!.EndsWith(".axaml.cs", StringComparison.OrdinalIgnoreCase)).ToList();
        return behind.Count > 0 ? behind : all.Take(1).ToList();
    }

    /// <summary>The view model: the x:DataType of the root element.</summary>
    public static List<DefinitionTarget> ViewModel(XamlAnalyzer analyzer) =>
        analyzer.Result.Doc.Root is { } root ? DeclarationsOf(analyzer.ScopeAt(root).DataType) : [];

    private static List<DefinitionTarget> DeclarationsOf(ITypeSymbol? type) =>
        type == null ? [] : Declarations(Members.UnwrapNullable(type).OriginalDefinition);

    /// <summary>Where a symbol is declared in the project's own source files (generated code is skipped).</summary>
    private static List<DefinitionTarget> Declarations(ISymbol symbol)
    {
        var result = new List<DefinitionTarget>();
        foreach (var loc in symbol.Locations)
        {
            if (!loc.IsInSource || loc.SourceTree?.FilePath is not { Length: > 0 } path || !File.Exists(path) || ProjectFiles.IsBuildOutput(path)) continue;
            var span = loc.GetLineSpan();
            result.Add(new DefinitionTarget(span.Path, span.StartLinePosition.Line, span.StartLinePosition.Character, span.EndLinePosition.Line, span.EndLinePosition.Character));
        }
        return result;
    }
}

/// <summary>A range of lines the editor can fold (0-based lines; the lines after StartLine up to EndLine are hidden).</summary>
public sealed record FoldRange(int StartLine, int EndLine, string? Kind);

public static class Folding
{
    /// <summary>
    /// Elements spanning several lines (start-tag line to the line before the end tag), multi-line start tags (to the line of
    /// the '&gt;'), multi-line comments, and &lt;!-- #region --&gt; … &lt;!-- #endregion --&gt; pairs.
    /// </summary>
    public static List<FoldRange> Compute(XamlAnalyzer analyzer, LineIndex lines)
    {
        var ranges = new List<FoldRange>();
        var doc = analyzer.Result.Doc;
        int Line(int offset) => lines.GetPosition(offset).Line;

        foreach (var el in doc.AllElements)
        {
            var startLine = Line(el.Start);
            var tagEndLine = el.StartTagEnd > 0 ? Line(el.StartTagEnd - 1) : startLine;
            if (tagEndLine > startLine) ranges.Add(new FoldRange(startLine, tagEndLine, null)); // many attributes
            if (el.EndTagStart >= 0 && !el.SelfClosing)
            {
                var endLine = Line(el.EndTagStart) - 1;
                if (endLine > tagEndLine) ranges.Add(new FoldRange(tagEndLine, endLine, null));
            }
        }

        var regions = new Stack<int>();
        var text = doc.Text;
        var i = 0;
        while ((i = text.IndexOf("<!--", i, StringComparison.Ordinal)) >= 0)
        {
            var close = text.IndexOf("-->", i + 4, StringComparison.Ordinal);
            var end = close < 0 ? text.Length : close + 3;
            var startLine = Line(i);
            var endLine = Line(Math.Max(end - 1, i));
            var body = text[(i + 4)..Math.Max(i + 4, close < 0 ? text.Length : close)].Trim();
            if (body.StartsWith("#region", StringComparison.OrdinalIgnoreCase)) regions.Push(startLine);
            else if (body.StartsWith("#endregion", StringComparison.OrdinalIgnoreCase))
            {
                if (regions.Count > 0 && regions.Pop() is var regionStart && endLine > regionStart) ranges.Add(new FoldRange(regionStart, endLine, "region"));
            }
            else if (endLine > startLine)
            {
                ranges.Add(new FoldRange(startLine, endLine, "comment"));
            }
            i = end;
        }
        return ranges.DistinctBy(r => (r.StartLine, r.EndLine)).OrderBy(r => r.StartLine).ThenBy(r => r.EndLine).ToList();
    }
}
