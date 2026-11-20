# Engineering principles for axaml-ls

Every change to this repo follows the five **CUPID** properties, made concrete below for a .NET language server.
The plans in `docs/plans/` refer to these rules by name (for example "Predictable: P3"), and a review checks
a change against them.

---

## C: Composable

Small parts with a small surface and few dependencies, so they combine in new places (a CLI check, a test, another
LSP feature) without dragging the server along.

- **C1. Analysis is a pure function.** Code in `Analysis/` and `Xaml/` takes its inputs as parameters and returns
  data (records). It performs no JSON, no file I/O and no logging side effects that change its result. Files are read
  in `Workspace/`; LSP JSON is built in `Lsp/`.
- **C2. Dependencies are passed in, not reached for.** New code takes what it needs as parameters or constructor
  arguments. It does not use ambient statics. A feature that needs project data takes a project context object, not
  `ProjectHost` and the whole server.
- **C3. `internal` by default.** Make a type `public` only when another assembly needs it. Tests already see
  internals through `InternalsVisibleTo`.
- **C4. Return the narrowest useful type.** Return `IReadOnlyList<T>`, `IReadOnlySet<T>` or a record, not a mutable
  collection a caller could change behind a cache's back.
- **C5. No new NuGet package without a written reason** (size, license, native dependencies) in the plan step that
  adds it.

## U: Unix philosophy

One thing, done well, that plays well with the rest.

- **U1. One file, one domain concept.** `Hover`, `Definition`, `DocumentHighlight` and `Formatting` each get their
  own file. A file that grows past about 400 lines, or does two jobs, is split, the way `LspServer` became
  `Lsp/Handlers/` and `Features` became `Hover`, `Definition`, `Completion/` and friends.
- **U2. An LSP handler only translates.** It turns JSON parameters into domain inputs, calls one analysis function,
  and turns the result back into JSON. Logic does not live in handlers.
- **U3. Text in, text out at the edges.** Offsets inside the server are absolute UTF-16 offsets. Conversion to
  line/character happens only at the LSP boundary (`LineIndex`).
- **U4. Features reuse primitives instead of re-implementing them.** Target lookup (`Targets.Find`), occurrence search
  (`Targets.OccurrencesAsync`), path resolution (`Assets.Resolve`), fuzzy matching and `FileFactsCache` are the shared
  pieces. Extend them; don't fork them.

## P: Predictable

Same input, same output. Testable, observable, no surprises.

- **P1. Deterministic output.** Every list sent to the client is sorted (ordinal comparers, explicit `ThenBy`). No
  dictionary or hash-set enumeration order leaks into results.
- **P2. No hidden global mutable state.** Caches are instances owned by an object with a clear lifetime (the server,
  a project), not `static` fields. Existing static caches are listed under "Known deviations" and are moved as plans
  touch them.
- **P3. Every cache documents its key and its invalidation.** Put it in the `<summary>`: what makes an entry stale,
  and who notices. "Keyed by text reference and project snapshot" is enough; "cached" is not.
- **P4. Concurrency rules:**
  - Publish immutable snapshots by a single reference write (`Volatile`). Never put a multi-field struct in shared
    state.
  - Never hold a `lock` across an `await`. Use `SemaphoreSlim` for async gates, and never `lock` on the semaphore.
  - No sync-over-async: no `.Result`, `.Wait()` or `GetAwaiter().GetResult()` outside `Main`.
  - Every `async` method takes a `CancellationToken` as its last parameter and passes it on, down to Roslyn.
- **P5. Time and file changes are injectable.** Debounces and TTLs use `TimeProvider`. File changes come through an
  `IFileChangeSource`. Tests drive both, so they never `Thread.Sleep` or poll.
- **P6. Observable.** Timings and counts go to a `System.Diagnostics.Metrics.Meter` named `AxamlLs`; `dotnet-counters`
  reads it without any package. Notable events (project loaded, watcher overflow, cache rebuilt) go to `Log`.
  Debug-level logs say what was skipped and why.
- **P7. False positives are worse than missed errors.** When the analyzer cannot know (unresolved type, opaque
  object), it stays silent. Formatters and refactorings refuse to run rather than risk changing meaning.
- **P8. Tests pin behavior, not implementation.** Each feature gets its own test file, built from small XAML strings
  or `samples/SampleApp`. Behavior with invariants (formatting, incremental edits, caches) also gets property tests:
  idempotence, round-trips, and agreement with a slow reference implementation.

## I: Idiomatic

Code that reads like the .NET and LSP code people already know.

- **I1. .NET conventions:**
  - File-scoped namespaces, `sealed` classes and records, nullable enabled, `var` where the type is obvious.
  - PascalCase for types and members, `_camelCase` for private fields.
  - The `TryX(out …)` pattern for "maybe".
  - `ConfigureAwait(false)` in server code.
  - `<summary>` on public and internal members that says *why*, not what.
- **I2. Toolchain enforces it.**
  - An `.editorconfig`, plus `<AnalysisLevel>latest-recommended</AnalysisLevel>` and `<EnforceCodeStyleInBuild>true`
    in `Directory.Build.props`.
  - CI builds with `-warnaserror`. Locally, warnings stay visible but don't block.
- **I3. LSP names stay LSP names.** Handlers are named after the method they serve (`textDocument/documentHighlight`
  → `DocumentHighlightHandler`). JSON field names match the specification exactly.
- **I4. Prefer the BCL.** `System.Text.Json.Nodes`, `System.IO.Enumeration`, `System.Reflection.Metadata`,
  `TimeProvider`, `Meter`. Hand-roll only what the BCL lacks, and keep it small (like `LruCache`).

## D: Domain-based

The code speaks Avalonia, XAML and LSP, not generic plumbing.

- **D1. Domain nouns.** Use ResourceKey, StyleClass, Selector, PseudoClass, ControlTheme, TemplatePart, BindingPath,
  DataType, Asset, View and ViewModel. Avoid `Manager`, `Helper`, `Util`, `Data`, `Info` and `Processor` in new
  names. (`WorkspaceManager` stays; renames for their own sake are churn.)
- **D2. Folders follow the domain as they grow:** `Analysis/Bindings`, `Analysis/Styles`, `Analysis/Resources`,
  `Analysis/Assets`, `Analysis/Formatting`, `Analysis/Refactorings`.
- **D3. Diagnostics read like Avalonia's own.** Codes follow the ranges in `Codes`. Messages match the Avalonia
  compiler's wording where one exists.

---

## Definition of done (every change)

1. `mise run test` is green; the change adds tests in its own test file.
2. `mise run smoke` passes. For performance work, `scripts/lsp-bench.py` (performance plan, P0.1) shows no
   regression.
3. No new compiler or analyzer warnings.
4. `README.md` (features, settings, known limits) is updated in the same change.
5. The change was checked against C, U, P, I and D above. Any rule broken on purpose is written down in the commit
   message.

## Known deviations (fix them when a plan touches the code)

| Where | Rule | What's wrong |
|---|---|---|
| Files over 400 lines: `XamlAnalyzer.cs` (~930), `Selectors.cs`, `Assets.cs` (check with `wc -l`) | U1 | Split by sub-domain when a plan next changes them |
| `Targets.CodeFiles`, `Targets.AddLocation` | U4 | Walk the folder and re-read C# files instead of using `FileIndex` and `FileFactsCache` |
| Async APIs (`Targets`, `Rename`, `ProjectXaml`) | P4 | No `CancellationToken` |
