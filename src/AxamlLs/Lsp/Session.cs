using System.Text.Json.Nodes;
using AxamlLs.Analysis;

namespace AxamlLs.Lsp;

/// <summary>What the editor said it supports in <c>initialize</c>, named in the server's terms instead of raw JSON paths.</summary>
internal sealed record ClientCapabilities(string ClientName, bool WorkDoneProgress)
{
    public static readonly ClientCapabilities None = new("?", WorkDoneProgress: false);

    public static ClientCapabilities Parse(JsonObject? initialize) => new(
        (string?)initialize?["clientInfo"]?["name"] ?? "?",
        WorkDoneProgress: (bool?)initialize?["capabilities"]?["window"]?["workDoneProgress"] ?? false);
}

/// <summary>The user's settings from <c>initializationOptions</c> (see README "Options").</summary>
internal sealed record ServerOptions(bool Verbose, InlayHintOptions InlayHints)
{
    public static readonly ServerOptions Default = new(Verbose: false, InlayHintOptions.Default);

    public static ServerOptions Parse(JsonObject? initialize)
    {
        var options = initialize?["initializationOptions"];
        return new ServerOptions(
            Verbose: (bool?)options?["verbose"] ?? false,
            new InlayHintOptions(BindingSource: (bool?)options?["inlayHints"]?["bindingSource"] ?? true));
    }
}

/// <summary>
/// The state <c>initialize</c> settles for the rest of the session. Written once, by the initialize request, before
/// the editor sends anything else; read by handlers afterwards. Each value is an immutable record published with one
/// reference write.
/// </summary>
internal sealed class Session
{
    private ClientCapabilities _client = ClientCapabilities.None;
    private ServerOptions _options = ServerOptions.Default;

    public ClientCapabilities Client => Volatile.Read(ref _client);
    public ServerOptions Options => Volatile.Read(ref _options);

    public void Initialize(JsonObject? initialize)
    {
        Volatile.Write(ref _client, ClientCapabilities.Parse(initialize));
        Volatile.Write(ref _options, ServerOptions.Parse(initialize));
    }
}
