using AxamlLs.Xaml;

namespace AxamlLs.Analysis;

/// <summary>A {StaticResource Key} / {DynamicResource Key} use, with the span of the key.</summary>
public sealed record ResourceUse(bool IsStatic, string Key, int Start, int End);

/// <summary>Resource keys that are used but declared nowhere (AXN0205).</summary>
public static class ResourceCheck
{
    /// <summary>Every StaticResource/DynamicResource key in the document, nested extensions included. Keys that are themselves markup are skipped.</summary>
    public static IEnumerable<ResourceUse> Uses(XamlAnalyzer analyzer)
    {
        foreach (var el in analyzer.Result.Doc.AllElements)
        {
            if (analyzer.Result.Elements.TryGetValue(el, out var info) && info.Ignored) continue;
            foreach (var attr in el.Attributes)
            {
                if (!attr.HasValue || attr.Name.StartsWith("xmlns", StringComparison.Ordinal) || !MarkupExtension.IsMarkup(attr.Value)) continue;
                if (MarkupExtension.Parse(MappedText.FromAttribute(attr)) is not { } ext) continue;
                foreach (var e in ext.SelfAndNested())
                {
                    if (e.LocalName is not ("StaticResource" or "DynamicResource")) continue;
                    if (e.Prefix != null && el.LookupNamespace(e.Prefix) != Workspace.XamlNamespaces.Avalonia) continue;
                    if ((e.Named("ResourceKey") ?? e.Positional(0)) is not { Value: { } value }) continue;
                    var key = value.Text.Trim();
                    if (key.Length == 0) continue;
                    var lead = value.Text.IndexOf(key, StringComparison.Ordinal);
                    yield return new ResourceUse(e.LocalName == "StaticResource", key, value.Abs(lead), value.Abs(lead + key.Length));
                }
            }
        }
    }

    /// <summary>The keys that count as declared: the project's .axaml, compiled themes and libraries, and keys set in C#.</summary>
    public static HashSet<string> KnownKeys(XamlAnalyzer analyzer)
    {
        var known = analyzer.Resources().Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        known.UnionWith(analyzer.Styles().CodeResourceKeys);
        return known;
    }

    public static IEnumerable<XamlDiagnostic> Unknown(XamlAnalyzer analyzer)
    {
        // Without the project the library keys are unknown, so any key could be one of them.
        if (analyzer.Project == null) yield break;
        var known = KnownKeys(analyzer);
        foreach (var use in Uses(analyzer))
        {
            if (known.Contains(use.Key)) continue;
            var message = $"Resource '{use.Key}' is not declared in this project or its themes.";
            if (Spelling.Closest(use.Key, known, 1) is [var near]) message += $" Did you mean '{near}'?";
            // A static key is resolved at load time; a dynamic one may legitimately be added at runtime.
            yield return new XamlDiagnostic(use.Start, use.End, use.IsStatic ? Severity.Warning : Severity.Information, Codes.UnknownResource, message);
        }
    }
}
