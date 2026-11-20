using System.Text.Json.Nodes;
using AxamlLs.Analysis;
using AxamlLs.Analysis.Formatting;

namespace AxamlLs.Lsp;

/// <summary>What the editor said it supports in <c>initialize</c>, named in the server's terms instead of raw JSON paths.</summary>
/// <param name="ClientName">clientInfo.name, for the log.</param>
/// <param name="WorkDoneProgress">The editor shows progress the server reports (project loading).</param>
/// <param name="WatchedFilesRegistration">The editor accepts file watchers registered at runtime (P1).</param>
/// <param name="RelativePatterns">Those watchers may be patterns relative to a folder (each project's).</param>
internal sealed record ClientCapabilities(string ClientName, bool WorkDoneProgress, bool WatchedFilesRegistration = false, bool RelativePatterns = false)
{
    public static readonly ClientCapabilities None = new("?", WorkDoneProgress: false);

    public static ClientCapabilities Parse(JsonObject? initialize)
    {
        var capabilities = initialize?["capabilities"];
        var watched = capabilities?["workspace"]?["didChangeWatchedFiles"];
        return new(
            (string?)initialize?["clientInfo"]?["name"] ?? "?",
            WorkDoneProgress: (bool?)capabilities?["window"]?["workDoneProgress"] ?? false,
            WatchedFilesRegistration: (bool?)watched?["dynamicRegistration"] ?? false,
            RelativePatterns: (bool?)watched?["relativePatternSupport"] ?? false);
    }
}

/// <summary>The user's settings from <c>initializationOptions</c> (see README "Options").</summary>
/// <summary>Which lenses are shown, besides the view model and code-behind ones.</summary>
/// <param name="ResourceReferences">"N references" on each <c>x:Key</c>.</param>
internal sealed record CodeLensOptions(bool ResourceReferences = true);

/// <summary>What follows a file rename besides path values.</summary>
/// <param name="UpdateClass">Rename the view's class when the view and its code-behind are renamed together.</param>
internal sealed record RenameFilesOptions(bool UpdateClass = true);

internal sealed record ServerOptions(bool Verbose, InlayHintOptions InlayHints, FormattingSettings Formatting, CodeLensOptions CodeLens, RenameFilesOptions RenameFiles)
{
    public static readonly ServerOptions Default = new(Verbose: false, InlayHintOptions.Default, FormattingSettings.Default, new CodeLensOptions(), new RenameFilesOptions());

    public static ServerOptions Parse(JsonObject? initialize)
    {
        var options = initialize?["initializationOptions"];
        return new ServerOptions(
            Verbose: (bool?)options?["verbose"] ?? false,
            new InlayHintOptions(BindingSource: (bool?)options?["inlayHints"]?["bindingSource"] ?? true),
            ParseFormatting(options?["formatting"]),
            new CodeLensOptions(ResourceReferences: (bool?)options?["codeLens"]?["resourceReferences"] ?? true),
            new RenameFilesOptions(UpdateClass: (bool?)options?["renameFiles"]?["updateClass"] ?? true));
    }

    /// <summary>Missing or unreadable values keep their defaults.</summary>
    private static FormattingSettings ParseFormatting(JsonNode? f)
    {
        var d = FormattingSettings.Default;
        if (f is not JsonObject) return d;
        T Get<T>(string name, T fallback) where T : struct
        {
            try
            {
                return f[name]?.GetValue<T>() ?? fallback;
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException)
            {
                return fallback;
            }
        }
        string? Text(string name) => f[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        return new FormattingSettings(
            Text("attributesOnSeparateLines") switch
            {
                "always" => AttributeLayout.Always,
                "never" => AttributeLayout.Never,
                _ => d.AttributesOnSeparateLines,
            },
            Get("attributesOnSeparateLinesThreshold", d.AttributesOnSeparateLinesThreshold),
            Get("firstAttributeOnSameLine", d.FirstAttributeOnSameLine),
            Get("maxLineLength", d.MaxLineLength),
            Get("spaceBeforeSelfClosingSlash", d.SpaceBeforeSelfClosingSlash),
            Get("keepBlankLines", d.KeepBlankLines),
            Text("indentAttributesBy") == "indent" ? AttributeIndent.Indent : d.IndentAttributesBy);
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
