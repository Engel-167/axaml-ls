# Plan: missing LSP features

A work plan for an agent or a person continuing `axaml-ls`. Read `docs/ENGINEERING.md` first: every step names the
CUPID rules it serves, and its "Definition of done" applies to each feature. The repo conventions in `docs/PLAN.md`
("How to work in this repo") still hold.

Features are ordered by value, except that F0 comes first. F0 is a refactor with no behavior change; it makes each
later feature "one handler, one analysis file, one test file".

| # | Feature | LSP method(s) | Size | Depends on |
|---|---|---|---|---|
| F0 | Handler split and document store | n/a | M | n/a |
| F1 | Formatting, including attribute layout | `textDocument/formatting`, `rangeFormatting` | L | F0 |
| F2 | Highlight other uses in the file | `textDocument/documentHighlight` | S | F0 |
| F3 | Project-wide symbol search | `workspace/symbol` | M | F0, performance P0.4 |
| F4 | Follow file renames and moves | `workspace/willRenameFiles` | M | F0, performance P3 |
| F5 | Expand selection | `textDocument/selectionRange` | S | F0 |
| F6 | Markup extension argument hints | `textDocument/signatureHelp` | M | F0 |
| F7 | Clickable paths | `textDocument/documentLink` | S | F0 |
| F8 | Partial semantic tokens | `semanticTokens/range`, `/full/delta` | S | F0 |
| F9 | Editor-requested diagnostics | `textDocument/diagnostic` (LSP 3.17) | M | F0 |
| F10 | Refactorings | `codeAction` (`refactor.*`) + `codeAction/resolve` | L | F0, F1 (shared layout rules) |
| F11 | "N references" lenses on `x:Key` | `codeLens` + `codeLens/resolve` | S | F0, performance P3 |

**Before each feature,** check that Zed (the main client) supports the method: search Zed's `lsp` crate capabilities
or try it with `--verbose`. If Zed ignores a method, still build it (other editors use it), but say so under README
"Known limits".

---

## F0. Handler split and document store (no behavior change)

**Status: done** (commits 2053e78 and e64b1db). `LspServer.cs` is 188 lines and `Lsp/Handlers/` holds one class per
method. `Features` became `Hover`, `Definition`, `Completion/`, `DocumentColors`, `PropertyLookup`, `ResourceValues`,
`SymbolDocs` and `SelfClosingTag`; the facade is gone. Not done yet: the bench (performance P0.1), which arrives with P0.

**Why:** `LspServer.cs` is about 1,000 lines. It mixes JSON-RPC dispatch, the open-document store, diagnostics
scheduling, capability negotiation and per-method JSON conversion. Every new feature would make it longer (violates
Unix U1, U2). `Features.cs` (about 1,070 lines) mixes hover, definition, completion and colors (U1, D2).

**Build:**

1. `Lsp/IRequestHandler.cs`:

   ```csharp
   /// <summary>Serves one LSP request method: translates JSON to domain inputs and back (no logic of its own).</summary>
   internal interface IRequestHandler
   {
       string Method { get; }
       /// <summary>Adds this handler's server capability (e.g. "hoverProvider": true).</summary>
       void Advertise(JsonObject capabilities, ClientCapabilities client);
       Task<JsonNode?> HandleAsync(JsonObject parameters, CancellationToken ct);
   }
   ```

   Same for `INotificationHandler`.
2. `Lsp/Documents.cs`: `OpenDocument`, the analysis cache, `Analyze(doc)`, `CurrentText(doc)` and `OpenBuffers()`,
   moved out of `LspServer` unchanged.
3. `Lsp/DiagnosticsPublisher.cs`: `Schedule`, `CollectDiagnostics`, the cross-file check and publish dedupe.
4. `Lsp/Protocol.cs`: `Range`, `Location`, `ToLsp(diagnostic)`, `UriToPath`/`PathToUri`, plus a `ClientCapabilities`
   record parsed once in `initialize`. It holds booleans like `SupportsPullDiagnostics` and
   `SupportsWatchedFilesRegistration` (Domain-based: name the capabilities, don't pass raw JSON around).
5. `Lsp/Handlers/*.cs`: one file per method, for example `HoverHandler` and `CompletionHandler`. Each takes only what
   it needs in its constructor (`Documents`, `ServerOptions`, …: Composable C2). `LspServer` keeps the read loop,
   `$/cancelRequest`, and a `Dictionary<string, IRequestHandler>` built from the handler list. `Initialize` asks each
   handler to `Advertise`.
6. Split `Analysis/Features.cs` by domain (D2): `Hover.cs`, `Definition.cs`, `Completion/*.cs` (elements, attributes,
   values, bindings, markup), `DocumentColors.cs`, and `PropertyLookup.cs` (`TargetProperty`, `AvaloniaProperties`,
   `SettableProperties`). Keep `Features` as a thin facade only if the tests need it, and delete it when they don't.

**Rules:**
- Move code, don't rewrite it. Make one commit per moved area, with tests green after each.
- `ServerOptions` (inlay hint options plus the formatting options from F1) is parsed in one place and is immutable.

**Done when:** `LspServer.cs` is under 250 lines, no handler is over 150 lines, all tests are green, `mise run smoke`
gives identical output, and the bench (performance P0.1) shows no regression.

---

## F1. Formatting

**Value:** the biggest gap; XAML people expect XAML Styler-like layout on save.

**Design: a whitespace-only formatter (Predictable P7).** The formatter only changes the whitespace *between*
tokens: indentation, line breaks between attributes, a space before `/>`. It never changes:
- attribute values, element or attribute names, or attribute order (sorting is a separate refactoring, F10)
- text content inside elements that have `xml:space="preserve"`, or that contain only text (`<TextBlock>Hello  world</TextBlock>`)
- comments' inner text, CDATA, or processing instructions

That makes "formatting never changes meaning" true *by construction*, and testable.

**Build:**

1. `Analysis/Formatting/FormattingOptions.cs` (record). It combines the request's `tabSize` and `insertSpaces` with
   `initializationOptions.formatting`:

   ```jsonc
   "formatting": {
     "attributesOnSeparateLines": "auto",   // "auto" (when the tag is longer than maxLineLength) | "always" | "never"
     "attributesOnSeparateLinesThreshold": 3, // with "auto": also break when the tag has more attributes than this
     "firstAttributeOnSameLine": true,
     "maxLineLength": 120,
     "spaceBeforeSelfClosingSlash": true,
     "keepBlankLines": 1,                   // collapse runs of blank lines between elements to at most this many
     "indentAttributesBy": "alignWithFirst" // "alignWithFirst" | "indent"
   }
   ```

   The defaults follow XAML Styler's defaults where they map. Document every option in README.
2. `Analysis/Formatting/XamlLayout.cs`: a pure function
   `IReadOnlyList<TextEdit> Format(XmlDoc doc, string text, FormattingOptions o, int rangeStart = 0, int rangeEnd = int.MaxValue)`.
   - It walks `XmlDoc` (elements, attributes and the text runs between them) and computes the desired whitespace for
     each gap.
   - It emits one edit per gap whose whitespace differs, which gives minimal edits so the cursor and undo history
     survive.
   - Range formatting returns only the edits whose gap intersects the range. Gaps of elements partly in the range
     still use whole-document indentation.
3. **Refuse when unsure:** if the document is not well formed, return no edits and log it at debug level. A tolerant
   parse of broken XML must never drive edits.
4. Handlers: `FormattingHandler` (`documentFormattingProvider`) and `RangeFormattingHandler`.
5. Nice-to-have once the above is stable: on-type formatting on `\n` (indent the new line) and on `>` (close tag), next
   to the existing `/` trigger.

**Tests** (`FormattingTests.cs`, each case as a small before/after string pair):
- One test per option.
- Nested property elements (`<Grid.RowDefinitions>`).
- Multi-line markup extension values are left as they are.
- Comments and `#region` comments keep their position.
- `xml:space="preserve"` content is untouched.
- Formatting a range touches only that range.
- **Property tests over every `.axaml` in `samples/` and the read-only Orion folder (read, format in memory, never
  write):**
  - **Meaning preserved:** removing all whitespace outside attribute values and text gives identical strings before
    and after.
  - **Idempotent:** `Format(Format(x)) == Format(x)`.
  - **Still well formed:** `WellFormedness.Check` of the result is null.
  - **Analysis unchanged:** analysing the result gives the same diagnostics (codes and messages; positions mapped).

**Check:** in Zed, `editor: format` on Orion files in a scratch copy (not the originals). `format_on_save` works with
`"formatter": "language_server"`.

**Status: done** except the nice-to-have on-type formatting on `\n` and `>` (item 5).
- `Analysis/Formatting/XamlLayout.cs` and `FormattingOptions.cs`, plus `Lsp/Handlers/FormattingHandlers.cs` (one
  class for both methods). Settings are parsed in `ServerOptions` and documented in README.
- Kept as written, beyond the list above:
  - inline containers (`TextBlock`, `Span`, …, or anything with a `Run` child), because whitespace between inlines
    shows as a space;
  - empty content (`<Grid></Grid>`);
  - everything outside the root element;
  - a comment on the same line as the item before it.
- Tests (`FormattingTests`):
  - one per option, plus property elements, multi-line values, comments, preserved content, ranges and CRLF;
  - the property test on every `.axaml` in `samples/` and Orion (65 files, two settings each): only whitespace
    changes, idempotent, well formed, same elements and attribute values, same diagnostics;
  - a request through the server, which gives no edits for a broken document.
- Still to check by hand: `editor: format` and format-on-save in Zed, on a scratch copy.

---

## F2. Highlight other uses in the file

**Build:** `Analysis/DocumentHighlights.cs`: `IReadOnlyList<Highlight> For(XamlFile file, int offset)`.
- `Targets.Find(analyzer, offset)` → `Targets.OccurrencesInFiles(target, [file])`. That is the synchronous,
  single-file search from performance P5; add it here if P5 hasn't yet.
- Kind: `Write` (3) for declarations (`x:Name`, `x:Key`, the class in a selector that defines it); `Read` (2) for uses;
  `Text` (1) when unsure.
- Also highlight the matching start and end tag names when the cursor is on a tag name. Reuse `LinkedTags.At`.

**Tests** (`DocumentHighlightTests.cs`): one per target kind (element name, resource key, style class, binding
member), plus tag pairs, and nothing at all on whitespace.

**Status: done.** `Analysis/DocumentHighlights.cs` uses `Targets.TextOccurrences(target, file, [file])` filtered to
the file (C# literals dropped). A class in a selector counts as a write. `DocumentHighlightTests` covers each target
kind, tag pairs and whitespace.

---

## F3. Project-wide symbol search

**Build:**

1. `Workspace/XamlSymbols.cs`: per-file facts in the `FileFactsCache` pattern (cheap: `XmlDoc` only, no Roslyn).
   Each fact is a `XamlSymbol(string Name, XamlSymbolKind Kind, string Path, int Start, int End, string? Container)`,
   where `XamlSymbolKind` is one of View (`x:Class`), ResourceKey, ElementName, Style (its selector), ControlTheme and
   DataTemplate.
2. Move `Assets.FuzzyScore` to `Text/FuzzyMatch.cs` (Composable: it isn't about assets) and use it to rank by query.
   Use deterministic tie-breaks (P1): score, then name length, then path.
3. `WorkspaceSymbolHandler`: results across all loaded projects, capped at 200. Map LSP `SymbolKind`: View → Class (5),
   ResourceKey → Key (20), ElementName → Field (8), Style → Struct (23), ControlTheme → Interface (11),
   DataTemplate → TypeParameter (26). Document the mapping in README.

**Tests** (`WorkspaceSymbolTests.cs`): every kind is found in `samples/SampleApp`, fuzzy query ranking, and the cap.

**Status: done.**
- `Workspace/XamlSymbols.cs` scans from the parse tree only and is cached per file in `ProjectIndexes.Symbols`.
- `Text/FuzzyMatch.cs` is moved out of `Assets`.
- `Lsp/Handlers/WorkspaceSymbolHandler.cs` searches every loaded project's folder.
- A view's span covers only its simple class name; the namespace becomes `containerName`.
- `samples/SampleApp` has no `ControlTheme`, so that kind is tested on inline text instead of being added to the
  samples, whose diagnostics other tests pin.

---

## F4. Follow file renames and moves

**Value:** renaming `Assets/logo.png` or moving a view today silently breaks `Source="/Assets/logo.png"` and
`StyleInclude` paths (and AXN0501 lights up).

**Build:**

1. `Workspace/AssetReferences.cs`: per-file facts. For each attribute or markup argument that `Assets.Accepts` (or
   `AcceptsMarkup`) treats as a path, record `(value span, written form, resolved absolute path)`. The written form is
   `Absolute` (`/x`), `Avares` (`avares://Asm/x`) or `Relative` (`../x`).
2. `Analysis/FileMoves.cs`: a pure function `IReadOnlyList<FileEdit> EditsFor(IReadOnlyList<(string Old, string New)> moves, …facts)`.
   - It handles files and folders (a folder move moves every reference under it).
   - Each reference keeps its written form: an absolute path stays absolute, `avares://` keeps its assembly, and a
     relative path is re-computed from the referencing file's *new* location (an `.axaml` file that itself moves
     changes its own relative references too).
   - It never rewrites references that leave the project, or `avares://` references to other assemblies (P7).
3. `x:Class`, behind the setting `"renameFiles": { "updateClass": true }`. When `Foo.axaml` → `Bar.axaml` and the
   class's simple name is `Foo`, and its code-behind `Foo.axaml.cs` moves to `Bar.axaml.cs` in the same batch, rename
   the class to `Bar` through the existing `Rename.ComputeAsync(SymbolTarget)`. That one call updates C# and XAML
   together. If the code-behind isn't part of the move, leave `x:Class` alone.
4. Server capability: `workspace.fileOperations.willRename` with filters for `**/*` files and folders under each
   project. The `WillRenameFilesHandler` returns a `WorkspaceEdit` (`Edits.ToWorkspaceEdit`). If the client lacks
   `willRename` but sends `didRenameFiles`, compute the same edits and send them with `workspace/applyEdit`.

**Tests** (`FileMoveTests.cs`):
- One test per written form.
- A folder move.
- A moved `.axaml` file with relative references.
- Outside-project and other-assembly references untouched.
- The `x:Class` rename only when both files move, and only with the setting on.

**Status: done**, except the `didRenameFiles` fallback for clients without `willRename`. Zed, VS Code and Helix send
`willRename`; the fallback waits for a client that needs it.
- `Analysis/AssetReferences.cs` finds path references from the analysis rather than as cached per-file facts, since
  `Assets.Accepts` needs the property's type. The file list is narrowed first with the P3 name pre-filter (the moved
  file's name). A moved folder or `.axaml` file reads every file.
- `DocumentLinks` (F7) now uses `AssetReferences`.
- `Analysis/FileMoves.cs` holds `EditsFor` and `RenamedViews`. It also refuses `%`-escaped values (P7) and keeps
  `#fragment`s and a leading `./`.
- `Lsp/Handlers/FileRenameHandler.cs`: the class rename goes through `Rename.ComputeAsync`, from the `x:Class` value.
- Tests: `FileMoveTests` covers each written form, a folder move, a moved view, values left alone and `RenamedViews`.
  A request on the real sample checks that `x:Class` and the code-behind follow, and that nothing does with the
  setting off.

---

## F5. Expand selection

**Build:** `Analysis/SelectionRanges.cs`: a pure function over `XmlDoc` plus `MarkupExtension.Parse`. From the
innermost range outwards:
1. The word.
2. Then, inside a markup extension: the argument value, the argument (`Path=Title`), the extension `{…}`, and the
   enclosing extension (for nested ones).
3. Then the attribute value (inside the quotes), and the attribute (`Name="…"`).
4. Then the start tag, then the element.
5. Then the parent's content, then the parent element, and so on up to the document.

Skip ranges equal to the previous one. Handle several positions per request, as the spec allows.

**Tests** (`SelectionRangeTests.cs`): chains for a binding inside a nested extension, a plain attribute, an element in
a property element, and a comment.

**Status: done.** `Analysis/SelectionRanges.cs` and `Lsp/Handlers/SelectionRangeHandler.cs`.
- Beyond the list above, a parent's content is offered twice: first trimmed of its surrounding whitespace (the
  children), then whole.
- A tag name or text content takes the place of the attribute step.
- Tests: `SelectionRangeTests` (the four chains above) and a request with two positions.

---

## F6. Markup extension argument hints

**Build:** `Analysis/MarkupSignatures.cs`.
- At the cursor inside `{Ext …}`, find the extension type. Use the built-ins Avalonia knows (`Binding`,
  `CompiledBinding`, `StaticResource`, `DynamicResource`, `TemplateBinding`, `x:Static`, `x:Type`, `RelativeSource`,
  `OnPlatform`, `OnFormFactor`) plus the custom extensions found by `Completion.CustomMarkupExtensions`.
- Offer one `SignatureInformation` per public constructor, with positional parameters (`{StaticResource ResourceKey}`)
  and settable properties listed as named parameters (`Path`, `Mode`, `Converter`, `ConverterParameter`,
  `StringFormat`, `FallbackValue`, …). Documentation comes from `SymbolDocs.Summary` (C# doc comments).
- `activeParameter` is the index of the argument the cursor is in. For a named argument, it is the index of that
  property in the list.
- Trigger characters: `{`, `,`, and space after the extension name. Retrigger on `=`.

**Tests** (`SignatureHelpTests.cs`): `{Binding |}`, `{Binding Title, Mode=|}`, `{StaticResource |}`, a custom
extension from `samples/SampleApp/Localization`, and nested `{Binding Converter={StaticResource |}}`, which answers
for the inner extension.

**Status: done.**
- `Analysis/MarkupSignatures.cs`: the built-ins are described in a table, since their Avalonia types are compiler
  intrinsics and not all resolvable. Custom extensions are read from Roslyn, the way completion reads them.
- `Lsp/Handlers/SignatureHelpHandler.cs` sends parameter labels as offsets, because `Path` appears both positionally
  and as a name.
- A constructor parameter takes its doc from the property of the same name (primary constructors).
- Signature help is not offered on the extension name, which is completion's job.
- Tests: `SignatureHelpTests` (the five cases above plus unclosed markup while typing) and a request test.

---

## F7. Clickable paths

**Build:** `DocumentLinkHandler` reuses `AssetReferences` from F4 (or `Assets.Resolve` directly if F4 isn't done).
- One link per path value that resolves to an existing project file.
- The target is the `file://` URI, and `tooltip` is the project-relative path.
- `StyleInclude`/`ResourceInclude` sources are included.
- Missing files are not linked: AXN0501 already reports them.

**Tests** (`DocumentLinkTests.cs`): absolute, `avares://` and relative values; a missing file gives no link; a URL
(`https://…`) gives no link.

**Status: done.** `Analysis/DocumentLinks.cs` uses `Assets.Resolve` directly, since F4 is not done. Plain path values
and the path arguments of `{SvgImage …}`-like extensions are linked. `DocumentLinkTests` covers the three written
forms, a markup argument, and missing files, URLs, other assemblies and non-path properties. In Zed, selection ranges
are not used (tree-sitter) and document links depend on the version; README "Known limits" says so.

---

## F8. Partial semantic tokens

**Build** (in the semantic tokens handler, which F0 split out):
- `range`: filter `Selectors.DocumentTokens` to the range before encoding.
- `full/delta`: keep the last `(resultId, int[] data)` per document. A new request computes the new array and answers
  with one `SemanticTokensEdit`, made by trimming the common prefix and suffix of the two arrays (the same approach as
  `vscode-languageserver`'s builder). `resultId` is a per-document counter.

**Tests** (`SemanticTokensTests.cs`): `range` equals the slice of `full`; applying the delta to the old array gives
the new `full` (a property test over 200 random single-character edits of a styles file, with a fixed seed).

**Status: done.**
- `Analysis/SemanticTokens.cs` holds the encoding (moved out of the handler), `Diff` and `Apply`.
- `Lsp/Handlers/SemanticTokensHandlers.cs` has one handler per method plus `SemanticTokensResults`: the last array
  per URI, LRU of 64.
- Unlike the plan, the edit's prefix and suffix are cut at whole tokens (multiples of 5), so a client never sees half a
  token change.
- A `previousResultId` the server doesn't know gets the full array.
- Tests (`SemanticTokensTests`): range against the decoded full tokens, the 200-edit property (seed 20261120) and a
  delta through the server.

---

## F9. Editor-requested diagnostics

**Why:** with pull diagnostics the editor decides when to ask (the visible file first), and "nothing changed" costs
one hash comparison.

**Build:**
1. If `ClientCapabilities.SupportsPullDiagnostics`, advertise `diagnosticProvider`:
   `{ interFileDependencies: true, workspaceDiagnostics: false }`, and **stop pushing** for that client. Keep push for
   everyone else; `DiagnosticsPublisher` picks the mode once.
2. `DocumentDiagnosticHandler`: `resultId` = a hash of the serialized diagnostics plus the project snapshot. If it
   equals `previousResultId`, return `{ kind: "unchanged", resultId }`.
3. When cross-file facts change (round 1's `CrossFileFactsChanged`) or a project snapshot is rebuilt, send
   `workspace/diagnostic/refresh` (if `refreshSupport`) instead of re-scheduling every open document.

**Tests** (`PullDiagnosticsTests.cs`): full, then unchanged, then full after an edit; a refresh is sent when a class
used elsewhere is removed; push mode when the client lacks the capability.

---

## F10. Refactorings

**Design (Composable, Unix):** `Analysis/Refactorings/IRefactoring.cs`, with one class per refactoring:

```csharp
internal interface IRefactoring
{
    /// <summary>Cheap check for the lightbulb: may this apply at the selection? No edits computed here.</summary>
    string? TitleAt(XamlFile file, int start, int end);
    /// <summary>The edits, computed only when the user picks the action (codeAction/resolve).</summary>
    IReadOnlyList<FileEdit> Edits(XamlFile file, int start, int end, ProjectContext project);
}
```

The `CodeActionHandler` lists titles with `kind` and `data` (refactoring id, uri, range). `codeAction/resolve` (set
`resolveProvider: true`) computes the edits, so listing actions stays cheap.

Refactorings, in this order:

1. **Sort attributes** (`refactor.rewrite`). Order: `x:Class`, `xmlns*`, `x:Name`/`Name`, `x:Key`, `x:DataType`,
   attached layout (`Grid.*`, `DockPanel.*`, `Canvas.*`), layout (`Width`, `Height`, `Margin`, `HorizontalAlignment`,
   …), the rest alphabetically, then events.
   - The order is configurable in `formatting.attributeOrder` and shared with F1's options type. The formatter itself
     never sorts.
   - Comments between attributes stop the sort for that element (P7).
2. **Wrap in…** (`refactor.rewrite`): `Border`, `Grid`, `StackPanel`, `Panel`, and `ScrollViewer` when exactly one
   element is selected. Wrapping re-indents through F1's `XamlLayout` (U4).
3. **Extract to resource** (`refactor.extract`): a literal value (color, thickness, number, string) becomes
   `<SolidColorBrush x:Key="…"/>`, `<Thickness x:Key>…`, `<x:Double>` and so on, placed in the nearest `Resources`
   (or a new `<UserControl.Resources>`). The value is replaced by `{StaticResource Key}`.
   - The type comes from `PropertyLookup.TargetProperty` → value type. If the type is unknown, the refactoring is not
     offered (P7).
   - The key is derived from the property (`ButtonBackgroundBrush`) and made unique against `ResourceIndex`.
   - The editor's rename then lets the user change it: return the key range so the client can start a rename where
     supported.
4. **Extract to style** (`refactor.extract`): the selected element's settable property attributes become a
   `<Style Selector="Type.newClass">` with `Setter`s in the nearest `Styles`. The element gets `Classes="newClass"`.
   Bindings and attached properties stay on the element.
5. **Convert to compiled binding**: `{ReflectionBinding X}` → `{Binding X}`, offered only when the path resolves
   against the scope's `x:DataType` (reuse the analyzer's binding check).

**Tests:** one file per refactoring (`SortAttributesTests.cs`, …). Each refactoring's result must be well formed and
analyse with no *new* diagnostics. That is a shared helper assertion in `TargetTestSupport`.

---

## F11. "N references" lenses on `x:Key`

**Build:**
- `codeLens` returns one lens per `x:Key` with no `command` (cheap: from `XamlSymbols`, F3) and `data = { uri, key }`.
- `codeLens/resolve` counts occurrences with `Targets.OccurrencesAsync(ResourceKeyTarget)`. That count is fast only
  with the performance plan's P3 name pre-filter, so do P3 first.
- The title is `3 references` or `unused`. The command is the existing `axaml.openFile` to the first use outside the
  declaring file (Zed has no generic "show references" command; note this in README).
- Behind the setting `"codeLens": { "resourceReferences": true }`, default on.

**Tests** (`CodeLensTests.cs`): counts for keys used in 0, 1 and several files (`samples/SampleApp`), and resolve
leaves the range unchanged.

**Status: done.**
- The lens holds the key's position. `codeLens/resolve` reuses `TargetLookup` and `Targets.OccurrencesAsync`, so the
  count is exactly what find-references returns.
- `Analysis/ResourceLenses.cs` holds the title and jump rules.
- If the document changed so the position no longer names a key, the lens gets an empty title rather than a wrong count.
- Found on the way: a handler that answered with part of its parameters left the request unanswered (the JSON node
  already had a parent). `LspServer` now copies such a result.
- Tests: `CodeLensTests` (four uses across two files on the sample, zero and one use, resolve through the server
  keeping the range). The bench shows no regression.

---

## Order of work

```
F0 ─┬─ F1 formatting ── F10 refactorings (shares layout + attribute order)
    ├─ F2 highlights, F5 selection, F7 links, F8 tokens      (small; any order)
    ├─ F3 workspace symbols ── F11 lenses (needs performance P3)
    ├─ F4 file moves (better after performance P3; uses AssetReferences, also feeds F7)
    ├─ F6 signature help
    └─ F9 pull diagnostics
```

Recommended overall order across both plans:
1. Performance P0.
2. Features F0.
3. Performance P1–P3.
4. Features F1, F2, F5, F7.
5. The rest by value.
