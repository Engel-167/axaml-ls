# Plan: performance, round 2

A work plan for an agent or a person continuing `axaml-ls`. It follows round 1 (commit `d196e8a`: `FileIndex`,
`FileFactsCache`, incremental sync, cancellation at the request level, fzf ranking, the preview LRU).

Read `docs/ENGINEERING.md` first: every step names the CUPID rules it serves, and its "Definition of done" applies to
each step here. The repo conventions in `docs/PLAN.md` ("How to work in this repo") still hold: commands, the
read-only Orion project, shell quirks.

Do the phases **in order**. P0 builds the measuring stick and the seams the later phases plug into.

| Phase | Work | Main CUPID property | Expected gain |
|---|---|---|---|
| P0 | Benchmark harness, metrics, injectable time and file changes, project-owned indexes | Predictable | Makes every later gain measurable and testable |
| P1 | Let the editor watch files (`workspace/didChangeWatchedFiles`) | Composable | No inotify watches in `bin/`/`obj/`; no watcher-limit failures |
| P2 | Parse once (well-formedness from the tolerant parser) | Unix | About 30–50% less CPU per analysis (measure in P0) |
| P3 | Name pre-filter for references, rename, and C# literal scans | Domain-based | References and rename touch only files that can match |
| P4 | Cache the library part of `TypesByName` | Predictable | "Import type" fix no longer walks every reference per snapshot |
| P5 | Pass cancellation down (Roslyn, project scans, completion) | Predictable | Cancelled work stops within milliseconds |
| P6 | Smaller image previews | Idiomatic | Resolve messages under 350 KB instead of up to 2.7 MB |

**Success targets.** These are measured with `scripts/lsp-bench.py` on the synthetic large project from P0.1, and
must be recorded in each phase's commit message:

| Measure | Round-1 result | Target after this plan |
|---|---|---|
| Full diagnostics pass (`codeAction`), median / p95 | 3.7 / 44 ms | ≤ 3 / ≤ 20 ms |
| Find references on a resource key, 500 `.axaml` files | not measured | ≤ 150 ms cold, ≤ 30 ms warm |
| Asset completion, p95 | 11.5 ms | ≤ 10 ms |
| inotify watches held by the server (Linux, `/proc/<pid>/fdinfo`) | one per folder, `bin/` and `obj/` included | 0 when the editor watches |
| Largest `completionItem/resolve` response | ~2.7 MB | ≤ 350 KB |

---

## P0. Measuring stick and seams

### P0.1 Benchmark harness: `scripts/lsp-bench.py` (Predictable P6, P8)

Round 1 measured with throwaway probes. Make them permanent.

- `scripts/make-big-project.py <out-dir>`: copies `samples/` and adds:
  - 30,000 files under `obj/`
  - 3,000 SVG and PNG assets in nested folders
  - 500 `.axaml` views (copies of `Second.axaml` without `x:Class`, each with an `x:Key` and a `Classes=` use)
  - 200 `.cs` files with `Classes.Add("…")` and `Resources["…"]`

  The output must be deterministic: fixed names and contents, no randomness.
- `scripts/lsp-bench.py <server dll> <project dir> [--scenario name]` speaks LSP over stdio (copy the framing from
  `scripts/lsp-smoke.py`). Scenarios:
  - `typing`: 40 incremental edits, `codeAction` after each (the full diagnostics pass)
  - `complete-asset`: types `Source="/ico_5_4"` char by char
  - `references`: references on an `x:Key` used in all 500 views
  - `rename`: `prepareRename` and `rename` of the same key; computes edits, never applies them
  - `resolve`: `completionItem/resolve` on the largest image
  - `cancel`: a references request cancelled after 5 ms

  It prints one JSON line per scenario: median, p95, max, response bytes and publish counts. With `--compare
  old.json`, it exits 1 on a regression over 15%.
- Add `mise run bench -- <dll>`, which builds the big project into a temp folder, runs every scenario and deletes the
  folder afterwards.

**Done when:** `mise run bench` on the current `main` prints a baseline. Commit that baseline as
`docs/plans/bench-baseline.json`.

**Status: done.** What changed from the sketch above, and why:
- Comparing against stored numbers was too noisy on a desktop (the `powersave` governor, a browser): the same build
  "regressed" by 15-100% between runs. So the gate is now side by side: `mise run bench` builds `main` (or
  `-- --base-ref REF`) in a temporary worktree, starts both servers on the same project and alternates rounds
  between them. An A/A run (the same build on both sides) passed 4 times out of 4.
- Every scenario warms up once, then runs 3 rounds; the round with the lowest median counts (best of N).
- A regression is >15% on a median (>30% on a p95, where GC pauses land) and more than 2 ms (5 ms for p95), and it
  must reproduce when the flagged scenarios are measured again.
- `docs/plans/bench-baseline.json` records absolute numbers for reference (`--compare` still checks against it, for
  quiet machines). Recorded with `scripts/bench.sh --no-base --out docs/plans/bench-baseline.json`.

### P0.2 Metrics (Predictable P6)

- New `Diagnostics/ServerMetrics.cs`. Make it a `sealed class` instance owned by `LspServer` and passed down, not a
  static (P2). It wraps `new Meter("AxamlLs")`:
  - `analysis.duration` (histogram, ms, tag `phase`: `parse`, `wellformed`, `resolve`, `diagnostics`)
  - `request.duration` (histogram, tag `method`)
  - `request.cancelled` (counter)
  - `index.rebuilds` (counter, tag `index`)
  - `files.scanned` (counter)
- Time measurement uses `TimeProvider.GetTimestamp()` / `GetElapsedTime`.
- **Done when:** `dotnet-counters monitor -n AxamlLs --counters AxamlLs` shows live numbers while typing in Zed. Add one
  test that records into a `MeterListener` and checks `request.duration` is reported for `textDocument/hover`.

**Status: done, with three differences.**
- The class is `Telemetry/ServerMetrics.cs`, not `Diagnostics/`, to keep it apart from XAML diagnostics and
  `System.Diagnostics`.
- Editors start the server as `dotnet AxamlLs.dll`, so the process is named `dotnet`. Use
  `dotnet-counters monitor -p <pid> --counters AxamlLs`. This was checked against a benched server: every
  `analysis.duration` phase and every `request.duration` method showed up.
- `index.rebuilds` and `files.scanned` exist but have no call sites yet. They record from the project indexes,
  which are still static, so P0.4 wires them in when `ProjectIndexes` owns those caches.

`tests/AxamlLs.Tests/LspTestClient.cs` drives an in-process server over in-memory pipes, and later handler tests can
use it.

### P0.3 Injectable file changes and time (Predictable P5, Composable C2)

```csharp
/// <summary>A change to a file below a project folder, from whichever source watches it.</summary>
internal readonly record struct FileChange(string Path, FileChangeKind Kind);
internal enum FileChangeKind { Created, Changed, Deleted }

/// <summary>Something that reports file changes: the editor (P1), a FileSystemWatcher, or a test.</summary>
internal interface IFileChangeSource
{
    event Action<IReadOnlyList<FileChange>> Changed;
    /// <summary>Raised when changes may have been lost (watcher overflow): every cache must revalidate.</summary>
    event Action Overflowed;
}
```

- Move the `FileSystemWatcher` out of `FileIndex` into `Workspace/FileSystemChangeSource.cs`. `FileIndex` takes an
  `IFileChangeSource` and a `TimeProvider` in its constructor.
- Tests use `TestFileChangeSource` (raises events on demand) and `FakeTimeProvider`. That type is in
  `Microsoft.Extensions.TimeProvider.Testing`, a test-only package; that is the C5 reason. Rewrite
  `File_index_prunes_build_folders_and_sees_new_files` without `Thread.Sleep`.

**Status: done.**
- `Workspace/FileChanges.cs` holds `FileChange`, `IFileChangeSource` and `FileSystemChangeSource`.
- The interface has one addition: `bool Reliable`. Without a watcher (the inotify limit) the index has to know to
  fall back to its one-second refresh.
- `FileIndex` forwards overflow as its own `Overflowed` event. `ProjectHost` reloads the project on it, because
  C# changes may have been lost (before, an overflow only reset the file caches).
- Tests: no `Thread.Sleep` is left. The index tests drive `TestFileChangeSource` and `FakeTimeProvider`. One test
  awaits a real watcher's rename event (an event with a timeout, not polling).

### P0.4 Project-owned indexes (Predictable P2, Composable C2)

- New `Workspace/ProjectIndexes.cs`: one instance per project folder, owned by `WorkspaceManager`. It holds the
  `FileIndex`, the `FileFactsCache`s of `ResourceIndex` and `ClassIndex`, and their merged results.
- `ResourceIndex` and `ClassIndex` become instance classes (or keep static `Scan` functions, which are pure, and move
  the cache fields into `ProjectIndexes`).
- Replace `XamlAnalyzer`'s mutable `ResourceProvider`, `StyleProvider` and `ProjectFiles` with one constructor
  argument:

  ```csharp
  /// <summary>What an analysis may ask about the rest of the project; null members mean "this document only".</summary>
  internal sealed record ProjectContext(string? ProjectDirectory, string? DocumentPath,
      Func<IReadOnlyList<ResourceEntry>> Resources, Func<ProjectStyles> Styles, Func<IReadOnlyList<XamlFile>> Files);
  ```

  `XamlAnalyzer.Analyze(text, project, compiledDefault, context)` sets it once; `ProjectXaml.Wire` disappears.
- **Done when:** no `static` field in `Workspace/` holds per-project data (`AssemblyResourceScanner` and
  `ThemeScanner` may keep a static cache keyed by DLL path and timestamp, since DLLs are global; say so in their
  `<summary>`). All tests are green, and the bench shows no change.

**Status: done.**
- `Workspace/ProjectIndexes.cs` holds, per folder, the `FileIndex`, the per-file fact caches of resources and
  styles, and their merged results. `WorkspaceManager.IndexesFor(folder)` owns the instances, and each
  `ProjectHost` gets its project folder's indexes. `ResourceIndex` and `ClassIndex` keep only their pure `Scan`
  functions; `FileIndex.For` and its static registry are gone.
- `ProjectContext` is passed to `XamlAnalyzer.Analyze`. The old provider properties are now read-only views of
  it, so callers didn't change. `ProjectContext.For(path, host, indexes, buffers, files)` replaces
  `ProjectXaml.Wire`, and is still the one place that builds the context for open documents and for project
  scans. The context also carries the `FileIndex`: asset completion and missing-file checks use it instead of
  looking one up by folder.
- `index.rebuilds` (tags `resources` and `styles`) and `files.scanned` now record from `ProjectIndexes`.
- A bug fixed on the way: asset deep search had `a && b || c && d` where `(…)` was meant. It could dereference a
  null index, which couldn't happen before because the index was never null.
- `AssemblyResourceScanner` and `ThemeScanner` keep static caches keyed by DLL path and timestamp. Their
  `<summary>` says why.

---

## P1. Let the editor watch files

**Why:** .NET's `FileSystemWatcher` with `IncludeSubdirectories` puts an inotify watch on *every* folder, including
`bin/` and `obj/`, which hold thousands. Large repos hit `fs.inotify.max_user_watches` (the server then falls back to
the one-second refresh), and builds flood the server with events it throws away. The editor already watches the
workspace. LSP lets the server ask it to forward only matching changes.

**Build** `Lsp/EditorFileWatcher.cs : IFileChangeSource` (Composable: `FileIndex` doesn't know who watches).

1. In `Initialize`, read `capabilities.workspace.didChangeWatchedFiles.dynamicRegistration` and
   `relativePatternSupport`.
2. On the `initialized` notification, if both are supported, send `client/registerCapability` with id
   `axaml-watch`, method `workspace/didChangeWatchedFiles` and these `watchers`:
   - `**/*.{axaml,xaml,cs,csproj,props,targets}` (kind 7 = create | change | delete)
   - asset extensions `**/*.{png,jpg,jpeg,gif,bmp,webp,ico,svg,ttf,otf}` (kind 5 = create | delete; content changes
     don't matter for assets)
   - with relative pattern support, a `RelativePattern` whose `baseUri` is each project folder, registered when its
     `ProjectHost` is created
3. Handle the `workspace/didChangeWatchedFiles` notification: map `FileChangeType` 1/2/3 to
   `Created`/`Changed`/`Deleted`, drop pruned paths (`FileIndex.IsPruned`), and raise one batched `Changed` event.
4. `WorkspaceManager` picks the source per project. Use `EditorFileWatcher` when the registration succeeded (the
   client answered the request without an error), else `FileSystemChangeSource`. Log which one is used, once per
   project (P6).
5. An editor that sends no events at all is a silent failure, so add a guard. If no event arrives within 10 s of the
   server itself writing a file (the C#-rename follow-up writes closed `.axaml` files), log a warning and switch that
   project to `FileSystemChangeSource`.

**Tests** (`FileWatchingTests.cs`):
- Registration JSON for a client with and without `relativePatternSupport`.
- Notification → `FileChange` batch mapping, including pruned paths dropped.
- `FileIndex` invalidates on `Created`/`Deleted` but not on `Changed` (structure versus content generation).
- The fallback switch, driven with `FakeTimeProvider`.

**Check:** in Zed, with `--verbose`, the log says "file changes from the editor".
`cat /proc/$(pgrep -f AxamlLs.dll)/fdinfo/* 2>/dev/null | grep -c '^inotify wd:'` reports 0 watches (one line per
watched folder).
Creating an `.svg` in Zed's project panel makes it show up in path completion.

**Status: done.**
- `Lsp/EditorFileWatcher.cs` and `Lsp/Handlers/WorkspaceNotifications.cs` (`initialized`,
  `workspace/didChangeWatchedFiles`). The registration call is passed in as a function, so the tests drive it
  without an editor.
- Differences from the sketch above:
  - With `relativePatternSupport`, each project folder registers its own `RelativePattern` when its indexes are
    created (`axaml-watch-N`). Without it, there is one workspace-wide glob registration on `initialized`.
  - While a registration is unanswered, the folder counts as unwatched, and the file index refreshes every second.
    The registration times out after 10 s and falls back to a watcher.
  - `LspClient.CallAsync` now waits for the editor's response. Before, the read loop dropped every response.
  - `FileIndex`'s one-second fallback generation only ever increases. A source that becomes reliable later could
    otherwise bring back an old generation that a cache entry still holds.
  - Every switch (registered, refused, silent) raises `Overflowed`, so caches revalidate whatever went unseen.
- Checked on the generated 33k-file project with an editor-like client:
  - Registration supported: 0 inotify watches, the log says "File changes for …: from the editor", and a new
    `.png` reported by `didChangeWatchedFiles` shows up in path completion.
  - Registration not supported: 350 watches (one per folder), and the same completion works through the
    fallback watcher.
  - Bench against `main`: no regression.
- Still to check by hand: the same in Zed with `--verbose`.

---

## P2. Parse once

**Why:** `XamlAnalyzer.Run` parses every document twice: `XmlDoc.Parse` (tolerant, gives the tree) and
`WellFormedness.Check` (`XmlReader`, gives the error message Avalonia would show). It also builds a second `LineIndex`
when there is an error. Measure the share with the `phase` tag from P0.2 before starting. If `wellformed` is under 15%
of analysis time, stop after P2.1.

**P2.1. Cheap wins (Unix U4):** pass the server's `LineIndex` into `Run` instead of building another, and skip
`WellFormedness.Check` when the text is unchanged from the previous analysis of the document (keep the last
`(text reference, error)` pair on `OpenDocument`).

**P2.2. Tolerant parser as the fast path, `XmlReader` as the oracle (Predictable P7, P8).** Keep `XmlReader` as the
source of truth for *messages*, and run it only when needed:

- `XmlDoc.Parse` also computes `bool CertainlyWellFormed`. It is true only when none of the following was seen:
  - an unclosed or mismatched tag, or more than one root, or text outside the root
  - a duplicate attribute, a missing or unterminated quote, or `<` inside an attribute value
  - an unknown entity reference, a `--` inside a comment, or an invalid name character
  - an undeclared namespace prefix (on an element or an attribute)
- `WellFormedness.Check` runs only when `CertainlyWellFormed` is false.
- Name it `CertainlyWellFormed`, not `IsWellFormed`: it is allowed to say "not sure", never to say "fine" wrongly.

**Tests** (`WellFormednessTests.cs`):
- One case per rule above: the parser says not certain, and `XmlReader` agrees there is an error.
- **Differential property test:** for every `.axaml` in `samples/` and 2,000 deterministic mutations of each (seeded
  `Random(42)`: delete, duplicate or swap a character, insert one of `<>"&/=`), assert that `CertainlyWellFormed`
  implies `XmlReader` finds no error. A single counterexample fails the build. The test prints the mutated text.

**Done when:** the bench `typing` median improves by the measured share, and the Orion diagnostics check (read-only)
reports exactly what it did before.

**Status: P2.1 done; P2.2 not started, by the rule above.**
- Measured share of `wellformed` in analysis time:

  | Where | Share |
  |---|---|
  | Bench project, server `phase` tag | under 1% (0.06 ms against 13 ms for `resolve`) |
  | Orion, 62 files, project loaded | 6% |
  | Samples, no project | 17% |

  Real projects are under 15%, so P2.2 stays unbuilt. Revisit it if a profile shows otherwise.
- P2.1 as implemented:
  - `XamlAnalyzer.Analyze` takes the caller's `LineIndex` (exposed as `Lines`, built lazily when not given) and the
    `WellFormed` check of an earlier analysis. The check is reused when the text is the same string object.
  - `Documents` passes both, so a re-analysis after a project reload skips the XML reader.
  - `ProjectXaml` keeps an unchanged file's text across project snapshots (no re-read from disk) and reuses its check.
- Checks:
  - `check` output on Orion and on `samples/` is identical to the previous commit (Orion: 0 diagnostics; samples:
    10 errors).
  - Bench against `main`: no regression. `typing` is unchanged, as expected for a sub-1% share.

---

## P3. Name pre-filter for references, rename, and C# literals

**Why:**
- `Targets.OccurrencesAsync` analyses *every* `.axaml` file of the project (`ProjectXaml.Files`). Each analysis
  includes Roslyn symbol resolution, even when the target name appears in three files.
- `Targets.CodeFiles` walks the folder and reads every `.cs` file on each request.
- `Targets.AddLocation` re-reads the whole C# file once *per reference* to compare lengths.

Roslyn solves the same problem with a per-document identifier index (`SyntaxTreeIndex`) checked before any binding.
Do the same, in AXAML terms (Domain-based D1).

**Build:**

1. `Workspace/NameIndex.cs`: per-file facts, using the `FileFactsCache` pattern, so it is cached by text, timestamp
   and generation.

   ```csharp
   /// <summary>The words a file contains (identifier-like runs [A-Za-z_][A-Za-z0-9_.-]*), for cheap "can this file mention X?" checks.</summary>
   internal sealed record FileWords(FrozenSet<string> Words);
   ```

   Split dotted runs too: `Grid.Row` adds `Grid.Row`, `Grid` and `Row`. `FrozenSet<string>` (BCL) gives fast lookups
   for read-heavy sets. Scanning is a single pass over the text, with no regex.
2. `ProjectXaml.Files(host, buffers, Func<string, bool>? mayContain = null)`: only files whose `FileWords` pass the
   filter are analysed. Others are not touched, not even read when their words are cached.
3. `Targets.CandidateNames(Target)` (Domain-based) says which words make a file a candidate:
   - **Element name:** the name (origin file only, as today).
   - **Resource key or class:** the key or class.
   - **Symbol:** its name plus the names it is known by in XAML. That means the CommunityToolkit pairs
     (`_title`/`title` ↔ `Title`, `Save` ↔ `SaveCommand`, as `CodeRenames` already derives), and for a type, its
     name, which also covers `x:Class`/`x:DataType` values via the dotted split.
4. C# literals: `CodeLiterals.Classes/ResourceKeys/PseudoClasses` results become per-file facts in the same cache,
   filtered by `FileIndex.WithExtensions(".cs")`. `Targets.CodeFiles` is deleted (U4).
5. `AddLocation`: compare `loc.SourceTree.Length` with the file's length from `FileInfo` once per path per request,
   kept in a request-scoped `Dictionary`. Better still, compare `SourceText` checksums with the open-buffer or disk
   text through a request-scoped cache.

**Tests** (`NameIndexTests.cs`):
- Word extraction, including dotted names and names inside markup (`{StaticResource AccentBrush}`).
- A references request on the big project analyses only candidate files (assert through the `files.scanned` metric
  from P0.2).
- The results equal the unfiltered search for every target kind on `samples/SampleApp`. This is a differential test:
  run with `mayContain = null` and with the filter, and compare.

**Done when:** bench `references` and `rename` meet the targets above.

---

## P4. Cache the library part of `TypesByName`

**Why:** `ProjectSnapshot.BuildTypesByName` walks every namespace of every referenced assembly (Avalonia has
thousands of types) for each new snapshot, on the first "import type" quick fix after each `.cs` save.

**Careful (Predictable P4):** Roslyn symbols from an old `Compilation` must not be mixed with a new one.
`SymbolEqualityComparer` and `ContainingAssembly` comparisons break across compilations. So cache **names**, not
symbols:

1. `Workspace/LibraryTypeNames.cs`: simple name → fully qualified metadata names
   (`"Button" → ["Avalonia.Controls.Button"]`) for public types of the referenced assemblies.
   - Built with `System.Reflection.Metadata` straight from the DLLs (like `ThemeScanner`), not from Roslyn symbols.
   - Cached by DLL path and timestamp, so it is shared by snapshots with the same references (reuse the
     reference-set check from round 1's `ProjectSnapshot` constructor).
2. `ProjectSnapshot.TypesNamed(string simpleName)` returns `IReadOnlyList<INamedTypeSymbol>`. It is built from:
   - the source types: `Compilation.GetSymbolsWithName(simpleName, SymbolFilter.Type)`, which is already indexed by
     Roslyn
   - `Compilation.GetTypeByMetadataName` for each cached library name

   Order: project first, then by namespace, as today.
3. `QuickFixes.UnknownType` and `MissingDataType` use `TypesNamed` and the source-only enumeration.
   `TypesByName` is deleted.

**Tests:** the same fixes as today in `QuickFixTests.cs`, unchanged and green. Add a test that two snapshots with the
same references share one `LibraryTypeNames` instance.

---

## P5. Pass cancellation down

**Why:** round 1 answers `$/cancelRequest` with `-32800`, but the work keeps running. For references and rename that
means a full Roslyn `SymbolFinder` search and the analysis of every project file.

**Build** (Predictable P4, Idiomatic I1):

1. Handlers get the token. When P0 of the features plan has split `LspServer`, the handler signature is
   `Task<JsonNode?> HandleAsync(JsonObject p, CancellationToken ct)`. Until then, pass `cts.Token` from
   `HandleRequestAsync`.
2. Thread `CancellationToken ct` as the last parameter through:
   - `Targets.OccurrencesAsync`, `CodeSymbolOccurrencesAsync` and `CodeReferencesOfAsync`, into
     `SymbolFinder.FindReferencesAsync(symbol, solution, ct)`
   - `Rename.ComputeAsync`, into `Renamer.RenameSymbolAsync(…, ct)`
   - `ProjectXaml.Files` (`ct.ThrowIfCancellationRequested()` between files)
   - `CodeRenames.DetectAsync`
   - `ProjectHost.EnsureLoadedAsync(ct)`: the *wait* is cancellable, the load itself keeps going for the next caller
3. Synchronous loops that can be long check the token every N items (N = 256). That covers `Assets.Complete`'s deep
   scan and `Completion.At` over types.
4. **Content modified** (LSP `-32801`): for `semanticTokens`, `inlayHint` and `codeLens`, if the document version
   changed between the start and the end of the computation, answer `ContentModified` instead of stale data.
5. ~~Remove the last sync-over-async~~ (done): the class hover uses `Targets.TextOccurrences`, the synchronous part
   of `OccurrencesAsync`.

**Tests** (`CancellationTests.cs`):
- A pre-cancelled token makes `OccurrencesAsync` throw `OperationCanceledException` before analysing any file (via
  `files.scanned`).
- A version change mid-request yields `-32801`, driven through a test hook in the document store.

**Done when:** bench `cancel` shows the server idle (no CPU, measured with `request.duration` of the cancelled
request) within 20 ms of the cancel.

---

## P6. Smaller image previews

**Why:** `Assets.PreviewMarkdown` embeds any image up to 2 MB as base64, which is about 2.7 MB of JSON per resolve.
The editor still has to parse and decode it.

**P6.1. No new package (Idiomatic I4).**
- Lower `MaxPreviewBytes` to 256 KB of source bytes (about 350 KB once encoded).
- New `Analysis/ImageInfo.cs`: read pixel size from headers only (PNG `IHDR`, JPEG `SOFn`, GIF logical screen, BMP
  `BITMAPINFOHEADER`, ICO directory, WebP `VP8`/`VP8L`/`VP8X`). That is a few dozen lines with
  `BinaryPrimitives`, no decoding.
- Show the info line as `` `/Assets/logo.png` · 512×512 · 1.4 MB `` for every image. Images over the cap show this
  line without a picture.
- SVG: strip comments, `<metadata>`, editor namespaces (`inkscape:`, `sodipodi:`) and whitespace runs before
  encoding. That is safe because it touches only non-rendering content. Then apply the same cap.

**P6.2. Optional, needs a decision (C5).** Real thumbnails for large rasters need a decoder. The options:

| Package | Size | License | Note |
|---|---|---|---|
| SkiaSharp + native assets | ~10 MB per platform | MIT | Native binaries per OS/arch in the release zip |
| SixLabors.ImageSharp 3 | ~1.5 MB managed | Six Labors Split License (free for open source; check the terms) | Pure managed |
| none (P6.1 only) | 0 | n/a | Large images show size and dimensions only |

If a package is chosen, cache thumbnails on disk the way the freedesktop thumbnail spec does: key
`sha256(path + mtime + size)` → `~/.cache/axaml-ls/thumbnails/<hash>.png`, at 128 px. Keep the in-memory LRU in front
of it.

**Tests** (`AssetPreviewTests.cs`):
- Header parsing for each format, using tiny fixture files in `tests/AxamlLs.Tests/Fixtures/images/`.
- An image over the cap gives markdown with no `data:` URI but with dimensions.
- SVG minification keeps every `<path d=…>` byte-identical.

---

## Order of work and dependencies

```
P0.1 bench ─┬─ P0.2 metrics ─┬─ P2 parse once
            │                ├─ P3 name pre-filter ── (features plan F11 "N references" lens uses it)
            ├─ P0.3 seams ───┴─ P1 editor watching
            └─ P0.4 ProjectIndexes ── P3, P4
P5 cancellation: after the features plan's F0 handler split if it is underway, else directly
P6 previews: independent; do it whenever
```
