using System.Collections.Concurrent;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace AxamlLs.Workspace;

/// <summary>A named element in a control's template: Button → PART_ContentPresenter (Avalonia.Controls.Presenters.ContentPresenter).</summary>
public sealed record TemplatePart(string Name, string TypeName);

/// <summary>What the control themes of an assembly or project say about one control type.</summary>
public sealed class ThemeFacts
{
    public HashSet<TemplatePart> Parts { get; } = [];
    /// <summary>Classes its themes style (Button.accent), without the dot.</summary>
    public HashSet<string> Classes { get; } = new(StringComparer.Ordinal);
    /// <summary>Pseudo-classes its themes style (:pressed), with the colon.</summary>
    public HashSet<string> PseudoClasses { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Reads control themes compiled into IL (Fluent, Simple, third-party). The XAML text is not shipped, but the
/// compiled builders are regular:
///   ControlTheme builder: ldtoken Button → ControlTheme::set_TargetType … ldftn Build_2 … ControlTemplate::set_Content
///                         ldstr "accent" → call Selectors::Class (also ":pressed")
///   template builder:     newobj ContentPresenter::.ctor … BeginInit … ldstr "PART_ContentPresenter" → set_Name … EndInit
/// so each template builder method is tied to its control type and each set_Name to the element being initialized.
/// Keys are full type names (Avalonia.Controls.Button). Cached per assembly file.
/// </summary>
public static class ThemeScanner
{
    private static readonly ConcurrentDictionary<string, (DateTime Stamp, Dictionary<string, ThemeFacts> Facts)> Cache = new(StringComparer.Ordinal);

    private static readonly Dictionary<int, OpCode> OpCodesByValue =
        typeof(OpCodes).GetFields().Select(f => (OpCode)f.GetValue(null)!).ToDictionary(o => (int)(ushort)o.Value);

    public static Dictionary<string, ThemeFacts> Scan(IEnumerable<string> assemblyPaths)
    {
        var result = new Dictionary<string, ThemeFacts>(StringComparer.Ordinal);
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
                Merge(result, cached.Facts);
            }
            catch (Exception ex)
            {
                Log.Debug($"Could not scan {path} for control themes: {ex.Message}");
            }
        }
        return result;
    }

    public static void Merge(Dictionary<string, ThemeFacts> into, IReadOnlyDictionary<string, ThemeFacts> from)
    {
        foreach (var (type, facts) in from)
        {
            if (!into.TryGetValue(type, out var target)) into[type] = target = new ThemeFacts();
            target.Parts.UnionWith(facts.Parts);
            target.Classes.UnionWith(facts.Classes);
            target.PseudoClasses.UnionWith(facts.PseudoClasses);
        }
    }

    private readonly record struct Op(int Code, int Token, string? String);

    private static Dictionary<string, ThemeFacts> ScanAssembly(string path)
    {
        var facts = new Dictionary<string, ThemeFacts>(StringComparer.Ordinal);
        using var pe = new PEReader(File.OpenRead(path));
        if (!pe.HasMetadata) return facts;
        var md = pe.GetMetadataReader();
        if (!md.TypeReferences.Any(h => md.GetString(md.GetTypeReference(h).Name) == "XamlIlRuntimeHelpers")) return facts;

        ThemeFacts For(string type) => facts.TryGetValue(type, out var f) ? f : facts[type] = new ThemeFacts();

        // Pass 1: control themes → their target type, template builders, classes.
        var builders = new Dictionary<MethodDefinitionHandle, string>();
        foreach (var handle in md.MethodDefinitions)
        {
            var ops = Read(pe, md, handle);
            if (ops == null) continue;
            string? lastType = null, target = null, lastString = null;
            MethodDefinitionHandle? pendingBuilder = null;
            foreach (var op in ops)
            {
                switch (op.Code)
                {
                    case 0xD0: // ldtoken
                        lastType = TypeFullName(md, MetadataTokens.EntityHandle(op.Token));
                        break;
                    case 0x72: // ldstr
                        lastString = op.String;
                        break;
                    case 0xFE06: // ldftn
                        var fn = MetadataTokens.EntityHandle(op.Token);
                        if (fn.Kind == HandleKind.MethodDefinition) pendingBuilder = (MethodDefinitionHandle)fn;
                        break;
                    case 0x28 or 0x6F: // call, callvirt
                        var (name, parent) = MemberName(md, MetadataTokens.EntityHandle(op.Token));
                        if (name == "set_TargetType") target = lastType;
                        else if (name == "set_Content" && parent == "ControlTemplate" && pendingBuilder is { } b && target != null) builders[b] = target;
                        else if (name == "Class" && parent == "Selectors" && target != null && lastString is { Length: > 0 } cls)
                        {
                            if (cls[0] == ':') For(target).PseudoClasses.Add(cls);
                            else For(target).Classes.Add(cls);
                        }
                        break;
                }
            }
        }

        // Pass 2: the names set inside each template builder, with the type of the element being initialized.
        foreach (var (builder, target) in builders)
        {
            var ops = Read(pe, md, builder);
            if (ops == null) continue;
            var initializing = new Stack<string>();
            string? lastNew = null, lastString = null;
            foreach (var op in ops)
            {
                switch (op.Code)
                {
                    case 0x72:
                        lastString = op.String;
                        break;
                    case 0x73: // newobj
                        lastNew = MemberParentFullName(md, MetadataTokens.EntityHandle(op.Token));
                        break;
                    case 0x28 or 0x6F:
                        var (name, _) = MemberName(md, MetadataTokens.EntityHandle(op.Token));
                        if (name == "BeginInit" && lastNew != null) initializing.Push(lastNew);
                        else if (name == "EndInit" && initializing.Count > 0) initializing.Pop();
                        else if (name == "set_Name" && lastString != null && (initializing.Count > 0 ? initializing.Peek() : lastNew) is { } owner)
                            For(target).Parts.Add(new TemplatePart(lastString, owner));
                        break;
                }
            }
        }
        return facts;
    }

    private static List<Op>? Read(PEReader pe, MetadataReader md, MethodDefinitionHandle handle)
    {
        var method = md.GetMethodDefinition(handle);
        if (method.RelativeVirtualAddress == 0) return null;
        var il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILReader();
        var ops = new List<Op>();
        while (il.RemainingBytes > 0)
        {
            int code = il.ReadByte();
            if (code == 0xFE) code = 0xFE00 | il.ReadByte();
            if (!OpCodesByValue.TryGetValue(code, out var opCode)) return ops;
            switch (opCode.OperandType)
            {
                case OperandType.InlineNone:
                    ops.Add(new(code, 0, null));
                    break;
                case OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar:
                    il.Offset += 1;
                    break;
                case OperandType.InlineVar:
                    il.Offset += 2;
                    break;
                case OperandType.InlineI8 or OperandType.InlineR:
                    il.Offset += 8;
                    break;
                case OperandType.InlineSwitch:
                    il.Offset += il.ReadInt32() * 4;
                    break;
                case OperandType.InlineString:
                    ops.Add(new(code, 0, md.GetUserString(MetadataTokens.UserStringHandle(il.ReadInt32()))));
                    break;
                default:
                    ops.Add(new(code, il.ReadInt32(), null));
                    break;
            }
        }
        return ops;
    }

    private static (string Name, string Parent) MemberName(MetadataReader md, EntityHandle handle)
    {
        if (handle.Kind == HandleKind.MethodSpecification) handle = md.GetMethodSpecification((MethodSpecificationHandle)handle).Method;
        switch (handle.Kind)
        {
            case HandleKind.MemberReference:
                var mr = md.GetMemberReference((MemberReferenceHandle)handle);
                return (md.GetString(mr.Name), SimpleName(md, mr.Parent));
            case HandleKind.MethodDefinition:
                var def = md.GetMethodDefinition((MethodDefinitionHandle)handle);
                return (md.GetString(def.Name), md.GetString(md.GetTypeDefinition(def.GetDeclaringType()).Name));
            default:
                return ("", "");
        }
    }

    private static string? MemberParentFullName(MetadataReader md, EntityHandle handle)
    {
        switch (handle.Kind)
        {
            case HandleKind.MemberReference:
                return TypeFullName(md, md.GetMemberReference((MemberReferenceHandle)handle).Parent);
            case HandleKind.MethodDefinition:
                return TypeFullName(md, md.GetMethodDefinition((MethodDefinitionHandle)handle).GetDeclaringType());
            default:
                return null;
        }
    }

    private static string SimpleName(MetadataReader md, EntityHandle handle) => handle.Kind switch
    {
        HandleKind.TypeReference => md.GetString(md.GetTypeReference((TypeReferenceHandle)handle).Name),
        HandleKind.TypeDefinition => md.GetString(md.GetTypeDefinition((TypeDefinitionHandle)handle).Name),
        _ => "",
    };

    private static string? TypeFullName(MetadataReader md, EntityHandle handle)
    {
        switch (handle.Kind)
        {
            case HandleKind.TypeReference:
                var r = md.GetTypeReference((TypeReferenceHandle)handle);
                var ns = md.GetString(r.Namespace);
                if (ns.Length == 0 && r.ResolutionScope.Kind == HandleKind.TypeReference) // nested type
                    return TypeFullName(md, (TypeReferenceHandle)r.ResolutionScope) + "+" + md.GetString(r.Name);
                return ns.Length == 0 ? md.GetString(r.Name) : ns + "." + md.GetString(r.Name);
            case HandleKind.TypeDefinition:
                var d = md.GetTypeDefinition((TypeDefinitionHandle)handle);
                var dns = md.GetString(d.Namespace);
                return dns.Length == 0 ? md.GetString(d.Name) : dns + "." + md.GetString(d.Name);
            default:
                return null;
        }
    }
}
