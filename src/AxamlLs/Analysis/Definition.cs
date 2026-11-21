using Microsoft.CodeAnalysis;

namespace AxamlLs.Analysis;

/// <summary>A definition: either a file location (C#) or an offset in the same document.</summary>
public sealed record DefinitionTarget(string? FilePath, int StartLine, int StartCharacter, int EndLine, int EndCharacter, int? DocumentOffset = null);

/// <summary>Go to definition: C# declarations (through source generators), resource declarations, asset files, named elements.</summary>
public static class Definition
{
    public static List<DefinitionTarget> At(XamlAnalyzer analyzer, int offset)
    {
        var result = new List<DefinitionTarget>();
        var r = analyzer.Result.RefAt(offset);
        if (r == null && ResourceValues.KeyAt(analyzer, offset) is { } key)
        {
            foreach (var e in analyzer.Resources().Where(e => e.Key == key.Key && e.Assembly == null))
                result.Add(new DefinitionTarget(e.FilePath, e.Line, e.Character, e.Line, e.Character + e.Key.Length));
            return result;
        }
        if (r == null && Assets.At(analyzer, offset) is { } file)
        {
            result.Add(new DefinitionTarget(file, 0, 0, 0, 0));
            return result;
        }
        if (r == null) return result;
        if (r.TargetElement != null)
        {
            result.Add(new DefinitionTarget(null, 0, 0, 0, 0, r.TargetElement.NameStart));
            return result;
        }
        if (r.Symbol == null) return result;
        foreach (var symbol in Symbols(r.Symbol))
        {
            foreach (var loc in symbol.Locations)
            {
                if (!loc.IsInSource || loc.SourceTree == null || !File.Exists(loc.SourceTree.FilePath)) continue;
                var span = loc.GetLineSpan();
                result.Add(new DefinitionTarget(span.Path, span.StartLinePosition.Line, span.StartLinePosition.Character,
                    span.EndLinePosition.Line, span.EndLinePosition.Character));
            }
            if (result.Count > 0) break;
        }
        return result;
    }

    /// <summary>
    /// The symbol itself, then — for source-generated members (CommunityToolkit [ObservableProperty]/[RelayCommand]) —
    /// the user-written field, partial property or method it was generated from.
    /// </summary>
    internal static IEnumerable<ISymbol> Symbols(ISymbol symbol)
    {
        if (symbol is IPropertySymbol { PartialDefinitionPart: { } def }) yield return def;
        yield return symbol;
        var type = symbol.ContainingType;
        if (type == null) yield break;
        if (symbol is IPropertySymbol p)
        {
            var camel = char.ToLowerInvariant(p.Name[0]) + p.Name[1..];
            foreach (var candidate in new[] { "_" + camel, camel, "m_" + camel })
            {
                foreach (var m in type.GetMembers(candidate).OfType<IFieldSymbol>()) yield return m;
            }
            if (p.Name.EndsWith("Command", StringComparison.Ordinal))
            {
                var baseName = p.Name[..^7];
                foreach (var candidate in new[] { baseName, baseName + "Async", "On" + baseName })
                {
                    foreach (var m in type.GetMembers(candidate).OfType<IMethodSymbol>()) yield return m;
                }
            }
        }
    }
}
