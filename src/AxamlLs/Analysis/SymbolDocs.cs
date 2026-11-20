using System.Xml.Linq;
using Microsoft.CodeAnalysis;

namespace AxamlLs.Analysis;

/// <summary>How a C# symbol is shown to the user: a one-line declaration and the summary of its doc comment.</summary>
internal static class SymbolDocs
{
    internal static string Describe(ISymbol symbol)
    {
        switch (symbol)
        {
            case ITypeSymbol t:
                var kind = t.TypeKind switch
                {
                    TypeKind.Interface => "interface",
                    TypeKind.Struct => "struct",
                    TypeKind.Enum => "enum",
                    _ => t.IsRecord ? "record" : "class",
                };
                var bases = t.BaseType is { SpecialType: not SpecialType.System_Object } b ? " : " + b.ToDisplayString() : "";
                return $"{kind} {t.ToDisplayString()}{bases}";
            case IPropertySymbol p:
                return $"(property) {p.Type.ToDisplayString()} {p.ContainingType.Name}.{p.Name}" + (p.SetMethod == null ? " { get; }" : " { get; set; }");
            case IFieldSymbol f when Members.IsAvaloniaProperty(f.Type):
                var name = f.Name.EndsWith("Property", StringComparison.Ordinal) ? f.Name[..^8] : f.Name;
                return $"(avalonia property) {Members.ValueTypeOf(f)?.ToDisplayString() ?? "?"} {f.ContainingType.Name}.{name}\n// {f.Type.ToDisplayString()}";
            case IFieldSymbol f when Members.IsRoutedEvent(f.Type):
                return $"(routed event) {f.ContainingType.Name}.{f.Name}\n// {f.Type.ToDisplayString()}";
            case IFieldSymbol f:
                return $"(field) {f.Type.ToDisplayString()} {f.ContainingType.Name}.{f.Name}";
            case IEventSymbol e:
                return $"(event) {e.Type.ToDisplayString()} {e.ContainingType.Name}.{e.Name}";
            case IMethodSymbol m:
                return "(method) " + m.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            default:
                return symbol.ToDisplayString();
        }
    }

    internal static string? Summary(ISymbol symbol)
    {
        var xml = symbol.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml) && symbol is IPropertySymbol { PartialDefinitionPart: { } def }) xml = def.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml)) return null;
        try
        {
            var summary = XElement.Parse(xml).Element("summary");
            if (summary == null) return null;
            var text = string.Concat(summary.Nodes().Select(n => n switch
            {
                XElement { Name.LocalName: "see" or "seealso" } e => "`" + ((string?)e.Attribute("cref") ?? (string?)e.Attribute("langword") ?? "").Split(':').Last() + "`",
                XElement { Name.LocalName: "paramref" or "typeparamref" } e => "`" + (string?)e.Attribute("name") + "`",
                XElement { Name.LocalName: "c" } e => "`" + e.Value + "`",
                XElement e => e.Value,
                _ => n.ToString(),
            }));
            return string.Join(' ', text.Split((char[])['\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim())).Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
