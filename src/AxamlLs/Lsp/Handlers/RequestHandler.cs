using System.Text.Json.Nodes;

namespace AxamlLs.Lsp.Handlers;

/// <summary>
/// Serves one LSP request method: turns its JSON parameters into domain inputs, calls the analysis, and turns the
/// result back into JSON. Logic lives in <c>Analysis/</c>, not here (docs/ENGINEERING.md U2).
/// </summary>
internal interface IRequestHandler
{
    /// <summary>The LSP method, e.g. <c>textDocument/hover</c>.</summary>
    string Method { get; }

    /// <summary>Adds this handler's entry to the server capabilities sent in <c>initialize</c>.</summary>
    void Advertise(JsonObject capabilities);

    Task<JsonNode?> HandleAsync(JsonObject parameters, CancellationToken ct);
}

/// <summary>Serves one LSP notification method; runs on the read loop, so it must be quick and never block.</summary>
internal interface INotificationHandler
{
    string Method { get; }

    void Advertise(JsonObject capabilities);

    void Handle(JsonObject parameters);
}

/// <summary>A request handler whose work is synchronous (most of them: analysis is CPU work on a cached document).</summary>
internal abstract class SyncRequestHandler : IRequestHandler
{
    public abstract string Method { get; }

    public virtual void Advertise(JsonObject capabilities)
    {
    }

    public Task<JsonNode?> HandleAsync(JsonObject parameters, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Handle(parameters));
    }

    protected abstract JsonNode? Handle(JsonObject parameters);
}

internal static class DocumentRequests
{
    /// <summary>The open document a request is about, analysed at its current version; false when it isn't open.</summary>
    public static bool TryAnalyze(this Documents documents, JsonObject p, out OpenDocument doc, out DocumentAnalysis analysis)
    {
        if (!documents.TryGet(Protocol.DocumentUri(p), out doc))
        {
            analysis = null!;
            return false;
        }
        analysis = documents.Analyze(doc);
        return true;
    }

    /// <summary>As <see cref="TryAnalyze"/>, plus the request's <c>position</c> as an offset.</summary>
    public static bool TryAnalyzeAt(this Documents documents, JsonObject p, out OpenDocument doc, out DocumentAnalysis analysis, out int offset)
    {
        offset = 0;
        if (!documents.TryAnalyze(p, out doc, out analysis)) return false;
        offset = Protocol.Offset(analysis.Lines, p["position"]!);
        return true;
    }
}
