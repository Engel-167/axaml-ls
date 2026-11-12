using System.Collections.Concurrent;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace AxamlLs.Workspace;

/// <summary>
/// Resource keys of compiled themes and libraries (Avalonia.Themes.Fluent, Simple, third-party themes).
/// Their AXAML is compiled to IL and the XAML text is not shipped, but every key survives as a string literal
/// next to the call that registers or uses it:
///   ldstr "SystemControlBackgroundAccentBrush" … call ResourceDictionary.AddDeferred
///   ldstr "SystemAccentColor"                  … newobj DynamicResourceExtension(object)
/// So the IL is scanned for string literals that feed those calls. Results are cached per assembly file.
/// </summary>
public static class AssemblyResourceScanner
{
    private static readonly ConcurrentDictionary<string, (DateTime Stamp, List<ResourceEntry> Entries)> Cache = new(StringComparer.Ordinal);

    private static readonly Dictionary<int, OpCode> OpCodesByValue =
        typeof(OpCodes).GetFields().Select(f => (OpCode)f.GetValue(null)!).ToDictionary(o => (int)(ushort)o.Value);

    public static List<ResourceEntry> Scan(IEnumerable<string> assemblyPaths)
    {
        var result = new List<ResourceEntry>();
        foreach (var path in assemblyPaths)
        {
            try
            {
                var stamp = File.GetLastWriteTimeUtc(path);
                if (!Cache.TryGetValue(path, out var cached) || cached.Stamp != stamp)
                {
                    cached = (stamp, ScanAssembly(path));
                    Cache[path] = cached;
                }
                result.AddRange(cached.Entries);
            }
            catch (Exception ex)
            {
                Log.Debug($"Could not scan {path} for resources: {ex.Message}");
            }
        }
        return result;
    }

    private static List<ResourceEntry> ScanAssembly(string path)
    {
        using var pe = new PEReader(File.OpenRead(path));
        if (!pe.HasMetadata) return [];
        var md = pe.GetMetadataReader();
        // Only assemblies with compiled XAML (they reference Avalonia's XAML runtime helpers).
        var hasXaml = md.TypeReferences.Any(h => md.GetString(md.GetTypeReference(h).Name) == "XamlIlRuntimeHelpers");
        if (!hasXaml) return [];

        var assemblyName = md.GetString(md.GetAssemblyDefinition().Name);
        var keys = new Dictionary<string, bool>(StringComparer.Ordinal); // key → declared (true) or only referenced
        foreach (var handle in md.MethodDefinitions)
        {
            var method = md.GetMethodDefinition(handle);
            if (method.RelativeVirtualAddress == 0) continue;
            var il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILReader();
            string? lastString = null;
            var distance = int.MaxValue;
            while (il.RemainingBytes > 0)
            {
                int op = il.ReadByte();
                if (op == 0xFE) op = 0xFE00 | il.ReadByte();
                if (distance != int.MaxValue) distance++;
                if (op == 0x72) // ldstr
                {
                    lastString = md.GetUserString(MetadataTokens.UserStringHandle(il.ReadInt32()));
                    distance = 0;
                    continue;
                }
                if (op is 0x28 or 0x6F or 0x73) // call, callvirt, newobj
                {
                    var token = il.ReadInt32();
                    if (lastString == null || distance > 6) continue;
                    var kind = Classify(md, MetadataTokens.EntityHandle(token));
                    if (kind != null && LooksLikeKey(lastString))
                    {
                        keys[lastString] = keys.GetValueOrDefault(lastString) || kind == true;
                        lastString = null;
                    }
                    continue;
                }
                SkipOperand(ref il, op);
            }
        }
        return keys.Keys.OrderBy(k => k, StringComparer.Ordinal)
            .Select(k => new ResourceEntry(k, GuessKind(k), "", 0, 0, assemblyName) { Assembly = assemblyName })
            .ToList();
    }

    /// <summary>true = the call declares a resource, false = it uses one, null = unrelated.</summary>
    private static bool? Classify(MetadataReader md, EntityHandle handle)
    {
        if (handle.Kind == HandleKind.MethodSpecification) handle = md.GetMethodSpecification((MethodSpecificationHandle)handle).Method;
        string name, parent;
        switch (handle.Kind)
        {
            case HandleKind.MemberReference:
                var mr = md.GetMemberReference((MemberReferenceHandle)handle);
                name = md.GetString(mr.Name);
                parent = TypeName(md, mr.Parent);
                break;
            case HandleKind.MethodDefinition:
                var mdef = md.GetMethodDefinition((MethodDefinitionHandle)handle);
                name = md.GetString(mdef.Name);
                parent = md.GetString(md.GetTypeDefinition(mdef.GetDeclaringType()).Name);
                break;
            default:
                return null;
        }
        return (name, parent) switch
        {
            ("AddDeferred", _) => true,
            ("Add", "ResourceDictionary" or "IResourceDictionary" or "IDictionary`2" or "Dictionary`2") => true,
            ("set_Item", "ResourceDictionary" or "IResourceDictionary" or "IDictionary`2") => true,
            (".ctor", "StaticResourceExtension" or "DynamicResourceExtension") => false,
            ("set_ResourceKey", _) => false,
            _ => null,
        };
    }

    private static string TypeName(MetadataReader md, EntityHandle parent) => parent.Kind switch
    {
        HandleKind.TypeReference => md.GetString(md.GetTypeReference((TypeReferenceHandle)parent).Name),
        HandleKind.TypeDefinition => md.GetString(md.GetTypeDefinition((TypeDefinitionHandle)parent).Name),
        HandleKind.TypeSpecification => GenericName(md, (TypeSpecificationHandle)parent),
        _ => "",
    };

    private static string GenericName(MetadataReader md, TypeSpecificationHandle spec)
    {
        // The generic type definition of e.g. IDictionary<object, object>.
        var blob = md.GetBlobReader(md.GetTypeSpecification(spec).Signature);
        if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance) return "";
        blob.ReadSignatureTypeCode();
        var handle = blob.ReadTypeHandle();
        return TypeName(md, handle);
    }

    private static bool LooksLikeKey(string s) =>
        s.Length is > 1 and < 120 && (char.IsLetter(s[0]) || s[0] == '_') && s.All(c => char.IsLetterOrDigit(c) || c is '_' or '.' or '-');

    /// <summary>Fluent and Simple name keys after their value type; good enough to rank completions.</summary>
    public static string GuessKind(string key)
    {
        foreach (var suffix in new[] { "Brush", "Color", "Thickness", "CornerRadius", "FontSize", "FontWeight", "FontFamily", "Opacity", "Geometry", "Glyph", "Duration" })
        {
            if (key.EndsWith(suffix, StringComparison.Ordinal)) return suffix;
        }
        return "resource";
    }

    private static void SkipOperand(ref BlobReader il, int op)
    {
        if (!OpCodesByValue.TryGetValue(op, out var code)) return;
        switch (code.OperandType)
        {
            case OperandType.InlineNone:
                break;
            case OperandType.ShortInlineBrTarget:
            case OperandType.ShortInlineI:
            case OperandType.ShortInlineVar:
                il.Offset += 1;
                break;
            case OperandType.InlineVar:
                il.Offset += 2;
                break;
            case OperandType.InlineI8:
            case OperandType.InlineR:
                il.Offset += 8;
                break;
            case OperandType.InlineSwitch:
                var n = il.ReadInt32();
                il.Offset += n * 4;
                break;
            default:
                il.Offset += 4;
                break;
        }
    }
}
