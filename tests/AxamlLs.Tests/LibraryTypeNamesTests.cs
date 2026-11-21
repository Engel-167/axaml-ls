using AxamlLs.Workspace;
using Microsoft.CodeAnalysis;

namespace AxamlLs.Tests;

/// <summary>Type lookup by simple name without walking every referenced namespace (performance plan P4).</summary>
[Collection("sample")]
public class LibraryTypeNamesTests(SampleProjectFixture sample)
{
    [Fact]
    public void Snapshots_with_the_same_references_share_the_library_names()
    {
        var p = sample.Project;
        var next = new ProjectSnapshot(p.ProjectPath, p.Compilation, p.CompiledBindingsDefault, previous: p);
        Assert.Same(p.LibraryTypes, next.LibraryTypes);
        var fresh = new ProjectSnapshot(p.ProjectPath, p.Compilation, p.CompiledBindingsDefault);
        Assert.NotSame(p.LibraryTypes, fresh.LibraryTypes);
        Assert.Contains("Avalonia.Controls.Button", p.LibraryTypes.Named("Button"));
    }

    /// <summary>
    /// Differential: for every simple name, TypesNamed finds the same types in the same order as walking every
    /// namespace of the compilation (what the server did before).
    /// </summary>
    [Fact]
    public void Types_named_equals_the_full_namespace_walk()
    {
        var p = sample.Project;
        var walk = Walk(p.Compilation);
        Assert.True(walk.Count > 1000, $"only {walk.Count} names");
        foreach (var group in walk)
        {
            var expected = group.Select(Describe).ToList();
            var actual = p.TypesNamed(group.Key).Select(Describe).ToList();
            Assert.True(expected.SequenceEqual(actual), $"{group.Key}: expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}]");
        }
        Assert.Equal(
            walk.SelectMany(g => g).Where(t => SymbolEqualityComparer.Default.Equals(t.ContainingAssembly, p.Compilation.Assembly)).Select(Describe).Order(),
            p.OwnTypes.Select(Describe).Order());
    }

    private static string Describe(INamedTypeSymbol t) => $"{t.ToDisplayString()} ({t.ContainingAssembly.Name})";

    /// <summary>The previous implementation, kept as the oracle.</summary>
    private static ILookup<string, INamedTypeSymbol> Walk(Compilation compilation)
    {
        var found = new List<INamedTypeSymbol>();
        var pending = new Stack<INamespaceSymbol>();
        pending.Push(compilation.GlobalNamespace);
        while (pending.Count > 0)
        {
            var ns = pending.Pop();
            foreach (var child in ns.GetNamespaceMembers())
            {
                if (ns.IsGlobalNamespace && child.Name is "System" or "Microsoft") continue;
                pending.Push(child);
            }
            foreach (var t in ns.GetTypeMembers())
            {
                if (t.IsImplicitlyDeclared || t.Name.Length == 0 || t.Name.StartsWith('<')) continue;
                var own = SymbolEqualityComparer.Default.Equals(t.ContainingAssembly, compilation.Assembly);
                if (t.DeclaredAccessibility == Accessibility.Public || own) found.Add(t);
            }
        }
        return found
            .OrderBy(t => SymbolEqualityComparer.Default.Equals(t.ContainingAssembly, compilation.Assembly) ? 0 : 1)
            .ThenBy(t => t.ContainingNamespace.ToDisplayString(), StringComparer.Ordinal)
            .ToLookup(t => t.Name, StringComparer.Ordinal);
    }
}
