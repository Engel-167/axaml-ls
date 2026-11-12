using AxamlLs.Xaml;
using Microsoft.CodeAnalysis;

namespace AxamlLs.Analysis;

public enum Severity
{
    Error = 1,
    Warning = 2,
    Information = 3,
    Hint = 4,
}

public sealed record XamlDiagnostic(int Start, int End, Severity Severity, string Code, string Message)
{
    /// <summary>What a quick fix needs to know that the message only says in words: the type searched (AXN0101) or the XML namespace (AXN0201).</summary>
    public object? Data { get; init; }
}

/// <summary>Diagnostic codes. AXN01xx = bindings, AXN02xx = types/properties, AXN03xx = code-behind, AXN04xx = styles, AXN05xx = files.</summary>
public static class Codes
{
    public const string XmlError = "AXN0001";
    public const string MarkupSyntax = "AXN0010";
    public const string PathSyntax = "AXN0011";
    public const string MissingDataType = "AXN0100";
    public const string UnknownBindingMember = "AXN0101";
    public const string UnknownElementName = "AXN0103";
    public const string UnknownTemplateProperty = "AXN0104";
    public const string UnknownType = "AXN0201";
    public const string UnknownProperty = "AXN0202";
    public const string UnknownMarkupExtension = "AXN0203";
    public const string UnknownStaticMember = "AXN0204";
    public const string UnknownResource = "AXN0205";
    public const string UnknownClass = "AXN0301";
    public const string UnknownHandler = "AXN0302";
    public const string UnusedClass = "AXN0401";
    public const string UnknownSelectorType = "AXN0402";
    public const string UnknownPseudoClass = "AXN0403";
    public const string UnknownTemplatePart = "AXN0404";
    public const string UnknownSelectorProperty = "AXN0405";
    public const string SelectorSyntax = "AXN0406";
    public const string MissingAsset = "AXN0501";
}

/// <summary>Something in the document that refers to a symbol (for hover and go-to-definition).</summary>
public sealed class SymbolRef
{
    public required int Start { get; init; }
    public required int End { get; init; }
    public ISymbol? Symbol { get; init; }
    /// <summary>The type of the value at this point (for bindings: the type after this segment).</summary>
    public ITypeSymbol? Type { get; init; }
    /// <summary>An element in this document that the reference points to (#name, ElementName).</summary>
    public XmlElement? TargetElement { get; init; }
    public string? Note { get; init; }
}

/// <summary>What a binding means at some point in the tree.</summary>
public sealed record Scope(ITypeSymbol? DataType, bool DataTypeRequired, bool Compiled, ITypeSymbol? TemplateTarget)
{
    public static Scope Root(bool compiled) => new(null, true, compiled, null);
}

public sealed class ElementInfo
{
    public INamedTypeSymbol? Type { get; set; }
    public bool IsPropertyElement { get; set; }
    public INamedTypeSymbol? OwnerType { get; set; }
    public string? PropertyName { get; set; }
    public ISymbol? Property { get; set; }
    /// <summary>In an ignorable namespace (d:, mc:Ignorable) or under an undeclared prefix: not analyzed.</summary>
    public bool Ignored { get; set; }
    public Scope? Scope { get; set; }
}

public sealed class AnalysisResult
{
    public required XmlDoc Doc { get; init; }
    public Workspace.ProjectSnapshot? Project { get; init; }
    public List<XamlDiagnostic> Diagnostics { get; } = [];
    public List<SymbolRef> Refs { get; } = [];
    public Dictionary<XmlElement, ElementInfo> Elements { get; } = [];
    public Dictionary<string, XmlElement> Names { get; } = new(StringComparer.Ordinal);
    public INamedTypeSymbol? ClassType { get; set; }

    /// <summary>The innermost reference covering an offset.</summary>
    public SymbolRef? RefAt(int offset)
    {
        SymbolRef? best = null;
        foreach (var r in Refs)
        {
            if (r.Start <= offset && offset <= r.End && (best == null || r.End - r.Start < best.End - best.Start)) best = r;
        }
        return best;
    }
}
