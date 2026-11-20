namespace AxamlLs.Analysis.Formatting;

/// <summary>When a start tag puts each attribute on its own line.</summary>
public enum AttributeLayout
{
    /// <summary>When the tag would be longer than the line limit, or has more attributes than the threshold.</summary>
    Auto,
    Always,
    Never,
}

/// <summary>Where attributes on their own lines start.</summary>
public enum AttributeIndent
{
    /// <summary>Under the first attribute (when it stays on the tag's line).</summary>
    AlignWithFirst,
    /// <summary>One indent deeper than the tag.</summary>
    Indent,
}

/// <summary>The user's formatting settings (<c>initializationOptions.formatting</c>, see README "Options").</summary>
/// <param name="AttributesOnSeparateLines">When attributes go on their own lines.</param>
/// <param name="AttributesOnSeparateLinesThreshold">With <see cref="AttributeLayout.Auto"/>: break when a tag has more attributes than this.</param>
/// <param name="FirstAttributeOnSameLine">Keep the first attribute on the tag's line when the others break.</param>
/// <param name="MaxLineLength">With <see cref="AttributeLayout.Auto"/>: break when the tag on one line would be longer.</param>
/// <param name="SpaceBeforeSelfClosingSlash"><c>&lt;Border /&gt;</c> instead of <c>&lt;Border/&gt;</c>.</param>
/// <param name="KeepBlankLines">Runs of blank lines between elements shrink to at most this many.</param>
/// <param name="IndentAttributesBy">Where broken attributes start.</param>
public sealed record FormattingSettings(
    AttributeLayout AttributesOnSeparateLines = AttributeLayout.Auto,
    int AttributesOnSeparateLinesThreshold = 3,
    bool FirstAttributeOnSameLine = true,
    int MaxLineLength = 120,
    bool SpaceBeforeSelfClosingSlash = true,
    int KeepBlankLines = 1,
    AttributeIndent IndentAttributesBy = AttributeIndent.AlignWithFirst)
{
    public static readonly FormattingSettings Default = new();
}

/// <summary>One formatting request: the editor's indentation (tab size, spaces or tabs) and the user's settings.</summary>
public sealed record FormattingOptions(int TabSize, bool InsertSpaces, FormattingSettings Settings)
{
    public string IndentUnit => InsertSpaces ? new string(' ', Math.Max(TabSize, 0)) : "\t";
}
