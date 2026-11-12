using AxamlLs.Text;
using AxamlLs.Workspace;
using AxamlLs.Xaml;
using Microsoft.CodeAnalysis;

namespace AxamlLs.Analysis;

public enum BindingKind
{
    /// <summary>{Binding}: compiled when x:CompileBindings / the project default says so.</summary>
    Default,
    Compiled,
    Reflection,
}

/// <summary>The inputs of one binding, from markup ({Binding ...}) or element syntax (&lt;Binding Path="..."/&gt;).</summary>
public sealed record BindingSpec(
    BindingKind Kind,
    MappedText? Path,
    MappedText? ElementName,
    MarkupExtension? RelativeSource,
    MappedText? RelativeSourceText,
    bool HasSource,
    MappedText? DataTypeText,
    MarkupExtension? DataTypeExtension,
    int AnchorStart,
    int AnchorEnd);

/// <summary>
/// Analyzes one AXAML document: XML errors, unresolved types/properties, and — the main point — binding paths
/// checked against x:DataType the way Avalonia's compiled bindings are checked at build time.
/// </summary>
public sealed class XamlAnalyzer
{
    private readonly XmlDoc _doc;
    private readonly ProjectSnapshot? _p;
    private readonly bool _compiledDefault;
    private readonly AnalysisResult _r;
    private readonly HashSet<XmlElement> _visited = [];
    private readonly Dictionary<(XmlElement, string), ITypeSymbol?> _bindingResults = [];
    private readonly Dictionary<int, ITypeSymbol?> _bindingOrigins = [];
    private HashSet<string>? _ignorablePrefixes;

    public AnalysisResult Result => _r;

    /// <summary>Project-wide x:Key resources (set by the server; null = only this document's keys).</summary>
    public Func<IReadOnlyList<ResourceEntry>>? ResourceProvider { get; set; }

    /// <summary>Style classes and ControlTheme parts of the whole project (set by the server; null = this document only).</summary>
    public Func<ProjectStyles>? StyleProvider { get; set; }

    /// <summary>The .axaml file being analyzed and its project's folder (set by the server; used for asset paths).</summary>
    public string? DocumentPath { get; set; }

    /// <summary>Every analyzed .axaml file of the project (set by the server; used by hover to say where a class is set). Null = only this document.</summary>
    public Func<IReadOnlyList<XamlFile>>? ProjectFiles { get; set; }
    public string? ProjectDirectory { get; set; }

    public ProjectStyles Styles() => StyleProvider?.Invoke() ?? ClassIndex.ScanText("", _doc.Text);

    /// <summary>The project's own keys (from .axaml files) followed by those of compiled themes and libraries.</summary>
    public IReadOnlyList<ResourceEntry> Resources(string? path = null)
    {
        var own = ResourceProvider?.Invoke() ?? ResourceIndex.Scan(path ?? "", _doc.Text);
        return _p == null ? own : [.. own, .. _p.LibraryResources];
    }
    public ProjectSnapshot? Project => _p;

    private XamlAnalyzer(XmlDoc doc, ProjectSnapshot? project, bool compiledDefault)
    {
        _doc = doc;
        _p = project is { HasAvalonia: true } ? project : null;
        _compiledDefault = compiledDefault;
        _r = new AnalysisResult { Doc = doc, Project = _p };
    }

    public static XamlAnalyzer Analyze(string text, ProjectSnapshot? project, bool compiledDefault)
    {
        var doc = XmlDoc.Parse(text);
        var analyzer = new XamlAnalyzer(doc, project, compiledDefault);
        analyzer.Run(text);
        return analyzer;
    }

    private void Run(string text)
    {
        var xmlError = WellFormedness.Check(text);
        if (xmlError != null)
        {
            var lines = new LineIndex(text);
            var start = lines.GetOffset(xmlError.Line, xmlError.Character);
            var end = start;
            while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] is not ('<' or '>')) end++;
            if (end == start) end = Math.Min(start + 1, text.Length);
            Report(start, end, Codes.XmlError, xmlError.Message);
        }

        foreach (var el in _doc.AllElements) ResolveElement(el);
        CollectNames();
        ResolveClass();
        foreach (var top in _doc.TopLevel) Visit(top, Scope.Root(_compiledDefault));
    }

    // ---------------------------------------------------------------- pass 1: element types

    private void ResolveElement(XmlElement el)
    {
        var info = new ElementInfo();
        _r.Elements[el] = info;
        if (el.Parent != null && _r.Elements[el.Parent].Ignored)
        {
            info.Ignored = true;
            return;
        }
        var ns = el.LookupNamespace(el.Prefix ?? "");
        if (ns == null || IsIgnorable(el, ns))
        {
            info.Ignored = true;
            return;
        }

        var local = el.LocalName;
        if (local.Length == 0) return;
        var localStart = el.NameStart + (el.Prefix != null ? el.Prefix.Length + 1 : 0);
        var dot = local.IndexOf('.');
        if (dot > 0)
        {
            info.IsPropertyElement = true;
            var ownerName = local[..dot];
            var propName = local[(dot + 1)..];
            info.PropertyName = propName;
            if (_p == null) return;
            var owner = _p.ResolveType(ns, ownerName);
            if (owner == null)
            {
                if (_p.IsCheckable(ns)) Report(localStart, localStart + dot, Codes.UnknownType, $"Unable to resolve type '{ownerName}' from namespace '{ns}'.", data: ns);
                return;
            }
            info.OwnerType = owner;
            AddTypeRef(localStart, localStart + dot, owner);
            var parentType = el.Parent != null ? _r.Elements[el.Parent].Type : null;
            var prop = parentType != null && Members.IsSameOrDerived(parentType, owner)
                ? Members.FindSettable(owner, propName)
                : Members.FindAttached(owner, propName) ?? Members.FindSettable(owner, propName);
            var propStart = localStart + dot + 1;
            if (prop == null)
            {
                if (!Members.HasUnknownBase(owner))
                    Report(propStart, propStart + propName.Length, Codes.UnknownProperty, $"Unable to find property '{propName}' on type '{Members.FullDisplay(owner)}'.");
                return;
            }
            info.Property = prop;
            _r.Refs.Add(new SymbolRef { Start = propStart, End = propStart + propName.Length, Symbol = prop, Type = Members.ValueTypeOf(prop) });
            return;
        }

        if (_p == null || ns == XamlNamespaces.X && local is "Arguments" or "Array" or "Null") return;
        var type = _p.ResolveType(ns, local) ?? _p.ResolveType(ns, local + "Extension");
        if (type == null)
        {
            if (_p.IsCheckable(ns) && ns != XamlNamespaces.X)
                Report(localStart, el.NameEnd, Codes.UnknownType, $"Unable to resolve type '{local}' from namespace '{ns}'.", data: ns);
            return;
        }
        info.Type = type;
        AddTypeRef(localStart, el.NameEnd, type);
        if (el.EndTagStart >= 0)
        {
            var endLocal = el.EndTagStart + 2 + (el.Prefix != null ? el.Prefix.Length + 1 : 0);
            AddTypeRef(endLocal, endLocal + local.Length, type);
        }
    }

    private void CollectNames()
    {
        foreach (var el in _doc.AllElements)
        {
            if (_r.Elements[el].Ignored) continue;
            var nameAttr = XAttr(el, "Name") ?? el.Attr("Name");
            if (nameAttr is { HasValue: true } && nameAttr.Value.Length > 0) _r.Names.TryAdd(nameAttr.Value.Trim(), el);
        }
    }

    private void ResolveClass()
    {
        var root = _doc.Root;
        if (root == null) return;
        var cls = XAttr(root, "Class");
        if (cls is not { HasValue: true } || _p == null) return;
        var name = cls.Value.Trim();
        var type = _p.Compilation.GetTypeByMetadataName(name);
        if (type == null)
        {
            Report(cls.ValueStart, cls.ValueEnd, Codes.UnknownClass, $"Unable to find the x:Class type '{name}' in project {Path.GetFileName(_p.ProjectPath)}.");
            return;
        }
        _r.ClassType = type;
        AddTypeRef(cls.ValueStart, cls.ValueEnd, type);
    }

    // ---------------------------------------------------------------- pass 2: scopes, attributes, bindings

    private void Visit(XmlElement el, Scope scope)
    {
        var info = _r.Elements[el];
        if (info.Ignored) return;
        _visited.Add(el);
        var s = ScopeFor(el, info, scope, report: true);

        // DataContext="{Binding …}" without x:DataType: the compiler takes the binding's type as the data type of the
        // element's other bindings and of its children, so analyse that attribute first, in the outer scope.
        var dataContext = InferrableDataContext(el, info);
        if (dataContext != null)
        {
            AnalyzeAttribute(el, info, dataContext, s);
            s = WithDataContext(el, info, s);
        }
        else
        {
            s = WithDataContext(el, info, s);
        }
        info.Scope = s;

        foreach (var attr in el.Attributes)
        {
            if (attr != dataContext) AnalyzeAttribute(el, info, attr, s);
        }
        if (!info.IsPropertyElement && el.LookupNamespace(el.Prefix ?? "") == XamlNamespaces.Avalonia &&
            el.LocalName is "Binding" or "CompiledBinding" or "ReflectionBinding")
        {
            AnalyzeBindingElement(el, s);
        }
        foreach (var child in el.Children) Visit(child, s);
    }

    /// <summary>The scope an element establishes for its own attributes and its children.</summary>
    private Scope ScopeFor(XmlElement el, ElementInfo info, Scope parent, bool report)
    {
        var s = parent;
        var compile = XAttr(el, "CompileBindings");
        if (compile is { HasValue: true } && bool.TryParse(compile.Value.Trim(), out var compiled)) s = s with { Compiled = compiled };

        if (IsTemplateBoundary(el, info))
        {
            ITypeSymbol? target = null;
            var targetAttr = el.Attr("TargetType");
            if (targetAttr is { HasValue: true }) target = ResolveTypeAttribute(el, targetAttr, report);
            ITypeSymbol? dataType = null;
            if (IsDataTemplate(el, info))
            {
                var dt = el.Attr("DataType");
                dataType = dt is { HasValue: true } ? ResolveTypeAttribute(el, dt, report) : InferItemType(el);
            }
            else
            {
                // A ControlTemplate inside a ControlTheme/Style usually leaves TargetType to the theme.
                target ??= s.TemplateTarget;
            }
            s = new Scope(dataType, DataTypeRequired: false, s.Compiled, target);
        }

        var xdt = XAttr(el, "DataType");
        if (xdt is { HasValue: true })
        {
            var t = ResolveTypeAttribute(el, xdt, report);
            s = s with { DataType = t, DataTypeRequired = false };
        }
        return s;
    }

    /// <summary>The element's DataContext="{…}" attribute when it decides the data type (no x:DataType on the element).</summary>
    private XmlAttr? InferrableDataContext(XmlElement el, ElementInfo info)
    {
        if (info.IsPropertyElement || XAttr(el, "DataType") != null) return null;
        var attr = el.Attr("DataContext");
        return attr is { HasValue: true } && MarkupExtension.IsMarkup(attr.Value) ? attr : null;
    }

    /// <summary>
    /// The scope after the element's own DataContext, when it has no x:DataType: the type of a DataContext="{Binding …}"
    /// (already analysed), or of the object in &lt;X.DataContext&gt;&lt;vm:Foo/&gt;&lt;/X.DataContext&gt;. A DataContext whose type
    /// cannot be told (a reflection binding, an unresolved path) leaves the data type unknown rather than wrong.
    /// </summary>
    private Scope WithDataContext(XmlElement el, ElementInfo info, Scope s)
    {
        if (info.IsPropertyElement || XAttr(el, "DataType") != null) return s;
        if (InferrableDataContext(el, info) != null)
            return s with { DataType = _bindingResults.GetValueOrDefault((el, "DataContext")), DataTypeRequired = false };

        foreach (var child in el.Children)
        {
            if (!_r.Elements.TryGetValue(child, out var ci) || !ci.IsPropertyElement || ci.PropertyName != "DataContext") continue;
            var value = child.Children.FirstOrDefault(c => _r.Elements.TryGetValue(c, out var vi) && !vi.IsPropertyElement && !vi.Ignored);
            if (value == null) return s;
            return s with { DataType = _r.Elements[value].Type, DataTypeRequired = false };
        }
        return s;
    }

    private static bool IsTemplateBoundary(XmlElement el, ElementInfo info)
    {
        if (info.IsPropertyElement) return false;
        if (info.Type != null)
        {
            return Members.InheritsByName(info.Type, "DataTemplate", "TreeDataTemplate", "ControlTemplate", "ItemsPanelTemplate", "Template",
                "Style", "ControlTheme", "ITemplate", "IDataTemplate") && !Members.InheritsByName(info.Type, "Control");
        }
        return el.LocalName.EndsWith("Template", StringComparison.Ordinal) || el.LocalName is "Style" or "ControlTheme";
    }

    private static bool IsDataTemplate(XmlElement el, ElementInfo info) =>
        info.Type != null ? Members.InheritsByName(info.Type, "IDataTemplate", "DataTemplate", "TreeDataTemplate") : el.LocalName.EndsWith("DataTemplate", StringComparison.Ordinal);

    /// <summary>&lt;ItemsControl ItemsSource="{Binding Items}"&gt;&lt;ItemsControl.ItemTemplate&gt;&lt;DataTemplate&gt; → the item type.</summary>
    private ITypeSymbol? InferItemType(XmlElement template)
    {
        var parent = template.Parent;
        if (parent == null || !_r.Elements.TryGetValue(parent, out var pinfo) || !pinfo.IsPropertyElement || pinfo.PropertyName != "ItemTemplate") return null;
        var itemsControl = parent.Parent;
        if (itemsControl == null) return null;
        return _bindingResults.TryGetValue((itemsControl, "ItemsSource"), out var t) && t != null ? Members.ElementType(t) : null;
    }

    private void AnalyzeAttribute(XmlElement el, ElementInfo info, XmlAttr attr, Scope s)
    {
        if (attr.Name == "xmlns" || attr.Name.StartsWith("xmlns:", StringComparison.Ordinal)) return;
        string? ns = null;
        if (attr.Prefix != null)
        {
            ns = el.LookupNamespace(attr.Prefix);
            if (ns == null || IsIgnorable(el, ns)) return;
            if (ns == XamlNamespaces.X) return; // Directives are handled where they matter (Class, DataType, Name).
        }
        if (info.IsPropertyElement) return;

        var local = attr.LocalName;
        var localStart = attr.NameStart + (attr.Prefix != null ? attr.Prefix.Length + 1 : 0);
        ISymbol? prop = null;
        if (_p != null && info.Type != null)
        {
            var dot = local.IndexOf('.');
            if (dot > 0)
            {
                var ownerName = local[..dot];
                var propName = local[(dot + 1)..];
                if (ownerName is not ("Classes" or "PseudoClasses"))
                {
                    var ownerNs = ns ?? el.LookupNamespace("");
                    var owner = _p.ResolveType(ownerNs, ownerName);
                    if (owner == null)
                    {
                        if (_p.IsCheckable(ownerNs)) Report(localStart, localStart + dot, Codes.UnknownType, $"Unable to resolve type '{ownerName}' from namespace '{ownerNs}'.", data: ownerNs);
                    }
                    else
                    {
                        AddTypeRef(localStart, localStart + dot, owner);
                        prop = Members.IsSameOrDerived(info.Type, owner)
                            ? Members.FindSettable(owner, propName)
                            : Members.FindAttached(owner, propName) ?? Members.FindSettable(owner, propName);
                        if (prop == null && !Members.HasUnknownBase(owner))
                            Report(localStart + dot + 1, attr.NameEnd, Codes.UnknownProperty, $"Unable to find attached property '{propName}' on type '{Members.FullDisplay(owner)}'.");
                        if (prop != null)
                            _r.Refs.Add(new SymbolRef { Start = localStart + dot + 1, End = attr.NameEnd, Symbol = prop, Type = Members.ValueTypeOf(prop) });
                    }
                }
            }
            else
            {
                prop = Members.FindSettable(info.Type, local);
                if (prop == null && !Members.HasUnknownBase(info.Type))
                    Report(attr.NameStart, attr.NameEnd, Codes.UnknownProperty, $"Unable to find property or event '{local}' on type '{Members.FullDisplay(info.Type)}'.");
                if (prop != null)
                    _r.Refs.Add(new SymbolRef { Start = attr.NameStart, End = attr.NameEnd, Symbol = prop, Type = Members.ValueTypeOf(prop) });
            }
        }

        if (!attr.HasValue) return;
        if (prop != null && Members.IsEvent(prop))
        {
            AnalyzeHandler(attr);
            return;
        }
        if (!MarkupExtension.IsMarkup(attr.Value)) return;
        var ext = MarkupExtension.Parse(MappedText.FromAttribute(attr));
        if (ext == null) return;

        // A binding on DataContext of an element that declares its own x:DataType: whether it resolves
        // against the outer or the inner type depends on the compiler version, so do not report members.
        var lenient = local == "DataContext" && XAttr(el, "DataType") != null;
        var result = AnalyzeMarkup(ext, el, s, lenient);
        _bindingResults[(el, local)] = result;
    }

    private void AnalyzeHandler(XmlAttr attr)
    {
        var name = attr.Value.Trim();
        if (_r.ClassType == null || name.Length == 0 || name.Contains('{')) return;
        var method = Members.FindMethods(_r.ClassType, name).FirstOrDefault();
        var start = attr.ValueStart + attr.Value.IndexOf(name, StringComparison.Ordinal);
        if (method == null)
        {
            if (!Members.HasUnknownBase(_r.ClassType))
                Report(start, start + name.Length, Codes.UnknownHandler, $"Unable to find event handler method '{name}' on type '{Members.FullDisplay(_r.ClassType)}'.");
            return;
        }
        _r.Refs.Add(new SymbolRef { Start = start, End = start + name.Length, Symbol = method });
    }

    // ---------------------------------------------------------------- markup extensions

    private ITypeSymbol? AnalyzeMarkup(MarkupExtension ext, XmlElement el, Scope s, bool lenient)
    {
        foreach (var e in ext.Errors) Report(e.Start, Math.Max(e.End, e.Start + 1), Codes.MarkupSyntax, e.Message);
        var ns = el.LookupNamespace(ext.Prefix ?? "");
        ITypeSymbol? result = null;
        var handledArgs = new HashSet<MarkupArg>();

        if (ns == XamlNamespaces.X)
        {
            switch (ext.LocalName)
            {
                case "Type":
                    if ((ext.Named("TypeName") ?? ext.Positional(0))?.Value is { } typeText) ResolveTypeName(el, typeText, report: true);
                    break;
                case "Static":
                    if ((ext.Named("Member") ?? ext.Positional(0))?.Value is { } memberText) result = ResolveStatic(el, memberText);
                    break;
            }
        }
        else if (ns == XamlNamespaces.Avalonia)
        {
            switch (ext.LocalName)
            {
                case "Binding":
                case "CompiledBinding":
                case "ReflectionBinding":
                    var kind = ext.LocalName switch
                    {
                        "CompiledBinding" => BindingKind.Compiled,
                        "ReflectionBinding" => BindingKind.Reflection,
                        _ => BindingKind.Default,
                    };
                    var pathArg = ext.Named("Path") ?? ext.Positional(0);
                    var relative = ext.Named("RelativeSource");
                    var dataType = ext.Named("DataType") ?? ext.Args.FirstOrDefault(a => a.Key != null && a.Key.EndsWith(":DataType", StringComparison.Ordinal) &&
                        el.LookupNamespace(a.Key[..a.Key.IndexOf(':')]) == XamlNamespaces.X);
                    if (relative != null) handledArgs.Add(relative);
                    if (dataType != null) handledArgs.Add(dataType);
                    var spec = new BindingSpec(kind, pathArg?.Value, ext.Named("ElementName")?.Value, relative?.Nested, relative?.Value,
                        ext.Named("Source") != null, dataType?.Value, dataType?.Nested, ext.NameStart, ext.NameEnd);
                    result = EvaluateBinding(spec, el, s, lenient);
                    break;
                case "TemplateBinding":
                    AnalyzeTemplateBinding(ext, el, s);
                    break;
                default:
                    CheckExtensionExists(ext, ns);
                    break;
            }
        }
        else
        {
            CheckExtensionExists(ext, ns);
        }

        foreach (var arg in ext.Args)
        {
            if (arg.Nested != null && !handledArgs.Contains(arg)) AnalyzeMarkup(arg.Nested, el, s, lenient);
        }
        return result;
    }

    private void CheckExtensionExists(MarkupExtension ext, string? ns)
    {
        if (_p == null || ext.LocalName.Length == 0) return;
        var type = _p.ResolveType(ns, ext.LocalName + "Extension") ?? _p.ResolveType(ns, ext.LocalName);
        if (type != null)
        {
            AddTypeRef(ext.NameStart, ext.NameEnd, type);
        }
        else if (_p.IsCheckable(ns))
        {
            Report(ext.NameStart, ext.NameEnd, Codes.UnknownMarkupExtension, $"Unable to resolve markup extension '{ext.Name}'.");
        }
    }

    private void AnalyzeTemplateBinding(MarkupExtension ext, XmlElement el, Scope s)
    {
        var arg = (ext.Named("Property") ?? ext.Positional(0))?.Value;
        if (arg == null || _p == null) return;
        var text = arg.Text.Trim();
        var start = arg.Abs(arg.Text.IndexOf(text, StringComparison.Ordinal));
        if (text.StartsWith('(') && text.EndsWith(')') && text.LastIndexOf('.') is var dot and > 0)
        {
            var owner = ResolveTypeName(el, new MappedText(text[1..dot], [.. Enumerable.Range(0, dot).Select(i => start + 1 + i)]), report: true);
            if (owner != null && Members.FindAttached(owner, text[(dot + 1)..^1]) == null && !Members.HasUnknownBase(owner))
                Report(start, start + text.Length, Codes.UnknownTemplateProperty, $"Unable to find attached property '{text[(dot + 1)..^1]}' on type '{Members.FullDisplay(owner)}'.");
            return;
        }
        var target = s.TemplateTarget;
        if (target == null || text.Length == 0) return;
        var prop = Members.FindSettable(target, text);
        if (prop == null)
        {
            if (!Members.HasUnknownBase(target))
                Report(start, start + text.Length, Codes.UnknownTemplateProperty, $"Unable to find property '{text}' on the templated type '{Members.FullDisplay(target)}'.");
            return;
        }
        _r.Refs.Add(new SymbolRef { Start = start, End = start + text.Length, Symbol = prop, Type = Members.ValueTypeOf(prop) });
    }

    private ITypeSymbol? ResolveStatic(XmlElement el, MappedText memberText)
    {
        if (_p == null) return null;
        var text = memberText.Text.Trim();
        var dot = text.LastIndexOf('.');
        if (dot <= 0) return null;
        var offset = memberText.Text.IndexOf(text, StringComparison.Ordinal);
        var typePart = new MappedText(text[..dot], [.. Enumerable.Range(0, dot + 1).Select(i => memberText.Abs(offset + i))]);
        var type = ResolveTypeName(el, typePart, report: true);
        if (type == null) return null;
        var memberName = text[(dot + 1)..];
        var start = memberText.Abs(offset + dot + 1);
        var end = memberText.Abs(offset + text.Length);
        var member = Members.SelfAndBases(type).SelectMany(t => t.GetMembers(memberName)).FirstOrDefault(m => m.IsStatic);
        if (member == null)
        {
            if (!Members.HasUnknownBase(type))
                Report(start, end, Codes.UnknownStaticMember, $"Unable to find static member '{memberName}' on type '{Members.FullDisplay(type)}'.");
            return null;
        }
        var valueType = member switch
        {
            IFieldSymbol f => f.Type,
            IPropertySymbol p => p.Type,
            _ => null,
        };
        _r.Refs.Add(new SymbolRef { Start = start, End = end, Symbol = member, Type = valueType });
        return valueType;
    }

    // ---------------------------------------------------------------- bindings

    private void AnalyzeBindingElement(XmlElement el, Scope s)
    {
        MappedText? Value(string name) => el.Attr(name) is { HasValue: true } a ? MappedText.FromAttribute(a) : null;
        var kind = el.LocalName switch
        {
            "CompiledBinding" => BindingKind.Compiled,
            "ReflectionBinding" => BindingKind.Reflection,
            _ => BindingKind.Default,
        };
        var relativeAttr = el.Attr("RelativeSource");
        MarkupExtension? relative = relativeAttr is { HasValue: true } && MarkupExtension.IsMarkup(relativeAttr.Value)
            ? MarkupExtension.Parse(MappedText.FromAttribute(relativeAttr))
            : null;
        var dataTypeAttr = el.Attr("DataType");
        MarkupExtension? dataTypeExt = dataTypeAttr is { HasValue: true } && MarkupExtension.IsMarkup(dataTypeAttr.Value)
            ? MarkupExtension.Parse(MappedText.FromAttribute(dataTypeAttr))
            : null;
        var spec = new BindingSpec(kind, Value("Path"), Value("ElementName"), relative, relative == null ? Value("RelativeSource") : null,
            el.Attr("Source") != null, dataTypeExt == null ? Value("DataType") : null, dataTypeExt, el.NameStart, el.NameEnd);
        EvaluateBinding(spec, el.Parent ?? el, s, lenient: false);
    }

    /// <summary>Checks a binding and returns the type of the value it produces (null when unknown).</summary>
    private ITypeSymbol? EvaluateBinding(BindingSpec spec, XmlElement el, Scope s, bool lenient)
    {
        var compiled = spec.Kind == BindingKind.Compiled || (spec.Kind == BindingKind.Default && s.Compiled);
        var path = spec.Path != null ? BindingPath.Parse(spec.Path.Text) : BindingPath.Parse("");
        if (spec.Path != null)
        {
            foreach (var e in path.Errors) Report(spec.Path.Abs(e.Start), Math.Max(spec.Path.Abs(e.End), spec.Path.Abs(e.Start) + 1), Codes.PathSyntax, e.Message);
        }

        var origin = ResolveBindingOrigin(spec, el, s, out var explicitSource, out var originElement);
        _bindingOrigins[spec.AnchorStart] = origin;
        var first = path.Segments.FirstOrDefault(x => x.Kind != PathSegmentKind.Not);
        var pathHasSource = first is { Kind: PathSegmentKind.Self or PathSegmentKind.Parent or PathSegmentKind.Element };

        if (!explicitSource && !pathHasSource && origin == null && compiled && s.DataTypeRequired)
        {
            Report(spec.AnchorStart, spec.AnchorEnd, Codes.MissingDataType,
                "Cannot parse a compiled binding without an explicit x:DataType directive. Set x:DataType on this element or an ancestor, or use ReflectionBinding.");
        }
        if (spec.HasSource) return null;

        var validate = !lenient && (compiled || explicitSource || pathHasSource);
        return Walk(path, spec.Path, origin, originElement, el, validate, path.Segments.Count);
    }

    private ITypeSymbol? ResolveBindingOrigin(BindingSpec spec, XmlElement el, Scope s, out bool explicitSource, out XmlElement? originElement)
    {
        explicitSource = false;
        originElement = null;
        if (spec.HasSource)
        {
            explicitSource = true;
            return null;
        }
        if (spec.ElementName != null)
        {
            explicitSource = true;
            var name = spec.ElementName.Text.Trim();
            if (!_r.Names.TryGetValue(name, out var target))
            {
                Report(spec.ElementName.Start, spec.ElementName.End, Codes.UnknownElementName, $"Unable to find an element named '{name}' in this document.");
                return null;
            }
            originElement = target;
            var type = _r.Elements[target].Type;
            _r.Refs.Add(new SymbolRef { Start = spec.ElementName.Start, End = spec.ElementName.End, Symbol = type, Type = type, TargetElement = target });
            return type;
        }
        if (spec.RelativeSource != null || spec.RelativeSourceText != null)
        {
            explicitSource = true;
            string? mode = null;
            MappedText? ancestorType = null;
            var level = 1;
            if (spec.RelativeSource != null)
            {
                mode = (spec.RelativeSource.Named("Mode") ?? spec.RelativeSource.Positional(0))?.Value?.Text.Trim();
                ancestorType = spec.RelativeSource.Named("AncestorType")?.Value;
                if (spec.RelativeSource.Named("AncestorLevel")?.Value is { } lv && int.TryParse(lv.Text.Trim(), out var l)) level = l;
            }
            else
            {
                mode = spec.RelativeSourceText!.Text.Trim();
            }
            if (ancestorType != null) mode ??= "FindAncestor";
            switch (mode)
            {
                case "Self":
                    if (!IsControlLike(el)) return null;
                    originElement = el;
                    return _r.Elements[el].Type;
                case "TemplatedParent":
                    return s.TemplateTarget;
                case "FindAncestor":
                    if (ancestorType == null) return null;
                    var t = ResolveTypeName(el, ancestorType, report: true);
                    if (t != null) originElement = FindAncestor(el, t, level);
                    return t;
                default:
                    return null;
            }
        }
        if (spec.DataTypeText != null || spec.DataTypeExtension != null)
        {
            explicitSource = true;
            if (spec.DataTypeExtension != null)
            {
                var typeArg = (spec.DataTypeExtension.Named("TypeName") ?? spec.DataTypeExtension.Positional(0))?.Value;
                return typeArg != null ? ResolveTypeName(el, typeArg, report: true) : null;
            }
            return ResolveTypeName(el, spec.DataTypeText!, report: true);
        }
        return s.DataType;
    }

    /// <summary>
    /// Walks the first <paramref name="count"/> segments of a binding path from <paramref name="current"/>, recording
    /// references and (when <paramref name="validate"/>) reporting members that do not exist. Returns the resulting type.
    /// </summary>
    public ITypeSymbol? Walk(BindingPath path, MappedText? text, ITypeSymbol? current, XmlElement? currentElement, XmlElement el, bool validate, int count)
    {
        int Abs(int i) => text?.Abs(i) ?? 0;
        var negated = false;
        for (var i = 0; i < count && i < path.Segments.Count; i++)
        {
            var seg = path.Segments[i];
            if (current != null) current = Members.UnwrapNullable(current);
            switch (seg.Kind)
            {
                case PathSegmentKind.Not:
                    negated = true;
                    break;

                case PathSegmentKind.Self:
                    currentElement = IsControlLike(el) ? el : null;
                    current = currentElement != null ? _r.Elements[el].Type : null;
                    break;

                case PathSegmentKind.Parent:
                {
                    ITypeSymbol? wanted = null;
                    if (seg.TypeName != null && text != null)
                    {
                        wanted = ResolveTypeName(el, Slice(text, seg.TypeStart, seg.TypeName.Length), report: validate);
                    }
                    var ancestor = !IsControlLike(el) ? null
                        : seg.TypeName == null ? FindAncestor(el, null, seg.Level)
                        : wanted != null ? FindAncestor(el, wanted, seg.Level) : null;
                    currentElement = ancestor;
                    current = wanted ?? (ancestor != null ? _r.Elements[ancestor].Type : null);
                    break;
                }

                case PathSegmentKind.Element:
                    if (!_r.Names.TryGetValue(seg.Name, out var target))
                    {
                        if (text != null && seg.Name.Length > 0)
                            Report(Abs(seg.Start), Abs(seg.End), Codes.UnknownElementName, $"Unable to find an element named '{seg.Name}' in this document.");
                        return null;
                    }
                    currentElement = target;
                    current = _r.Elements[target].Type;
                    _r.Refs.Add(new SymbolRef { Start = Abs(seg.Start), End = Abs(seg.End), Symbol = current, Type = current, TargetElement = target });
                    break;

                case PathSegmentKind.Property:
                {
                    if (current == null || Members.IsOpaque(current))
                    {
                        current = null;
                        currentElement = null;
                        break;
                    }
                    var member = Members.FindBindable(current, seg.Name);
                    if (member == null)
                    {
                        if (validate && !Members.HasUnknownBase(current))
                            Report(Abs(seg.Start), Abs(seg.End), Codes.UnknownBindingMember,
                                $"Unable to resolve property or method of name '{seg.Name}' on type '{Members.FullDisplay(current)}'.", data: current);
                        return null;
                    }
                    var memberType = Members.ValueTypeOf(member);
                    if (seg.Name == "DataContext" && currentElement != null && DataTypeOf(currentElement) is { } dc) memberType = dc;
                    _r.Refs.Add(new SymbolRef { Start = Abs(seg.Start), End = Abs(seg.End), Symbol = member, Type = memberType });
                    current = memberType;
                    currentElement = null;
                    break;
                }

                case PathSegmentKind.Indexer:
                    current = current == null ? null : Members.IndexerType(current);
                    currentElement = null;
                    break;

                case PathSegmentKind.Attached:
                {
                    currentElement = null;
                    if (seg.TypeName == null || text == null)
                    {
                        current = null;
                        break;
                    }
                    var owner = ResolveTypeName(el, Slice(text, seg.TypeStart, seg.TypeName.Length), report: validate);
                    if (owner == null)
                    {
                        current = null;
                        break;
                    }
                    var field = Members.FindAttached(owner, seg.Name);
                    if (field == null)
                    {
                        if (validate && !Members.HasUnknownBase(owner))
                            Report(Abs(seg.NameStart), Abs(seg.NameStart + seg.Name.Length), Codes.UnknownBindingMember,
                                $"Unable to find attached property '{seg.Name}' on type '{Members.FullDisplay(owner)}'.");
                        return null;
                    }
                    current = Members.ValueTypeOf(field);
                    _r.Refs.Add(new SymbolRef { Start = Abs(seg.NameStart), End = Abs(seg.NameStart + seg.Name.Length), Symbol = field, Type = current });
                    break;
                }

                case PathSegmentKind.Cast:
                    current = seg.TypeName != null && text != null ? ResolveTypeName(el, Slice(text, seg.TypeStart, seg.TypeName.Length), report: validate) : null;
                    currentElement = null;
                    break;

                case PathSegmentKind.Stream:
                    current = current == null ? null : Members.StreamType(current);
                    currentElement = null;
                    break;
            }
        }
        if (negated && _p != null) return _p.Compilation.GetSpecialType(SpecialType.System_Boolean);
        return current;
    }

    /// <summary>
    /// The type a binding's path starts from (x:DataType, ElementName, RelativeSource…), for the binding whose markup extension
    /// name starts at <paramref name="anchorStart"/>. Null when unknown.
    /// </summary>
    public ITypeSymbol? BindingOriginAt(int anchorStart) => _bindingOrigins.GetValueOrDefault(anchorStart);

    /// <summary>The x:DataType in effect at an element (also for elements not visited yet, e.g. a later #name).</summary>
    public ITypeSymbol? DataTypeOf(XmlElement el)
    {
        if (_visited.Contains(el)) return _r.Elements[el].Scope?.DataType;
        var chain = el.SelfAndAncestors().Reverse().ToList();
        var s = Scope.Root(_compiledDefault);
        foreach (var e in chain)
        {
            if (_r.Elements[e].Scope is { } known && _visited.Contains(e))
            {
                s = known;
                continue;
            }
            s = WithDataContext(e, _r.Elements[e], ScopeFor(e, _r.Elements[e], s, report: false));
        }
        return s.DataType;
    }

    /// <summary>The scope at an element for completion (computed even when the element was not visited).</summary>
    public Scope ScopeAt(XmlElement el) =>
        _visited.Contains(el) && _r.Elements[el].Scope is { } s ? s : new Scope(DataTypeOf(el), true, _compiledDefault, null);

    /// <summary>
    /// False for a binding hosted by a non-visual object such as a Setter: there "self" and the ancestors are
    /// those of the styled control, which the XAML tree does not show.
    /// </summary>
    private bool IsControlLike(XmlElement el)
    {
        var type = _r.Elements[el].Type;
        return type == null ? el.LocalName != "Setter" : Members.InheritsByName(type, "StyledElement", "Visual");
    }

    private XmlElement? FindAncestor(XmlElement el, ITypeSymbol? type, int level)
    {
        var count = 0;
        for (var a = el.Parent; a != null; a = a.Parent)
        {
            var info = _r.Elements[a];
            if (info.IsPropertyElement || info.Ignored) continue;
            if (type == null || info.Type != null && Members.IsSameOrDerived(info.Type, type))
            {
                if (++count >= level) return a;
            }
        }
        return null;
    }

    // ---------------------------------------------------------------- type names

    private ITypeSymbol? ResolveTypeAttribute(XmlElement el, XmlAttr attr, bool report)
    {
        if (MarkupExtension.IsMarkup(attr.Value))
        {
            var ext = MarkupExtension.Parse(MappedText.FromAttribute(attr));
            if (ext is { LocalName: "Type" })
            {
                var arg = (ext.Named("TypeName") ?? ext.Positional(0))?.Value;
                return arg != null ? ResolveTypeName(el, arg, report) : null;
            }
            return null;
        }
        return ResolveTypeName(el, MappedText.FromAttribute(attr), report);
    }

    /// <summary>Resolves "prefix:Name" (or "Name" in the default namespace) in the context of an element.</summary>
    public INamedTypeSymbol? ResolveTypeName(XmlElement el, MappedText text, bool report)
    {
        if (_p == null) return null;
        var raw = text.Text;
        var name = raw.Trim();
        if (name.Length == 0) return null;
        var lead = raw.IndexOf(name, StringComparison.Ordinal);
        var start = text.Abs(lead);
        var end = text.Abs(lead + name.Length);
        var colon = name.IndexOf(':');
        var prefix = colon > 0 ? name[..colon] : "";
        var local = colon > 0 ? name[(colon + 1)..] : name;
        var ns = el.LookupNamespace(prefix);
        var type = _p.ResolveType(ns, local);
        if (type == null)
        {
            if (report && _p.IsCheckable(ns))
                Report(start, end, Codes.UnknownType, ns == null ? $"Undeclared namespace prefix '{prefix}'." : $"Unable to resolve type '{local}' from namespace '{ns}'.", data: ns);
            return null;
        }
        AddTypeRef(start, end, type);
        return type;
    }

    private static MappedText Slice(MappedText text, int start, int length) =>
        new(text.Text.Substring(start, length), [.. Enumerable.Range(start, length + 1).Select(text.Abs)]);

    // ---------------------------------------------------------------- helpers

    /// <summary>An attribute in the XAML (x:) namespace, whatever prefix the document uses for it.</summary>
    public static XmlAttr? XAttr(XmlElement el, string localName)
    {
        foreach (var a in el.Attributes)
        {
            if (a.Prefix != null && a.LocalName == localName && el.LookupNamespace(a.Prefix) == XamlNamespaces.X) return a;
        }
        return null;
    }

    private bool IsIgnorable(XmlElement el, string ns)
    {
        if (ns is XamlNamespaces.MarkupCompatibility or XamlNamespaces.Blend) return true;
        if (_ignorablePrefixes == null)
        {
            _ignorablePrefixes = [];
            foreach (var e in _doc.AllElements)
            {
                foreach (var a in e.Attributes)
                {
                    if (a.LocalName == "Ignorable" && a.Prefix != null && e.LookupNamespace(a.Prefix) == XamlNamespaces.MarkupCompatibility)
                    {
                        foreach (var p in a.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)) _ignorablePrefixes.Add(p);
                    }
                }
            }
        }
        foreach (var prefix in _ignorablePrefixes)
        {
            if (el.LookupNamespace(prefix) == ns) return true;
        }
        return false;
    }

    private void AddTypeRef(int start, int end, ITypeSymbol type) =>
        _r.Refs.Add(new SymbolRef { Start = start, End = end, Symbol = type, Type = type });

    private void Report(int start, int end, string code, string message, Severity severity = Severity.Error, object? data = null) =>
        _r.Diagnostics.Add(new XamlDiagnostic(start, Math.Max(end, start), severity, code, message) { Data = data });
}
