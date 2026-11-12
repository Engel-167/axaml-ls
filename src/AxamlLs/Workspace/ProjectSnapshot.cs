using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;

namespace AxamlLs.Workspace;

public static class XamlNamespaces
{
    public const string Avalonia = "https://github.com/avaloniaui";
    public const string X = "http://schemas.microsoft.com/winfx/2006/xaml";
    public const string MarkupCompatibility = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    public const string Blend = "http://schemas.microsoft.com/expression/blend/2008";
}

/// <summary>An immutable view of a C# project's types, as seen by XAML.</summary>
public sealed class ProjectSnapshot
{
    private readonly ConcurrentDictionary<(string, string), INamedTypeSymbol?> _typeCache = new();

    public string ProjectPath { get; }
    public Compilation Compilation { get; }
    public bool CompiledBindingsDefault { get; }
    /// <summary>XML namespace URI → CLR namespaces, from [XmlnsDefinition] attributes.</summary>
    public IReadOnlyDictionary<string, List<string>> Xmlns { get; }

    public ProjectSnapshot(string projectPath, Compilation compilation, bool compiledBindingsDefault)
    {
        ProjectPath = projectPath;
        Compilation = compilation;
        CompiledBindingsDefault = compiledBindingsDefault;
        Xmlns = ReadXmlnsDefinitions(compilation);
    }

    public bool HasAvalonia => Xmlns.ContainsKey(XamlNamespaces.Avalonia);

    private List<ResourceEntry>? _libraryResources;

    /// <summary>Resource keys declared or used by compiled themes and libraries the project references.</summary>
    public IReadOnlyList<ResourceEntry> LibraryResources => _libraryResources ??= AssemblyResourceScanner.Scan(
        Compilation.References.OfType<PortableExecutableReference>().Select(r => r.FilePath).OfType<string>()
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}packs{Path.DirectorySeparatorChar}", StringComparison.Ordinal)));

    private Dictionary<string, ThemeFacts>? _themes;

    /// <summary>What compiled themes (Fluent, Simple, …) say about each control type: template parts, classes, pseudo-classes.</summary>
    public IReadOnlyDictionary<string, ThemeFacts> LibraryThemes => _themes ??= ThemeScanner.Scan(
        Compilation.References.OfType<PortableExecutableReference>().Select(r => r.FilePath).OfType<string>()
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}packs{Path.DirectorySeparatorChar}", StringComparison.Ordinal)));

    private ILookup<string, INamedTypeSymbol>? _typesByName;

    /// <summary>
    /// Top-level types by simple name, from the project and every reference (for "import this type" fixes): public types,
    /// plus the project's own internal ones; the System namespaces are skipped. The project's types come first.
    /// </summary>
    public ILookup<string, INamedTypeSymbol> TypesByName => _typesByName ??= BuildTypesByName();

    private ILookup<string, INamedTypeSymbol> BuildTypesByName()
    {
        var found = new List<INamedTypeSymbol>();
        var pending = new Stack<INamespaceSymbol>();
        pending.Push(Compilation.GlobalNamespace);
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
                var own = SymbolEqualityComparer.Default.Equals(t.ContainingAssembly, Compilation.Assembly);
                if (t.DeclaredAccessibility == Accessibility.Public || own) found.Add(t);
            }
        }
        return found
            .OrderBy(t => SymbolEqualityComparer.Default.Equals(t.ContainingAssembly, Compilation.Assembly) ? 0 : 1)
            .ThenBy(t => t.ContainingNamespace.ToDisplayString(), StringComparer.Ordinal)
            .ToLookup(t => t.Name, StringComparer.Ordinal);
    }

    /// <summary>True when an unresolved type in this XML namespace is a real error (we know the vocabulary).</summary>
    public bool IsCheckable(string? ns) =>
        ns != null && HasAvalonia &&
        (ns.StartsWith("using:", StringComparison.Ordinal) || ns.StartsWith("clr-namespace:", StringComparison.Ordinal) || Xmlns.ContainsKey(ns));

    public IEnumerable<string> ClrNamespacesFor(string? ns)
    {
        if (ns == null) yield break;
        if (ns.StartsWith("using:", StringComparison.Ordinal))
        {
            yield return ns[6..].Trim();
        }
        else if (ns.StartsWith("clr-namespace:", StringComparison.Ordinal))
        {
            var rest = ns[14..];
            var semi = rest.IndexOf(';');
            yield return (semi >= 0 ? rest[..semi] : rest).Trim();
        }
        else if (Xmlns.TryGetValue(ns, out var list))
        {
            foreach (var n in list) yield return n;
        }
    }

    public INamedTypeSymbol? ResolveType(string? ns, string name)
    {
        if (ns == null || name.Length == 0) return null;
        return _typeCache.GetOrAdd((ns, name), key => ResolveUncached(key.Item1, key.Item2));
    }

    private INamedTypeSymbol? ResolveUncached(string ns, string name)
    {
        if (ns == XamlNamespaces.X)
        {
            var special = name switch
            {
                "String" => SpecialType.System_String,
                "Int32" => SpecialType.System_Int32,
                "Int64" => SpecialType.System_Int64,
                "Int16" => SpecialType.System_Int16,
                "Byte" => SpecialType.System_Byte,
                "Double" => SpecialType.System_Double,
                "Single" => SpecialType.System_Single,
                "Decimal" => SpecialType.System_Decimal,
                "Boolean" => SpecialType.System_Boolean,
                "Char" => SpecialType.System_Char,
                "Object" => SpecialType.System_Object,
                _ => SpecialType.None,
            };
            if (special != SpecialType.None) return Compilation.GetSpecialType(special);
            return name switch
            {
                "TimeSpan" => Compilation.GetTypeByMetadataName("System.TimeSpan"),
                "Uri" => Compilation.GetTypeByMetadataName("System.Uri"),
                "Type" => Compilation.GetTypeByMetadataName("System.Type"),
                _ => null,
            };
        }

        var metadataName = name.Replace('.', '+');
        foreach (var clrNs in ClrNamespacesFor(ns))
        {
            var full = clrNs.Length == 0 ? metadataName : clrNs + "." + metadataName;
            var t = Compilation.GetTypeByMetadataName(full);
            if (t != null) return t;
            // Ambiguous (same full name in several assemblies): prefer the project's own or the first public one.
            var candidates = Compilation.GetTypesByMetadataName(full);
            if (candidates.Length > 0)
            {
                return candidates.FirstOrDefault(c => SymbolEqualityComparer.Default.Equals(c.ContainingAssembly, Compilation.Assembly))
                       ?? candidates.FirstOrDefault(c => c.DeclaredAccessibility == Accessibility.Public)
                       ?? candidates[0];
            }
        }
        return null;
    }

    /// <summary>All public types visible in an XML namespace (for completion).</summary>
    public IEnumerable<INamedTypeSymbol> TypesIn(string? ns)
    {
        var seen = new HashSet<string>();
        foreach (var clrNs in ClrNamespacesFor(ns))
        {
            foreach (var nsSymbol in FindNamespaces(clrNs))
            {
                foreach (var t in nsSymbol.GetTypeMembers())
                {
                    if (t.IsImplicitlyDeclared || t.Name.Length == 0 || t.Name.StartsWith('<')) continue;
                    var sameAssembly = SymbolEqualityComparer.Default.Equals(t.ContainingAssembly, Compilation.Assembly);
                    if (t.DeclaredAccessibility != Accessibility.Public && !sameAssembly) continue;
                    if (seen.Add(t.Name)) yield return t;
                }
            }
        }
    }

    private IEnumerable<INamespaceSymbol> FindNamespaces(string clrNs)
    {
        var parts = clrNs.Split('.', StringSplitOptions.RemoveEmptyEntries);
        // The merged global namespace includes every referenced assembly.
        INamespaceSymbol? current = Compilation.GlobalNamespace;
        foreach (var part in parts)
        {
            current = current?.GetNamespaceMembers().FirstOrDefault(n => n.Name == part);
            if (current == null) yield break;
        }
        if (current != null) yield return current;
    }

    private static Dictionary<string, List<string>> ReadXmlnsDefinitions(Compilation compilation)
    {
        var map = new Dictionary<string, List<string>>();
        var assemblies = new List<IAssemblySymbol> { compilation.Assembly };
        assemblies.AddRange(compilation.SourceModule.ReferencedAssemblySymbols);
        foreach (var asm in assemblies)
        {
            foreach (var attr in asm.GetAttributes())
            {
                if (attr.AttributeClass?.Name != "XmlnsDefinitionAttribute" || attr.ConstructorArguments.Length < 2) continue;
                if (attr.ConstructorArguments[0].Value is not string uri || attr.ConstructorArguments[1].Value is not string clr) continue;
                if (!map.TryGetValue(uri, out var list)) map[uri] = list = [];
                if (!list.Contains(clr)) list.Add(clr);
            }
        }
        return map;
    }
}
