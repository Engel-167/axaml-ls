
namespace AxamlLs.Analysis;

/// <summary>LSP CompletionItemKind values (the numbers are the protocol's).</summary>
public enum CompletionItemKind
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

public sealed record CompletionItem(string Label, CompletionItemKind Kind, string? Detail, string? Documentation, string InsertText, bool IsSnippet, int ReplaceStart, int ReplaceEnd, string? SortText = null)
{
    /// <summary>#RRGGBB / #AARRGGBB: the item is sent as a color so the editor draws a swatch.</summary>
    public string? ColorHex { get; init; }
    /// <summary>A file whose preview is filled in on completionItem/resolve (only for the highlighted item).</summary>
    public string? PreviewPath { get; init; }
}
