using System.Text.RegularExpressions;
using AxamlLs.Lsp;
using AxamlLs.Workspace;
using AxamlLs.Xaml;
using Microsoft.CodeAnalysis;

namespace AxamlLs.Analysis.Refactorings;

/// <summary>
/// "Extract to resource": a literal value becomes a keyed resource in the nearest ancestor's <c>Resources</c> (or a new
/// <c>&lt;Root.Resources&gt;</c>), and the value becomes <c>{StaticResource Key}</c>. Offered only when the property's
/// type is one a resource can be written for (P7): a brush from a color, Color, Thickness, CornerRadius, FontFamily,
/// double, int, bool or string. The key comes from the element and property (<c>ButtonBackgroundBrush</c>), made unique.
/// </summary>
internal sealed partial class ExtractToResource : IRefactoring
{
    public string Id => "extractToResource";

    public string Kind => "refactor.extract";

    public string? TitleAt(XamlFile file, int start, int end, RefactoringSettings settings) => Find(file, start) != null ? "Extract to resource" : null;

    public IReadOnlyList<FileEdit> Edits(XamlFile file, int start, int end, RefactoringSettings settings)
    {
        if (Find(file, start) is not var (el, attr, kind, property)) return [];
        var root = file.Analyzer.Result.Doc.Root!;
        var x = RefactoringText.PrefixOf(el, XamlNamespaces.X)!;
        var avalonia = RefactoringText.PrefixOf(el, XamlNamespaces.Avalonia);
        var key = UniqueKey(file, (el.LocalName == "Setter" ? "" : el.LocalName) + property + (kind == "Brush" && !property.EndsWith("Brush", StringComparison.Ordinal) ? "Brush" : ""));
        var raw = file.Text[attr.ValueStart..attr.ValueEnd].Trim();
        var keyAttr = $"{x}:Key=\"{key}\"";
        var resource = kind switch
        {
            "Brush" => $"<{RefactoringText.Qualified(avalonia, "SolidColorBrush")} {keyAttr} Color=\"{raw}\" />",
            "Double" or "Int32" or "Boolean" or "String" => $"<{x}:{kind} {keyAttr}>{raw}</{x}:{kind}>",
            _ => $"<{RefactoringText.Qualified(avalonia, kind)} {keyAttr}>{raw}</{RefactoringText.Qualified(avalonia, kind)}>",
        };

        var insert = Container(root, el) is { } container
            ? RefactoringText.AppendChild(file.Path, file.Text, container, _ => resource)
            : RefactoringText.NewPropertyElement(file.Path, file.Text, root, "Resources", _ => resource);
        if (insert == null) return [];
        return [new FileEdit(file.Path, attr.ValueStart, attr.ValueEnd, $"{{StaticResource {key}}}"), insert];
    }

    /// <summary>The attribute at the offset, what kind of resource its value makes, and the property's name.</summary>
    private static (XmlElement El, XmlAttr Attr, string Kind, string Property)? Find(XamlFile file, int offset)
    {
        var el = RefactoringText.StartTagAt(file.Analyzer.Result.Doc, offset);
        if (el?.Parent == null || !RefactoringText.Complete(el)) return null;
        var attr = el.Attributes.FirstOrDefault(a => a.ValueStart <= offset && offset <= a.ValueEnd);
        if (attr == null || attr.Prefix != null || attr.Value.Trim().Length == 0 || MarkupExtension.IsMarkup(attr.Value)) return null;
        if (RefactoringText.PrefixOf(el, XamlNamespaces.X) == null || RefactoringText.PrefixOf(el, XamlNamespaces.Avalonia) == null) return null;
        if (el.LocalName == "Setter" && attr.LocalName != "Value") return null;
        if (attr.Name.Contains('.') || PropertyLookup.TargetProperty(file.Analyzer, el, attr) is not { } symbol || ValueType(symbol) is not { } type) return null;
        var kind = KindOf(type, attr.Value.Trim());
        if (kind == null) return null;
        var property = el.LocalName == "Setter" ? el.Attr("Property")?.Value.Trim() ?? "" : attr.LocalName;
        return (el, attr, kind, property[(property.LastIndexOf('.') + 1)..]);
    }

    private static ITypeSymbol? ValueType(ISymbol symbol)
    {
        var type = symbol switch
        {
            IPropertySymbol p => p.Type,
            IMethodSymbol { Parameters.Length: 2 } m => m.Parameters[1].Type,
            IFieldSymbol { Type: INamedTypeSymbol { TypeArguments.Length: > 0 } t } => t.TypeArguments[^1],
            _ => null,
        };
        return type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } n ? n.TypeArguments[0] : type;
    }

    // Namespace and name: the display string would carry a nullable annotation (IBrush?).
    private static string? KindOf(ITypeSymbol type, string value) => $"{type.ContainingNamespace?.ToDisplayString()}.{type.Name}" switch
    {
        "Avalonia.Media.IBrush" or "Avalonia.Media.Brush" or "Avalonia.Media.ISolidColorBrush" or "Avalonia.Media.SolidColorBrush" => ColorLiteral().IsMatch(value) ? "Brush" : null,
        "Avalonia.Media.Color" => "Color",
        "Avalonia.Thickness" => "Thickness",
        "Avalonia.CornerRadius" => "CornerRadius",
        "Avalonia.Media.FontFamily" => "FontFamily",
        _ => type.SpecialType switch
        {
            SpecialType.System_Double => "Double",
            SpecialType.System_Int32 => "Int32",
            SpecialType.System_Boolean => "Boolean",
            SpecialType.System_String => "String",
            _ => null,
        },
    };

    [GeneratedRegex("^(#[0-9A-Fa-f]{3,8}|[A-Za-z]+)$")]
    private static partial Regex ColorLiteral();

    /// <summary>The nearest ancestor's Resources (inside its ResourceDictionary, if it has one); null: make one on the root.</summary>
    private static XmlElement? Container(XmlElement root, XmlElement el)
    {
        if (root.LocalName == "ResourceDictionary") return root;
        for (var a = el.Parent; a != null; a = a.Parent)
        {
            if (a.LocalName.Contains('.')) continue;
            if (a.Children.FirstOrDefault(c => c.Name == a.Name + ".Resources") is not { } resources) continue;
            return resources.Children is [{ LocalName: "ResourceDictionary" } dictionary] ? dictionary : resources;
        }
        return null;
    }

    private static string UniqueKey(XamlFile file, string wanted)
    {
        var taken = file.Analyzer.Resources().Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        if (!taken.Contains(wanted)) return wanted;
        for (var i = 2; ; i++)
        {
            if (!taken.Contains(wanted + i)) return wanted + i;
        }
    }
}
