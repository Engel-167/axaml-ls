using System.Text.RegularExpressions;

namespace AxamlLs.Workspace;

/// <summary>A string literal in C# code that names something XAML also names (a class, a resource key, a pseudo-class).</summary>
public readonly record struct CodeLiteral(string Name, int Start, int End);

/// <summary>
/// Finds the C# string literals that refer to XAML names: style classes (<c>Classes.Add("a")</c>), resource keys
/// (<c>Resources["k"]</c>, <c>TryGetResource("k"…)</c>) and pseudo-classes (<c>PseudoClasses.Set(":x", …)</c>).
/// Offsets point at the name inside the quotes, so a rename edit replaces exactly the name.
/// </summary>
public static partial class CodeLiterals
{
    /// <summary>Classes.Add("a"), Classes.Set("a", on), new Classes("a", "b"), Classes.AddRange(["a", "b"]) …</summary>
    public static IEnumerable<CodeLiteral> Classes(string text)
    {
        foreach (Match call in ClassesCall().Matches(text))
        {
            var args = call.Groups[1];
            foreach (Match lit in ClassLiteral().Matches(args.Value))
                yield return new CodeLiteral(lit.Groups[1].Value, args.Index + lit.Groups[1].Index, args.Index + lit.Groups[1].Index + lit.Groups[1].Length);
        }
    }

    /// <summary>Resources["k"] = …, Resources.Add("k", …), TryGetResource("k", …), FindResource("k").</summary>
    public static IEnumerable<CodeLiteral> ResourceKeys(string text)
    {
        foreach (Match m in ResourceKeyCall().Matches(text))
        {
            var g = m.Groups[1];
            yield return new CodeLiteral(g.Value, g.Index, g.Index + g.Length);
        }
    }

    /// <summary>PseudoClasses.Set(":x", on), Add(":x"), Remove(":x") …; the name keeps its colon out of the span.</summary>
    public static IEnumerable<CodeLiteral> PseudoClasses(string text)
    {
        foreach (Match m in PseudoClassCall().Matches(text))
        {
            var g = m.Groups[1];
            yield return new CodeLiteral(g.Value, g.Index, g.Index + g.Length);
        }
    }

    /// <summary>The argument list of a call on a Classes collection (up to the closing parenthesis or line end).</summary>
    [GeneratedRegex(@"\bClasses\s*(?:\.\s*(?:Add|AddRange|Set|Insert|Remove|Toggle|Contains|Replace)\s*|\)?\s*)\(([^)\n]*)")]
    private static partial Regex ClassesCall();

    [GeneratedRegex("\"([A-Za-z_][A-Za-z0-9_-]*)\"")]
    private static partial Regex ClassLiteral();

    [GeneratedRegex(@"\b(?:Resources\s*\[|Resources\s*\.\s*Add\s*\(|(?:Try)?(?:Find|Get)Resource\s*\(|TryFindResource\s*\()\s*""([^""\\\r\n]+)""")]
    private static partial Regex ResourceKeyCall();

    [GeneratedRegex(@"\bPseudoClasses\s*\.\s*(?:Set|Add|Remove|Toggle|Contains)\s*\(\s*"":?([A-Za-z_][A-Za-z0-9_-]*)""")]
    private static partial Regex PseudoClassCall();
}
