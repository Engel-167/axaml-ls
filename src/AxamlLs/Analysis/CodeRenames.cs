using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using AxamlLs.Lsp;
using AxamlLs.Workspace;

namespace AxamlLs.Analysis;

/// <summary>A C# declaration renamed in code: the symbol as it was (from the compilation before the change) and its new name.</summary>
public sealed record CodeRename(ISymbol OldSymbol, string NewName);

/// <summary>
/// Spots renames done in C# (by the editor's C# language server, or by hand) when a .cs file is saved, so the AXAML that
/// uses the old names can follow. A save counts as a rename only when the new text is the old text with some identifiers
/// swapped one-for-one and nothing else changed, and one of the swapped identifiers is a declaration's name.
/// </summary>
public static class CodeRenames
{
    /// <summary>The renames between a document's text in <paramref name="before"/> and <paramref name="newText"/>.</summary>
    public static async Task<List<CodeRename>> DetectAsync(Document before, SourceText newText, CancellationToken ct = default)
    {
        var result = new List<CodeRename>();
        if (await before.GetSyntaxTreeAsync(ct).ConfigureAwait(false) is not { } oldTree) return result;
        var newTree = CSharpSyntaxTree.ParseText(newText, (CSharpParseOptions)oldTree.Options, oldTree.FilePath, ct);
        if (SwappedIdentifiers(await oldTree.GetRootAsync(ct).ConfigureAwait(false), await newTree.GetRootAsync(ct).ConfigureAwait(false)) is not { } swaps)
            return result;
        if (await before.GetSemanticModelAsync(ct).ConfigureAwait(false) is not { } model) return result;

        var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var (oldToken, newName) in swaps)
        {
            if (oldToken.Parent is not { } node || model.GetDeclaredSymbol(node, ct) is not { } declared) continue;
            if (declared.Name != oldToken.ValueText || !seen.Add(declared)) continue; // a constructor named after its class, …
            result.Add(new CodeRename(declared, newName));
            result.AddRange(GeneratedFrom(declared, newName));
        }
        return result;
    }

    /// <summary>
    /// The AXAML edits that make <paramref name="files"/> (analysed against the project as it was before the rename) use
    /// the new names. Only places whose text is exactly the old name are changed, so a binding path segment, x:Name
    /// reference or {x:Static} member that merely resolves to the symbol by another spelling is left alone.
    /// </summary>
    public static List<FileEdit> AxamlEdits(IReadOnlyList<CodeRename> renames, IReadOnlyList<XamlFile> files)
    {
        var edits = new List<FileEdit>();
        var textOf = files.ToDictionary(f => f.Path, f => f.Text, StringComparer.Ordinal);
        foreach (var r in renames)
        {
            var found = new List<Occurrence>();
            foreach (var f in files)
            {
                if (f.Path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase)) Targets.SymbolOccurrencesIn(r.OldSymbol, f, found);
            }
            foreach (var o in found)
            {
                if (!textOf.TryGetValue(o.Path, out var text) || o.End > text.Length || o.Start >= o.End) continue;
                if (o.End - o.Start != r.OldSymbol.Name.Length || string.CompareOrdinal(text, o.Start, r.OldSymbol.Name, 0, r.OldSymbol.Name.Length) != 0) continue;
                edits.Add(new FileEdit(o.Path, o.Start, o.End, r.NewName));
            }
        }
        return edits.Distinct().ToList();
    }

    /// <summary>
    /// The identifiers that differ, as (old token, new name), when the two trees are the same token for token except for
    /// identifiers swapped consistently (every old name always becomes the same new name). Null for any other difference,
    /// including comments, whitespace and typing that changed more than names.
    /// </summary>
    public static List<(SyntaxToken Old, string NewName)>? SwappedIdentifiers(SyntaxNode oldRoot, SyntaxNode newRoot)
    {
        var a = oldRoot.DescendantTokens().ToList();
        var b = newRoot.DescendantTokens().ToList();
        if (a.Count != b.Count) return null;
        var swaps = new List<(SyntaxToken, string)>();
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < a.Count; i++)
        {
            var (ta, tb) = (a[i], b[i]);
            if (ta.RawKind != tb.RawKind) return null;
            if (ta.ToFullString() == tb.ToFullString()) continue;
            if (!ta.IsKind(SyntaxKind.IdentifierToken) || ta.ValueText == tb.ValueText) return null;
            if (ta.LeadingTrivia.ToFullString() != tb.LeadingTrivia.ToFullString() || ta.TrailingTrivia.ToFullString() != tb.TrailingTrivia.ToFullString()) return null;
            if (map.TryGetValue(ta.ValueText, out var earlier) && earlier != tb.ValueText) return null;
            map[ta.ValueText] = tb.ValueText;
            swaps.Add((ta, tb.ValueText));
        }
        return swaps.Count > 0 ? swaps : null;
    }

    /// <summary>
    /// CommunityToolkit members generated from a renamed declaration, with the names the generator will give them:
    /// [ObservableProperty] _title → Title, [RelayCommand] Save/SaveAsync/OnSave → SaveCommand.
    /// </summary>
    private static IEnumerable<CodeRename> GeneratedFrom(ISymbol declared, string newName)
    {
        if (declared.ContainingType is not { } type) yield break;
        foreach (var member in type.GetMembers())
        {
            if (member is not IPropertySymbol generated || !Targets.IsGenerated(generated) || SymbolEqualityComparer.Default.Equals(member, declared)) continue;
            if (!Features.DefinitionSymbols(generated).Any(s => SymbolEqualityComparer.Default.Equals(s, declared))) continue;
            if (GeneratedName(declared, declared.Name) != generated.Name) continue; // not this generator's naming: leave it
            if (GeneratedName(declared, newName) is { } renamed && renamed != generated.Name) yield return new CodeRename(generated, renamed);
        }
    }

    /// <summary>The name CommunityToolkit derives from a field or method name, or null when the name doesn't fit its rules.</summary>
    private static string? GeneratedName(ISymbol source, string name)
    {
        switch (source)
        {
            case IFieldSymbol:
            {
                var bare = name.StartsWith("m_", StringComparison.Ordinal) ? name[2..] : name.TrimStart('_');
                return bare.Length == 0 ? null : char.ToUpperInvariant(bare[0]) + bare[1..];
            }
            case IMethodSymbol:
            {
                var bare = name.EndsWith("Async", StringComparison.Ordinal) && name.Length > 5 ? name[..^5] : name;
                if (bare.StartsWith("On", StringComparison.Ordinal) && bare.Length > 2 && char.IsUpper(bare[2])) bare = bare[2..];
                return bare + "Command";
            }
            default:
                return null;
        }
    }
}
