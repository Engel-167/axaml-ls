using System.Text.Json.Nodes;
using AxamlLs.Analysis;
using AxamlLs.Workspace;

namespace AxamlLs.Lsp.Handlers;

/// <summary>
/// <c>workspace/willRenameFiles</c>: before the editor renames or moves files and folders, the edits that keep path
/// values (and, with <c>renameFiles.updateClass</c>, a renamed view's class) pointing at them.
/// </summary>
internal sealed class WillRenameFilesHandler(Documents documents, WorkspaceManager workspace, Session session) : IRequestHandler
{
    public string Method => "workspace/willRenameFiles";

    public void Advertise(JsonObject capabilities)
    {
        var workspaceCaps = capabilities["workspace"] as JsonObject ?? new JsonObject();
        capabilities["workspace"] = workspaceCaps;
        workspaceCaps["fileOperations"] = new JsonObject
        {
            ["willRename"] = new JsonObject
            {
                ["filters"] = new JsonArray(new JsonObject { ["scheme"] = "file", ["pattern"] = new JsonObject { ["glob"] = "**/*" } }),
            },
        };
    }

    public async Task<JsonNode?> HandleAsync(JsonObject p, CancellationToken ct)
    {
        var moves = (p["files"] as JsonArray ?? [])
            .Select(f => (Old: (string?)f?["oldUri"], New: (string?)f?["newUri"]))
            .Where(f => f.Old != null && f.New != null && f.Old.StartsWith("file:", StringComparison.Ordinal) && f.New.StartsWith("file:", StringComparison.Ordinal))
            .Select(f => new FileMove(Path.GetFullPath(Protocol.UriToPath(f.Old!)), Path.GetFullPath(Protocol.UriToPath(f.New!))))
            .ToList();
        if (moves.Count == 0) return null;

        var edits = new List<FileEdit>();
        foreach (var host in moves.Select(m => workspace.GetHostFor(m.Old)).OfType<ProjectHost>().Distinct())
        {
            ct.ThrowIfCancellationRequested();
            await host.EnsureLoadedAsync(ct).ConfigureAwait(false);
            if (host.ProjectDirectory is not { } root) continue;
            var inProject = moves.Where(m => m.Old.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)).ToList();
            if (inProject.Count == 0) continue;
            var files = documents.ProjectFiles(host, mentioning: Mentioning(inProject), ct: ct);
            var hostEdits = FileMoves.EditsFor(inProject, root, files.Select(f => (f.Path, AssetReferences.Find(f.Analyzer)))).ToList();
            if (session.Options.RenameFiles.UpdateClass) hostEdits.AddRange(await ClassRenamesAsync(inProject, files, host, ct).ConfigureAwait(false));
            edits.AddRange(Rename.InsideProject(hostEdits, root));
        }
        return edits.Count == 0 ? null : Edits.ToWorkspaceEdit(edits, documents.TextProvider());
    }

    /// <summary>
    /// The files to read: those that may mention a moved file's name. A moved folder or .axaml file needs them all (its
    /// relative references, or references through the folder's name, can be anywhere).
    /// </summary>
    private static string[]? Mentioning(IReadOnlyList<FileMove> moves) =>
        moves.Any(m => Directory.Exists(m.Old) || m.Old.EndsWith(".axaml", StringComparison.Ordinal))
            ? null
            : [.. moves.Select(m => Path.GetFileName(m.Old))];

    private static async Task<IReadOnlyList<FileEdit>> ClassRenamesAsync(IReadOnlyList<FileMove> moves, IReadOnlyList<XamlFile> files, ProjectHost host, CancellationToken ct)
    {
        var edits = new List<FileEdit>();
        foreach (var (view, oldName, newName) in FileMoves.RenamedViews(moves))
        {
            if (files.FirstOrDefault(f => f.Path == view) is not { } origin || origin.Analyzer.Result.Doc.Root is not { } root) continue;
            if (Analysis.XamlAnalyzer.XAttr(root, "Class") is not { HasValue: true } cls) continue;
            var full = cls.Value.Trim();
            if (full[(full.LastIndexOf('.') + 1)..] != oldName) continue; // the class isn't named after the file: leave it
            // On the simple name's last character: the class itself, not its namespace.
            var at = cls.Map[cls.Value.TrimEnd().Length - 1];
            if (Targets.Find(origin.Analyzer, at) is not { Target: SymbolTarget target }) continue;
            var result = await Rename.ComputeAsync(target, newName, origin, files, host, ct).ConfigureAwait(false);
            if (result.Error == null) edits.AddRange(result.Edits);
            else Log.Info($"Not renaming class {full}: {result.Error}");
        }
        return edits;
    }
}
