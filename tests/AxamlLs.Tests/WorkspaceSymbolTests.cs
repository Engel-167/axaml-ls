using AxamlLs.Lsp.Handlers;
using AxamlLs.Workspace;

namespace AxamlLs.Tests;

/// <summary>Project-wide symbol search (features plan F3).</summary>
[Collection("sample")]
public class WorkspaceSymbolTests(SampleProjectFixture sample)
{
    private static readonly Dictionary<string, string> NoBuffers = [];

    [Fact]
    public void Every_kind_in_the_sample_is_found_with_its_span()
    {
        var symbols = sample.Indexes.Symbols(NoBuffers).ToList();
        string TextOf(XamlSymbol s) => File.ReadAllText(s.Path)[s.Start..s.End];

        var view = Assert.Single(symbols, s => s.Kind == XamlSymbolKind.View && s.Name == "MainView");
        Assert.Equal("SampleApp.Views", view.Container);
        Assert.Equal("MainView", TextOf(view));
        var key = Assert.Single(symbols, s => s.Kind == XamlSymbolKind.ResourceKey && s.Name == "Accent" && s.Path.EndsWith("MainView.axaml", StringComparison.Ordinal));
        Assert.Equal("SolidColorBrush", key.Container);
        Assert.Equal("Accent", TextOf(key));
        Assert.Contains(symbols, s => s.Kind == XamlSymbolKind.ElementName && s.Name == "Header" && s.Container == "TextBlock");
        Assert.Contains(symbols, s => s.Kind == XamlSymbolKind.Style && s.Name == "Button.Card");
        Assert.Contains(symbols, s => s.Kind == XamlSymbolKind.DataTemplate && s.Name == "vm:ItemViewModel");
        // Line and column agree with the offset.
        var text = File.ReadAllText(view.Path);
        Assert.Equal(text.Take(view.Start).Count(c => c == '\n'), view.Line);
    }

    [Fact]
    public void Control_themes_are_found_by_key_or_target_type()
    {
        var symbols = XamlSymbols.Scan("/p/Themes.axaml", """
            <Styles xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Styles.Resources>
                <ControlTheme x:Key="PillButton" TargetType="Button"/>
                <ControlTheme TargetType="TextBox"/>
              </Styles.Resources>
            </Styles>
            """);
        Assert.Contains(symbols, s => s is { Kind: XamlSymbolKind.ControlTheme, Name: "PillButton", Container: "Button" });
        Assert.Contains(symbols, s => s is { Kind: XamlSymbolKind.ControlTheme, Name: "TextBox" });
    }

    [Fact]
    public void Search_ranks_by_fuzzy_score_then_shorter_names_and_caps()
    {
        var symbols = sample.Indexes.Symbols(NoBuffers).ToList();
        Assert.Equal("Accent", XamlSymbols.Search(symbols, "accent", 10)[0].Name);
        Assert.Equal("MainView", XamlSymbols.Search(symbols, "mainv", 10)[0].Name);
        Assert.Empty(XamlSymbols.Search(symbols, "zzzqqq", 10));

        var many = Enumerable.Range(0, 500).Select(i => new XamlSymbol($"Key{i}", XamlSymbolKind.ResourceKey, "/p/a.axaml", i, i + 1, 0, i, 0, i + 1, null)).ToList();
        var top = XamlSymbols.Search(many, "key", 200);
        Assert.Equal(200, top.Count);
        Assert.Equal(top, XamlSymbols.Search(many.AsEnumerable().Reverse(), "key", 200)); // deterministic, whatever the input order
    }

    [Fact]
    public void Kinds_map_to_lsp_symbol_kinds()
    {
        Assert.Equal([5, 20, 8, 23, 11, 26], Enum.GetValues<XamlSymbolKind>().Select(WorkspaceSymbolHandler.Kind));
    }
}
