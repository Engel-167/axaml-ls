using System.Text.RegularExpressions;
using AxamlLs.Lsp;
using AxamlLs.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Rename;

namespace AxamlLs.Analysis;

/// <summary>The edits of a rename, or why it cannot be done (the server turns an error into an InvalidParams response).</summary>
public sealed record RenameResult(List<FileEdit> Edits, string? Error)
{
    public static RenameResult Fail(string message) => new([], message);
}

/// <summary>Rename of element names, resource keys, style classes and C# members referenced from XAML.</summary>
public static partial class Rename
{
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_.-]*$")]
    private static partial Regex XamlName();

    /// <summary>Types and members from libraries cannot be renamed: only those declared in a source file of the project.</summary>
    public static bool CanRename(Target target) =>
        target is not SymbolTarget s || SourceSymbol(s.Symbol) != null;

    private static ISymbol? SourceSymbol(ISymbol symbol)
    {
        if (!Targets.IsGenerated(symbol)) return symbol;
        return Features.DefinitionSymbols(symbol).FirstOrDefault(c => !Targets.IsGenerated(c));
    }

    public static string? Validate(Target target, string newName)
    {
        if (target is SymbolTarget)
            return SyntaxFacts.IsValidIdentifier(newName) ? null : $"'{newName}' is not a valid C# identifier.";
        return XamlName().IsMatch(newName) ? null : $"'{newName}' is not a valid name (letters, digits, '_', '-' and '.', not starting with a digit).";
    }

    public static async Task<RenameResult> ComputeAsync(Target target, string newName, XamlFile origin, IReadOnlyList<XamlFile> files, ProjectHost? host)
    {
        if (Validate(target, newName) is { } invalid) return RenameResult.Fail(invalid);
        if (target is not SymbolTarget symbolTarget)
        {
            var occurrences = await Targets.OccurrencesAsync(target, origin, files, host).ConfigureAwait(false);
            return new RenameResult([.. occurrences.Select(o => new FileEdit(o.Path, o.Start, o.End, newName))], null);
        }
        return await RenameSymbolAsync(symbolTarget.Symbol, newName, origin, files, host).ConfigureAwait(false);
    }

    private static async Task<RenameResult> RenameSymbolAsync(ISymbol symbol, string newName, XamlFile origin, IReadOnlyList<XamlFile> files, ProjectHost? host)
    {
        if (host?.Solution is not { } solution) return RenameResult.Fail("The project is not loaded yet.");
        var edits = new List<FileEdit>();
        var generated = Targets.IsGenerated(symbol);

        // Which declaration Roslyn renames, and to what.
        ISymbol? toRename = symbol;
        var roslynName = newName;
        if (generated)
        {
            (toRename, roslynName, var error) = GeneratorSource(symbol, newName);
            if (error != null) return RenameResult.Fail(error);
            if (toRename == null) return RenameResult.Fail($"Cannot find the source declaration of '{symbol.Name}'.");
            // References to the generated member from the project's own C# (Title = "x") stay the generator's job: rewrite them too.
            foreach (var o in await Targets.CodeReferencesOfAsync(symbol, host).ConfigureAwait(false))
                edits.Add(new FileEdit(o.Path, o.Start, o.End, newName));
        }

        try
        {
            var renamed = await Renamer.RenameSymbolAsync(solution, toRename, new SymbolRenameOptions(), roslynName).ConfigureAwait(false);
            foreach (var change in renamed.GetChanges(solution).GetProjectChanges().SelectMany(pc => pc.GetChangedDocuments()))
            {
                var oldDoc = solution.GetDocument(change);
                var newDoc = renamed.GetDocument(change);
                if (oldDoc?.FilePath is not { } path || newDoc == null || !File.Exists(path) || ProjectFiles.IsBuildOutput(path)) continue;
                if (!path.StartsWith(host.ProjectDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
                var oldText = await oldDoc.GetTextAsync().ConfigureAwait(false);
                if (oldText.Length != File.ReadAllText(path).Length) continue; // changed on disk since Roslyn read it
                foreach (var c in await newDoc.GetTextChangesAsync(oldDoc).ConfigureAwait(false))
                    edits.Add(new FileEdit(path, c.Span.Start, c.Span.End, c.NewText ?? ""));
            }
        }
        catch (Exception ex)
        {
            Log.Error("Roslyn rename failed", ex);
            return RenameResult.Fail($"Cannot rename '{symbol.Name}': {ex.Message}");
        }

        // XAML names the member as the generated property / command, i.e. with the new name as the user typed it.
        foreach (var f in files)
        {
            var found = new List<Occurrence>();
            Targets.SymbolOccurrencesIn(symbol, f, found);
            edits.AddRange(found.Select(o => new FileEdit(o.Path, o.Start, o.End, newName)));
        }
        return new RenameResult(edits, null);
    }

    /// <summary>
    /// The user-written declaration a CommunityToolkit member comes from and its new name:
    /// a [RelayCommand] method for XxxCommand, a partial property itself, or the [ObservableProperty] field.
    /// </summary>
    private static (ISymbol? Symbol, string Name, string? Error) GeneratorSource(ISymbol generated, string newName)
    {
        var type = generated.ContainingType;
        if (type == null || generated is not IPropertySymbol p) return (null, newName, null);
        if (p.PartialDefinitionPart is { } def && !Targets.IsGenerated(def)) return (def, newName, null);

        if (p.Name.EndsWith("Command", StringComparison.Ordinal) && p.Name.Length > 7)
        {
            var baseName = p.Name[..^7];
            foreach (var (candidate, prefix, suffix) in new[] { (baseName, "", ""), (baseName + "Async", "", "Async"), ("On" + baseName, "On", "") })
            {
                var method = type.GetMembers(candidate).OfType<IMethodSymbol>().FirstOrDefault(m => !Targets.IsGenerated(m));
                if (method == null) continue;
                if (!newName.EndsWith("Command", StringComparison.Ordinal) || newName.Length == 7)
                    return (null, newName, $"The command comes from the [RelayCommand] method '{method.Name}', so its new name must end with 'Command' (for example '{baseName}Command').");
                return (method, prefix + newName[..^7] + suffix, null);
            }
        }

        var camel = char.ToLowerInvariant(p.Name[0]) + p.Name[1..];
        foreach (var prefix in new[] { "_", "m_", "" })
        {
            var field = type.GetMembers(prefix + camel).OfType<IFieldSymbol>().FirstOrDefault(f => !Targets.IsGenerated(f));
            if (field != null) return (field, prefix + char.ToLowerInvariant(newName[0]) + newName[1..], null);
        }
        return (null, newName, null);
    }
}
