using System.Text.Json.Nodes;
using AxamlLs.Workspace;

namespace AxamlLs.Lsp.Handlers;

/// <summary><c>workspace/symbol</c>: views, resource keys, element names, styles, control themes and data templates of every loaded project.</summary>
internal sealed class WorkspaceSymbolHandler(Documents documents, WorkspaceManager workspace) : SyncRequestHandler
{
    private const int Limit = 200;

    public override string Method => "workspace/symbol";

    public override void Advertise(JsonObject capabilities) => capabilities["workspaceSymbolProvider"] = true;

    protected override JsonNode? Handle(JsonObject p)
    {
        var buffers = documents.OpenBuffers();
        var symbols = workspace.Hosts.Select(h => h.Indexes).Distinct().SelectMany(i => i.Symbols(buffers));
        var array = new JsonArray();
        foreach (var s in XamlSymbols.Search(symbols, (string?)p["query"] ?? "", Limit))
        {
            var item = new JsonObject
            {
                ["name"] = s.Name,
                ["kind"] = Kind(s.Kind),
                ["location"] = new JsonObject
                {
                    ["uri"] = Protocol.PathToUri(s.Path),
                    ["range"] = new JsonObject
                    {
                        ["start"] = new JsonObject { ["line"] = s.Line, ["character"] = s.Character },
                        ["end"] = new JsonObject { ["line"] = s.EndLine, ["character"] = s.EndCharacter },
                    },
                },
            };
            if (s.Container != null) item["containerName"] = s.Container;
            array.Add(item);
        }
        return array;
    }

    /// <summary>LSP SymbolKind for each kind (README lists the mapping).</summary>
    internal static int Kind(XamlSymbolKind kind) => kind switch
    {
        XamlSymbolKind.View => 5, // Class
        XamlSymbolKind.ResourceKey => 20, // Key
        XamlSymbolKind.ElementName => 8, // Field
        XamlSymbolKind.Style => 23, // Struct
        XamlSymbolKind.ControlTheme => 11, // Interface
        _ => 26, // TypeParameter: DataTemplate
    };
}
