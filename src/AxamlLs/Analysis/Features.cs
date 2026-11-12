using System.Text;
using System.Xml.Linq;
using AxamlLs.Workspace;
using AxamlLs.Xaml;
using Microsoft.CodeAnalysis;

namespace AxamlLs.Analysis;

public sealed record HoverInfo(int Start, int End, string Markdown);

/// <summary>A definition: either a file location (C#) or an offset in the same document.</summary>
public sealed record DefinitionTarget(string? FilePath, int StartLine, int StartCharacter, int EndLine, int EndCharacter, int? DocumentOffset = null);

public static class Features
{
    // ---------------------------------------------------------------- hover

    public static HoverInfo? Hover(XamlAnalyzer analyzer, int offset)
    {
        var r = analyzer.Result.RefAt(offset);
        if (r != null) return SymbolHover(r);
        return ResourceHover(analyzer, offset) ?? Selectors.Hover(analyzer, offset) ?? CompactValueHover(analyzer, offset) ?? ColorHover(analyzer, offset);
    }

    private static HoverInfo? SymbolHover(SymbolRef r)
    {
        var sb = new StringBuilder();
        if (r.TargetElement != null)
        {
            var nameAttr = XamlAnalyzer.XAttr(r.TargetElement, "Name") ?? r.TargetElement.Attr("Name");
            sb.Append("```xml\n<").Append(r.TargetElement.Name);
            if (nameAttr != null) sb.Append(' ').Append(nameAttr.Name).Append("=\"").Append(nameAttr.Value).Append('"');
            sb.Append(" />\n```\n");
            if (r.Type != null) sb.Append("Element of type `").Append(Members.FullDisplay(r.Type)).Append('`');
            return new HoverInfo(r.Start, r.End, sb.ToString());
        }
        if (r.Symbol == null) return null;

        sb.Append("```csharp\n").Append(Describe(r.Symbol)).Append("\n```");
        if (r.Symbol is not ITypeSymbol && r.Type != null && !SymbolEqualityComparer.Default.Equals(r.Type, Members.ValueTypeOf(r.Symbol)))
            sb.Append("\n\nResolved type: `").Append(Members.FullDisplay(r.Type)).Append('`');
        var doc = Summary(r.Symbol);
        if (doc != null) sb.Append("\n\n").Append(doc);
        return new HoverInfo(r.Start, r.End, sb.ToString());
    }

    /// <summary>A resource key in a use or in its x:Key: where it is declared, and a swatch when it is a color.</summary>
    private static HoverInfo? ResourceHover(XamlAnalyzer analyzer, int offset)
    {
        var found = ResourceKeyAt(analyzer, offset) is { } use ? (use.Key, use.Start, use.End) : ((string, int, int)?)null;
        if (found == null && Targets.Find(analyzer, offset) is { Target: ResourceKeyTarget decl } at) found = (decl.Key, at.Start, at.End);
        if (found is not var (key, start, end)) return null;

        var entries = analyzer.Resources().Where(e => e.Key == key).ToList();
        if (entries.Count == 0)
            return new HoverInfo(start, end, $"Resource `{key}` is not declared in this project's .axaml files (it may come from a theme or a library).");
        var blocks = new List<string>();
        foreach (var e in entries)
        {
            if (e.Assembly != null)
            {
                blocks.Add($"Resource `{e.Key}` ({e.ElementName}) from the compiled theme/library `{e.Assembly}`");
                continue;
            }
            var block = $"```xml\n<{e.ElementName} x:Key=\"{e.Key}\"{(e.ValuePreview != null ? " " + e.ValuePreview : "")} />\n```\n`{Path.GetFileName(e.FilePath)}` line {e.Line + 1}";
            if (ResourceArgb(analyzer, e) is { } argb) block = $"{ColorSupport.Swatch(argb)} `{ColorSupport.Hex(argb)}`\n\n{block}";
            blocks.Add(block);
        }
        return new HoverInfo(start, end, string.Join("\n\n", blocks));
    }

    /// <summary>Thickness, CornerRadius and BoxShadows values as a table of what every side means.</summary>
    private static HoverInfo? CompactValueHover(XamlAnalyzer analyzer, int offset)
    {
        if (analyzer.Project == null || analyzer.Result.Doc.ElementAt(offset) is not { } el) return null;
        var attr = el.Attributes.FirstOrDefault(a => a.HasValue && a.ValueStart <= offset && offset <= a.ValueEnd);
        if (attr == null || MarkupExtension.IsMarkup(attr.Value) || attr.Name.StartsWith("xmlns", StringComparison.Ordinal)) return null;
        var type = Members.ValueTypeOf(TargetProperty(analyzer, el, attr)) is { } t ? Members.UnwrapNullable(t) : null;
        var parts = InlayHints.Parts(type?.Name, MappedText.FromAttribute(attr));
        var lines = InlayHints.Expand(type?.Name, parts);
        if (lines.Count == 0) return null;
        var text = string.Join("\n\n", lines.Select(l => "`" + l + "`"));
        return new HoverInfo(attr.ValueStart, attr.ValueEnd, $"**{type!.Name}**\n\n{text}");
    }

    /// <summary>A color literal: a swatch and the same color as hex, rgb() and hsl().</summary>
    private static HoverInfo? ColorHover(XamlAnalyzer analyzer, int offset)
    {
        foreach (var c in DocumentColors(analyzer))
        {
            if (offset < c.Start || offset > c.End) continue;
            return new HoverInfo(c.Start, c.End, $"{ColorSupport.Swatch(c.Argb)} " + string.Join(" · ", ColorSupport.Presentations(c.Argb).Select(p => $"`{p}`")));
        }
        return null;
    }

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

    // ---------------------------------------------------------------- definition

    public static List<DefinitionTarget> Definition(XamlAnalyzer analyzer, int offset)
    {
        var result = new List<DefinitionTarget>();
        var r = analyzer.Result.RefAt(offset);
        if (r == null && ResourceKeyAt(analyzer, offset) is { } key)
        {
            foreach (var e in analyzer.Resources().Where(e => e.Key == key.Key && e.Assembly == null))
                result.Add(new DefinitionTarget(e.FilePath, e.Line, e.Character, e.Line, e.Character + e.Key.Length));
            return result;
        }
        if (r == null && AssetAt(analyzer, offset) is { } file)
        {
            result.Add(new DefinitionTarget(file, 0, 0, 0, 0));
            return result;
        }
        if (r == null) return result;
        if (r.TargetElement != null)
        {
            result.Add(new DefinitionTarget(null, 0, 0, 0, 0, r.TargetElement.NameStart));
            return result;
        }
        if (r.Symbol == null) return result;
        foreach (var symbol in DefinitionSymbols(r.Symbol))
        {
            foreach (var loc in symbol.Locations)
            {
                if (!loc.IsInSource || loc.SourceTree == null || !File.Exists(loc.SourceTree.FilePath)) continue;
                var span = loc.GetLineSpan();
                result.Add(new DefinitionTarget(span.Path, span.StartLinePosition.Line, span.StartLinePosition.Character,
                    span.EndLinePosition.Line, span.EndLinePosition.Character));
            }
            if (result.Count > 0) break;
        }
        return result;
    }

    /// <summary>
    /// The symbol itself, then — for source-generated members (CommunityToolkit [ObservableProperty]/[RelayCommand]) —
    /// the user-written field, partial property or method it was generated from.
    /// </summary>
    internal static IEnumerable<ISymbol> DefinitionSymbols(ISymbol symbol)
    {
        if (symbol is IPropertySymbol { PartialDefinitionPart: { } def }) yield return def;
        yield return symbol;
        var type = symbol.ContainingType;
        if (type == null) yield break;
        if (symbol is IPropertySymbol p)
        {
            var camel = char.ToLowerInvariant(p.Name[0]) + p.Name[1..];
            foreach (var candidate in new[] { "_" + camel, camel, "m_" + camel })
            {
                foreach (var m in type.GetMembers(candidate).OfType<IFieldSymbol>()) yield return m;
            }
            if (p.Name.EndsWith("Command", StringComparison.Ordinal))
            {
                var baseName = p.Name[..^7];
                foreach (var candidate in new[] { baseName, baseName + "Async", "On" + baseName })
                {
                    foreach (var m in type.GetMembers(candidate).OfType<IMethodSymbol>()) yield return m;
                }
            }
        }
    }

    // ---------------------------------------------------------------- completion

    public enum ItemKind
    {
        Method = 2,
        Function = 3,
        File = 17,
        Folder = 19,
        Field = 5,
        Variable = 6,
        Class = 7,
        Interface = 8,
        Property = 10,
        Value = 12,
        Enum = 13,
        Keyword = 14,
        Constant = 21,
        Snippet = 15,
        EnumMember = 20,
        Color = 16,
        Struct = 22,
        Event = 23,
    }

    public sealed record Completion(string Label, ItemKind Kind, string? Detail, string? Documentation, string InsertText, bool IsSnippet, int ReplaceStart, int ReplaceEnd, string? SortText = null)
    {
        /// <summary>#RRGGBB / #AARRGGBB: the item is sent as a color so the editor draws a swatch.</summary>
        public string? ColorHex { get; init; }
        /// <summary>A file whose preview is filled in on completionItem/resolve (only for the highlighted item).</summary>
        public string? PreviewPath { get; init; }
    }

    private static readonly string[] BindingKeys =
        ["Path", "Mode", "Converter", "ConverterParameter", "ConverterCulture", "StringFormat", "FallbackValue", "TargetNullValue", "ElementName", "RelativeSource", "Source", "DataType", "Priority", "UpdateSourceTrigger", "Delay"];

    internal static readonly string[] MarkupExtensions =
        ["Binding", "CompiledBinding", "ReflectionBinding", "TemplateBinding", "StaticResource", "DynamicResource", "RelativeSource", "OnPlatform", "OnFormFactor", "x:Static", "x:Type", "x:Null", "x:True", "x:False"];

    public static List<Completion> Complete(XamlAnalyzer analyzer, int offset)
    {
        var doc = analyzer.Result.Doc;
        var list = new List<Completion>();
        var el = StartTagAt(doc, offset);
        if (el == null) return list;

        if (offset <= el.NameEnd)
        {
            CompleteElementNames(analyzer, el, el.NameStart, el.NameEnd, list);
            return list;
        }

        foreach (var attr in el.Attributes)
        {
            if (attr.HasValue && attr.ValueStart <= offset && offset <= attr.ValueEnd)
            {
                CompleteAttributeValue(analyzer, el, attr, offset, list);
                return list;
            }
            if (attr.NameStart <= offset && offset <= attr.NameEnd)
            {
                // Classes.accent="{Binding IsSpecial}": a class switched on and off by a bool.
                if (attr.Name.StartsWith("Classes.", StringComparison.Ordinal) && offset >= attr.NameStart + 8)
                {
                    var type = analyzer.Result.Elements.GetValueOrDefault(el)?.Type;
                    var present = el.Attributes.Where(a => a != attr && a.Name.StartsWith("Classes.", StringComparison.Ordinal)).Select(a => a.Name[8..]).ToHashSet(StringComparer.Ordinal);
                    Selectors.CompleteClassNames(analyzer, el, type, analyzer.Styles(), attr.NameStart, attr.NameEnd, list,
                        c => attr.HasValue ? "Classes." + c : $"Classes.{c}=\"{{Binding $1}}\"$0", c => "Classes." + c, snippet: !attr.HasValue, exclude: present);
                    return list;
                }
                CompleteAttributeNames(analyzer, el, attr.NameStart, attr.NameEnd, list);
                return list;
            }
        }
        // Whitespace inside the tag: a new attribute.
        if (offset > 0 && char.IsWhiteSpace(doc.Text[offset - 1]) || offset < doc.Text.Length && doc.Text[offset] is '>' or '/')
            CompleteAttributeNames(analyzer, el, offset, offset, list);
        return list;
    }

    private static XmlElement? StartTagAt(XmlDoc doc, int offset)
    {
        XmlElement? best = null;
        foreach (var e in doc.AllElements)
        {
            var tagEnd = e.StartTagEnd > 0 ? e.StartTagEnd - 1 : e.End;
            if (e.Start < offset && offset <= tagEnd && (best == null || e.Start > best.Start)) best = e;
        }
        return best;
    }

    private static void CompleteElementNames(XamlAnalyzer analyzer, XmlElement el, int start, int end, List<Completion> list)
    {
        var p = analyzer.Project;
        if (p == null) return;
        // A tag being typed from scratch gets its closing tag too:  <Button |>⏎</Button>
        // (an existing tag being renamed keeps only the name).
        var fresh = el.Attributes.Count == 0 && el.StartTagEnd < 0 && el.EndTagStart < 0;
        string Insert(string label, bool isPropertyElement) =>
            !fresh ? label : isPropertyElement ? $"{label}>$0\n</{label}>" : $"{label} $1>\n</{label}>";
        // Property elements of the parent: <Grid.RowDefinitions>.
        if (el.Parent != null && analyzer.Result.Elements.TryGetValue(el.Parent, out var parentInfo) && parentInfo.Type != null)
        {
            var prefix = el.Parent.Prefix != null ? el.Parent.Prefix + ":" : "";
            foreach (var prop in SettableProperties(parentInfo.Type))
            {
                var label = $"{prefix}{el.Parent.LocalName}.{prop.Name}";
                list.Add(new Completion(label, ItemKind.Property, prop.Type.ToDisplayString(), null, Insert(label, true), fresh, start, end, "0" + label));
            }
        }
        foreach (var (prefix, ns) in DeclaredNamespaces(el))
        {
            if (ns is XamlNamespaces.X or XamlNamespaces.Blend or XamlNamespaces.MarkupCompatibility) continue;
            foreach (var type in p.TypesIn(ns))
            {
                if (type.IsStatic || type.TypeKind is not (TypeKind.Class or TypeKind.Struct) || type.IsGenericType) continue;
                if (type.IsAbstract) continue;
                var label = prefix.Length == 0 ? type.Name : $"{prefix}:{type.Name}";
                list.Add(new Completion(label, ItemKind.Class, type.ContainingNamespace?.ToDisplayString(), Summary(type), Insert(label, false), fresh, start, end, "1" + label));
            }
        }
    }

    private static void CompleteAttributeNames(XamlAnalyzer analyzer, XmlElement el, int start, int end, List<Completion> list)
    {
        var info = analyzer.Result.Elements.GetValueOrDefault(el);
        var present = el.Attributes.Select(a => a.Name).ToHashSet();
        var xPrefix = DeclaredNamespaces(el).FirstOrDefault(n => n.Ns == XamlNamespaces.X).Prefix ?? "x";
        void Add(string name, ItemKind kind, string? detail, string? docs, string sort)
        {
            if (present.Contains(name)) return;
            list.Add(new Completion(name, kind, detail, docs, name + "=\"$1\"$0", true, start, end, sort + name));
        }

        if (info?.Type != null)
        {
            foreach (var prop in SettableProperties(info.Type))
            {
                Add(prop.Name, ItemKind.Property, prop.Type.ToDisplayString(), Summary(prop), "1");
            }
            foreach (var ev in Members.SelfAndBases(info.Type).SelectMany(t => t.GetMembers()).OfType<IEventSymbol>()
                         .Where(e => e.DeclaredAccessibility == Accessibility.Public && !e.IsStatic).DistinctBy(e => e.Name))
            {
                Add(ev.Name, ItemKind.Event, ev.Type.ToDisplayString(), Summary(ev), "2");
            }
        }
        Add($"{xPrefix}:Name", ItemKind.Keyword, "x:Name directive", null, "3");
        Add($"{xPrefix}:DataType", ItemKind.Keyword, "x:DataType directive — the type compiled bindings resolve against", null, "3");
        Add($"{xPrefix}:CompileBindings", ItemKind.Keyword, "x:CompileBindings directive", null, "3");
        Add($"{xPrefix}:Key", ItemKind.Keyword, "x:Key directive", null, "3");
        if (el.Parent == null) Add($"{xPrefix}:Class", ItemKind.Keyword, "x:Class directive", null, "3");
    }

    private static void CompleteAttributeValue(XamlAnalyzer analyzer, XmlElement el, XmlAttr attr, int offset, List<Completion> list)
    {
        var p = analyzer.Project;
        var text = MappedText.FromAttribute(attr);
        var info = analyzer.Result.Elements.GetValueOrDefault(el);
        var isX = attr.Prefix != null && el.LookupNamespace(attr.Prefix) == XamlNamespaces.X;
        var prop = !isX && p != null ? TargetProperty(analyzer, el, attr) : null;
        var valueType = prop != null && !Members.IsEvent(prop) ? Members.ValueTypeOf(prop) is { } vt ? Members.UnwrapNullable(vt) : null : null;

        if (Selectors.SelectorAttr(el) == attr)
        {
            Selectors.Complete(analyzer, el, attr, offset, list, analyzer.Styles());
            return;
        }
        // Classes="accent big|": class names (those styled for this control first), without the ones already there.
        if (attr.Name == "Classes" && !MarkupExtension.IsMarkup(attr.Value))
        {
            var (cs, ce) = ClassWord(text, offset);
            var present = attr.Value.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
            present.Remove(text.Text[text.IndexOf(cs)..text.IndexOf(ce)]);
            Selectors.CompleteClassNames(analyzer, el, info?.Type, analyzer.Styles(), cs, ce, list, c => c, exclude: present);
            return;
        }
        if (el.LocalName == "Setter" && attr.LocalName == "Property" && !MarkupExtension.IsMarkup(attr.Value))
        {
            CompleteSetterProperty(analyzer, el, text, offset, list);
            return;
        }
        if (MarkupExtension.IsMarkup(attr.Value))
        {
            var ext = MarkupExtension.Parse(text);
            if (ext != null) CompleteInMarkup(analyzer, el, ext, offset, list, valueType);
            return;
        }
        if (!isX && Assets.Accepts(el, prop) is { } accepts)
        {
            Assets.Complete(analyzer, text, offset, accepts, list);
            return;
        }

        var (wordStart, wordEnd) = Word(text, offset, allowColon: true);
        if (isX && attr.LocalName == "DataType" || !isX && attr.LocalName is "DataType" or "TargetType" or "AncestorType")
        {
            CompleteTypeNames(analyzer, el, wordStart, wordEnd, list);
            return;
        }
        if (p == null || prop == null) return;

        if (Members.IsEvent(prop))
        {
            if (analyzer.Result.ClassType == null) return;
            foreach (var m in Members.SelfAndBases(analyzer.Result.ClassType).SelectMany(t => t.GetMembers()).OfType<IMethodSymbol>()
                         .Where(m => m.MethodKind == MethodKind.Ordinary && m.Parameters.Length == 2).DistinctBy(m => m.Name))
            {
                list.Add(new Completion(m.Name, ItemKind.Method, m.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), null, m.Name, false, wordStart, wordEnd));
            }
            return;
        }
        if (valueType == null) return;
        CompleteValuesOfType(p, valueType, wordStart, wordEnd, list, attr.ValueStart, attr.ValueEnd);
        AddMarkupOpeners(analyzer, el, valueType, attr.ValueStart, attr.ValueEnd, list);
    }

    /// <summary>
    /// Markup extensions that can supply this value, listed after the literal values. Each one inserts
    /// "{Name |}" and the completion inside it takes over (binding paths, resource keys…).
    /// </summary>
    private static void AddMarkupOpeners(XamlAnalyzer analyzer, XmlElement el, ITypeSymbol valueType, int start, int end, List<Completion> list)
    {
        void Add(string name, string detail, string sort, bool argument = true) =>
            list.Add(new Completion($"{{{name}{(argument ? " …" : "")}}}", ItemKind.Snippet, detail, null,
                argument ? $"{{{name} $1}}$0" : $"{{{name}}}$0", true, start, end, "9" + sort));

        Add("Binding", "bind to a member of the data context (x:DataType)", "a");
        if (ResourceKindFor(valueType) is { } kind && analyzer.Resources().Any(r => Fits(r, kind)))
        {
            Add("DynamicResource", "resource that follows theme/runtime changes", "b");
            Add("StaticResource", "resource resolved once at load", "c");
        }
        if (analyzer.ScopeAt(el).TemplateTarget != null) Add("TemplateBinding", "property of the templated control", "d");
        if (valueType.IsReferenceType || valueType is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T })
            Add("x:Null", "null", "e", argument: false);
    }

    /// <summary>
    /// The property an attribute value is for: the attribute's own property, or for &lt;Setter Property="X" Value="…"/&gt;
    /// property X of the styled type (Style Selector / ControlTheme TargetType).
    /// </summary>
    internal static ISymbol? TargetProperty(XamlAnalyzer analyzer, XmlElement el, XmlAttr attr)
    {
        var info = analyzer.Result.Elements.GetValueOrDefault(el);
        if (el.LocalName == "Setter" && attr.LocalName == "Value")
        {
            var propName = el.Attr("Property")?.Value.Trim();
            if (string.IsNullOrEmpty(propName)) return null;
            var dot = propName.LastIndexOf('.');
            if (dot > 0)
            {
                var owner = analyzer.ResolveTypeName(el, new MappedText(propName[..dot], [.. Enumerable.Range(0, dot + 1)]), report: false);
                return owner == null ? null : Members.FindAttached(owner, propName[(dot + 1)..]) ?? Members.FindSettable(owner, propName[(dot + 1)..]);
            }
            var target = StyledType(analyzer, el) ?? analyzer.Project?.ResolveType(XamlNamespaces.Avalonia, "Control");
            return target == null ? null : Members.FindSettable(target, propName);
        }
        if (info?.Type == null) return null;
        var local = attr.LocalName;
        var d = local.IndexOf('.');
        if (d > 0)
        {
            var owner = analyzer.ResolveTypeName(el, new MappedText(local[..d], [.. Enumerable.Range(0, d + 1)]), report: false);
            return owner == null ? null : Members.FindAttached(owner, local[(d + 1)..]) ?? Members.FindSettable(owner, local[(d + 1)..]);
        }
        return Members.FindSettable(info.Type, local);
    }

    /// <summary>
    /// Setter Property="|": the Avalonia properties of the styled type (styled and attached ones; direct properties
    /// cannot be set by styles), or "Grid.|" → the attached properties of Grid.
    /// </summary>
    private static void CompleteSetterProperty(XamlAnalyzer analyzer, XmlElement setter, MappedText text, int offset, List<Completion> list)
    {
        var (ws, we) = Word(text, offset, allowColon: true);
        var typed = text.Text[text.IndexOf(ws)..text.IndexOf(offset)];
        var dot = typed.LastIndexOf('.');
        if (dot > 0)
        {
            var ownerText = typed[..dot];
            var owner = analyzer.ResolveTypeName(setter, new MappedText(ownerText, [.. Enumerable.Range(0, ownerText.Length + 1)]), report: false);
            if (owner == null) return;
            foreach (var (name, field, valueType) in AvaloniaProperties(owner).Where(x => x.Field.Type.Name == "AttachedProperty"))
                list.Add(new Completion($"{ownerText}.{name}", ItemKind.Property, Members.Display(valueType), Summary(field), $"{ownerText}.{name}", false, ws, we, "0" + name));
            return;
        }
        var target = StyledType(analyzer, setter) ?? analyzer.Project?.ResolveType(XamlNamespaces.Avalonia, "Control");
        if (target == null) return;
        foreach (var (name, field, valueType) in AvaloniaProperties(target))
        {
            if (field.Type.Name == "DirectPropertyBase" || field.Type.Name.StartsWith("DirectProperty", StringComparison.Ordinal)) continue;
            var docs = Members.FindSettable(target, name) is { } clr ? Summary(clr) : Summary(field);
            var own = SymbolEqualityComparer.Default.Equals(field.ContainingType, target);
            list.Add(new Completion(name, ItemKind.Property, Members.Display(valueType), docs, name, false, ws, we, (own ? "0" : "1") + name));
        }
    }

    /// <summary>The XxxProperty fields of a type and its bases: (Xxx, field, value type).</summary>
    internal static IEnumerable<(string Name, IFieldSymbol Field, ITypeSymbol? ValueType)> AvaloniaProperties(ITypeSymbol type)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in Members.SelfAndBases(type))
        {
            foreach (var f in t.GetMembers().OfType<IFieldSymbol>())
            {
                if (!f.IsStatic || f.DeclaredAccessibility != Accessibility.Public || !f.Name.EndsWith("Property", StringComparison.Ordinal) || f.Name.Length <= 8) continue;
                if (!Members.IsAvaloniaProperty(f.Type)) continue;
                var name = f.Name[..^8];
                if (!seen.Add(name)) continue;
                var valueType = f.Type is INamedTypeSymbol { TypeArguments.Length: > 0 } g ? g.TypeArguments[^1] : null;
                yield return (name, f, valueType);
            }
        }
    }

    /// <summary>The project file a path value under the cursor points to (Svg Path, Image Source …).</summary>
    public static string? AssetAt(XamlAnalyzer analyzer, int offset)
    {
        var el = analyzer.Result.Doc.ElementAt(offset);
        var attr = el?.Attributes.FirstOrDefault(a => a.HasValue && a.ValueStart <= offset && offset <= a.ValueEnd);
        if (el == null || attr == null || MarkupExtension.IsMarkup(attr.Value) || Assets.Accepts(el, TargetProperty(analyzer, el, attr)) == null) return null;
        return Assets.Resolve(analyzer, attr.Value) is { } file && File.Exists(file) ? file : null;
    }

    public static List<InlayHint> InlayHintsIn(XamlAnalyzer analyzer, int start, int end, InlayHintOptions? options = null) =>
        InlayHints.Compute(analyzer, start, end, (el, attr) => attr.Name.StartsWith("xmlns", StringComparison.Ordinal) ? null : TargetProperty(analyzer, el, attr), options);

    public static IEnumerable<XamlDiagnostic> AssetDiagnostics(XamlAnalyzer analyzer) =>
        Assets.Missing(analyzer, (el, attr) => attr.Prefix == null || attr.Name.Contains('.') ? TargetProperty(analyzer, el, attr) : null);

    /// <summary>The control type a Setter applies to: ControlTheme TargetType, or the last type in a Style selector.</summary>
    private static ITypeSymbol? StyledType(XamlAnalyzer analyzer, XmlElement setter) => Selectors.StyledType(analyzer, setter);

    private static void CompleteValuesOfType(ProjectSnapshot p, ITypeSymbol type, int start, int end, List<Completion> list, int valueStart = -1, int valueEnd = -1)
    {
        if (type.SpecialType == SpecialType.System_Boolean)
        {
            list.Add(new Completion("True", ItemKind.Keyword, "bool", null, "True", false, start, end));
            list.Add(new Completion("False", ItemKind.Keyword, "bool", null, "False", false, start, end));
            return;
        }
        if (type.TypeKind == TypeKind.Enum)
        {
            foreach (var f in type.GetMembers().OfType<IFieldSymbol>().Where(f => f.IsConst))
            {
                list.Add(new Completion(f.Name, ItemKind.EnumMember, type.Name, Summary(f), f.Name, false, start, end));
            }
            return;
        }
        // Brushes and colors accept the named colors (Background="Red").
        if (ResourceKindFor(type) is "Brush" or "Color" && p.Compilation.GetTypeByMetadataName("Avalonia.Media.Colors") is { } colors)
        {
            var known = ColorSupport.KnownColors(p);
            foreach (var c in colors.GetMembers().OfType<IPropertySymbol>().Where(c => c.IsStatic))
            {
                var hex = known.TryGetValue(c.Name, out var argb) ? ColorSupport.Hex(argb) : null;
                list.Add(new Completion(c.Name, hex != null ? ItemKind.Color : ItemKind.Value, hex ?? "named color", null, c.Name, false, start, end, "0" + c.Name)
                {
                    ColorHex = hex,
                });
            }
            // Zeroed templates for each syntax Avalonia parses, after the names and before the {…} openers.
            // A color literal is the whole value, so they replace all of it ("#", "rgb(" … are not word characters).
            if (valueStart >= 0) (start, end) = (valueStart, valueEnd);
            for (var i = 0; i < ColorSupport.Templates.Length; i++)
            {
                var (label, snippet, detail) = ColorSupport.Templates[i];
                list.Add(new Completion(label, ItemKind.Snippet, detail, null, snippet, true, start, end, $"8{i:D2}"));
            }
            return;
        }
        // Types with well-known static values of themselves: FontWeight.Bold, Cursor…
        foreach (var m in Members.SelfAndBases(type).Take(1).SelectMany(t => t.GetMembers()))
        {
            if (m is IPropertySymbol { IsStatic: true } sp && SymbolEqualityComparer.Default.Equals(sp.Type, type) ||
                m is IFieldSymbol { IsStatic: true, DeclaredAccessibility: Accessibility.Public } sf && SymbolEqualityComparer.Default.Equals(sf.Type, type))
            {
                list.Add(new Completion(m.Name, ItemKind.Value, type.Name, Summary(m), m.Name, false, start, end, "0" + m.Name));
            }
        }
    }

    private static void CompleteInMarkup(XamlAnalyzer analyzer, XmlElement el, MarkupExtension ext, int offset, List<Completion> list, ITypeSymbol? expected = null)
    {
        // Extension name: {Bin|
        if (offset <= ext.NameEnd)
        {
            foreach (var name in MarkupExtensions)
            {
                list.Add(new Completion(name, ItemKind.Keyword, "markup extension", null, name, false, ext.NameStart, ext.NameEnd, "0" + name));
            }
            foreach (var (label, type) in CustomMarkupExtensions(analyzer, el))
            {
                list.Add(new Completion(label, ItemKind.Class, type.ToDisplayString(), Summary(type), label, false, ext.NameStart, ext.NameEnd, "1" + label));
            }
            return;
        }

        MarkupArg? current = null;
        foreach (var arg in ext.Args)
        {
            if (arg.Start <= offset && offset <= Math.Max(arg.End, arg.ValueEnd)) current = arg;
        }
        if (current?.Nested != null && current.Nested.Start < offset && offset <= current.Nested.End && (!current.Nested.Closed || offset < current.Nested.End))
        {
            CompleteInMarkup(analyzer, el, current.Nested, offset, list);
            return;
        }

        var isBinding = ext.LocalName is "Binding" or "CompiledBinding" or "ReflectionBinding" && ext.Prefix == null;
        var isResource = ext.LocalName is "StaticResource" or "DynamicResource";

        // Cursor on a key ("Mo|de=") or in a blank argument slot ("{Binding Foo, |"): offer keys / positional values.
        if (current == null || current.Key != null && offset <= current.KeyEnd)
        {
            var start = current?.KeyStart ?? offset;
            var end = current?.KeyEnd ?? offset;
            if (isBinding)
            {
                foreach (var key in BindingKeys)
                {
                    if (ext.Named(key) != null) continue;
                    list.Add(new Completion(key, ItemKind.Property, "Binding." + key, null, key + "=", false, start, end, "1" + key));
                }
                if (ext.Positional(0) == null && ext.Named("Path") == null)
                    CompleteBindingPath(analyzer, el, ext, new MappedText("", [offset]), offset, list);
            }
            else if (isResource)
            {
                CompleteResourceKeys(analyzer, start, end, list, expected);
            }
            else if (ext.LocalName == "TemplateBinding")
            {
                CompleteTemplateProperties(analyzer, el, start, end, list);
            }
            else if (ext.Prefix is { } prefix && el.LookupNamespace(prefix) is { } extNs && extNs != XamlNamespaces.X && analyzer.Project is { } p)
            {
                // A custom extension: its settable properties as Key=.
                var type = p.ResolveType(extNs, ext.LocalName + "Extension") ?? p.ResolveType(extNs, ext.LocalName);
                if (type != null)
                {
                    foreach (var prop in SettableProperties(type).Where(pr => ext.Named(pr.Name) == null))
                        list.Add(new Completion(prop.Name, ItemKind.Property, prop.Type.ToDisplayString(), Summary(prop), prop.Name + "=", false, start, end, "1" + prop.Name));
                }
            }
            return;
        }

        var value = current.Value;
        if (value == null) return;
        var (ws, we) = Word(value, offset, allowColon: true);
        if (current.Key == null)
        {
            // Positional value.
            if (isBinding && current == ext.Positional(0)) CompleteBindingPath(analyzer, el, ext, value, offset, list);
            else if (isResource) CompleteResourceKeys(analyzer, value.Start, value.End, list, expected);
            else if (ext.LocalName == "TemplateBinding") CompleteTemplateProperties(analyzer, el, ws, we, list);
            else if (ext.LocalName == "RelativeSource") AddWords(list, ["Self", "TemplatedParent", "FindAncestor", "DataContext"], "RelativeSourceMode", ws, we);
            else if (ext.LocalName is "Type" or "Static") CompleteTypeNames(analyzer, el, ws, we, list);
            else if (Assets.AcceptsMarkup(ext.LocalName) is { } accepts) Assets.Complete(analyzer, value, offset, accepts, list);
            return;
        }
        switch (current.Key)
        {
            case "Path" when isBinding:
                CompleteBindingPath(analyzer, el, ext, value, offset, list);
                break;
            case "Mode" when isBinding:
                AddWords(list, ["Default", "OneWay", "TwoWay", "OneTime", "OneWayToSource"], "BindingMode", ws, we);
                break;
            case "Mode" when ext.LocalName == "RelativeSource":
                AddWords(list, ["Self", "TemplatedParent", "FindAncestor", "DataContext"], "RelativeSourceMode", ws, we);
                break;
            case "UpdateSourceTrigger":
                AddWords(list, ["Default", "PropertyChanged", "LostFocus", "Explicit"], "UpdateSourceTrigger", ws, we);
                break;
            case "ElementName":
                foreach (var name in analyzer.Result.Names.Keys)
                    list.Add(new Completion(name, ItemKind.Variable, "<" + analyzer.Result.Names[name].Name + ">", null, name, false, ws, we));
                break;
            case "DataType":
            case "AncestorType":
                CompleteTypeNames(analyzer, el, ws, we, list);
                break;
            case "Converter":
                CompleteResourceKeys(analyzer, value.Start, value.End, list);
                break;
        }
    }

    /// <summary>Markup extensions from the document's own xmlns prefixes: {localization:Localize …}.</summary>
    internal static IEnumerable<(string Label, INamedTypeSymbol Type)> CustomMarkupExtensions(XamlAnalyzer analyzer, XmlElement el)
    {
        if (analyzer.Project is not { } p) yield break;
        foreach (var (prefix, ns) in DeclaredNamespaces(el))
        {
            if (prefix.Length == 0 || ns is XamlNamespaces.X or XamlNamespaces.Blend or XamlNamespaces.MarkupCompatibility or XamlNamespaces.Avalonia) continue;
            foreach (var type in p.TypesIn(ns))
            {
                if (type.IsAbstract || type.IsStatic || type.TypeKind != TypeKind.Class) continue;
                var isExtension = Members.InheritsByName(type, "MarkupExtension") ||
                                  Members.SelfAndBases(type).Any(t => t.GetMembers("ProvideValue").OfType<IMethodSymbol>().Any(m => m.DeclaredAccessibility == Accessibility.Public));
                if (!isExtension) continue;
                var name = type.Name.EndsWith("Extension", StringComparison.Ordinal) && type.Name.Length > 9 ? type.Name[..^9] : type.Name;
                yield return ($"{prefix}:{name}", type);
            }
        }
    }

    private static void AddWords(List<Completion> list, string[] words, string detail, int start, int end)
    {
        foreach (var w in words) list.Add(new Completion(w, ItemKind.EnumMember, detail, null, w, false, start, end));
    }

    private static void CompleteTemplateProperties(XamlAnalyzer analyzer, XmlElement el, int start, int end, List<Completion> list)
    {
        if (analyzer.ScopeAt(el).TemplateTarget is not { } target) return;
        foreach (var prop in SettableProperties(target))
            list.Add(new Completion(prop.Name, ItemKind.Property, prop.Type.ToDisplayString(), Summary(prop), prop.Name, false, start, end));
    }

    private static void CompleteBindingPath(XamlAnalyzer analyzer, XmlElement el, MarkupExtension ext, MappedText value, int offset, List<Completion> list)
    {
        var idx = value.IndexOf(offset);
        var wordStartIdx = idx;
        while (wordStartIdx > 0 && BindingPath.IsIdentPart(value.Text[wordStartIdx - 1])) wordStartIdx--;
        var wordEndIdx = idx;
        while (wordEndIdx < value.Text.Length && BindingPath.IsIdentPart(value.Text[wordEndIdx])) wordEndIdx++;
        var start = value.Abs(wordStartIdx);
        var end = value.Abs(wordEndIdx);
        var before = wordStartIdx > 0 ? value.Text[wordStartIdx - 1] : '\0';

        // $parent[Typ|] or $parent[local:Typ|;2]
        var typeStart = idx;
        while (typeStart > 0 && (BindingPath.IsIdentPart(value.Text[typeStart - 1]) || value.Text[typeStart - 1] == ':')) typeStart--;
        if (typeStart > 0 && value.Text[typeStart - 1] == '[' && value.Text[..(typeStart - 1)].EndsWith("$parent", StringComparison.Ordinal))
        {
            var typeEnd = idx;
            while (typeEnd < value.Text.Length && (BindingPath.IsIdentPart(value.Text[typeEnd]) || value.Text[typeEnd] == ':')) typeEnd++;
            Selectors.CompleteTypes(analyzer, el, value.Abs(typeStart), value.Abs(typeEnd), list, pipe: false);
            return;
        }
        if (before == '#')
        {
            foreach (var name in analyzer.Result.Names.Keys)
                list.Add(new Completion(name, ItemKind.Variable, "<" + analyzer.Result.Names[name].Name + ">", null, name, false, start, end));
            return;
        }
        if (before == '$')
        {
            list.Add(new Completion("parent", ItemKind.Keyword, "$parent[Type] — an ancestor control", null, "parent", false, start, end));
            list.Add(new Completion("self", ItemKind.Keyword, "$self — the target control", null, "self", false, start, end));
            return;
        }

        var prefixText = value.Text[..wordStartIdx];
        var path = BindingPath.Parse(prefixText);
        var s = analyzer.ScopeAt(el);

        // Origin of the binding (ElementName / RelativeSource / DataType / x:DataType).
        ITypeSymbol? origin = s.DataType;
        XmlElement? originElement = null;
        if (ext.Named("ElementName")?.Value is { } en && analyzer.Result.Names.TryGetValue(en.Text.Trim(), out var named))
        {
            originElement = named;
            origin = analyzer.Result.Elements[named].Type;
        }
        else if (ext.Named("DataType") is { } dt)
        {
            var typeText = dt.Nested != null ? (dt.Nested.Named("TypeName") ?? dt.Nested.Positional(0))?.Value : dt.Value;
            origin = typeText != null ? analyzer.ResolveTypeName(el, typeText, report: false) : null;
        }
        else if (ext.Named("RelativeSource")?.Nested is { } rs)
        {
            var mode = (rs.Named("Mode") ?? rs.Positional(0))?.Value?.Text.Trim();
            if (rs.Named("AncestorType")?.Value is { } at) origin = analyzer.ResolveTypeName(el, at, report: false);
            else if (mode == "Self")
            {
                originElement = el;
                origin = analyzer.Result.Elements[el].Type;
            }
            else if (mode == "TemplatedParent") origin = s.TemplateTarget;
        }

        var type = analyzer.Walk(path, value, origin, originElement, el, validate: false, path.Segments.Count);
        if (prefixText.Trim().Length == 0)
        {
            list.Add(new Completion("$parent", ItemKind.Keyword, "an ancestor control: $parent[Type]", null, "\\$parent[$1]$0", true, start, end, "2$parent"));
            list.Add(new Completion("$self", ItemKind.Keyword, "the target control", null, "\\$self", true, start, end, "2$self"));
            foreach (var name in analyzer.Result.Names.Keys)
                list.Add(new Completion("#" + name, ItemKind.Variable, "<" + analyzer.Result.Names[name].Name + ">", null, "#" + name, false, start, end, "3" + name));
        }
        else if (!path.EndsExpectingMember)
        {
            return;
        }
        if (type == null || Members.IsOpaque(type)) return;
        AddBindableMembers(Members.UnwrapNullable(type), start, end, list);
    }

    private static void AddBindableMembers(ITypeSymbol type, int start, int end, List<Completion> list)
    {
        var seen = new HashSet<string>();
        foreach (var t in Members.Hierarchy(type))
        {
            if (t.SpecialType == SpecialType.System_Object) continue;
            foreach (var m in t.GetMembers())
            {
                if (m.DeclaredAccessibility != Accessibility.Public || m.IsImplicitlyDeclared || m.IsStatic) continue;
                switch (m)
                {
                    case IPropertySymbol { IsIndexer: false } prop when seen.Add(prop.Name):
                        list.Add(new Completion(prop.Name, ItemKind.Property, prop.Type.ToDisplayString(), Summary(prop), prop.Name, false, start, end, "0" + prop.Name));
                        break;
                    case IMethodSymbol { MethodKind: MethodKind.Ordinary } method when !method.Name.StartsWith("get_", StringComparison.Ordinal) && seen.Add(method.Name):
                        list.Add(new Completion(method.Name, ItemKind.Method, method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), Summary(method), method.Name, false, start, end, "1" + method.Name));
                        break;
                }
            }
        }
    }

    private static void CompleteTypeNames(XamlAnalyzer analyzer, XmlElement el, int start, int end, List<Completion> list)
    {
        var p = analyzer.Project;
        if (p == null) return;
        foreach (var (prefix, ns) in DeclaredNamespaces(el))
        {
            if (ns is XamlNamespaces.X or XamlNamespaces.Blend or XamlNamespaces.MarkupCompatibility) continue;
            var own = ns.StartsWith("using:", StringComparison.Ordinal) || ns.StartsWith("clr-namespace:", StringComparison.Ordinal);
            foreach (var type in p.TypesIn(ns))
            {
                if (type.IsStatic || type.IsGenericType) continue;
                var label = prefix.Length == 0 ? type.Name : $"{prefix}:{type.Name}";
                list.Add(new Completion(label, ItemKind.Class, type.ToDisplayString(), Summary(type), label, false, start, end, (own ? "0" : "1") + label));
            }
        }
    }

    private static void CompleteResourceKeys(XamlAnalyzer analyzer, int start, int end, List<Completion> list, ITypeSymbol? expected = null)
    {
        var want = expected != null ? ResourceKindFor(expected) : null;
        var seen = new HashSet<string>();
        foreach (var r in analyzer.Resources())
        {
            if (!seen.Add(r.Key)) continue;
            var fits = want != null && Fits(r, want);
            // Own resources first, then theme ones; within each, those that fit the property first.
            var rank = (r.Assembly == null ? 0 : 2) + (want == null || fits ? 0 : 1);
            var hex = ResourceColor(analyzer, r);
            list.Add(new Completion(r.Key, hex != null ? ItemKind.Color : ItemKind.Value, ResourceDetail(r), ResourceDoc(r), r.Key, false, start, end, $"{rank}{r.Key}")
            {
                ColorHex = hex,
            });
        }
    }

    /// <summary>The color of a project resource such as &lt;SolidColorBrush Color="#FF6600"/&gt; or &lt;Color&gt;Red&lt;/Color&gt;.</summary>
    private static string? ResourceColor(XamlAnalyzer analyzer, ResourceEntry r) =>
        ResourceArgb(analyzer, r) is { } argb ? ColorSupport.Hex(argb) : null;

    private static uint? ResourceArgb(XamlAnalyzer analyzer, ResourceEntry r)
    {
        if (r.Assembly != null || r.ValuePreview == null) return null;
        var local = r.ElementName[(r.ElementName.IndexOf(':') + 1)..];
        if (local is not ("SolidColorBrush" or "Color")) return null;
        var value = r.ValuePreview.StartsWith("Color=\"", StringComparison.Ordinal) ? r.ValuePreview[7..^1] : r.ValuePreview;
        return ColorSupport.TryParse(value, ColorSupport.KnownColors(analyzer.Project), out var argb) ? argb : null;
    }

    // ---------------------------------------------------------------- document colors

    public sealed record ColorSpan(int Start, int End, uint Argb);

    /// <summary>Color literals in attribute values of Brush/Color properties: Background="#FF6600", Color="Red".</summary>
    public static List<ColorSpan> DocumentColors(XamlAnalyzer analyzer)
    {
        var result = new List<ColorSpan>();
        var known = ColorSupport.KnownColors(analyzer.Project);
        foreach (var el in analyzer.Result.Doc.AllElements)
        {
            foreach (var attr in el.Attributes)
            {
                if (!attr.HasValue || attr.Value.Length == 0 || MarkupExtension.IsMarkup(attr.Value) || attr.Name.StartsWith("xmlns", StringComparison.Ordinal)) continue;
                // BoxShadow="1 2 4 0 #66000000": a swatch for the color piece of each shadow.
                if (analyzer.Project != null && Members.ValueTypeOf(TargetProperty(analyzer, el, attr)) is { Name: "BoxShadows" or "BoxShadow" })
                {
                    foreach (var (s, e) in InlayHints.ShadowColors(attr.Value))
                    {
                        if (ColorSupport.TryParse(attr.Value[s..e], known, out var shadowColor))
                            result.Add(new ColorSpan(attr.Map[s], attr.Map[e], shadowColor));
                    }
                    continue;
                }
                if (!ColorSupport.TryParse(attr.Value, known, out var argb)) continue;
                var isHex = attr.Value.TrimStart().StartsWith('#');
                if (analyzer.Project != null)
                {
                    // Only where a color is meant ("Red" is also a valid string for Text="Red").
                    var prop = TargetProperty(analyzer, el, attr);
                    var type = Members.ValueTypeOf(prop);
                    if (type == null ? !isHex : ResourceKindFor(Members.UnwrapNullable(type)) is not ("Brush" or "Color")) continue;
                }
                else if (!isHex)
                {
                    continue;
                }
                var trimmed = attr.Value.Trim();
                var lead = attr.Value.IndexOf(trimmed, StringComparison.Ordinal);
                result.Add(new ColorSpan(attr.Map[lead], attr.Map[lead + trimmed.Length], argb));
            }
        }
        return result;
    }

    private static string ResourceDetail(ResourceEntry r) =>
        r.Assembly != null ? $"{r.ElementName} · {r.Assembly}"
        : r.ValuePreview != null ? $"<{r.ElementName}> {r.ValuePreview}" : $"<{r.ElementName}>";

    private static string? ResourceDoc(ResourceEntry r) =>
        r.Assembly != null ? $"From the compiled theme/library `{r.Assembly}`."
        : r.FilePath.Length > 0 ? $"Declared in `{Path.GetFileName(r.FilePath)}`" : null;

    /// <summary>The resource "kind" a property type takes: Brush, Color, Thickness, double… (null = no idea).</summary>
    private static string? ResourceKindFor(ITypeSymbol type)
    {
        var name = type.Name;
        if (name is "IBrush" or "IImmutableBrush" || name.EndsWith("Brush", StringComparison.Ordinal)) return "Brush";
        if (type.SpecialType == SpecialType.System_Double) return "Double";
        return name is "Color" or "Thickness" or "CornerRadius" or "FontFamily" or "FontWeight" or "Geometry" or "StreamGeometry" or "IImage" or "TimeSpan"
            ? name.Replace("Stream", "").Replace("IImage", "Image") : name;
    }

    private static bool Fits(ResourceEntry r, string want)
    {
        if (r.Assembly != null)
        {
            return want == "Double" ? r.ElementName is "FontSize" or "Opacity" : r.ElementName == want ||
                   want == "Geometry" && r.ElementName == "Glyph" || want == "TimeSpan" && r.ElementName == "Duration";
        }
        var local = r.ElementName[(r.ElementName.IndexOf(':') + 1)..];
        return want switch
        {
            "Brush" => local.EndsWith("Brush", StringComparison.Ordinal),
            "Double" => local == "Double",
            "Geometry" => local.EndsWith("Geometry", StringComparison.Ordinal),
            "Image" => local is "Bitmap" or "DrawingImage" or "SvgImage" || local.EndsWith("Image", StringComparison.Ordinal),
            _ => local == want,
        };
    }

    /// <summary>The resource key under the cursor in {StaticResource Key} / {DynamicResource Key}.</summary>
    public static (string Key, int Start, int End)? ResourceKeyAt(XamlAnalyzer analyzer, int offset)
    {
        var doc = analyzer.Result.Doc;
        foreach (var el in doc.AllElements)
        {
            foreach (var attr in el.Attributes)
            {
                if (!attr.HasValue || offset < attr.ValueStart || offset > attr.ValueEnd || !MarkupExtension.IsMarkup(attr.Value)) continue;
                var ext = MarkupExtension.Parse(MappedText.FromAttribute(attr));
                if (ext == null) return null;
                foreach (var e in ext.SelfAndNested())
                {
                    if (e.LocalName is not ("StaticResource" or "DynamicResource")) continue;
                    var arg = (e.Named("ResourceKey") ?? e.Positional(0))?.Value;
                    if (arg != null && arg.Start <= offset && offset <= arg.End) return (arg.Text.Trim(), arg.Start, arg.End);
                }
                return null;
            }
        }
        return null;
    }

    internal static IEnumerable<IPropertySymbol> SettableProperties(ITypeSymbol type) =>
        Members.SelfAndBases(type).SelectMany(t => t.GetMembers()).OfType<IPropertySymbol>()
            .Where(p => p.DeclaredAccessibility == Accessibility.Public && !p.IsStatic && !p.IsIndexer &&
                        (p.SetMethod is { DeclaredAccessibility: Accessibility.Public } || Members.ElementType(p.Type) != null))
            .DistinctBy(p => p.Name);

    public static IEnumerable<(string Prefix, string Ns)> DeclaredNamespaces(XmlElement el)
    {
        var seen = new HashSet<string>();
        foreach (var e in el.SelfAndAncestors())
        {
            if (e.NamespaceDeclarations == null) continue;
            foreach (var (prefix, ns) in e.NamespaceDeclarations)
            {
                if (seen.Add(prefix)) yield return (prefix, ns);
            }
        }
    }

    /// <summary>
    /// After typing '/' just before the '>' of a start tag (cursor after the '/'), the span of a directly following,
    /// empty matching end tag: &lt;Button A="1"/&gt;&lt;/Button&gt; → remove "&lt;/Button&gt;". Null when there is nothing to remove.
    /// </summary>
    public static (int Start, int End)? SelfClosingEdit(string text, int offset)
    {
        if (offset < 2 || offset >= text.Length || text[offset - 1] != '/' || text[offset] != '>') return null;
        var lt = text.LastIndexOf('<', offset - 2);
        if (lt < 0 || text.IndexOf('>', lt, offset - lt) >= 0 && InsideTagEnds(text, lt, offset)) return null;
        var nameEnd = lt + 1;
        while (nameEnd < text.Length && (char.IsLetterOrDigit(text[nameEnd]) || text[nameEnd] is '_' or ':' or '.' or '-')) nameEnd++;
        var name = text[(lt + 1)..nameEnd];
        if (name.Length == 0) return null;
        var i = offset + 1;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        var close = "</" + name;
        if (string.CompareOrdinal(text, i, close, 0, close.Length) != 0) return null;
        var j = i + close.Length;
        while (j < text.Length && char.IsWhiteSpace(text[j])) j++;
        if (j >= text.Length || text[j] != '>') return null;
        return (offset + 1, j + 1);
    }

    /// <summary>True when a '>' outside quotes ends the tag that starts at <paramref name="lt"/> before <paramref name="end"/>.</summary>
    private static bool InsideTagEnds(string text, int lt, int end)
    {
        char? quote = null;
        for (var i = lt + 1; i < end - 1; i++)
        {
            var c = text[i];
            if (quote != null)
            {
                if (c == quote) quote = null;
            }
            else if (c is '"' or '\'') quote = c;
            else if (c == '>') return true;
        }
        return false;
    }

    /// <summary>The space-separated class name around the cursor in Classes="a b|c".</summary>
    private static (int Start, int End) ClassWord(MappedText text, int offset)
    {
        var i = text.IndexOf(offset);
        var s = i;
        while (s > 0 && !char.IsWhiteSpace(text.Text[s - 1])) s--;
        var e = i;
        while (e < text.Text.Length && !char.IsWhiteSpace(text.Text[e])) e++;
        return (text.Abs(s), text.Abs(e));
    }

    private static (int Start, int End) Word(MappedText text, int offset, bool allowColon)
    {
        bool Part(char c) => BindingPath.IsIdentPart(c) || allowColon && c is ':' or '.';
        var i = text.IndexOf(offset);
        var s = i;
        while (s > 0 && Part(text.Text[s - 1])) s--;
        var e = i;
        while (e < text.Text.Length && Part(text.Text[e])) e++;
        return (text.Abs(s), text.Abs(e));
    }
}
