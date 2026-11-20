using System.Text.Json.Nodes;
using AxamlLs.Analysis;
using AxamlLs.Workspace;

namespace AxamlLs.Lsp.Handlers;

/// <summary>
/// <c>textDocument/codeLens</c>: lenses on the root element that jump to the view model and to the code-behind
/// (Zed shows them with "code_lens": "on"), and an unresolved "references" lens on each <c>x:Key</c>, counted only
/// when the editor resolves it.
/// </summary>
internal sealed class CodeLensHandler(Documents documents, Session session) : SyncRequestHandler
{
    public override string Method => "textDocument/codeLens";

    public override void Advertise(JsonObject capabilities) =>
        capabilities["codeLensProvider"] = new JsonObject { ["resolveProvider"] = true };

    protected override JsonNode? Handle(JsonObject p)
    {
        var array = new JsonArray();
        if (!documents.TryAnalyze(p, out var doc, out var analysis)) return array;
        var analyzer = analysis.Analyzer;
        if (analyzer.Result.Doc.Root is not { } root) return array;
        void Add(string title, DefinitionTarget? target)
        {
            if (target?.FilePath == null) return;
            array.Add(new JsonObject
            {
                ["range"] = Protocol.Range(analysis.Lines, root.NameStart, root.NameEnd),
                ["command"] = OpenFileCommand.Create(title, target),
            });
        }
        var viewModel = Navigation.ViewModel(analyzer).FirstOrDefault();
        if (viewModel != null && analyzer.ScopeAt(root).DataType is { } dataType) Add($"→ {dataType.Name}", viewModel);
        Add("→ code-behind", Navigation.CodeBehind(analyzer).FirstOrDefault());

        if (!session.Options.CodeLens.ResourceReferences) return array;
        foreach (var key in XamlSymbols.Scan(doc.Path, analyzer.Result.Doc).Where(s => s.Kind == XamlSymbolKind.ResourceKey))
        {
            array.Add(new JsonObject
            {
                ["range"] = Protocol.Range(analysis.Lines, key.Start, key.End),
                ["data"] = new JsonObject { ["uri"] = Protocol.DocumentUri(p), ["line"] = key.Line, ["character"] = key.Character },
            });
        }
        return array;
    }
}

/// <summary>
/// <c>codeLens/resolve</c>: counts a key's uses with the find-references search and points the lens at the first use
/// in another file. Lenses that already have a command come back as they are.
/// </summary>
internal sealed class CodeLensResolveHandler(Documents documents, TargetLookup targets) : IRequestHandler
{
    public string Method => "codeLens/resolve";

    public void Advertise(JsonObject capabilities)
    {
    }

    public async Task<JsonNode?> HandleAsync(JsonObject parameters, CancellationToken ct)
    {
        // A copy: the parameters still belong to the request message, and a node can have only one parent.
        var lens = parameters.DeepClone().AsObject();
        if (lens["command"] != null || lens["data"] is not JsonObject data) return lens;
        var at = new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = (string?)data["uri"] },
            ["position"] = new JsonObject { ["line"] = (int?)data["line"] ?? 0, ["character"] = (int?)data["character"] ?? 0 },
        };
        if (await targets.AtAsync(at, ct).ConfigureAwait(false) is not { Found.Target: ResourceKeyTarget } ctx)
        {
            // The document changed under the lens: show nothing rather than a wrong count.
            lens["command"] = new JsonObject { ["title"] = "", ["command"] = "" };
            return lens;
        }
        var occurrences = await Targets.OccurrencesAsync(ctx.Found.Target, ctx.Origin, ctx.Files, ctx.Host, ct).ConfigureAwait(false);
        var title = ResourceLenses.Title(ResourceLenses.Uses(occurrences));
        if (ResourceLenses.Jump(occurrences, ctx.Origin.Path) is { } jump)
        {
            var (line, character) = documents.TextProvider()(jump.Path).Lines.GetPosition(jump.Start);
            lens["command"] = OpenFileCommand.Create(title, jump.Path, line, character);
        }
        else
        {
            lens["command"] = new JsonObject { ["title"] = title, ["command"] = "" };
        }
        return lens;
    }
}

/// <summary>The <c>axaml.openFile</c> command the lenses run: the editor opens a file at a line.</summary>
internal static class OpenFileCommand
{
    public const string Name = "axaml.openFile";

    public static JsonObject Create(string title, DefinitionTarget target) => Create(title, target.FilePath!, target.StartLine, target.StartCharacter);

    public static JsonObject Create(string title, string path, int line, int character) => new()
    {
        ["title"] = title,
        ["command"] = Name,
        ["arguments"] = new JsonArray(Protocol.PathToUri(path), line, character),
    };
}

/// <summary><c>workspace/executeCommand</c>: runs <see cref="OpenFileCommand"/> through <c>window/showDocument</c>.</summary>
internal sealed class ExecuteCommandHandler(LspClient client) : IRequestHandler
{
    public string Method => "workspace/executeCommand";

    public void Advertise(JsonObject capabilities) =>
        capabilities["executeCommandProvider"] = new JsonObject { ["commands"] = new JsonArray(OpenFileCommand.Name) };

    public async Task<JsonNode?> HandleAsync(JsonObject p, CancellationToken ct)
    {
        if ((string?)p["command"] != OpenFileCommand.Name || p["arguments"] is not JsonArray { Count: >= 1 } args) return null;
        var line = args.Count > 1 ? (int?)args[1] ?? 0 : 0;
        var character = args.Count > 2 ? (int?)args[2] ?? 0 : 0;
        await client.ShowDocumentAsync((string?)args[0] ?? "", line, character).ConfigureAwait(false);
        return null;
    }
}
