using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using AxamlLs.Analysis;
using AxamlLs.Text;
using AxamlLs.Workspace;

namespace AxamlLs.Lsp;

/// <summary>The AXAML language server: diagnostics, hover, go-to-definition and completion.</summary>
public sealed class LspServer : IDisposable
{
    private sealed class OpenDocument
    {
        public required string Uri { get; init; }
        public required string Path { get; init; }
        public string Text { get; set; } = "";
        public int Version { get; set; }
        public CancellationTokenSource? Pending { get; set; }
        public (int Version, ProjectSnapshot? Project, XamlAnalyzer Analyzer, LineIndex Lines)? Cached { get; set; }
    }

    private readonly JsonRpc _rpc;
    private readonly WorkspaceManager _workspace = new();
    private readonly ConcurrentDictionary<string, OpenDocument> _documents = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<ProjectHost, byte> _announcedLoads = new();
    private readonly ConcurrentDictionary<ProjectHost, ProjectXaml> _projectXaml = new();
    private readonly CancellationTokenSource _shutdown = new();
    private bool _clientSupportsProgress;
    private InlayHintOptions _hintOptions = InlayHintOptions.Default;
    private int _nextRequestId;

    public LspServer(Stream input, Stream output)
    {
        _rpc = new JsonRpc(input, output);
        _workspace.ProjectUpdated += OnProjectUpdated;
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
            if (method == null) continue; // A response to one of our requests.
            if (id != null)
            {
                _ = Task.Run(() => HandleRequestAsync(id, method, message["params"] as JsonObject));
            }
            else
            {
                try
                {
                    HandleNotification(method, message["params"] as JsonObject);
                }
                catch (Exception ex)
                {
                    Log.Error($"Notification {method} failed", ex);
                }
            }
        }
    }

    private async Task HandleRequestAsync(JsonNode id, string method, JsonObject? p)
    {
        JsonNode? result = null;
        JsonObject? error = null;
        try
        {
            result = method switch
            {
                "initialize" => Initialize(p),
                "shutdown" => null,
                "textDocument/hover" => Hover(p!),
                "textDocument/definition" => Definition(p!),
                "textDocument/completion" => Completion(p!),
                "textDocument/documentColor" => DocumentColor(p!),
                "textDocument/colorPresentation" => ColorPresentation(p!),
                "textDocument/semanticTokens/full" => SemanticTokens(p!),
                "textDocument/onTypeFormatting" => OnTypeFormatting(p!),
                "completionItem/resolve" => ResolveCompletion(p!),
                "textDocument/inlayHint" => InlayHint(p!),
                "textDocument/codeAction" => CodeAction(p!),
                "textDocument/documentSymbol" => DocumentSymbols(p!),
                "textDocument/foldingRange" => FoldingRanges(p!),
                "textDocument/typeDefinition" => ToLocations(p!, Navigation.TypeDefinition),
                "textDocument/implementation" => ToLocations(p!, Navigation.Implementation),
                "textDocument/codeLens" => CodeLens(p!),
                "workspace/executeCommand" => await ExecuteCommandAsync(p!).ConfigureAwait(false),
                "textDocument/references" => await ReferencesAsync(p!).ConfigureAwait(false),
                "textDocument/prepareRename" => await PrepareRenameAsync(p!).ConfigureAwait(false),
                "textDocument/rename" => await RenameAsync(p!).ConfigureAwait(false),
                _ => throw new MethodNotFoundException(method),
            };
        }
        catch (MethodNotFoundException)
        {
            error = new JsonObject { ["code"] = -32601, ["message"] = $"Method not found: {method}" };
        }
        catch (InvalidParamsException ex)
        {
            error = new JsonObject { ["code"] = -32602, ["message"] = ex.Message };
        }
        catch (Exception ex)
        {
            Log.Error($"Request {method} failed", ex);
            error = new JsonObject { ["code"] = -32603, ["message"] = ex.Message };
        }
        var response = new JsonObject { ["id"] = id };
        if (error != null) response["error"] = error;
        else response["result"] = result;
        await _rpc.SendAsync(response).ConfigureAwait(false);
    }

    private sealed class MethodNotFoundException(string method) : Exception(method);

    /// <summary>The request was understood but its arguments cannot be used (an invalid new name): reported as JSON-RPC -32602.</summary>
    private sealed class InvalidParamsException(string message) : Exception(message);

    private void HandleNotification(string method, JsonObject? p)
    {
        switch (method)
        {
            case "initialized":
                break;
            case "exit":
                _shutdown.Cancel();
                break;
            case "textDocument/didOpen":
            {
                var td = p!["textDocument"]!;
                var uri = (string)td["uri"]!;
                var doc = new OpenDocument { Uri = uri, Path = UriToPath(uri), Text = (string?)td["text"] ?? "", Version = (int?)td["version"] ?? 0 };
                _documents[uri] = doc;
                Schedule(doc, delayMs: 0);
                break;
            }
            case "textDocument/didChange":
            {
                var uri = (string)p!["textDocument"]!["uri"]!;
                if (!_documents.TryGetValue(uri, out var doc)) break;
                lock (doc)
                {
                    doc.Version = (int?)p["textDocument"]!["version"] ?? doc.Version + 1;
                    foreach (var change in p["contentChanges"]!.AsArray())
                    {
                        // Full sync: the last change carries the whole document.
                        if (change?["range"] == null) doc.Text = (string?)change?["text"] ?? doc.Text;
                    }
                }
                Schedule(doc, delayMs: 200);
                // Classes set here may change which selector classes elsewhere count as used.
                foreach (var other in _documents.Values)
                {
                    if (other != doc) Schedule(other, delayMs: 800);
                }
                break;
            }
            case "textDocument/didClose":
            {
                var uri = (string)p!["textDocument"]!["uri"]!;
                if (_documents.TryRemove(uri, out var doc)) doc.Pending?.Cancel();
                _ = PublishAsync(uri, null, []);
                break;
            }
        }
    }

    private JsonObject Initialize(JsonObject? p)
    {
        _clientSupportsProgress = (bool?)p?["capabilities"]?["window"]?["workDoneProgress"] ?? false;
        if ((bool?)p?["initializationOptions"]?["verbose"] == true) Log.Verbose = true;
        _hintOptions = new InlayHintOptions(BindingSource: (bool?)p?["initializationOptions"]?["inlayHints"]?["bindingSource"] ?? true);
        Log.Info($"axaml-ls {typeof(LspServer).Assembly.GetName().Version} started (client: {(string?)p?["clientInfo"]?["name"] ?? "?"})");
        return new JsonObject
        {
            ["capabilities"] = new JsonObject
            {
                ["textDocumentSync"] = new JsonObject { ["openClose"] = true, ["change"] = 1 },
                ["hoverProvider"] = true,
                ["definitionProvider"] = true,
                ["referencesProvider"] = true,
                ["documentSymbolProvider"] = true,
                ["foldingRangeProvider"] = true,
                ["typeDefinitionProvider"] = true,
                ["implementationProvider"] = true,
                ["codeLensProvider"] = new JsonObject { ["resolveProvider"] = false },
                ["executeCommandProvider"] = new JsonObject { ["commands"] = new JsonArray(OpenFileCommand) },
                ["codeActionProvider"] = new JsonObject { ["codeActionKinds"] = new JsonArray("quickfix") },
                ["renameProvider"] = new JsonObject { ["prepareProvider"] = true },
                ["colorProvider"] = true,
                ["inlayHintProvider"] = true,
                ["documentOnTypeFormattingProvider"] = new JsonObject { ["firstTriggerCharacter"] = "/" },
                ["semanticTokensProvider"] = new JsonObject
                {
                    ["legend"] = new JsonObject
                    {
                        ["tokenTypes"] = new JsonArray(TokenTypes.Select(t => (JsonNode)t).ToArray()),
                        ["tokenModifiers"] = new JsonArray(),
                    },
                    ["full"] = true,
                },
                ["completionProvider"] = new JsonObject
                {
                    ["triggerCharacters"] = new JsonArray("<", ".", "{", " ", ":", "$", "#", "=", "\"", ",", "/", "["),
                    ["resolveProvider"] = true,
                },
            },
            ["serverInfo"] = new JsonObject { ["name"] = "axaml-ls", ["version"] = typeof(LspServer).Assembly.GetName().Version?.ToString(3) },
        };
    }

    // ---------------------------------------------------------------- analysis & diagnostics

    private void Schedule(OpenDocument doc, int delayMs)
    {
        CancellationTokenSource cts;
        lock (doc)
        {
            doc.Pending?.Cancel();
            doc.Pending = cts = new CancellationTokenSource();
        }
        _ = Task.Run(async () =>
        {
            try
            {
                if (delayMs > 0) await Task.Delay(delayMs, cts.Token).ConfigureAwait(false);
                var (version, analyzer, lines) = Analyze(doc);
                if (cts.IsCancellationRequested) return;
                var diagnostics = new JsonArray();
                foreach (var (d, unnecessary) in CollectDiagnostics(analyzer))
                {
                    var lsp = ToLsp(d, lines);
                    if (unnecessary) lsp["tags"] = new JsonArray(1); // Unnecessary: the editor fades it
                    diagnostics.Add(lsp);
                }
                await PublishAsync(doc.Uri, version, diagnostics).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log.Error($"Analysis of {doc.Path} failed", ex);
            }
        });
    }

    /// <summary>
    /// Everything reported for a document: the analyzer's own diagnostics plus those that need other files (assets, resources,
    /// selector classes), which are added after analysis because the providers are set after it.
    /// </summary>
    internal static List<(XamlDiagnostic Diagnostic, bool Unnecessary)> CollectDiagnostics(XamlAnalyzer analyzer)
    {
        var all = new List<(XamlDiagnostic, bool)>();
        all.AddRange(analyzer.Result.Diagnostics.Select(d => (d, false)));
        all.AddRange(Features.AssetDiagnostics(analyzer).Select(d => (d, false)));
        all.AddRange(ResourceCheck.Unknown(analyzer).Select(d => (d, false)));
        all.AddRange(Selectors.Validate(analyzer).Select(d => (d, false)));
        if (analyzer.StyleProvider?.Invoke() is { } styles)
            all.AddRange(Selectors.UnusedClasses(analyzer.Result.Doc, styles.UsedClasses).Select(d => (d, true)));
        return all;
    }

    private (int Version, XamlAnalyzer Analyzer, LineIndex Lines) Analyze(OpenDocument doc)
    {
        string text;
        int version;
        lock (doc)
        {
            text = doc.Text;
            version = doc.Version;
        }
        var host = _workspace.GetHostFor(doc.Path);
        if (host != null) AnnounceLoad(host);
        var project = host?.Current;
        var cached = doc.Cached;
        if (cached is { } c && c.Version == version && ReferenceEquals(c.Project, project)) return (version, c.Analyzer, c.Lines);

        var analyzer = XamlAnalyzer.Analyze(text, project, host?.CompiledBindingsDefault ?? false);
        ProjectXaml.Wire(analyzer, doc.Path, host, OpenBuffers,
            host == null ? null : () => _projectXaml.GetOrAdd(host, _ => new ProjectXaml()).Files(host, OpenBuffers()));
        var lines = new LineIndex(text);
        doc.Cached = (version, project, analyzer, lines);
        Log.Debug($"Analyzed {Path.GetFileName(doc.Path)} v{version}: {analyzer.Result.Diagnostics.Count} diagnostics (project {(project == null ? "not loaded" : "loaded")})");
        return (version, analyzer, lines);
    }

    /// <summary>Unsaved editor contents by file path (they win over the disk when indexing resources).</summary>
    private IReadOnlyDictionary<string, string> OpenBuffers()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var d in _documents.Values)
        {
            lock (d) map[d.Path] = d.Text;
        }
        return map;
    }

    private void OnProjectUpdated(ProjectHost host)
    {
        foreach (var doc in _documents.Values)
        {
            if (_workspace.GetHostFor(doc.Path, startLoading: false) == host) Schedule(doc, delayMs: 0);
        }
    }

    /// <summary>Shows "Loading Orion_POS.csproj" in Zed's status bar until the first load finishes.</summary>
    private void AnnounceLoad(ProjectHost host)
    {
        if (!_announcedLoads.TryAdd(host, 0) || host.Current != null || !_clientSupportsProgress) return;
        var token = $"axaml-load-{Path.GetFileNameWithoutExtension(host.ProjectPath)}";
        _ = Task.Run(async () =>
        {
            try
            {
                var id = Interlocked.Increment(ref _nextRequestId);
                await _rpc.SendAsync(new JsonObject { ["id"] = $"axaml-{id}", ["method"] = "window/workDoneProgress/create", ["params"] = new JsonObject { ["token"] = token } });
                await _rpc.SendAsync(Progress(token, new JsonObject { ["kind"] = "begin", ["title"] = $"Loading {Path.GetFileName(host.ProjectPath)}" }));
                await host.EnsureLoadedAsync();
                var end = host.LoadError == null ? "Project loaded" : $"Project failed to load: {host.LoadError}";
                await _rpc.SendAsync(Progress(token, new JsonObject { ["kind"] = "end", ["message"] = end }));
            }
            catch (Exception ex)
            {
                Log.Error("Progress reporting failed", ex);
            }
        });
    }

    private static JsonObject Progress(string token, JsonObject value) =>
        new() { ["method"] = "$/progress", ["params"] = new JsonObject { ["token"] = token, ["value"] = value } };

    private Task PublishAsync(string uri, int? version, JsonArray diagnostics)
    {
        var p = new JsonObject { ["uri"] = uri, ["diagnostics"] = diagnostics };
        if (version != null) p["version"] = version;
        return _rpc.SendAsync(new JsonObject { ["method"] = "textDocument/publishDiagnostics", ["params"] = p });
    }

    private static JsonObject ToLsp(XamlDiagnostic d, LineIndex lines) => new()
    {
        ["range"] = Range(lines, d.Start, d.End),
        ["severity"] = (int)d.Severity,
        ["code"] = d.Code,
        ["source"] = "axaml",
        ["message"] = d.Message,
    };

    // ---------------------------------------------------------------- requests

    private (OpenDocument Doc, XamlAnalyzer Analyzer, LineIndex Lines, int Offset)? Locate(JsonObject p)
    {
        var uri = (string)p["textDocument"]!["uri"]!;
        if (!_documents.TryGetValue(uri, out var doc)) return null;
        var (_, analyzer, lines) = Analyze(doc);
        var pos = p["position"]!;
        var offset = lines.GetOffset((int)pos["line"]!, (int)pos["character"]!);
        return (doc, analyzer, lines, offset);
    }

    /// <summary>What a find-references or rename request works with: the target under the cursor, the project's files, its host.</summary>
    private sealed record TargetContext(OpenDocument Doc, XamlFile Origin, IReadOnlyList<XamlFile> Files, ProjectHost? Host, TargetAt Found);

    private async Task<TargetContext?> TargetContextAsync(JsonObject p)
    {
        var uri = (string)p["textDocument"]!["uri"]!;
        if (!_documents.TryGetValue(uri, out var doc)) return null;
        var host = _workspace.GetHostFor(doc.Path);
        if (host != null)
        {
            AnnounceLoad(host);
            await host.EnsureLoadedAsync().ConfigureAwait(false);
        }
        var (_, analyzer, lines) = Analyze(doc);
        var pos = p["position"]!;
        var offset = lines.GetOffset((int)pos["line"]!, (int)pos["character"]!);
        if (Targets.Find(analyzer, offset) is not { } found) return null;
        var origin = new XamlFile(doc.Path, analyzer.Result.Doc.Text, analyzer, lines);
        IReadOnlyList<XamlFile> files = host?.Current != null ? _projectXaml.GetOrAdd(host, _ => new ProjectXaml()).Files(host, OpenBuffers()) : [origin];
        return new TargetContext(doc, origin, files, host, found);
    }

    /// <summary>Text of a file as edits and locations must see it: the open buffer if there is one, else the disk.</summary>
    private Func<string, (string Text, LineIndex Lines)> TextProvider()
    {
        var open = OpenBuffers();
        var cache = new Dictionary<string, (string, LineIndex)>(StringComparer.Ordinal);
        return path =>
        {
            if (cache.TryGetValue(path, out var hit)) return hit;
            var text = open.TryGetValue(path, out var t) ? t : File.ReadAllText(path);
            return cache[path] = (text, new LineIndex(text));
        };
    }

    /// <summary>The outline: hierarchical DocumentSymbols, one per element.</summary>
    private JsonNode? DocumentSymbols(JsonObject p)
    {
        var uri = (string)p["textDocument"]!["uri"]!;
        if (!_documents.TryGetValue(uri, out var doc)) return new JsonArray();
        var (_, analyzer, lines) = Analyze(doc);
        JsonArray Convert(IEnumerable<OutlineSymbol> symbols)
        {
            var array = new JsonArray();
            foreach (var s in symbols)
            {
                var node = new JsonObject
                {
                    ["name"] = s.Name,
                    ["kind"] = s.Kind,
                    ["range"] = Range(lines, s.Start, s.End),
                    ["selectionRange"] = Range(lines, s.SelectionStart, s.SelectionEnd),
                    ["children"] = Convert(s.Children),
                };
                if (s.Detail != null) node["detail"] = s.Detail;
                array.Add(node);
            }
            return array;
        }
        return Convert(Outline.Build(analyzer));
    }

    /// <summary>Quick fixes for the diagnostics at the requested range, recomputed here rather than taken from the client's copies.</summary>
    private JsonNode? CodeAction(JsonObject p)
    {
        var actions = new JsonArray();
        var uri = (string)p["textDocument"]!["uri"]!;
        if (!_documents.TryGetValue(uri, out var doc)) return actions;
        var (_, analyzer, lines) = Analyze(doc);
        var range = p["range"]!;
        var start = lines.GetOffset((int)range["start"]!["line"]!, (int)range["start"]!["character"]!);
        var end = lines.GetOffset((int)range["end"]!["line"]!, (int)range["end"]!["character"]!);
        var wanted = (p["context"]?["diagnostics"] as JsonArray)?.Select(d => (string?)d?["code"]).OfType<string>().ToHashSet() ?? [];
        var textOf = TextProvider();
        foreach (var (d, _) in CollectDiagnostics(analyzer))
        {
            if (d.Start > end || d.End < start || wanted.Count > 0 && !wanted.Contains(d.Code)) continue;
            foreach (var fix in QuickFixes.For(analyzer, d))
            {
                try
                {
                    var action = new JsonObject
                    {
                        ["title"] = fix.Title,
                        ["kind"] = "quickfix",
                        ["diagnostics"] = new JsonArray(ToLsp(d, lines)),
                        ["edit"] = Edits.ToWorkspaceEdit(fix.Edits, textOf),
                    };
                    if (fix.Preferred) action["isPreferred"] = true;
                    actions.Add(action);
                }
                catch (IOException ex)
                {
                    Log.Error($"Quick fix '{fix.Title}' skipped", ex);
                }
            }
        }
        return actions;
    }

    private async Task<JsonNode?> ReferencesAsync(JsonObject p)
    {
        var array = new JsonArray();
        if (await TargetContextAsync(p).ConfigureAwait(false) is not { } ctx) return array;
        var includeDeclaration = (bool?)p["context"]?["includeDeclaration"] ?? true;
        var textOf = TextProvider();
        foreach (var o in await Targets.OccurrencesAsync(ctx.Found.Target, ctx.Origin, ctx.Files, ctx.Host).ConfigureAwait(false))
        {
            if (o.IsDeclaration && !includeDeclaration) continue;
            array.Add(new JsonObject { ["uri"] = PathToUri(o.Path), ["range"] = Edits.Range(textOf(o.Path).Lines, o.Start, o.End) });
        }
        return array;
    }

    private async Task<JsonNode?> PrepareRenameAsync(JsonObject p)
    {
        if (await TargetContextAsync(p).ConfigureAwait(false) is not { } ctx || !Rename.CanRename(ctx.Found.Target)) return null;
        return new JsonObject
        {
            ["range"] = Edits.Range(ctx.Origin.Lines, ctx.Found.Start, ctx.Found.End),
            ["placeholder"] = ctx.Origin.Text[ctx.Found.Start..ctx.Found.End],
        };
    }

    private async Task<JsonNode?> RenameAsync(JsonObject p)
    {
        var newName = (string?)p["newName"] ?? "";
        if (await TargetContextAsync(p).ConfigureAwait(false) is not { } ctx) return null;
        if (!Rename.CanRename(ctx.Found.Target)) throw new InvalidParamsException("This name is declared in a library and cannot be renamed here.");
        var result = await Rename.ComputeAsync(ctx.Found.Target, newName, ctx.Origin, ctx.Files, ctx.Host).ConfigureAwait(false);
        if (result.Error != null) throw new InvalidParamsException(result.Error);
        var projectDir = ctx.Host?.ProjectDirectory ?? ctx.Origin.Analyzer.ProjectDirectory;
        var edits = projectDir == null ? result.Edits : result.Edits.Where(e => e.Path.StartsWith(projectDir + Path.DirectorySeparatorChar, StringComparison.Ordinal)).ToList();
        return Edits.ToWorkspaceEdit(edits, TextProvider());
    }

    private JsonNode? Hover(JsonObject p)
    {
        if (Locate(p) is not { } at) return null;
        var hover = Features.Hover(at.Analyzer, at.Offset);
        if (hover == null) return null;
        return new JsonObject
        {
            ["contents"] = new JsonObject { ["kind"] = "markdown", ["value"] = hover.Markdown },
            ["range"] = Range(at.Lines, hover.Start, hover.End),
        };
    }

    private JsonNode? Definition(JsonObject p) => ToLocations(p, Features.Definition);

    /// <summary>Runs a "where is it" query at the cursor and answers with LSP locations.</summary>
    private JsonNode? ToLocations(JsonObject p, Func<XamlAnalyzer, int, List<DefinitionTarget>> query)
    {
        if (Locate(p) is not { } at) return null;
        var array = new JsonArray();
        foreach (var target in query(at.Analyzer, at.Offset)) array.Add(Location(target, at.Doc.Uri, at.Lines));
        return array;
    }

    private static JsonObject Location(DefinitionTarget target, string documentUri, LineIndex lines)
    {
        if (target.DocumentOffset is { } off) return new JsonObject { ["uri"] = documentUri, ["range"] = Range(lines, off, off) };
        return new JsonObject
        {
            ["uri"] = PathToUri(target.FilePath!),
            ["range"] = new JsonObject
            {
                ["start"] = new JsonObject { ["line"] = target.StartLine, ["character"] = target.StartCharacter },
                ["end"] = new JsonObject { ["line"] = target.EndLine, ["character"] = target.EndCharacter },
            },
        };
    }

    private JsonNode? FoldingRanges(JsonObject p)
    {
        var uri = (string)p["textDocument"]!["uri"]!;
        var array = new JsonArray();
        if (!_documents.TryGetValue(uri, out var doc)) return array;
        var (_, analyzer, lines) = Analyze(doc);
        foreach (var r in Folding.Compute(analyzer, lines))
        {
            var node = new JsonObject { ["startLine"] = r.StartLine, ["endLine"] = r.EndLine };
            if (r.Kind != null) node["kind"] = r.Kind;
            array.Add(node);
        }
        return array;
    }

    /// <summary>Lenses on the root element that jump to the view model and to the code-behind (Zed shows them with "code_lens": "on").</summary>
    private JsonNode? CodeLens(JsonObject p)
    {
        var array = new JsonArray();
        var uri = (string)p["textDocument"]!["uri"]!;
        if (!_documents.TryGetValue(uri, out var doc)) return array;
        var (_, analyzer, lines) = Analyze(doc);
        if (analyzer.Result.Doc.Root is not { } root) return array;
        void Add(string title, DefinitionTarget? target)
        {
            if (target?.FilePath == null) return;
            array.Add(new JsonObject
            {
                ["range"] = Range(lines, root.NameStart, root.NameEnd),
                ["command"] = new JsonObject
                {
                    ["title"] = title,
                    ["command"] = OpenFileCommand,
                    ["arguments"] = new JsonArray(PathToUri(target.FilePath), target.StartLine, target.StartCharacter),
                },
            });
        }
        var viewModel = Navigation.ViewModel(analyzer).FirstOrDefault();
        if (viewModel != null && analyzer.ScopeAt(root).DataType is { } dataType) Add($"→ {dataType.Name}", viewModel);
        Add("→ code-behind", Navigation.CodeBehind(analyzer).FirstOrDefault());
        return array;
    }

    private const string OpenFileCommand = "axaml.openFile";

    /// <summary>The lens command: asks the editor to open a file at a line (window/showDocument).</summary>
    private async Task<JsonNode?> ExecuteCommandAsync(JsonObject p)
    {
        if ((string?)p["command"] != OpenFileCommand || p["arguments"] is not JsonArray { Count: >= 1 } args) return null;
        var line = args.Count > 1 ? (int?)args[1] ?? 0 : 0;
        var character = args.Count > 2 ? (int?)args[2] ?? 0 : 0;
        var position = new JsonObject { ["line"] = line, ["character"] = character };
        await _rpc.SendAsync(new JsonObject
        {
            ["id"] = $"axaml-{Interlocked.Increment(ref _nextRequestId)}",
            ["method"] = "window/showDocument",
            ["params"] = new JsonObject
            {
                ["uri"] = (string?)args[0],
                ["takeFocus"] = true,
                ["selection"] = new JsonObject { ["start"] = position.DeepClone(), ["end"] = position.DeepClone() },
            },
        }).ConfigureAwait(false);
        return null;
    }

    private JsonNode? Completion(JsonObject p)
    {
        if (Locate(p) is not { } at) return null;
        var items = new JsonArray();
        var incomplete = false;
        foreach (var c in Features.Complete(at.Analyzer, at.Offset))
        {
            var item = new JsonObject
            {
                ["label"] = c.Label,
                ["kind"] = (int)c.Kind,
                ["textEdit"] = new JsonObject { ["range"] = Range(at.Lines, c.ReplaceStart, Math.Max(c.ReplaceEnd, c.ReplaceStart)), ["newText"] = c.InsertText },
                ["filterText"] = c.Label,
            };
            if (c.Detail != null) item["detail"] = c.Detail;
            if (c.ColorHex != null)
            {
                // LSP convention for swatches: kind Color with the color as plain-string documentation.
                item["kind"] = 16;
                item["documentation"] = c.ColorHex;
            }
            else if (c.Documentation != null)
            {
                item["documentation"] = new JsonObject { ["kind"] = "markdown", ["value"] = c.Documentation };
            }
            if (c.IsSnippet) item["insertTextFormat"] = 2;
            if (c.SortText != null) item["sortText"] = c.SortText;
            if (c.PreviewPath != null) item["data"] = new JsonObject { ["preview"] = c.PreviewPath, ["root"] = at.Analyzer.ProjectDirectory };
            if (c.Kind is Features.ItemKind.File or Features.ItemKind.Folder) incomplete = true;
            items.Add(item);
        }
        // File lists depend on the typed name (deep matches), so ask again as it changes.
        return new JsonObject { ["isIncomplete"] = incomplete, ["items"] = items };
    }

    /// <summary>Fills in a file's preview only for the item the editor highlights (images are big as data: URIs).</summary>
    private static JsonNode ResolveCompletion(JsonObject item)
    {
        var result = (JsonObject)item.DeepClone();
        if ((string?)item["data"]?["preview"] is { } path && Assets.PreviewMarkdown(path, (string?)item["data"]?["root"]) is { } markdown)
            result["documentation"] = new JsonObject { ["kind"] = "markdown", ["value"] = markdown };
        return result;
    }

    /// <summary>Names of the parts of Thickness / CornerRadius / BoxShadow values, and implicit markup keys (Path=).</summary>
    private JsonNode? InlayHint(JsonObject p)
    {
        var uri = (string)p["textDocument"]!["uri"]!;
        if (!_documents.TryGetValue(uri, out var doc)) return new JsonArray();
        var (_, analyzer, lines) = Analyze(doc);
        var range = p["range"]!;
        var start = lines.GetOffset((int)range["start"]!["line"]!, (int)range["start"]!["character"]!);
        var end = lines.GetOffset((int)range["end"]!["line"]!, (int)range["end"]!["character"]!);
        var array = new JsonArray();
        foreach (var h in Features.InlayHintsIn(analyzer, start, end, _hintOptions))
        {
            var (line, ch) = lines.GetPosition(h.Offset);
            array.Add(new JsonObject
            {
                ["position"] = new JsonObject { ["line"] = line, ["character"] = ch },
                ["label"] = h.Label,
                ["kind"] = h.IsType ? 1 : 2, // Type : Parameter
                ["paddingRight"] = h.PadRight,
                ["paddingLeft"] = h.PadLeft,
            });
        }
        return array;
    }

    private JsonNode? DocumentColor(JsonObject p)
    {
        var uri = (string)p["textDocument"]!["uri"]!;
        if (!_documents.TryGetValue(uri, out var doc)) return new JsonArray();
        var (_, analyzer, lines) = Analyze(doc);
        var array = new JsonArray();
        foreach (var c in Features.DocumentColors(analyzer))
        {
            array.Add(new JsonObject
            {
                ["range"] = Range(lines, c.Start, c.End),
                ["color"] = new JsonObject
                {
                    ["red"] = (c.Argb >> 16 & 0xFF) / 255.0,
                    ["green"] = (c.Argb >> 8 & 0xFF) / 255.0,
                    ["blue"] = (c.Argb & 0xFF) / 255.0,
                    ["alpha"] = (c.Argb >> 24 & 0xFF) / 255.0,
                },
            });
        }
        return array;
    }

    /// <summary>How a picked color is written back: hex, rgb()/rgba() or hsl()/hsla().</summary>
    private static JsonNode ColorPresentation(JsonObject p)
    {
        var c = p["color"]!;
        static uint Channel(JsonNode? v) => (uint)Math.Clamp(Math.Round(((double?)v ?? 0) * 255), 0, 255);
        var argb = Channel(c["alpha"]) << 24 | Channel(c["red"]) << 16 | Channel(c["green"]) << 8 | Channel(c["blue"]);
        var array = new JsonArray();
        foreach (var text in ColorSupport.Presentations(argb))
            array.Add(new JsonObject { ["label"] = text, ["textEdit"] = new JsonObject { ["range"] = p["range"]!.DeepClone(), ["newText"] = text } });
        return array;
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Typing '/' before '>' in &lt;Button …&gt;&lt;/Button&gt; makes it &lt;Button …/&gt;: the now-redundant end tag is removed.</summary>
    private JsonArray? OnTypeFormatting(JsonObject p)
    {
        var uri = (string)p["textDocument"]!["uri"]!;
        if ((string?)p["ch"] != "/" || !_documents.TryGetValue(uri, out var doc)) return null;
        string text;
        lock (doc) text = doc.Text;
        var lines = new LineIndex(text);
        var pos = p["position"]!;
        var offset = lines.GetOffset((int)pos["line"]!, (int)pos["character"]!);
        var edit = Features.SelfClosingEdit(text, offset);
        if (edit is not { } e) return null;
        return new JsonArray(new JsonObject { ["range"] = Range(lines, e.Start, e.End), ["newText"] = "" });
    }

    // ---------------------------------------------------------------- semantic tokens (style selectors)

    /// <summary>Custom token types; languages/axaml/semantic_token_rules.json maps them to theme styles.</summary>
    private static readonly string[] TokenTypes = ["selectorType", "styleClass", "styleClassUnused", "pseudoClass", "templateSelector", "selectorName", "nestingSelector"];

    private JsonObject? SemanticTokens(JsonObject p)
    {
        var uri = (string)p["textDocument"]!["uri"]!;
        if (!_documents.TryGetValue(uri, out var doc)) return null;
        var (_, analyzer, lines) = Analyze(doc);
        var used = analyzer.StyleProvider?.Invoke()?.UsedClasses;
        var data = new JsonArray();
        var (prevLine, prevChar) = (0, 0);
        foreach (var (token, start, end) in Selectors.DocumentTokens(analyzer.Result.Doc).OrderBy(t => t.Start))
        {
            int type = token.Kind switch
            {
                SelectorTokenKind.Type => 0,
                SelectorTokenKind.Class => used == null || used.Contains(token.Text[1..]) ? 1 : 2,
                SelectorTokenKind.PseudoClass => 3,
                SelectorTokenKind.Template => 4,
                SelectorTokenKind.Name => 5,
                SelectorTokenKind.Nesting => 6,
                _ => -1,
            };
            if (type < 0) continue;
            var (line, ch) = lines.GetPosition(start);
            var (endLine, endCh) = lines.GetPosition(end);
            if (endLine != line || endCh <= ch) continue;
            data.Add(line - prevLine);
            data.Add(line == prevLine ? ch - prevChar : ch);
            data.Add(endCh - ch);
            data.Add(type);
            data.Add(0);
            (prevLine, prevChar) = (line, ch);
        }
        return new JsonObject { ["data"] = data };
    }

    private static JsonObject Range(LineIndex lines, int start, int end)
    {
        var (sl, sc) = lines.GetPosition(start);
        var (el, ec) = lines.GetPosition(end);
        return new JsonObject
        {
            ["start"] = new JsonObject { ["line"] = sl, ["character"] = sc },
            ["end"] = new JsonObject { ["line"] = el, ["character"] = ec },
        };
    }

    public static string UriToPath(string uri) => new Uri(uri).LocalPath;

    public static string PathToUri(string path) => new Uri(path).AbsoluteUri;

    public void Dispose()
    {
        _workspace.Dispose();
        _shutdown.Dispose();
    }
}
