# axaml-ls

A language server for **AXAML**, the XAML dialect of [Avalonia®](https://avaloniaui.net) apps. It is written in C# and
loads your `.csproj` with Roslyn, so it sees your view models (CommunityToolkit-generated members included) and
Avalonia's own types.

Editors: [Zed](https://zed.dev) through the [AXAML extension](https://github.com/Engel-167/zed-axaml), which downloads
the server for you. Any other editor that speaks LSP can run it over stdio. For a live preview of your views, see
[axaml-previewer](https://github.com/Engel-167/axaml-previewer).

## Features

- **Diagnostics** that match what Avalonia's XAML compiler reports, while you type.
- **Hover** on bindings, types, properties and handlers, with C# doc comments.
- **Go to definition**: from a binding to the view-model property or field (also for `[ObservableProperty]` and `[RelayCommand]`), from an `x:Class`, `x:DataType` or element type to its class, from `Click="OnSave"` to the handler, and from `#name` to the element.
- **Completion**: binding paths (members of the `x:DataType`, then `.`-chained), element names (with the closing tag), attributes, property values (enums, `True`/`False`, named colors, `FontWeight.Bold`-style statics, and fitting resources as `{StaticResource …}`/`{DynamicResource …}`, in `Setter Value` too), `x:DataType` types, markup extensions and `{Binding}` keys.
- **Style selectors**: completion of control types, `.classes` the project sets, `:pseudo-classes` declared by the control (`:pointerover`, `:pressed`, …, plus `:not()`, `:is()`, `:nth-child()`), `/template/`, and `#PART_…` template parts: the ones the control's actual theme names (read from the compiled Fluent/Simple IL, or from the project's own `ControlTheme`s), filtered by the type before `#`. Selectors are colored by part (with semantic tokens on), and a class that no element in the project sets is faded with a hint (AXN0401).
- **Classes**: `Classes="|"` and `Classes.|` offer class names, those styled for that control first (by the project's selectors or its theme, e.g. Fluent's `accent` on `Button`).
- **Setter properties**: `<Setter Property="|">` offers the styled type's Avalonia properties (direct properties excluded, since styles can't set them); `Grid.|` offers Grid's attached properties.
- **File paths** in `Svg Path`, `Image Source`, `Window Icon`, `StyleInclude`/`ResourceInclude Source` and other `Uri` values (and `{SvgImage …}`): folder-by-folder completion for `/…` (project root), `avares://Assembly/…` and paths relative to the file (`../…`), filtered to the file types the property takes; typing part of a name also finds matching files in deeper folders, so a whole path is one completion. Picking a folder inserts it without the slash: Zed never reopens the menu after a completion from a language server, but typing `/` does. The highlighted file shows an image preview (embedded as a `data:` URI on `completionItem/resolve`, since Zed's Markdown only renders those). A path to a project file that doesn't exist is a warning (AXN0501), and go-to-definition opens the file.
- **Inlay hints** that name the parts of compact values, as Rider does: `Padding="[lr:]7 [tb:]2"`, `Margin="[l:]0 [t:]4 [r:]8 [b:]0"`, `CornerRadius="[top:]9 [bottom:]4"` / `[tl:] [tr:] [br:] [bl:]`, `BoxShadow="[x:]1 [y:]2 [blur:]4 [spread:]0 #66000000"`, the implicit first key of markup extensions (`{Binding [Path=]Name}`, `{StaticResource [ResourceKey=]Key}`), the type a binding path starts from (`{Binding [Path=][(MainViewModel).]Title}`, switchable, see below), and Grid definitions (`ColumnDefinitions="[0:]Auto,[1:]*,[2:]2*"`, with `Grid.Column="2"` followed by `→ 2*`). Layouts Avalonia rejects (three-value Thickness) get no hints. Zed shows inlay hints only with `"inlay_hints": { "enabled": true }`.
- **Self-closing tags**: typing `/` before the `>` of `<Button …></Button>` removes the empty `</Button>` (on-type formatting; Zed's `use_on_type_formatting` must be on, which is the default).
- **Custom markup extensions** from your xmlns prefixes (`{localization:Localize …}`), with their properties as keys; `$parent[Type]` offers control types.
- **Color values**: named colors with swatches, and templates for every syntax Avalonia parses (`#RRGGBB`, `#AARRGGBB`, `rgb()`, `rgba()`, `hsl()`, `hsla()`, `hsv()`, `hsva()`); the color picker writes hex, rgb or hsl.
- **Resources**: every `x:Key` in the project's `.axaml` files (read from disk and from unsaved buffers, no build needed) plus the keys of compiled themes such as Fluent (read from the theme DLL), ranked by whether they fit the property; hover and go-to-definition on a key.
- **Find all references** and **rename**, across files: element names (`x:Name`, `#name`, `ElementName=`, selector `#name`; within the file), resource keys (`x:Key`, `{StaticResource}`/`{DynamicResource}`, and `Resources["key"]` / `TryGetResource("key")` in C#), style classes (selectors, `Classes="…"`, `Classes.x=`, `Setter Property="Classes"`, and `Classes.Add("x")` in C#), and C# members and types used from XAML (binding segments, properties, handlers, `x:Class`/`x:DataType`). Rename of a view-model member also renames it in C# through Roslyn; for CommunityToolkit it renames the source declaration (`[ObservableProperty]` field → the field, a partial property → the property, `[RelayCommand]` method → the method, so the new command name must end in `Command`) and keeps the XAML in step. Library members cannot be renamed. Rename only edits files inside the project folder.
- **Quick fixes** (`editor: toggle code actions`): "Change to 'X'" for an unknown property, binding member, resource key or markup extension (closest names first); "Import 'Ns' as xmlns:prefix" for an unknown type (or just the prefix when the namespace is already declared); "Add `x:DataType="vm:MainViewModel"`" for a compiled binding without one (the view's own view model first); "Create handler 'OnSave' in MainView.axaml.cs" for a missing event handler (with the `using` its arguments need); "Change to '…'" for a missing file.
- **Hover** also shows a color swatch for color resources and literals (with `#hex`, `rgb()` and `hsl()`), a table for `Thickness`/`CornerRadius`/`BoxShadow` values (`Left 7 · Top 2 · Right 7 · Bottom 2`), and for selector parts the type, where a class is set (first five places), what a pseudo-class means and who declares it, template parts, `/template/` and `^`.
- **Outline and breadcrumbs**: one symbol per element (`Button #SaveButton .accent`, `Style Button.Actions:pointerover`, `SolidColorBrush [AccentBrush]`, `Setter Background`), nested. Zed uses it only with `"document_symbols": "on"` (see Setup); without it the tree-sitter outline lists named elements, `x:Key` resources and styles.
- **Folding** of multi-line elements, multi-line start tags, comments and `<!-- #region Name -->` … `<!-- #endregion -->` blocks. Zed uses it only with `"document_folding_ranges": "on"`.
- **Go to the view model and the code-behind**: `editor: go to type definition` jumps to the type of the symbol under the cursor, or (anywhere else) to the `x:DataType` in scope; `editor: go to implementation` jumps to the `x:Class` in `MainView.axaml.cs`, or to the handler method from an event attribute. With `"code_lens": "on"`, the root element also shows `→ MainViewModel` and `→ code-behind` lenses that open those files.

## What it checks

| Code | Meaning |
|---|---|
| AXN0001 | XML is not well formed (also undeclared prefixes) |
| AXN0010 / AXN0011 | Markup extension or binding path syntax |
| AXN0100 | Compiled binding with no `x:DataType` in scope (Avalonia AVLN2100) |
| AXN0101 | Binding path member does not exist on the type (AVLN2000) |
| AXN0103 | `#name` / `ElementName` not found in the document |
| AXN0104 | `TemplateBinding` property missing on the template's `TargetType` |
| AXN0201 | Unknown type: element, `x:DataType`, `{x:Type}`, cast, `$parent[T]` |
| AXN0202 | Unknown property or event on an element (attached properties too) |
| AXN0203 | Unknown markup extension |
| AXN0204 | `{x:Static}` member does not exist |
| AXN0205 | `{StaticResource}` key declared nowhere (warning); `{DynamicResource}` (information). Keys count from the project's `.axaml`, compiled themes and libraries, and C# (`Resources["k"]`, `TryGetResource("k")`) |
| AXN0301 | `x:Class` type not found |
| AXN0302 | Event handler method missing in code-behind (AVLN3000) |
| AXN0401 | Selector class no element in the project sets (faded hint) |
| AXN0402 | Selector type does not resolve (error) |
| AXN0403 | `:pseudo-class` neither declared by the type, nor styled by its theme, nor set in the project's C# (warning) |
| AXN0404 | `#PART_x` after `/template/` is not in the control's template, when the theme is known (warning) |
| AXN0405 | `[Prop=value]` names a property the type does not have (warning) |
| AXN0406 | Selector syntax: unbalanced parentheses, nothing after `>` or `/template/` (error) |
| AXN0501 | A path to a project file that does not exist (warning) |

The binding rules follow Avalonia 12, where compiled bindings are the default. The server reads
`AvaloniaUseCompiledBindingsByDefault` from the project or a `Directory.Build.props`, falling back to
Avalonia's default for the version you reference. It also honours `x:CompileBindings`, and
`ReflectionBinding` is never checked. Inside a `DataTemplate` with no `DataType`, the item type is
inferred from the parent `ItemsSource` binding, as the Avalonia compiler does. Supported path syntax:
`!`, `$self`, `$parent[T;n]`, `#name`, indexers, `(Owner.Attached)`, casts `((vm:T)DataContext)` and
the `^` stream operator. `#name.DataContext` resolves to that element's `x:DataType`.

When it cannot know a type (`object`, `Source=`, a style setter's `Self`, a base type that did not
resolve), it says nothing instead of guessing. On a real 62-file Avalonia 12 project it reports
nothing, and the fixture's deliberate mistakes match Avalonia's own build errors one for one.

## Install

The server needs the .NET SDK (10 or later): it loads your project through MSBuild.

- **Zed**: install the AXAML extension. It downloads the latest release on first use.
- **From a release**: unzip `axaml-ls-<version>.zip` from [Releases](https://github.com/Engel-167/axaml-ls/releases)
  and run `dotnet AxamlLs.dll` (LSP over stdin/stdout).
- **From source**: `mise run install` publishes it to `~/.local/share/axaml-ls` and puts an `axaml-ls` launcher in
  `~/.local/bin`. Editors that find `axaml-ls` on the PATH (the Zed extension does) use it instead of a download.

Options the server reads from `lsp.axaml-ls.initialization_options`:

```jsonc
{
  "lsp": {
    "axaml-ls": {
      "initialization_options": {
        "verbose": false,
        "inlayHints": { "bindingSource": true }   // false hides the (MainViewModel). hint before binding paths
      }
    }
  }
}
```

`--verbose` (or `"verbose": true`) logs to stderr, which editors show as the server log.

## Development

Everything is pinned with [mise](https://mise.jdx.dev) (`mise install` gets the .NET SDK).

| Task | What it does |
|---|---|
| `mise run build` | Build the server (Debug) |
| `mise run test` | Unit + fixture tests (loads `samples/SampleApp` through MSBuild) |
| `mise run check -- <dir>` | Lint `.axaml` files from the terminal, compiler style. Exits 1 on errors, so it also works in CI |
| `scripts/lsp-*.py` | Probes against the installed server and a real project: `lsp-probe` (completion), `lsp-hints`, `lsp-diagnostics` (hunt false positives), `lsp-references` (references; `rename` prints a WorkspaceEdit, never applies it), `lsp-codeactions` (quick fixes for errors introduced in memory), `lsp-request` (outline, folding, code lens, type definition, implementation, hover) |
| `mise run smoke` | Drive the server over LSP stdio like an editor (`scripts/lsp-smoke.py`) |
| `mise run package` | Build the release zip locally (`dist/axaml-ls-<version>.zip`), the same as CI |

Releases: bump `<Version>` in `src/AxamlLs/AxamlLs.csproj`, then push a tag `v<version>`. The `release` workflow tests,
packages and publishes `axaml-ls-<version>.zip`.

Layout:

```
src/AxamlLs/
  Xaml/          error-tolerant XML parser, markup extension + binding path parsers
  Workspace/     MSBuildWorkspace project loading, xmlns → CLR namespace map, .cs file watching,
                 ProjectXaml (every .axaml of the project, analyzed and cached), ResourceIndex/ClassIndex/CodeLiterals (names in .axaml and .cs)
  Analysis/      XamlAnalyzer (scopes, x:DataType, bindings), Members (Avalonia lookup rules), Features (hover/definition/completion),
                 Targets + Rename (references, rename), QuickFixes, Selectors (+ validation, hover), Outline, Navigation/Folding
  Lsp/           JSON-RPC + LSP server, Edits (WorkspaceEdit)
tests/           xunit tests
samples/SampleApp/  Avalonia 12 + CommunityToolkit fixture; Views/Broken.axaml lists expected diagnostics
```

## Known limits

- A `DataTemplate` with no `DataType` and nothing to infer it from is not reported. Avalonia does report it.
- Resource keys from compiled themes are found by scanning their IL, and their value type is guessed from the key's name (`…Brush`, `…Color`). Theme brushes whose names don't end in `Brush` (e.g. `ButtonBackground`) appear under `{DynamicResource |}`, not among the direct `Background="|"` suggestions.
- An unknown resource key (AXN0205) counts keys set from C# only when written as a string literal (`Resources["k"]`, `Resources.Add("k", …)`, `TryGetResource("k", …)`, `FindResource("k")`); a computed key is not seen, and a `{DynamicResource}` may legitimately be added at runtime, so it is only information.
- Rename and references for element names stay inside one file (name scopes), and for C# members they use the loaded Roslyn solution: unsaved `.cs` buffers are not seen, and files outside the project folder are never edited. References to a `[ObservableProperty]` member in C# are found through its source field.
- Style selectors: a type-less compound at the top of a style (`.accent:pointerover`) is not checked for pseudo-classes, and `#PART_x` is only checked when the control has a known theme (compiled or the project's own `ControlTheme`).
- "Used" classes come from `Classes="…"`, `Classes.x="…"` in `.axaml` and `Classes.Add/Set/…("x")` calls in `.cs`; a class built from a computed string counts as unused.
- Duplicate `x:Class` across files (AVLN2002) is not detected yet.
- Only the first target framework of a multi-targeted project is loaded.
- C# edits are picked up when the file is saved to disk; unsaved `.cs` buffers are not seen.

## License

MIT, see [LICENSE](LICENSE).

Avalonia is a registered trademark of AvaloniaUI OÜ. This project is not affiliated with or endorsed by AvaloniaUI OÜ.
All other trademarks are the property of their respective owners.
