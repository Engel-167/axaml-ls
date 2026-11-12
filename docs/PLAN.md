# Plan: features 1–9 for axaml-ls

This is a work plan for an agent (or a person) continuing `axaml-ls`, the AXAML language server in this repo.
Do the phases **in order**: later features reuse what earlier ones build. Each feature lists what to build, where,
the exact behavior, the tests to add, and how to check it on a real project.

| # | Feature | Phase |
|---|---------|-------|
| 0 | Shared groundwork: project-wide XAML index, `WorkspaceEdit` helpers | A |
| 1 | Unknown resource keys reported | B |
| 3 | Find all references | C |
| 2 | Rename across files | C |
| 4 | Quick fixes (code actions) | D |
| 5 | Selector validation | E |
| 6 | Better hover | E |
| 7 | More inlay hints | F |
| 8 | Outline and breadcrumbs (`documentSymbol`) | F |
| 9 | Folding, go to view model and code-behind | F |

---

## How to work in this repo (read first)

**Layout**
- `src/AxamlLs/` is the server (.NET 10, C#). Key files:
  - `Lsp/LspServer.cs`: JSON-RPC dispatch (`HandleRequestAsync` switch), capabilities (`Initialize`), publishing diagnostics (`Schedule`), per-document analysis cache (`Analyze(doc)`).
  - `Analysis/XamlAnalyzer.cs`: analyzes one document. It produces `AnalysisResult` (`Diagnostics`, `Refs` = `SymbolRef` list with offsets → Roslyn symbols, `Elements`, `Names`, `ClassType`). The server sets `ResourceProvider`, `StyleProvider`, `DocumentPath` and `ProjectDirectory` after `Analyze`.
  - `Analysis/Features.cs`: hover, definition, completion, document colors, and helpers such as `TargetProperty(analyzer, el, attr)` (the C# property an attribute sets, Setter `Value` included), `ResourceKeyAt`, `AssetAt` and `Word`.
  - `Analysis/Selectors.cs`: selector tokenizer (`Tokenize`), selector completion, `StyledType`, `TemplateParts`, `PseudoClasses`, `UnusedClasses`, `ClassesWithTypes`.
  - `Analysis/InlayHints.cs`, `Analysis/Assets.cs`, `Analysis/ColorSupport.cs`, `Analysis/Members.cs` (Roslyn symbol helpers).
  - `Xaml/XmlDoc.cs` (tolerant XML parser with offsets), `Xaml/MarkupExtension.cs` (`MappedText` = text plus a map back to document offsets; `MarkupExtension.Parse`), `Xaml/BindingPath.cs`.
  - `Workspace/ProjectHost.cs` (MSBuildWorkspace and Roslyn compilation, file watching), `Workspace/ProjectSnapshot.cs`, `Workspace/ResourceIndex.cs` (x:Key in project .axaml), `Workspace/ClassIndex.cs` (classes and project ControlTheme parts), `Workspace/ThemeScanner.cs` and `Workspace/AssemblyResourceScanner.cs` (read compiled themes' IL).
- `tests/AxamlLs.Tests/` holds xUnit tests. `SampleProjectFixture` loads `samples/SampleApp` (a real Avalonia 12 project) once; test classes use `[Collection("sample")]`. **Put each new feature's tests in a new file** (`ResourceKeyTests.cs`, `ReferencesTests.cs` …); `CompletionTests.cs` is already long.
- `samples/SampleApp/` is the test project. Add files there when a test needs them (a view model, a second view, assets).
- `languages/axaml/`, `src/lib.rs` and `extension.toml` make up the Zed extension (tree-sitter queries plus server startup). Most features need no changes there.

**Commands** (run from the repo root; `dotnet` comes from mise)
- `mise run test`: restores the sample and runs all tests. **Must stay green.** There are 104 tests at the start of this plan.
- `mise run install`: publishes to `~/.local/share/axaml-ls`. Run it after each feature, then run `editor: restart language server` in Zed.
- `mise exec -- python3 scripts/lsp-probe.py <file.axaml> '<anchor>@@<snippet with |>'` sends completion requests to the **installed** server against a real file.
- `mise exec -- python3 scripts/lsp-hints.py <file.axaml>` prints the inlay hints for a file.
- `mise exec -- python3 scripts/lsp-diagnostics.py <folder>` opens every `.axaml` file in one session and prints diagnostics. Use it to hunt false positives.
- Extend these scripts, or copy one into a new `scripts/lsp-*.py`, for new requests (references, rename, codeAction …). They speak plain LSP over stdio.

**Real-world check project:** `../Orion_POS/Orion_POS/` (62 `.axaml` files, Fluent theme, CommunityToolkit, Svg, Jeek localization). **Read-only: never write to it.** Rename and code actions must only *compute* edits in your checks; never apply them to Orion files.

**Shell quirks on this machine:** `rm` is aliased to `rm -i`, so use `/bin/rm -f`. `ls` prints nothing, so use `/bin/ls`.

**Conventions**
- C#: file-scoped namespaces, `sealed` records and classes, XML `<summary>` on public members that explains *why* where it isn't obvious. No new NuGet packages unless a step says so.
- JSON is built with `System.Text.Json.Nodes` (`JsonObject`, `JsonArray`), as in the existing handlers.
- Offsets: analysis works in absolute UTF-16 offsets into the document text. Convert with `LineIndex.GetPosition(offset)` and `GetOffset(line, ch)` only at the LSP boundary.
- Diagnostic codes live in `Codes` (`Analysis/AnalysisModel.cs`). Ranges: 00xx syntax, 01xx bindings, 02xx types and properties, 03xx code-behind, 04xx styles, 05xx files. New codes are given below.
- Diagnostics that depend on *other* files (resources, classes, assets) are added in `LspServer.Schedule` after analysis, like `Features.AssetDiagnostics` and `Selectors.UnusedClasses`. They are not added in `XamlAnalyzer`, because the providers are set after `Analyze`.
- Every request handler must survive bad input: `HandleRequestAsync` already catches exceptions, but return empty results instead of throwing for normal "nothing here" cases.
- **False positives are worse than missed errors.** When unsure (unresolved type, opaque object, unknown base), stay silent. This rule runs through the whole analyzer; keep following it.
- Update `README.md` (features list, settings, known limits) in the same change as each feature.
- Commit after each feature only if the user has asked you to commit; otherwise leave the changes for them to review.

**After each feature, check:**
1. `mise run test` is green, including the new tests.
2. `mise run install`.
3. The probe or diagnostics script on 2–3 Orion files gives the expected output, with no false positives from `lsp-diagnostics.py` on the whole Orion folder.
4. README updated.

---

## Phase A: Shared groundwork

### A1. Project-wide XAML index (`Workspace/ProjectXaml.cs`)

Features 1, 2, 3, 5 and 9 need to look at *every* `.axaml` file of the project, not only open ones.

```csharp
public sealed record XamlFile(string Path, string Text, XamlAnalyzer Analyzer, LineIndex Lines);

public sealed class ProjectXaml
{
    // Analyzes every .axaml under the project folder (skip ProjectFiles.IsBuildOutput), open buffers winning over disk.
    // Cache per file keyed by (text hash or disk timestamp, ProjectSnapshot reference) so repeated calls are cheap.
    public IReadOnlyList<XamlFile> Files(ProjectHost host, IReadOnlyDictionary<string, string> openBuffers);
}
```
- Keep one `ProjectXaml` per `ProjectHost` in `LspServer` (a `ConcurrentDictionary<ProjectHost, ProjectXaml>`).
- When creating each analyzer, set the same providers that `LspServer.Analyze(doc)` sets (`ResourceProvider`, `StyleProvider`, `DocumentPath`, `ProjectDirectory`). Move that wiring into one helper method that both places call, so they can't drift apart.
- Analyses of open documents can reuse `doc.Cached`.
- Analyzing all 62 Orion files may take a second or two the first time. That's acceptable for references and rename, but **not** for diagnostics on every keystroke. Diagnostics must keep using the cheap providers (`ResourceIndex`, `ClassIndex`).

Tests (`ProjectXamlTests.cs`): it finds `MainView.axaml` and `Broken.axaml` in the sample; an open buffer's text wins over the disk; a second call with unchanged files returns the same analyzer instances (cache hit).

### A2. Edits helpers (`Lsp/Edits.cs`)

```csharp
public sealed record FileEdit(string Path, int Start, int End, string NewText); // offsets into that file's current text
public static JsonObject ToWorkspaceEdit(IEnumerable<FileEdit> edits, Func<string, (string Text, LineIndex Lines)> textOf);
```
- Produce `{ "changes": { "<file uri>": [ { range, newText } ] } }`. Zed applies `changes` to unopened files too.
- Sort edits per file and drop exact duplicates. If two edits overlap, keep the first and log the rest.
- `textOf` returns the open buffer's text if the file is open, else the disk text. Offsets must come from the same text.
- URIs: use the same path↔URI conversion as `LspServer.UriToPath`. Add `PathToUri` next to it if it doesn't exist.

Tests: two files, overlapping and duplicate edits, and a non-ASCII line (positions are UTF-16).

---

## Phase B

### Feature 1: Unknown resource keys (AXN0205)

**Goal.** `{StaticResource Missing}` / `{DynamicResource Missing}` with a key declared nowhere is reported.

**Where.** New `Analysis/ResourceCheck.cs` with `public static IEnumerable<XamlDiagnostic> Unknown(XamlAnalyzer analyzer)`. Call it in `LspServer.Schedule`, next to `Features.AssetDiagnostics`. Add `Codes.UnknownResource = "AXN0205"`.

**Rules.**
- Walk every attribute value that is a markup extension, including nested ones (`Value="{Binding X, Converter={StaticResource C}}"`) and markup in `Setter Value`.
- Only `StaticResource` / `DynamicResource` without a prefix, or with a prefix that maps to the Avalonia namespace. Take the key from `ResourceKey=` or positional argument 0. **Skip** keys that are themselves markup (`{x:Static …}`, `{x:Type …}`).
- Known keys: `analyzer.Resources()` (project `.axaml` plus compiled themes and libraries) **plus keys set in C#**. Extend `ResourceIndex` (or add `CodeResourceIndex`) to scan `.cs` files with timestamp caching. Patterns:
  - `Resources["Key"]`, `Resources.Add("Key"`
  - `TryGetResource("Key"`, `FindResource("Key"`
  - `Application.Current.Resources["Key"]` (covered by the first pattern)
  Use a regex in the style of `ClassIndex.ClassesCall`.
- Also count `x:Key` of elements inside `<ResourceDictionary.ThemeDictionaries>` (already `x:Key`, check it's indexed) and keys from `ResourceInclude` / `MergeResourceInclude` files in the project (also `.axaml`, so already indexed).
- Severity:
  - `StaticResource`: **Warning**. Avalonia resolves some keys at runtime from `Application.Resources`, but those are in the project's `App.axaml`, which is indexed.
  - `DynamicResource`: **Information**. It may legitimately be added at runtime.
- Message: `Resource 'Foo' is not declared in this project or its themes.` If a close key exists (Levenshtein distance ≤ 2, or ≤ ⅓ of the length), append ` Did you mean 'Fooo'?`. Put the Levenshtein helper in `Analysis/Spelling.cs`; feature 4 reuses it.
- Range: just the key text (positional value's `MappedText.Start..End`).
- If the project isn't loaded (`analyzer.Project == null`), report nothing: library keys are unknown then.

**Tests** (`ResourceKeyTests.cs`, give the analyzer a `ResourceProvider` like `ColorAndOpenerTests` does):
- missing static key → one warning on the key range
- existing project key → none
- Fluent key (`SystemControlBackgroundAccentBrush`) → none
- dynamic missing → Information
- nested converter key checked
- `{StaticResource {x:Static …}}` → none
- C# `Resources["FromCode"] = …` in a sample `.cs` file → none for `{StaticResource FromCode}`
- the "did you mean" text

**Check on Orion.** `lsp-diagnostics.py` must show **zero** AXN0205 unless one is a real bug. If you find one, check it by hand with `grep -rn 'x:Key="Thatkey"'` before trusting it. Print AXN0205 lines in full by editing the filter at the bottom of the script.

---

## Phase C: References, then rename

Both features start from "what is under the cursor" and "where else is it". Build references first; rename is references plus edits.

### Shared: the target under the cursor (`Analysis/Targets.cs`)

```csharp
public abstract record Target;
public sealed record ElementNameTarget(string Name) : Target;     // x:Name="Foo" / Name="Foo"
public sealed record ResourceKeyTarget(string Key) : Target;      // x:Key="Foo" or {StaticResource Foo}
public sealed record StyleClassTarget(string Class) : Target;     // .Foo in a selector, Classes="… Foo …", Classes.Foo=
public sealed record SymbolTarget(ISymbol Symbol) : Target;       // a C# member/type from a SymbolRef (binding path segment, property, type, handler)

public static Target? At(XamlAnalyzer analyzer, int offset);
public static IEnumerable<(string Path, int Start, int End)> Occurrences(Target target, IReadOnlyList<XamlFile> files, ProjectSnapshot? project);
```

**`At`** recognizes, in this order:
1. A selector token under the cursor (`Selectors.Tokenize` on the `Selector` attribute value, mapped with `MappedText`). `.Foo` → `StyleClassTarget("Foo")`; `#Foo` → `ElementNameTarget`.
2. A `Classes` attribute value word → `StyleClassTarget`; the `Foo` part of a `Classes.Foo` attribute name → `StyleClassTarget`.
3. An `x:Key` value → `ResourceKeyTarget`. Otherwise `Features.ResourceKeyAt` (cursor in `{StaticResource Foo}`) → `ResourceKeyTarget`.
4. An `x:Name`/`Name` value → `ElementNameTarget`. A `SymbolRef` whose `TargetElement != null` (`#name`, `ElementName=name`) → `ElementNameTarget` of that element's name.
5. `analyzer.Result.RefAt(offset)` with a `Symbol` → `SymbolTarget`.

**`Occurrences`** returns ranges covering *only the name* (no `.`, `#`, quotes or `Classes.` prefix):
- `ElementNameTarget`, **same file only** (names are per name scope; good enough):
  - the `x:Name`/`Name` value
  - `#Name` in binding paths (find `SymbolRef`s with `TargetElement` whose name matches, or tokenize paths)
  - `ElementName=Name` values
  - `#Name` tokens in selectors in the same file
- `ResourceKeyTarget`, all files:
  - `x:Key` values
  - `StaticResource` / `DynamicResource` keys (same walk as feature 1)
  - plus C# string literals found by the patterns from feature 1, as `(path, start, end)` from a regex over the file text
- `StyleClassTarget`, all files:
  - `.Class` tokens in every selector
  - words in `Classes="…"`
  - `Classes.Class` attribute names
  - `Setter Property="Classes"` values
  - plus C# literals matched by `ClassIndex`'s `ClassesCall` regex. Refactor that regex to also return match positions.
- `SymbolTarget`, all files:
  - every `SymbolRef` in every `XamlFile` whose `Symbol` equals the target (`SymbolEqualityComparer.Default`; compare `OriginalDefinition`)
  - plus C# references via Roslyn: `SymbolFinder.FindReferencesAsync(symbol, solution)`, keeping only locations with `IsInSource` and a real file path (skip generated files; their path is under `obj/` or empty), plus the declaration locations
  - expose `ProjectHost.Solution` (today it's the private `_solution`) as a public read-only property
  - for CommunityToolkit members, also include the user-written source: `[ObservableProperty]` field or partial property, `[RelayCommand]` method. `Features.DefinitionSymbols` already finds those; reuse it.

### Feature 3: Find all references

- Capability `"referencesProvider": true`; handler `textDocument/references`.
- Response: `Location[]` (`{ uri, range }`). Honor `context.includeDeclaration`. The declaration is the `x:Name` / `x:Key` value, or the C# declaration.
- Positions in C# files: use Roslyn `Location.GetLineSpan()` directly; it already gives line and character.

**Tests** (`ReferencesTests.cs`; add a second view `samples/SampleApp/Views/Second.axaml` that uses the same resource key, class and view-model property as `MainView.axaml`):
- from a selector `.Card` → the selector, a `Classes="Card"` in the other file, and `Classes.Add("Card")` in a sample `.cs`
- from `{StaticResource Accent}` → its `x:Key` and both uses
- from a binding segment `Title` → both views' bindings plus the C# declaration (the `[ObservableProperty]` field)
- from `#SaveButton` in a binding → the `x:Name` and the use

**Check on Orion:** write `scripts/lsp-references.py <file> <line> <col>` and try a class (`ExpandButton`), a key (`SpaceGrotesk`), and a view-model property.

### Feature 2: Rename

- Capabilities `"renameProvider": { "prepareProvider": true }`; handlers `textDocument/prepareRename` and `textDocument/rename`.
- `prepareRename`: return `{ range, placeholder }` for the name under the cursor when `Targets.At` finds a target. Return `null` for things that can't be renamed: types and properties from libraries, i.e. `SymbolTarget` whose symbol is not in source.
- `rename`: validate `newName`. For names, keys and classes, require `^[A-Za-z_][A-Za-z0-9_.-]*$`; for C# symbols, use `SyntaxFacts.IsValidIdentifier`. If it's invalid, return a JSON-RPC error with code -32602 and a short message.
  - `ElementNameTarget` / `ResourceKeyTarget` / `StyleClassTarget`: `Occurrences` → `FileEdit`s → `Edits.ToWorkspaceEdit`.
  - `SymbolTarget` (a view-model member from a binding, a handler method):
    1. C# side: `Renamer.RenameSymbolAsync(solution, symbol, new SymbolRenameOptions(), newName)`. For each changed document: `newDoc.GetTextChangesAsync(oldDoc)` → `FileEdit`s (skip generated documents).
    2. CommunityToolkit, so XAML keeps matching what the generator will produce:
       - the binding refers to the generated property `Title` of field `_title`/`title` → rename the **field** to the toolkit's form of the new name (`_newName` if the field had an underscore, else `newName`, first letter lower case)
       - a partial property → rename the partial property
       - `SaveCommand` from `[RelayCommand] Save()` → rename the **method** to `newName` without a `Command` suffix (refuse if `newName` doesn't end in `Command`)
    3. XAML side: every `SymbolRef` occurrence of the symbol in all `XamlFile`s → `FileEdit` with `newName` (the property/command name as XAML sees it).
    4. Merge both sides into one `WorkspaceEdit`.
- Never touch files outside the project folder.

**Tests** (`RenameTests.cs`; compute edits and assert on them, don't apply to disk):
- rename class `Card` → `Tile`: selector, `Classes`, `Classes.Card`, C# literal
- rename key, all uses
- rename `x:Name`: `#name` and `ElementName`
- rename view-model `[ObservableProperty] private string _title` via a binding `Title` → field `_heading` + both XAML bindings `Heading`
- `prepareRename` on `Background` (a library property) → null
- invalid new name → error

**Check on Orion:** call `textDocument/rename` through a probe script and *print* the WorkspaceEdit. Don't apply it.

---

## Phase D

### Feature 4: Quick fixes (`textDocument/codeAction`)

- Capability `"codeActionProvider": { "codeActionKinds": ["quickfix"] }`; handler `textDocument/codeAction`.
- Input: `params.range` and `params.context.diagnostics`. Each diagnostic has `code` and `range`. **Recompute** everything you need from the analyzer at that range instead of trusting the client's message text.
- Output: `CodeAction[]`, each `{ title, kind: "quickfix", diagnostics: [d], edit: WorkspaceEdit, isPreferred? }`.
- New file `Analysis/QuickFixes.cs`; one method per code, so each fix can be tested on its own.

| Code | Fix | Details |
|---|---|---|
| AXN0202 unknown property, AXN0101 unknown binding member, AXN0205 unknown resource, AXN0203 unknown markup extension | **"Change to 'X'"** (up to 3, closest first) | Candidates: `SettableProperties` of the element type / members of the binding origin type (`AddBindableMembers` logic) / `analyzer.Resources()` keys / markup extension names. Use `Spelling.Closest(word, candidates, max: 3)` from feature 1. The edit replaces the diagnostic range. |
| AXN0201 unknown type | **"Import 'Ns' as xmlns:prefix"** | Find public types with that simple name. Add a lazy `ProjectSnapshot.TypesByName` index built from `Compilation.GlobalNamespace`, project and references, skipping `System.*` and non-public types. For each candidate: if a prefix for its CLR namespace already exists (`DeclaredNamespaces`), just prefix the element name (start **and** end tag). Otherwise also insert `xmlns:<p>="using:<Ns>"` on the root element after its last xmlns attribute. Prefix = lowercase last segment of the namespace, made unique. |
| AXN0100 missing x:DataType | **"Add x:DataType=\"vm:MainViewModel\""** | Candidates: project types whose name is the view name with `View`→`ViewModel` (`MainView` → `MainViewModel`), then any type named `*ViewModel` in the project, at most 5. Insert the attribute on the element the diagnostic points at (or the root), with the xmlns import as above if needed. |
| AXN0302 unknown handler | **"Create handler 'OnSave' in MainView.axaml.cs"** | Find the code-behind partial class: `analyzer.Result.ClassType` → its source declaration whose file ends in `.axaml.cs`. Get the event's delegate `Invoke` parameters from the event symbol (the `SymbolRef` of the attribute name, or `Members.FindSettable`), e.g. `(object? sender, RoutedEventArgs e)`. Insert `\n    private void OnSave(object? sender, RoutedEventArgs e)\n    {\n    }\n` before the class's closing brace (Roslyn `ClassDeclarationSyntax.CloseBraceToken.SpanStart`). Add a `using` if the args type's namespace isn't imported. |
| AXN0401 unused class | **"Remove the class from this selector"**: optional; skip unless trivial. | |
| AXN0501 missing file | **"Change to '…'"** for the closest existing file path in the same folder (Levenshtein on the file name). | |

**Tests** (`QuickFixTests.cs`): one test per row, asserting the action titles and the exact resulting text (apply the edits to the string in the test).

**Check on Orion:** introduce errors **in memory** in the probe (e.g. `Backgroun="Red"`, `{StaticResource SpaceGrotsk}`, `<UnknownWidget/>`) and print the code actions. In Zed the user runs `editor: toggle code actions` (cmd/ctrl + `.`).

---

## Phase E

### Feature 5: Selector validation

New diagnostics in `Selectors.cs`, added in `LspServer.Schedule` next to `UnusedClasses` (they need `StyleProvider` and themes):

| Code | When | Severity |
|---|---|---|
| AXN0402 | A `Type` token that doesn't resolve (`Selectors.ResolveType` returns null). The project must be loaded, and the prefix must be checkable (`ProjectSnapshot.IsCheckable`). | Error (Avalonia's compiler fails on it) |
| AXN0403 | `:pseudo` not declared by the compound's type (`PseudoClasses`, theme pseudo-classes from `ThemeScanner`) **and** not set anywhere in project C# (`PseudoClasses.Set(":x"`, `PseudoClasses.Add(":x"`; extend `ClassIndex` to collect these as `ProjectStyles.CodePseudoClasses`). Skip selector functions (`:not`, `:is`, `:nth-child`, `:nth-last-child`). | Warning |
| AXN0404 | `#PART_X` after `/template/` when the owner's `TemplateParts` list is **non-empty** and doesn't contain it, and `[TemplatePart]` doesn't declare it either. If the owner has no known theme, say nothing. | Warning |
| AXN0405 | `[Prop=value]` where `Prop` isn't a property of the compound's type (`Members.FindSettable` / `FindAttached` for `(Owner.Prop)`). | Warning |
| AXN0406 | Syntax: unbalanced `(`, an empty compound after `>`, `/template/` at the end. | Error |

- Range: the token (for `.class`/`#name`/`:pseudo`, the part after the sigil).
- Type-less compounds use `StyledType(analyzer, styleElement)` as the type, like completion does. A nested style's `^` refers to the outer style's type.

**Tests** (`SelectorValidationTests.cs`): one case per code, plus negatives:
- `Button:pointerover` ok
- `Button:flyout-open` ok (theme)
- `TextBox:error` ok (theme)
- a custom pseudo set in a sample `.cs` ok
- `Button /template/ ContentPresenter#PART_ContentPresenter` ok
- `MyControl` without a theme → no AXN0404

**Check on Orion:** `lsp-diagnostics.py` must show no AXN040x except real bugs. Orion has many `/template/` selectors; every one should pass.

### Feature 6: Better hover

Extend `Features.Hover`. Each case is a separate private method; return the first that applies.

1. **Resource key** (extend the existing branch):
   - If the resource is a color or brush and its value parses (`ResourceColor`), prepend a swatch image: an SVG 14×14 `<rect>` filled with the color, base64-encoded as `![](data:image/svg+xml;base64,…)`. Zed renders `data:` images; see `Assets.PreviewMarkdown`.
   - For a theme key, say which theme assembly it comes from.
2. **Selector token**, using `Selectors.Tokenize` to find the token under the cursor:
   - type → the type's C# summary
   - `.class` → "Set on N elements" with the first 5 file:line locations (from feature 3's `Occurrences`, or `ClassIndex` if feature 3 isn't done)
   - `:pseudo` → meaning plus declaring type. Add a static dictionary of common meanings: pointerover, pressed, disabled, focus, focus-visible, focus-within, selected, checked, unchecked, indeterminate, empty, flyout-open, error, expanded, open, dragging, inlineholder… When unsure, show only "declared by X".
   - `/template/` → "matches elements inside the template of the control before it"
   - `#PART_X` → part type and theme (`TemplateParts`)
   - `^` → "the selector of the enclosing style: …" with the outer selector text
3. **Compact values** (Thickness, CornerRadius, BoxShadow): a small table, e.g. `Left 7 · Top 2 · Right 7 · Bottom 2`, reusing the same splitting as `InlayHints`. Refactor the splitting into a shared parser so hints and hover agree.
4. **Color literal**: swatch plus the same color as hex / `rgb()` / `hsl()` (`ColorSupport.Presentations`).

**Tests** (`HoverTests.cs`): one per case, asserting key fragments of the markdown.

---

## Phase F

### Feature 7: More inlay hints

In `Analysis/InlayHints.cs`:
1. **Binding source type** (Rider shows `(MainViewModel).` before the path):
   - For `{Binding Path}` / `{CompiledBinding …}` with a non-empty path that doesn't start with `$`, `#`, `!` or `(`, add a hint `(<TypeName>).` at the path start, *after* the existing `Path=` hint. Use `PadRight: false`.
   - The type is the binding's origin. Today the analyzer computes it in the private `ResolveBindingOrigin`. Expose a public `XamlAnalyzer.BindingOriginAt(XmlElement el, MarkupExtension ext)` (or record it while analyzing, in a dictionary keyed by the extension's `Start`) instead of duplicating the logic.
   - Skip it when the origin is unknown or opaque (`Members.IsOpaque`).
   - Make it switchable: read `initializationOptions.inlayHints.bindingSource` (default `true`) in `Initialize`, and document it in the README under the existing `lsp.axaml-ls.initialization_options` example.
2. **Grid definitions**: `ColumnDefinitions="Auto,*,2*"` / `RowDefinitions` (value type name `ColumnDefinitions` / `RowDefinitions`). Add an index hint `0:` `1:` `2:` before each entry, so `Grid.Column="2"` is easy to map.
3. Optionally, `Grid.Column="2"` gets a hint showing that column's definition (`*`), when the parent Grid's definitions are in the attribute form.

**Tests:** extend `InlayHintTests` with `Hinted(...)` cases: `{Binding [Path=][(MainViewModel).]Title}`; no type hint for `$parent[…]`; the grid indexes.

### Feature 8: Outline and breadcrumbs

- Capability `"documentSymbolProvider": true`; handler `textDocument/documentSymbol` returning **hierarchical** `DocumentSymbol[]`.
- One symbol per element, children nested:
  - `name`: the element name plus identity, e.g. `Button #SaveButton .accent .big`, `Style Button.Actions:pointerover` (the selector), `SolidColorBrush [AccentBrush]` (x:Key), `ControlTheme [Button]`, `Setter Background`.
  - `detail`: the resolved type's full name.
  - `kind`: root = Class (5); named element = Field (8); Style/ControlTheme = Namespace (3); resource with key = Constant (14); Setter = Property (7); others = Object (19).
  - `range` = whole element (`el.Start..el.End`); `selectionRange` = the tag name.
  - Skip property elements (`<Grid.ColumnDefinitions>`) as their own symbols but keep their children; `d:`/ignored elements are skipped.
- Zed uses LSP symbols only with `"languages": { "AXAML": { "document_symbols": "on" } }`. Document it. Also improve the tree-sitter fallback `languages/axaml/outline.scm` so `x:Key` and `Selector` show too, with captures like the existing `Name` ones.

**Tests** (`OutlineTests.cs`): shape of the tree for a small document (names, kinds, nesting, property elements flattened).

### Feature 9: Folding, go to view model and code-behind

1. **Folding:** capability `"foldingRangeProvider": true`; handler `textDocument/foldingRange`.
   - Every element spanning several lines: from the start-tag line to the line before the end tag.
   - Multi-line comments with `kind: "comment"`.
   - `<!-- #region Name -->` … `<!-- #endregion -->` pairs with `kind: "region"`.
   - Multi-line start tags (many attributes) fold to the `>` line.
   - Document `"document_folding_ranges": "on"` for AXAML in the README.
2. **Go to view model:** capability `"typeDefinitionProvider": true`; handler `textDocument/typeDefinition`.
   - On a symbol → that symbol's type's declaration.
   - Anywhere else → the `x:DataType` of the scope at the cursor (`analyzer.ScopeAt(element).DataType`), i.e. the view model class file.
   - Zed: `editor: go to type definition`.
3. **Go to code-behind:** capability `"implementationProvider": true`; handler `textDocument/implementation`.
   - Anywhere in the file → the `x:Class` type's declaration in the `.axaml.cs` file.
   - On an event handler attribute → the handler method.
   - Zed: `editor: go to implementation`.
4. **Code lens (only if Zed supports it):** check Zed's source as described below. If `textDocument/codeLens` is supported, add lenses on the root element: `→ MainViewModel` and `→ code-behind`, whose commands open those files. If unsupported, skip it and say so in the README.

**Tests** (`NavigationTests.cs`): folding ranges for a sample with a region and a multi-line tag; type definition from a plain element inside an `x:DataType` scope lands in `MainViewModel.cs`; implementation lands in `MainView.axaml.cs`.

---

## Checking what Zed supports

When unsure whether Zed handles an LSP feature or setting, read its source instead of guessing. That's how the completion-retrigger and markdown-image behavior were found:

```sh
curl -s https://raw.githubusercontent.com/zed-industries/zed/main/crates/editor/src/completions.rs | grep -n 'confirm'
curl -s https://raw.githubusercontent.com/zed-industries/zed/main/crates/lsp/src/lsp.rs | grep -n 'resolve_support' -A10   # client capabilities
curl -s https://raw.githubusercontent.com/zed-industries/zed/main/assets/settings/default.json | grep -n 'document_symbols' -B6 -A4
```

Known facts:
- Zed never reopens the completion menu after a language-server completion (`confirm: None`).
- Markdown in docs and hover renders `data:` images but not `file://` ones.
- Semantic tokens, LSP outline (`document_symbols`) and LSP folding (`document_folding_ranges`) are all **off by default** in Zed and need per-language settings.
- Inlay hints need `"inlay_hints": { "enabled": true }`.
