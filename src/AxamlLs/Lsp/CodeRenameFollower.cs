using AxamlLs.Analysis;
using AxamlLs.Workspace;

namespace AxamlLs.Lsp;

/// <summary>
/// A saved .cs file renamed declarations (the C# server's rename, or by hand): make the project's AXAML use the new names.
/// Called before the project is rebuilt, so the AXAML is still analysed against the old code and finds the old names.
/// Open files change through workspace/applyEdit (undoable, left unsaved); closed ones are rewritten on disk, but only
/// when the disk still holds the text that was analysed.
/// </summary>
internal sealed class CodeRenameFollower(Documents documents, LspClient client, EditorFileWatcher watcher)
{
    public async Task FollowAsync(ProjectHost host, IReadOnlyList<CodeRename> renames)
    {
        var buffers = documents.OpenBuffers();
        var files = documents.ProjectFiles(host, buffers);
        var edits = CodeRenames.AxamlEdits(renames, files);
        if (edits.Count == 0) return;

        var byPath = files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var openEdits = new List<FileEdit>();
        var applied = 0;
        foreach (var group in edits.GroupBy(e => e.Path, StringComparer.Ordinal))
        {
            if (buffers.ContainsKey(group.Key))
            {
                openEdits.AddRange(group);
                continue;
            }
            var written = await RewriteClosedFileAsync(group.Key, byPath[group.Key].Text, [.. group]).ConfigureAwait(false);
            // The editor should now report this file changed; if it never does, its file events can't be relied on.
            if (written > 0) watcher.ExpectChange(group.Key);
            applied += written;
        }
        if (openEdits.Count > 0)
        {
            await client.ApplyEditAsync("Follow C# rename", Edits.ToWorkspaceEdit(openEdits, path => (byPath[path].Text, byPath[path].Lines))).ConfigureAwait(false);
            applied += openEdits.Count;
        }
        if (applied == 0) return;

        var names = string.Join(", ", renames.Where(r => edits.Any(e => e.NewText == r.NewName)).Select(r => $"{r.OldSymbol.Name} → {r.NewName}").Distinct());
        await client.ShowMessageAsync(3, $"axaml-ls: renamed {names} in {applied} AXAML place{(applied == 1 ? "" : "s")}.").ConfigureAwait(false);
    }

    /// <summary>Applies edits to a file that isn't open, keeping its BOM; skips it if it changed since it was analysed.</summary>
    /// <returns>How many edits were applied.</returns>
    private static async Task<int> RewriteClosedFileAsync(string path, string analysedText, IReadOnlyList<FileEdit> edits)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            var bom = bytes is [0xEF, 0xBB, 0xBF, ..];
            var disk = new System.Text.UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
            if (disk != analysedText) return 0;
            await File.WriteAllTextAsync(path, Edits.Apply(disk, edits), new System.Text.UTF8Encoding(bom)).ConfigureAwait(false);
            return edits.Count;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not update {path} after a rename", ex);
            return 0;
        }
    }
}
