using AxamlLs.Lsp;

namespace AxamlLs.Analysis;

/// <summary>A file or folder that is being renamed or moved.</summary>
public sealed record FileMove(string Old, string New);

/// <summary>
/// The edits that keep path values pointing at their files when files and folders move (features plan F4).
/// <list type="bullet">
/// <item>A reference to a moved file, or to anything under a moved folder, is rewritten in its written form: absolute
/// stays absolute, <c>avares://</c> keeps its assembly, relative is computed from where the referencing file will be.</item>
/// <item>A moved .axaml file's own relative references are rewritten even when their targets stay.</item>
/// <item>Refused (left as written, P7): a target that would leave the project, and values written with <c>%</c>
/// escapes, which a rewrite would not reproduce.</item>
/// </list>
/// Edits are in the files' current (pre-move) paths and offsets, as <c>workspace/willRenameFiles</c> expects.
/// </summary>
public static class FileMoves
{
    /// <summary>Where <paramref name="path"/> will be: itself moved, or under a moved folder; null when it stays.</summary>
    public static string? Moved(string path, IReadOnlyList<FileMove> moves)
    {
        foreach (var m in moves)
        {
            if (string.Equals(path, m.Old, StringComparison.Ordinal)) return m.New;
            var folder = Path.TrimEndingDirectorySeparator(m.Old) + Path.DirectorySeparatorChar;
            if (path.StartsWith(folder, StringComparison.Ordinal)) return Path.Combine(m.New, path[folder.Length..]);
        }
        return null;
    }

    /// <param name="moves">The batch, with full paths.</param>
    /// <param name="projectRoot">The project folder.</param>
    /// <param name="files">Each .axaml file's path and its path references (from <see cref="AssetReferences.Find"/>).</param>
    public static IEnumerable<FileEdit> EditsFor(IReadOnlyList<FileMove> moves, string projectRoot, IEnumerable<(string Path, IReadOnlyList<AssetReference> References)> files)
    {
        var root = Path.TrimEndingDirectorySeparator(projectRoot);
        foreach (var (path, references) in files)
        {
            var newFile = Moved(path, moves) ?? path;
            foreach (var r in references)
            {
                var newTarget = Moved(r.Resolved, moves) ?? r.Resolved;
                var targetMoves = !string.Equals(newTarget, r.Resolved, StringComparison.Ordinal);
                var fileMoves = !string.Equals(newFile, path, StringComparison.Ordinal);
                if (!targetMoves && !(fileMoves && r.Form == PathForm.Relative)) continue;
                if (r.Written.Contains('%') || !newTarget.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
                if (Rewrite(r, newTarget, newFile, root) is { } written && written != r.Written) yield return new FileEdit(path, r.Start, r.End, written);
            }
        }
    }

    /// <summary>
    /// Views renamed together with their code-behind in the same batch (<c>Foo.axaml</c> → <c>Bar.axaml</c> and
    /// <c>Foo.axaml.cs</c> → <c>Bar.axaml.cs</c>): the class may follow, as (view's current path, old name, new name).
    /// A view moved alone, or only to another folder, gives nothing.
    /// </summary>
    public static IEnumerable<(string View, string OldName, string NewName)> RenamedViews(IReadOnlyList<FileMove> moves)
    {
        foreach (var m in moves)
        {
            if (!m.Old.EndsWith(".axaml", StringComparison.Ordinal) || !m.New.EndsWith(".axaml", StringComparison.Ordinal)) continue;
            if (!moves.Any(c => c.Old == m.Old + ".cs" && c.New == m.New + ".cs")) continue;
            var (oldName, newName) = (Path.GetFileNameWithoutExtension(m.Old), Path.GetFileNameWithoutExtension(m.New));
            if (oldName != newName) yield return (m.Old, oldName, newName);
        }
    }

    private static string? Rewrite(AssetReference r, string target, string newFile, string root)
    {
        var hash = r.Written.IndexOf('#');
        var fragment = hash >= 0 ? r.Written[hash..] : "";
        var fromRoot = Path.GetRelativePath(root, target).Replace('\\', '/');
        switch (r.Form)
        {
            case PathForm.Absolute:
                return "/" + fromRoot + fragment;
            case PathForm.Avares:
                var slash = r.Written.IndexOf('/', "avares://".Length);
                return slash < 0 ? null : r.Written[..(slash + 1)] + fromRoot + fragment;
            default:
                var relative = Path.GetRelativePath(Path.GetDirectoryName(newFile)!, target).Replace('\\', '/');
                if (r.Written.StartsWith("./", StringComparison.Ordinal) && !relative.StartsWith("../", StringComparison.Ordinal)) relative = "./" + relative;
                return relative + fragment;
        }
    }
}
