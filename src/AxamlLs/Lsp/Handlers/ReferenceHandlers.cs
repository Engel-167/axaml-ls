using System.Text.Json.Nodes;
using AxamlLs.Analysis;
using AxamlLs.Workspace;

namespace AxamlLs.Lsp.Handlers;

/// <summary>What find-references and rename work with: the target under the cursor, the project's files, its host.</summary>
internal sealed record TargetContext(OpenDocument Doc, XamlFile Origin, IReadOnlyList<XamlFile> Files, ProjectHost? Host, TargetAt Found);

/// <summary>Finds the target (element name, resource key, style class, C# symbol) under a request's position.</summary>
internal sealed class TargetLookup(Documents documents, ProjectLoadProgress progress)
{
    public async Task<TargetContext?> AtAsync(JsonObject p)
    {
        if (!documents.TryGet(Protocol.DocumentUri(p), out var doc)) return null;
        var host = documents.HostFor(doc);
        if (host != null)
        {
            progress.Announce(host);
            await host.EnsureLoadedAsync().ConfigureAwait(false);
        }
        var analysis = documents.Analyze(doc);
        var offset = Protocol.Offset(analysis.Lines, p["position"]!);
        if (Targets.Find(analysis.Analyzer, offset) is not { } found) return null;
        var origin = new XamlFile(doc.Path, analysis.Analyzer.Result.Doc.Text, analysis.Analyzer, analysis.Lines);
        // Only files that can mention the target are analysed (an element name needs none: it stays in its file).
        IReadOnlyList<XamlFile> files = host?.Current != null ? documents.ProjectFiles(host, mentioning: Targets.CandidateNames(found, origin.Text)) : [origin];
        return new TargetContext(doc, origin, files, host, found);
    }
}

/// <summary><c>textDocument/references</c>: every use of the target across the project's AXAML and C#.</summary>
internal sealed class ReferencesHandler(Documents documents, TargetLookup targets) : IRequestHandler
{
    public string Method => "textDocument/references";

    public void Advertise(JsonObject capabilities) => capabilities["referencesProvider"] = true;

    public async Task<JsonNode?> HandleAsync(JsonObject p, CancellationToken ct)
    {
        var array = new JsonArray();
        if (await targets.AtAsync(p).ConfigureAwait(false) is not { } ctx) return array;
        var includeDeclaration = (bool?)p["context"]?["includeDeclaration"] ?? true;
        var textOf = documents.TextProvider();
        foreach (var o in await Targets.OccurrencesAsync(ctx.Found.Target, ctx.Origin, ctx.Files, ctx.Host).ConfigureAwait(false))
        {
            if (o.IsDeclaration && !includeDeclaration) continue;
            array.Add(new JsonObject { ["uri"] = Protocol.PathToUri(o.Path), ["range"] = Protocol.Range(textOf(o.Path).Lines, o.Start, o.End) });
        }
        return array;
    }
}

/// <summary><c>textDocument/prepareRename</c>: the range and current name, when the target can be renamed here.</summary>
internal sealed class PrepareRenameHandler(TargetLookup targets) : IRequestHandler
{
    public string Method => "textDocument/prepareRename";

    public void Advertise(JsonObject capabilities)
    {
    }

    public async Task<JsonNode?> HandleAsync(JsonObject p, CancellationToken ct)
    {
        if (await targets.AtAsync(p).ConfigureAwait(false) is not { } ctx || !Rename.CanRename(ctx.Found.Target)) return null;
        return new JsonObject
        {
            ["range"] = Protocol.Range(ctx.Origin.Lines, ctx.Found.Start, ctx.Found.End),
            ["placeholder"] = ctx.Origin.Text[ctx.Found.Start..ctx.Found.End],
        };
    }
}

/// <summary><c>textDocument/rename</c>: edits across AXAML and C#, limited to files inside the project folder.</summary>
internal sealed class RenameHandler(Documents documents, TargetLookup targets) : IRequestHandler
{
    public string Method => "textDocument/rename";

    public void Advertise(JsonObject capabilities) => capabilities["renameProvider"] = new JsonObject { ["prepareProvider"] = true };

    public async Task<JsonNode?> HandleAsync(JsonObject p, CancellationToken ct)
    {
        var newName = (string?)p["newName"] ?? "";
        if (await targets.AtAsync(p).ConfigureAwait(false) is not { } ctx) return null;
        if (!Rename.CanRename(ctx.Found.Target)) throw new InvalidParamsException("This name is declared in a library and cannot be renamed here.");
        var result = await Rename.ComputeAsync(ctx.Found.Target, newName, ctx.Origin, ctx.Files, ctx.Host).ConfigureAwait(false);
        if (result.Error != null) throw new InvalidParamsException(result.Error);
        var projectDir = ctx.Host?.ProjectDirectory ?? ctx.Origin.Analyzer.ProjectDirectory;
        return Edits.ToWorkspaceEdit(Rename.InsideProject(result.Edits, projectDir), documents.TextProvider());
    }
}
