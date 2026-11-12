using System.Text.Json.Nodes;
using AxamlLs.Text;

namespace AxamlLs.Lsp;

/// <summary>A text replacement in one file, in offsets into that file's current text.</summary>
public sealed record FileEdit(string Path, int Start, int End, string NewText);

public static class Edits
{
    /// <summary>
    /// Builds <c>{ "changes": { uri: [ { range, newText } ] } }</c>. Edits are sorted per file, exact duplicates dropped,
    /// and of two overlapping edits only the first is kept (the rest are logged). Zed applies <c>changes</c> to files that are not open too.
    /// </summary>
    /// <param name="textOf">The text the offsets refer to: the open buffer if the file is open, else the disk text.</param>
    public static JsonObject ToWorkspaceEdit(IEnumerable<FileEdit> edits, Func<string, (string Text, LineIndex Lines)> textOf)
    {
        var changes = new JsonObject();
        foreach (var group in edits.Distinct().GroupBy(e => e.Path, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var (_, lines) = textOf(group.Key);
            var array = new JsonArray();
            var lastEnd = -1;
            foreach (var e in group.OrderBy(e => e.Start).ThenBy(e => e.End))
            {
                if (e.Start < lastEnd)
                {
                    Log.Info($"Dropping an overlapping edit at {e.Start}..{e.End} in {group.Key}");
                    continue;
                }
                lastEnd = Math.Max(lastEnd, e.End);
                array.Add(new JsonObject { ["range"] = Range(lines, e.Start, e.End), ["newText"] = e.NewText });
            }
            changes[LspServer.PathToUri(group.Key)] = array;
        }
        return new JsonObject { ["changes"] = changes };
    }

    public static JsonObject Range(LineIndex lines, int start, int end)
    {
        var (sl, sc) = lines.GetPosition(start);
        var (el, ec) = lines.GetPosition(end);
        return new JsonObject
        {
            ["start"] = new JsonObject { ["line"] = sl, ["character"] = sc },
            ["end"] = new JsonObject { ["line"] = el, ["character"] = ec },
        };
    }

    /// <summary>Applies one file's edits to its text (for tests and for checking edits).</summary>
    public static string Apply(string text, IEnumerable<FileEdit> edits)
    {
        foreach (var e in edits.OrderByDescending(e => e.Start).ThenByDescending(e => e.End))
            text = text[..e.Start] + e.NewText + text[e.End..];
        return text;
    }
}
