using System.Text.Json.Nodes;
using AxamlLs.Analysis;

namespace AxamlLs.Lsp.Handlers;

/// <summary>
/// <c>textDocument/codeLens</c>: lenses on the root element that jump to the view model and to the code-behind
/// (Zed shows them with "code_lens": "on").
/// </summary>
internal sealed class CodeLensHandler(Documents documents) : SyncRequestHandler
{
    public override string Method => "textDocument/codeLens";

    public override void Advertise(JsonObject capabilities) =>
        capabilities["codeLensProvider"] = new JsonObject { ["resolveProvider"] = false };

    protected override JsonNode? Handle(JsonObject p)
    {
        var array = new JsonArray();
        if (!documents.TryAnalyze(p, out _, out var analysis)) return array;
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
        return array;
    }
}

/// <summary>The <c>axaml.openFile</c> command the lenses run: the editor opens a file at a line.</summary>
internal static class OpenFileCommand
{
    public const string Name = "axaml.openFile";

    public static JsonObject Create(string title, DefinitionTarget target) => new()
    {
        ["title"] = title,
        ["command"] = Name,
        ["arguments"] = new JsonArray(Protocol.PathToUri(target.FilePath!), target.StartLine, target.StartCharacter),
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
