using System.Collections.Concurrent;
using AxamlLs.Xaml;

namespace AxamlLs.Workspace;

/// <summary>A class some selector styles, with the type of its compound when there is one (Button.accent → Button).</summary>
public sealed record StyledClass(string? TypeText, string Class);

/// <summary>A part of a project ControlTheme's template, keyed by the theme's TargetType as written.</summary>
public sealed record ProjectTemplatePart(string TargetText, string Name, string TypeText);

/// <summary>What the project's own files say about style classes and control templates.</summary>
public sealed class ProjectStyles
{
    public static readonly ProjectStyles Empty = new();

    /// <summary>Classes set on elements: Classes="a b", Classes.a="…" in .axaml, Classes.Add("a")-style calls in .cs.</summary>
    public HashSet<string> UsedClasses { get; } = new(StringComparer.Ordinal);
    /// <summary>Classes that selectors in the project's styles target.</summary>
    public HashSet<StyledClass> StyledClasses { get; } = [];
    public List<ProjectTemplatePart> TemplateParts { get; } = [];
    /// <summary>Resource keys the project's C# code sets or looks up: Resources["k"], TryGetResource("k"…).</summary>
    public HashSet<string> CodeResourceKeys { get; } = new(StringComparer.Ordinal);
    /// <summary>Pseudo-classes the project's C# code switches (PseudoClasses.Set(":x", …)), with the colon.</summary>
    public HashSet<string> CodePseudoClasses { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Scans the project's .axaml and .cs files (from disk with timestamp caching; open buffers win) for style classes
/// and ControlTheme template parts.
/// </summary>
public static class ClassIndex
{
    private sealed record FileFacts(string[] Used, StyledClass[] Styled, ProjectTemplatePart[] Parts, string[] Keys, string[] Pseudo);

    private static readonly ConcurrentDictionary<string, (DateTime Stamp, FileFacts Facts)> Cache = new(StringComparer.Ordinal);

    public static ProjectStyles ForDirectory(string directory, IReadOnlyDictionary<string, string> openBuffers)
    {
        var result = new ProjectStyles();
        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories)
                .Where(f => (f.EndsWith(".axaml", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal)) && !ProjectFiles.IsBuildOutput(f))
                .ToList();
        }
        catch (Exception)
        {
            files = [];
        }
        foreach (var file in files.Concat(openBuffers.Keys.Where(k => k.StartsWith(directory, StringComparison.Ordinal))).Distinct())
        {
            FileFacts facts;
            if (openBuffers.TryGetValue(file, out var text))
            {
                facts = ScanFile(file, text);
            }
            else
            {
                try
                {
                    var stamp = File.GetLastWriteTimeUtc(file);
                    if (!Cache.TryGetValue(file, out var cached) || cached.Stamp != stamp)
                    {
                        cached = (stamp, ScanFile(file, File.ReadAllText(file)));
                        Cache[file] = cached;
                    }
                    facts = cached.Facts;
                }
                catch (IOException)
                {
                    continue;
                }
            }
            result.UsedClasses.UnionWith(facts.Used);
            result.StyledClasses.UnionWith(facts.Styled);
            result.TemplateParts.AddRange(facts.Parts);
            result.CodeResourceKeys.UnionWith(facts.Keys);
            result.CodePseudoClasses.UnionWith(facts.Pseudo);
        }
        return result;
    }

    /// <summary>The classes a file sets on elements.</summary>
    public static string[] Scan(string file, string text) => ScanFile(file, text).Used;

    public static ProjectStyles ScanText(string file, string text)
    {
        var facts = ScanFile(file, text);
        var styles = new ProjectStyles();
        styles.UsedClasses.UnionWith(facts.Used);
        styles.StyledClasses.UnionWith(facts.Styled);
        styles.TemplateParts.AddRange(facts.Parts);
        styles.CodeResourceKeys.UnionWith(facts.Keys);
        styles.CodePseudoClasses.UnionWith(facts.Pseudo);
        return styles;
    }

    private static FileFacts ScanFile(string file, string text)
    {
        if (file.EndsWith(".cs", StringComparison.Ordinal))
        {
            // Classes.Add("a"), Classes.Set("a", on), new Classes("a", "b"), Classes.AddRange(["a", "b"]) …
            var found = CodeLiterals.Classes(text).Select(l => l.Name).ToHashSet(StringComparer.Ordinal);
            return new FileFacts([.. found], [], [],
                [.. CodeLiterals.ResourceKeys(text).Select(l => l.Name).Distinct()],
                [.. CodeLiterals.PseudoClasses(text).Select(l => ":" + l.Name).Distinct()]);
        }

        var used = new HashSet<string>(StringComparer.Ordinal);
        var styled = new HashSet<StyledClass>();
        var parts = new List<ProjectTemplatePart>();
        foreach (var el in XmlDoc.Parse(text).AllElements)
        {
            foreach (var a in el.Attributes)
            {
                if (a.Name == "Classes" && a.HasValue && !MarkupExtension.IsMarkup(a.Value))
                    used.UnionWith(a.Value.Split([' ', '\t', '\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries));
                else if (a.Name.StartsWith("Classes.", StringComparison.Ordinal))
                    used.Add(a.Name[8..]);
            }
            // <Setter Property="Classes" Value="a b"/> is rare but legal.
            if (el.LocalName == "Setter" && el.Attr("Property")?.Value == "Classes" && el.Attr("Value") is { HasValue: true } v)
                used.UnionWith(v.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries));

            if (el.LocalName == "Style" && el.Attr("Selector") is { HasValue: true } selector)
                styled.UnionWith(Analysis.Selectors.ClassesWithTypes(selector.Value).Select(c => new StyledClass(c.TypeText, c.Class)));

            // Template parts: a ControlTheme's template, a Style's <Setter Property="Template"> (the template of the
            // selector's target, e.g. RadioButton.segment → RadioButton), or a <ControlTemplate TargetType="…"> on its own.
            if (el.LocalName == "ControlTemplate")
            {
                foreach (var target in TemplateTargets(el))
                {
                    foreach (var part in el.Descendants())
                    {
                        var name = part.Attributes.FirstOrDefault(a => a.LocalName == "Name" && a.HasValue);
                        if (name != null && !MarkupExtension.IsMarkup(name.Value)) parts.Add(new ProjectTemplatePart(target, name.Value, part.Name));
                    }
                }
            }
        }
        return new FileFacts([.. used], [.. styled], [.. parts], [], []);
    }

    /// <summary>The control types a ControlTemplate is for, as written; none when it can't be told.</summary>
    private static IEnumerable<string> TemplateTargets(XmlElement template)
    {
        if (template.Attr("TargetType") is { HasValue: true } own && !string.IsNullOrWhiteSpace(own.Value))
            return [TypeText(own.Value)];
        // Walk up to what owns it; a template nested in another template is part of that one, not a control's.
        for (var e = template.Parent; e != null; e = e.Parent)
        {
            if (e.LocalName == "ControlTemplate") return [];
            if (e.LocalName == "ControlTheme")
                return e.Attr("TargetType") is { HasValue: true } t ? [TypeText(t.Value)] : [];
            if (e.LocalName == "Setter" && e.Attr("Property")?.Value != "Template") return [];
            if (e.LocalName == "Style") return StyleTargets(e);
        }
        return [];
    }

    /// <summary>The enclosing Style or ControlTheme of a nested style.</summary>
    private static XmlElement? Owner(XmlElement style) =>
        style.Parent?.SelfAndAncestors().FirstOrDefault(a => a.LocalName is "Style" or "ControlTheme");

    /// <summary>The types a Style's selector targets; a nested "^…" style inherits its parent style's (or ControlTheme's).</summary>
    private static List<string> StyleTargets(XmlElement style)
    {
        var result = new List<string>();
        if (style.Attr("Selector") is not { HasValue: true } selector) return result;
        foreach (var (type, nested) in Analysis.Selectors.TargetTypes(selector.Value))
        {
            if (type != null) result.Add(type);
            else if (nested && Owner(style) is { } owner)
            {
                if (owner.LocalName == "Style") result.AddRange(StyleTargets(owner));
                else if (owner.Attr("TargetType") is { HasValue: true } t) result.Add(TypeText(t.Value));
            }
        }
        return result.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>"Button", "local:Foo" or "{x:Type local:Foo}" → the type name as written.</summary>
    private static string TypeText(string value)
    {
        var v = value.Trim();
        if (v.StartsWith('{') && v.EndsWith('}'))
        {
            var inner = v[1..^1].Trim();
            var space = inner.IndexOf(' ');
            v = space > 0 ? inner[(space + 1)..].Trim() : inner;
            if (v.StartsWith("TypeName=", StringComparison.Ordinal)) v = v[9..];
        }
        return v;
    }
}
