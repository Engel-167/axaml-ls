using System.Collections.Frozen;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace AxamlLs.Workspace;

/// <summary>
/// The public top-level types of a project's referenced assemblies, by simple name: <c>"Button"</c> →
/// <c>["Avalonia.Controls.Button"]</c>. Read from the DLLs' metadata, not from Roslyn symbols, so it holds names only and
/// can be shared by snapshots of different compilations (symbols of one compilation must not be mixed with another's).
/// One instance per reference set: a <see cref="ProjectSnapshot"/> passes it on to the next one while the referenced
/// files and their timestamps are the same.
/// </summary>
public sealed class LibraryTypeNames
{
    private readonly FrozenDictionary<string, string[]> _byName;

    private LibraryTypeNames(FrozenDictionary<string, string[]> byName) => _byName = byName;

    /// <summary>The metadata names (<c>Namespace.Name`arity</c>) of the public types with this simple name.</summary>
    public IReadOnlyList<string> Named(string simpleName) => _byName.TryGetValue(simpleName, out var names) ? names : [];

    /// <summary>Reads every DLL; one that cannot be read is skipped (its types are then not offered).</summary>
    /// <param name="dllPaths">The referenced assemblies.</param>
    public static LibraryTypeNames Scan(IEnumerable<string> dllPaths)
    {
        var byName = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var path in dllPaths)
        {
            try
            {
                ScanAssembly(path, byName);
            }
            catch (Exception ex) when (ex is IOException or BadImageFormatException or InvalidOperationException or UnauthorizedAccessException)
            {
                Log.Debug($"Skipped the types of {Path.GetFileName(path)}: {ex.Message}");
            }
        }
        return new LibraryTypeNames(byName.ToFrozenDictionary(p => p.Key, p => p.Value.Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal));
    }

    private static void ScanAssembly(string path, Dictionary<string, List<string>> byName)
    {
        using var pe = new PEReader(File.OpenRead(path));
        if (!pe.HasMetadata) return;
        var md = pe.GetMetadataReader();
        foreach (var handle in md.TypeDefinitions)
        {
            var type = md.GetTypeDefinition(handle);
            if (!type.GetDeclaringType().IsNil || (type.Attributes & TypeAttributes.VisibilityMask) != TypeAttributes.Public) continue;
            var ns = md.GetString(type.Namespace);
            if (ns.Length == 0 || IsFrameworkNamespace(ns)) continue;
            var metadataName = md.GetString(type.Name);
            if (metadataName.Length == 0 || metadataName[0] == '<') continue;
            var tick = metadataName.IndexOf('`');
            var simple = tick < 0 ? metadataName : metadataName[..tick];
            if (!byName.TryGetValue(simple, out var list)) byName[simple] = list = [];
            list.Add($"{ns}.{metadataName}");
        }
    }

    /// <summary>System.* and Microsoft.*: never what an "import this type" fix should offer.</summary>
    internal static bool IsFrameworkNamespace(string ns) =>
        ns is "System" or "Microsoft" || ns.StartsWith("System.", StringComparison.Ordinal) || ns.StartsWith("Microsoft.", StringComparison.Ordinal);
}
