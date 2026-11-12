using Microsoft.CodeAnalysis;

namespace AxamlLs.Analysis;

/// <summary>Member lookups with Avalonia's rules (CLR properties, AvaloniaProperty fields, attached properties, routed events).</summary>
public static class Members
{
    public static IEnumerable<ITypeSymbol> SelfAndBases(ITypeSymbol type)
    {
        for (var t = type; t != null; t = t.BaseType) yield return t;
    }

    /// <summary>The type, its bases and all its interfaces.</summary>
    public static IEnumerable<ITypeSymbol> Hierarchy(ITypeSymbol type)
    {
        foreach (var t in SelfAndBases(type)) yield return t;
        foreach (var i in type.AllInterfaces) yield return i;
    }

    /// <summary>True when some base type failed to resolve, so a missing member might be there.</summary>
    public static bool HasUnknownBase(ITypeSymbol type)
    {
        foreach (var t in SelfAndBases(type))
        {
            if (t.TypeKind == TypeKind.Error) return true;
        }
        return type.AllInterfaces.Any(i => i.TypeKind == TypeKind.Error);
    }

    /// <summary>Types we cannot see into: object, dynamic, type parameters, unresolved types.</summary>
    public static bool IsOpaque(ITypeSymbol? type) =>
        type == null || type.TypeKind is TypeKind.Error or TypeKind.TypeParameter or TypeKind.Dynamic or TypeKind.Pointer ||
        type.SpecialType == SpecialType.System_Object;

    public static ITypeSymbol UnwrapNullable(ITypeSymbol type) =>
        type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } n ? n.TypeArguments[0] : type;

    public static bool IsSameOrDerived(ITypeSymbol type, ITypeSymbol baseType) =>
        Hierarchy(type).Any(t => SymbolEqualityComparer.Default.Equals(t.OriginalDefinition, baseType.OriginalDefinition));

    public static bool InheritsByName(ITypeSymbol type, params string[] names) =>
        Hierarchy(type).Any(t => names.Contains(t.Name));

    public static bool IsAvaloniaProperty(ITypeSymbol type) =>
        SelfAndBases(type).Any(t => t.Name == "AvaloniaProperty" && t.ContainingNamespace?.ToDisplayString() == "Avalonia");

    public static bool IsRoutedEvent(ITypeSymbol type) =>
        SelfAndBases(type).Any(t => t.Name == "RoutedEvent" && t.ContainingNamespace?.ToDisplayString() == "Avalonia.Interactivity");

    public static bool IsEvent(ISymbol symbol) =>
        symbol is IEventSymbol || (symbol is IFieldSymbol f && IsRoutedEvent(f.Type));

    /// <summary>A member that can be set from a XAML attribute on an instance of <paramref name="type"/>.</summary>
    public static ISymbol? FindSettable(ITypeSymbol type, string name)
    {
        foreach (var t in SelfAndBases(type))
        {
            foreach (var m in t.GetMembers(name))
            {
                if (m is IPropertySymbol { IsIndexer: false, IsStatic: false } or IEventSymbol { IsStatic: false }) return m;
            }
        }
        return FindAvaloniaPropertyField(type, name) ?? FindRoutedEventField(type, name);
    }

    /// <summary>An attached property (or attached routed event) defined on <paramref name="owner"/>.</summary>
    public static ISymbol? FindAttached(ITypeSymbol owner, string name)
    {
        var field = FindAvaloniaPropertyField(owner, name);
        if (field != null) return field;
        foreach (var t in SelfAndBases(owner))
        {
            foreach (var m in t.GetMembers("Set" + name))
            {
                if (m is IMethodSymbol { IsStatic: true, Parameters.Length: 2 }) return m;
            }
        }
        return FindRoutedEventField(owner, name);
    }

    public static IFieldSymbol? FindAvaloniaPropertyField(ITypeSymbol type, string name)
    {
        foreach (var t in SelfAndBases(type))
        {
            foreach (var m in t.GetMembers(name + "Property"))
            {
                if (m is IFieldSymbol { IsStatic: true } f && IsAvaloniaProperty(f.Type)) return f;
            }
        }
        return null;
    }

    private static IFieldSymbol? FindRoutedEventField(ITypeSymbol type, string name)
    {
        foreach (var t in SelfAndBases(type))
        {
            foreach (var m in t.GetMembers(name + "Event"))
            {
                if (m is IFieldSymbol { IsStatic: true } f && IsRoutedEvent(f.Type)) return f;
            }
        }
        return null;
    }

    /// <summary>A member a binding path segment can name: property, field, method (bound as a command), or AvaloniaProperty.</summary>
    public static ISymbol? FindBindable(ITypeSymbol type, string name)
    {
        foreach (var t in Hierarchy(type))
        {
            foreach (var m in t.GetMembers(name))
            {
                switch (m)
                {
                    case IPropertySymbol { IsIndexer: false }:
                    case IMethodSymbol { MethodKind: MethodKind.Ordinary }:
                    case IFieldSymbol { IsStatic: false }:
                        return m;
                }
            }
        }
        return FindAvaloniaPropertyField(type, name);
    }

    /// <summary>The type of the value a member produces or accepts.</summary>
    public static ITypeSymbol? ValueTypeOf(ISymbol? symbol)
    {
        switch (symbol)
        {
            case IPropertySymbol p:
                return p.Type;
            case IEventSymbol e:
                return e.Type;
            case IFieldSymbol f when IsAvaloniaProperty(f.Type):
                foreach (var t in SelfAndBases(f.Type))
                {
                    if (t is INamedTypeSymbol { Name: "AvaloniaProperty", IsGenericType: true } g) return g.TypeArguments[0];
                }
                return null;
            case IFieldSymbol f:
                return f.Type;
            case IMethodSymbol { IsStatic: true, Parameters.Length: 2 } m when m.Name.StartsWith("Set", StringComparison.Ordinal):
                return m.Parameters[1].Type;
            default:
                return null; // Methods bound as commands, etc.
        }
    }

    public static ITypeSymbol? IndexerType(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol array) return array.ElementType;
        foreach (var t in Hierarchy(type))
        {
            foreach (var m in t.GetMembers())
            {
                if (m is IPropertySymbol { IsIndexer: true } p) return p.Type;
            }
        }
        return null;
    }

    /// <summary>T for an IEnumerable&lt;T&gt; (used to infer a DataTemplate's type from ItemsSource).</summary>
    public static ITypeSymbol? ElementType(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol array) return array.ElementType;
        if (type.SpecialType == SpecialType.System_String) return null;
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Collections_Generic_IEnumerable_T } self) return self.TypeArguments[0];
        foreach (var i in type.AllInterfaces)
        {
            if (i.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T) return i.TypeArguments[0];
        }
        return null;
    }

    /// <summary>T for Task&lt;T&gt;, ValueTask&lt;T&gt; or IObservable&lt;T&gt; (the '^' operator).</summary>
    public static ITypeSymbol? StreamType(ITypeSymbol type)
    {
        foreach (var t in Hierarchy(type))
        {
            if (t is INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } g && g.Name is "Task" or "ValueTask" or "IObservable")
                return g.TypeArguments[0];
        }
        return null;
    }

    public static IEnumerable<IMethodSymbol> FindMethods(ITypeSymbol type, string name) =>
        SelfAndBases(type).SelectMany(t => t.GetMembers(name)).OfType<IMethodSymbol>().Where(m => m.MethodKind == MethodKind.Ordinary);

    public static string Display(ITypeSymbol? type) => type?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) ?? "?";

    public static string FullDisplay(ITypeSymbol? type) => type?.ToDisplayString() ?? "?";
}
