namespace AxamlLs.Analysis;

/// <summary>
/// What a "N references" lens on an <c>x:Key</c> says and where it jumps (features plan F11): uses are the occurrences
/// that aren't the declaration, and the jump goes to the first use in another file (Zed has no generic "show
/// references" command), else to the first use in this file, else to the declaration.
/// </summary>
public static class ResourceLenses
{
    public static string Title(int uses) => uses switch
    {
        0 => "unused",
        1 => "1 reference",
        _ => $"{uses} references",
    };

    public static int Uses(IEnumerable<Occurrence> occurrences) => occurrences.Count(o => !o.IsDeclaration);

    /// <summary>The occurrence to open: a use outside <paramref name="declaringPath"/> first, in path and position order.</summary>
    public static Occurrence? Jump(IEnumerable<Occurrence> occurrences, string declaringPath)
    {
        var ordered = occurrences.OrderBy(o => o.Path, StringComparer.Ordinal).ThenBy(o => o.Start).ToList();
        return ordered.FirstOrDefault(o => !o.IsDeclaration && o.Path != declaringPath)
            ?? ordered.FirstOrDefault(o => !o.IsDeclaration)
            ?? ordered.FirstOrDefault(o => o.IsDeclaration);
    }
}
