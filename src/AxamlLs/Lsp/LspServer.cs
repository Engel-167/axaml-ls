using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using AxamlLs.Lsp.Handlers;
using AxamlLs.Telemetry;
using AxamlLs.Workspace;

namespace AxamlLs.Lsp;

/// <summary>
/// The AXAML language server's message loop: reads JSON-RPC messages, routes each to the handler for its method,
/// negotiates capabilities in <c>initialize</c> and honours <c>$/cancelRequest</c>. What each method does lives in
/// <c>Lsp/Handlers/</c>; documents, diagnostics and the editor connection are their own classes.
/// </summary>
public sealed class LspServer : IDisposable
{
    private readonly JsonRpc _rpc;
    private readonly LspClient _client;
    private readonly Session _session = new();
    private readonly WorkspaceManager _workspace;
    private readonly Dictionary<string, IRequestHandler> _requests;
    private readonly Dictionary<string, INotificationHandler> _notifications;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inFlight = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ServerMetrics _metrics;

    public LspServer(Stream input, Stream output)
        : this(input, output, new ServerMetrics())
    {
    }

    /// <param name="input">Messages from the editor.</param>
    /// <param name="output">Messages to the editor.</param>
    /// <param name="metrics">Where timings and counts go; the server disposes it.</param>
    internal LspServer(Stream input, Stream output, ServerMetrics metrics)
    {
        _metrics = metrics;
        _rpc = new JsonRpc(input, output);
        _client = new LspClient(_rpc);
        var watcher = new EditorFileWatcher(_client.CallAsync, () => _session.Client);
        _workspace = new WorkspaceManager(metrics, watcher.SourceFor);
        var progress = new ProjectLoadProgress(_client, _session);
        var documents = new Documents(_workspace, progress, metrics);
        var diagnostics = new DiagnosticsPublisher(documents, _client, metrics);
        var targets = new TargetLookup(documents, progress);
        _workspace.ProjectUpdated += diagnostics.OnProjectUpdated;
        _workspace.CodeRenamed = new CodeRenameFollower(documents, _client, watcher).FollowAsync;

        IRequestHandler[] requests =
        [
            new HoverHandler(documents),
            new DefinitionHandler(documents),
            new TypeDefinitionHandler(documents),
            new ImplementationHandler(documents),
            new ReferencesHandler(documents, targets),
            new PrepareRenameHandler(targets),
            new RenameHandler(documents, targets),
            new CompletionHandler(documents),
            new CompletionResolveHandler(),
            new CodeActionHandler(documents),
            new ContentModifiedGuard(new CodeLensHandler(documents), documents),
            new ExecuteCommandHandler(_client),
            new DocumentSymbolHandler(documents),
            new FoldingRangeHandler(documents),
            new LinkedEditingRangeHandler(documents),
            new DocumentHighlightHandler(documents),
            new OnTypeFormattingHandler(documents),
            new FormattingHandler(documents, _session, range: false),
            new FormattingHandler(documents, _session, range: true),
            new ContentModifiedGuard(new InlayHintHandler(documents, _session), documents),
            new ContentModifiedGuard(new SemanticTokensHandler(documents), documents),
            new DocumentColorHandler(documents),
            new ColorPresentationHandler(),
        ];
        INotificationHandler[] notifications =
        [
            new DidOpenHandler(documents, diagnostics),
            new DidChangeHandler(documents, diagnostics),
            new DidCloseHandler(documents, diagnostics),
            new InitializedHandler(watcher),
            new DidChangeWatchedFilesHandler(watcher),
        ];
        _requests = requests.ToDictionary(h => h.Method, StringComparer.Ordinal);
        _notifications = notifications.ToDictionary(h => h.Method, StringComparer.Ordinal);
    }

    public async Task RunAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            JsonObject? message;
            try
            {
                message = await _rpc.ReadAsync(_shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            if (message == null) break;
            var method = (string?)message["method"];
            var id = message["id"]?.DeepClone();
            if (method == null)
            {
                // A response to one of our requests.
                if (id != null) _client.OnResponse(message);
                continue;
            }
            var parameters = message["params"] as JsonObject;
            if (id != null) _ = Task.Run(() => HandleRequestAsync(id, method, parameters));
            else HandleNotification(method, parameters);
        }
    }

    private async Task HandleRequestAsync(JsonNode id, string method, JsonObject? p)
    {
        JsonNode? result = null;
        JsonObject? error = null;
        var started = _metrics.Timestamp();
        // Only known methods become tag values: a client's typo must not create a new time series.
        var metricMethod = method is "initialize" or "shutdown" || _requests.ContainsKey(method) ? method : "unknown";
        var key = id.ToJsonString();
        using var cts = new CancellationTokenSource();
        _inFlight[key] = cts;
        try
        {
            // Queued behind other work and already cancelled (the editor moved on): don't compute an answer nobody reads.
            cts.Token.ThrowIfCancellationRequested();
            result = method switch
            {
                "initialize" => Initialize(p),
                "shutdown" => null,
                _ when _requests.TryGetValue(method, out var handler) => await handler.HandleAsync(p ?? [], cts.Token).ConfigureAwait(false),
                _ => throw new UnknownMethodException(method),
            };
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            error = ErrorCodes.Error(ErrorCodes.RequestCancelled, "Request cancelled");
        }
        catch (UnknownMethodException)
        {
            error = ErrorCodes.Error(ErrorCodes.MethodNotFound, $"Method not found: {method}");
        }
        catch (InvalidParamsException ex)
        {
            error = ErrorCodes.Error(ErrorCodes.InvalidParams, ex.Message);
        }
        catch (ContentModifiedException ex)
        {
            error = ErrorCodes.Error(ErrorCodes.ContentModified, ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error($"Request {method} failed", ex);
            error = ErrorCodes.Error(ErrorCodes.InternalError, ex.Message);
        }
        finally
        {
            _inFlight.TryRemove(key, out _);
        }
        if (error == null && cts.IsCancellationRequested)
        {
            result = null;
            error = ErrorCodes.Error(ErrorCodes.RequestCancelled, "Request cancelled");
        }
        _metrics.RequestSince(metricMethod, started);
        if ((int?)error?["code"] == ErrorCodes.RequestCancelled) _metrics.RequestCancelled(metricMethod);
        await _client.RespondAsync(id, result, error).ConfigureAwait(false);
    }

    /// <summary>No handler serves the method (answered with -32601). Private, so nothing a handler calls can throw it.</summary>
    private sealed class UnknownMethodException(string method) : Exception(method);

    /// <summary>Notifications run on the read loop, in order, so a didChange is applied before any later request reads the text.</summary>
    private void HandleNotification(string method, JsonObject? p)
    {
        try
        {
            switch (method)
            {
                case "exit":
                    _shutdown.Cancel();
                    break;
                case "$/cancelRequest":
                    // The request it cancels may be running on another thread right now.
                    if (p?["id"] is { } cancelled && _inFlight.TryGetValue(cancelled.ToJsonString(), out var running)) running.Cancel();
                    break;
                default:
                    if (p != null && _notifications.TryGetValue(method, out var handler)) handler.Handle(p);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Notification {method} failed", ex);
        }
    }

    private JsonObject Initialize(JsonObject? p)
    {
        _session.Initialize(p);
        if (_session.Options.Verbose) Log.Verbose = true;
        Log.Info($"axaml-ls {typeof(LspServer).Assembly.GetName().Version} started (client: {_session.Client.ClientName})");
        var capabilities = new JsonObject();
        foreach (var handler in _notifications.Values) handler.Advertise(capabilities);
        foreach (var handler in _requests.Values) handler.Advertise(capabilities);
        return new JsonObject
        {
            ["capabilities"] = capabilities,
            ["serverInfo"] = new JsonObject { ["name"] = "axaml-ls", ["version"] = typeof(LspServer).Assembly.GetName().Version?.ToString(3) },
        };
    }

    public void Dispose()
    {
        _workspace.Dispose();
        _shutdown.Dispose();
        _rpc.Dispose();
        _metrics.Dispose();
    }
}
