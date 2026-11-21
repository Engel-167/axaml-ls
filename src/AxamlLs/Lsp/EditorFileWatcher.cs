using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using AxamlLs.Workspace;

namespace AxamlLs.Lsp;

/// <summary>
/// File changes from the editor (<c>workspace/didChangeWatchedFiles</c>) instead of a <see cref="FileSystemWatcher"/>
/// of our own. .NET's recursive watcher puts an inotify watch on every folder, <c>bin/</c> and <c>obj/</c> included,
/// so big repositories hit the system limit and builds flood the server with events it throws away. The editor
/// already watches the workspace and forwards only the patterns registered here.
/// <list type="bullet">
/// <item>Each project folder gets a source from <see cref="SourceFor"/>. With relative-pattern support it registers
/// its own folder; otherwise all folders share one workspace-wide registration.</item>
/// <item>While the registration is unanswered, the folder counts as unwatched (the file index refreshes every second).
/// When the editor refuses or does not answer, the folder falls back to a <see cref="FileSystemChangeSource"/>.</item>
/// <item>An editor that accepts the registration and then sends nothing would leave the caches stale forever. So when
/// the server writes a file itself (<see cref="ExpectChange"/>) and no event for it arrives within
/// <see cref="SilenceLimit"/>, the folder falls back too.</item>
/// </list>
/// Every switch raises <see cref="IFileChangeSource.Overflowed"/>, so caches revalidate whatever went unseen.
/// </summary>
internal sealed class EditorFileWatcher
{
    public const string Method = "workspace/didChangeWatchedFiles";
    public static readonly TimeSpan RegistrationTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan SilenceLimit = TimeSpan.FromSeconds(10);

    private const string CodeGlob = "**/*.{axaml,xaml,cs,csproj,props,targets}";
    private const string AssetGlob = "**/*.{png,jpg,jpeg,gif,bmp,webp,ico,svg,ttf,otf}";
    private const int CreateChangeDelete = 7;
    private const int CreateDelete = 5; // an asset's content doesn't matter, only whether it exists

    private readonly Func<string, JsonObject, CancellationToken, Task<JsonObject>> _call;
    private readonly Func<ClientCapabilities> _capabilities;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly List<Scope> _scopes = [];
    private readonly ConcurrentDictionary<string, ITimer> _expected = new(StringComparer.Ordinal);
    private Registration? _workspaceWide;
    private int _nextId;

    /// <param name="call">Sends a request to the editor and returns its response message (see <see cref="LspClient.CallAsync"/>).</param>
    /// <param name="capabilities">What the editor supports, known once <c>initialize</c> ran.</param>
    /// <param name="time">The clock for the registration timeout and the silence guard.</param>
    public EditorFileWatcher(Func<string, JsonObject, CancellationToken, Task<JsonObject>> call, Func<ClientCapabilities> capabilities, TimeProvider? time = null)
    {
        _call = call;
        _capabilities = capabilities;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The <c>initialized</c> notification: registers the workspace-wide watchers when they are the kind used.</summary>
    public void OnInitialized()
    {
        var c = _capabilities();
        if (c.WatchedFilesRegistration && !c.RelativePatterns) WorkspaceWide();
    }

    /// <summary>The change source for a project folder (see the class summary for which one).</summary>
    public IFileChangeSource SourceFor(string root)
    {
        var c = _capabilities();
        if (!c.WatchedFilesRegistration)
        {
            Log.Info($"File changes for {root}: from a file system watcher (the editor cannot register file watchers)");
            return new FileSystemChangeSource(root);
        }
        var registration = c.RelativePatterns
            ? Register($"axaml-watch-{Interlocked.Increment(ref _nextId)}", Protocol.PathToUri(root))
            : WorkspaceWide();
        var scope = new Scope(this, root, registration);
        lock (_gate) _scopes.Add(scope);
        return scope;
    }

    /// <summary>The <c>workspace/didChangeWatchedFiles</c> notification.</summary>
    public void OnDidChangeWatchedFiles(JsonObject parameters)
    {
        var changes = Changes(parameters);
        foreach (var change in changes)
        {
            if (_expected.TryRemove(change.Path, out var timer)) timer.Dispose();
        }
        Scope[] scopes;
        lock (_gate) scopes = [.. _scopes];
        foreach (var scope in scopes) scope.FromEditor(changes);
    }

    /// <summary>
    /// The server just wrote <paramref name="path"/>: the editor should report it within <see cref="SilenceLimit"/>, or
    /// the folder stops relying on the editor.
    /// </summary>
    public void ExpectChange(string path)
    {
        Registration? registration;
        lock (_gate) registration = _scopes.FirstOrDefault(s => s.Covers(path) && s.Registration.State == WatchState.Active)?.Registration;
        if (registration == null) return;
        var timer = _time.CreateTimer(_ =>
        {
            if (!_expected.TryRemove(path, out var expired)) return;
            expired.Dispose();
            registration.Fail($"the editor reported no change to {Path.GetFileName(path)} within {SilenceLimit.TotalSeconds:0} s of the server writing it");
        }, null, SilenceLimit, Timeout.InfiniteTimeSpan);
        if (_expected.TryRemove(path, out var previous)) previous.Dispose();
        _expected[path] = timer;
    }

    /// <summary>The <c>client/registerCapability</c> parameters: code files (created, changed, deleted) and assets (created, deleted).</summary>
    /// <param name="id">The registration id.</param>
    /// <param name="baseUri">A folder for relative patterns, or null for workspace-wide globs.</param>
    internal static JsonObject RegistrationParams(string id, string? baseUri)
    {
        JsonNode Pattern(string glob) => baseUri == null ? glob : new JsonObject { ["baseUri"] = baseUri, ["pattern"] = glob };
        return new JsonObject
        {
            ["registrations"] = new JsonArray(new JsonObject
            {
                ["id"] = id,
                ["method"] = Method,
                ["registerOptions"] = new JsonObject
                {
                    ["watchers"] = new JsonArray(
                        new JsonObject { ["globPattern"] = Pattern(CodeGlob), ["kind"] = CreateChangeDelete },
                        new JsonObject { ["globPattern"] = Pattern(AssetGlob), ["kind"] = CreateDelete }),
                },
            }),
        };
    }

    /// <summary>The changes of a <c>workspace/didChangeWatchedFiles</c> notification (FileChangeType 1, 2, 3).</summary>
    internal static List<FileChange> Changes(JsonObject parameters)
    {
        var result = new List<FileChange>();
        foreach (var change in parameters["changes"] as JsonArray ?? [])
        {
            if ((string?)change?["uri"] is not { } uri || !uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) continue;
            FileChangeKind? kind = (int?)change["type"] switch
            {
                1 => FileChangeKind.Created,
                2 => FileChangeKind.Changed,
                3 => FileChangeKind.Deleted,
                _ => null,
            };
            if (kind != null) result.Add(new FileChange(Protocol.UriToPath(uri), kind.Value));
        }
        return result;
    }

    private Registration WorkspaceWide()
    {
        lock (_gate) return _workspaceWide ??= Register("axaml-watch", baseUri: null);
    }

    private Registration Register(string id, string? baseUri)
    {
        var registration = new Registration(id);
        _ = RegisterAsync(registration, baseUri);
        return registration;
    }

    private async Task RegisterAsync(Registration registration, string? baseUri)
    {
        try
        {
            using var timeout = new CancellationTokenSource(RegistrationTimeout, _time);
            var response = await _call("client/registerCapability", RegistrationParams(registration.Id, baseUri), timeout.Token).ConfigureAwait(false);
            if (response["error"] is { } error) registration.Fail($"the editor refused to watch files: {(string?)error["message"]}");
            else registration.Activate();
        }
        catch (OperationCanceledException)
        {
            registration.Fail($"the editor did not answer the file watcher registration within {RegistrationTimeout.TotalSeconds:0} s");
        }
        catch (Exception ex)
        {
            registration.Fail($"the file watcher registration failed: {ex.Message}");
        }
    }

    private void Remove(Scope scope)
    {
        lock (_gate) _scopes.Remove(scope);
    }

    internal enum WatchState
    {
        Pending,
        Active,
        Failed,
    }

    /// <summary>One <c>client/registerCapability</c>: unanswered, accepted, or given up on (final).</summary>
    internal sealed class Registration(string id)
    {
        private readonly object _gate = new();
        private WatchState _state;

        public string Id => id;

        public WatchState State
        {
            get
            {
                lock (_gate) return _state;
            }
        }

        public string? Reason { get; private set; }

        public event Action? StateChanged;

        public void Activate() => Move(WatchState.Active, null);

        public void Fail(string reason) => Move(WatchState.Failed, reason);

        private void Move(WatchState to, string? reason)
        {
            lock (_gate)
            {
                if (_state == WatchState.Failed || _state == to) return;
                _state = to;
                Reason = reason;
            }
            StateChanged?.Invoke();
        }
    }

    /// <summary>One project folder's changes: the editor's while its registration is active, else a watcher's.</summary>
    private sealed class Scope : IFileChangeSource
    {
        private readonly EditorFileWatcher _owner;
        private readonly object _gate = new();
        private WatchState _applied = WatchState.Pending;
        private FileSystemChangeSource? _fallback;
        private bool _disposed;

        public Scope(EditorFileWatcher owner, string root, Registration registration)
        {
            _owner = owner;
            Root = root;
            Registration = registration;
            registration.StateChanged += Apply;
            Apply(); // it may have been answered already (a shared registration)
        }

        public string Root { get; }

        public Registration Registration { get; }

        public event Action<IReadOnlyList<FileChange>>? Changed;

        public event Action? Overflowed;

        public bool Reliable
        {
            get
            {
                lock (_gate) return _applied == WatchState.Active || _fallback is { Reliable: true };
            }
        }

        public bool Covers(string path) =>
            path.Length > Root.Length + 1 && path[Root.Length] == Path.DirectorySeparatorChar && path.StartsWith(Root, StringComparison.Ordinal);

        public void FromEditor(IReadOnlyList<FileChange> changes)
        {
            lock (_gate)
            {
                if (_applied != WatchState.Active) return;
            }
            var mine = changes.Where(c => Covers(c.Path) && !InPrunedFolder(c.Path)).ToList();
            if (mine.Count > 0) Changed?.Invoke(mine);
        }

        /// <summary>bin/, obj/, dot-folders: the file index never lists them, so their changes are noise.</summary>
        private bool InPrunedFolder(string path)
        {
            var folders = path.AsSpan(Root.Length + 1);
            var last = folders.LastIndexOf(Path.DirectorySeparatorChar);
            if (last < 0) return false;
            foreach (var range in folders[..last].Split(Path.DirectorySeparatorChar))
            {
                if (FileIndex.IsPrunedName(folders[..last][range])) return true;
            }
            return false;
        }

        private void Apply()
        {
            var state = Registration.State;
            lock (_gate)
            {
                if (_disposed || state == _applied || _applied == WatchState.Failed) return;
                _applied = state;
                if (state == WatchState.Failed)
                {
                    _fallback = new FileSystemChangeSource(Root);
                    _fallback.Changed += changes => Changed?.Invoke(changes);
                    _fallback.Overflowed += () => Overflowed?.Invoke();
                }
            }
            Log.Info(state == WatchState.Active
                ? $"File changes for {Root}: from the editor"
                : $"File changes for {Root}: from a file system watcher ({Registration.Reason})");
            // Whatever changed while unregistered, or before the switch, went unseen: caches revalidate.
            Overflowed?.Invoke();
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
            }
            Registration.StateChanged -= Apply;
            _fallback?.Dispose();
            _owner.Remove(this);
        }
    }
}
