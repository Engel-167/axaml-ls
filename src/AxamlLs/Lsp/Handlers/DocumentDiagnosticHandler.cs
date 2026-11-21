using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace AxamlLs.Lsp.Handlers;

/// <summary>
/// <c>textDocument/diagnostic</c> (pull, F9): the editor asks for a document's diagnostics when it wants them. The
/// <c>resultId</c> is a hash of the answer, so asking again for an unchanged result costs one comparison on the
/// editor's side and an <c>unchanged</c> reply on ours. Advertised only to editors that said they pull.
/// </summary>
internal sealed class DocumentDiagnosticHandler(Documents documents, DiagnosticsPublisher diagnostics, Session session) : SyncRequestHandler
{
    public override string Method => "textDocument/diagnostic";

    public override void Advertise(JsonObject capabilities)
    {
        if (!session.Client.PullDiagnostics) return;
        capabilities["diagnosticProvider"] = new JsonObject { ["interFileDependencies"] = true, ["workspaceDiagnostics"] = false };
    }

    protected override JsonNode? Handle(JsonObject p)
    {
        if (!documents.TryGet(Protocol.DocumentUri(p), out var doc)) return new JsonObject { ["kind"] = "full", ["items"] = new JsonArray() };
        var items = diagnostics.Collect(documents.Analyze(doc));
        var resultId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(items.ToJsonString())), 0, 12);
        if ((string?)p["previousResultId"] == resultId) return new JsonObject { ["kind"] = "unchanged", ["resultId"] = resultId };
        return new JsonObject { ["kind"] = "full", ["resultId"] = resultId, ["items"] = items };
    }
}
