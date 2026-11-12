using AxamlLs.Analysis;

namespace AxamlLs.Tests;

[Collection("sample")]
public class CompletionTests(SampleProjectFixture sample)
{
    private const string Header = """
        <UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     xmlns:vm="using:SampleApp.ViewModels" x:DataType="vm:MainViewModel">
        """;

    private List<Features.Completion> CompleteAt(string body)
    {
        var text = Header + body.Replace("|", "") + "\n</UserControl>";
        var offset = Header.Length + body.IndexOf('|');
        return Features.Complete(sample.Analyze(text), offset);
    }

    [Fact]
    public void Binding_members_of_the_data_type()
    {
        var labels = CompleteAt("<TextBlock Text=\"{Binding |}\"/>").Select(c => c.Label).ToList();
        Assert.Contains("Title", labels);
        Assert.Contains("SaveCommand", labels);
        Assert.Contains("Items", labels);
        Assert.Contains("$parent", labels);
    }

    [Fact]
    public void Binding_members_after_a_dot()
    {
        var labels = CompleteAt("<TextBlock Text=\"{Binding Selected.N|}\"/>").Select(c => c.Label).ToList();
        Assert.Contains("Name", labels);
        Assert.Contains("Price", labels);
        Assert.DoesNotContain("Title", labels);
    }

    [Fact]
    public void Attribute_names_of_the_element()
    {
        var items = CompleteAt("<TextBlock |/>");
        Assert.Contains(items, c => c.Label == "Text" && c.IsSnippet);
        Assert.Contains(items, c => c.Label == "PointerPressed");
    }

    [Fact]
    public void Enum_values()
    {
        var labels = CompleteAt("<TextBlock TextWrapping=\"|\"/>").Select(c => c.Label).ToList();
        Assert.Contains("Wrap", labels);
    }

    [Fact]
    public void Element_names()
    {
        var labels = CompleteAt("<StackPanel><Butt|</StackPanel>").Select(c => c.Label).ToList();
        Assert.Contains("Button", labels);
        Assert.Contains("StackPanel.Children", labels);
    }

    [Fact]
    public void DataType_values()
    {
        var labels = CompleteAt("<StackPanel x:DataType=\"vm:|\"/>").Select(c => c.Label).ToList();
        Assert.Contains("vm:ItemViewModel", labels);
    }
}

[Collection("sample")]
public class ResourceAndSnippetTests(SampleProjectFixture sample)
{
    private const string Header = """
        <UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     xmlns:vm="using:SampleApp.ViewModels" x:DataType="vm:MainViewModel">
        """;

    private (XamlAnalyzer Analyzer, int Offset) At(string body, IReadOnlyList<AxamlLs.Workspace.ResourceEntry>? resources = null)
    {
        var text = Header + body.Replace("|", "") + "\n</UserControl>";
        var analyzer = sample.Analyze(text);
        if (resources != null) analyzer.ResourceProvider = () => resources;
        return (analyzer, Header.Length + body.IndexOf('|'));
    }

    private static readonly AxamlLs.Workspace.ResourceEntry[] ProjectResources =
    [
        new("AccentBrush", "SolidColorBrush", "/proj/GlobalStyles.axaml", 4, 30, "Color=\"#FF6600\""),
        new("FontSize.Body", "x:Double", "/proj/GlobalResources.axaml", 7, 22, "14"),
    ];

    [Theory]
    [InlineData("<TextBlock Foreground=\"{StaticResource |}\"/>")]
    [InlineData("<TextBlock Foreground=\"{StaticResource Acc|}\"/>")]
    [InlineData("<TextBlock FontSize=\"{DynamicResource |}\"/>")]
    [InlineData("<TextBlock Text=\"{Binding Title, Converter={StaticResource |}}\"/>")]
    public void Resource_keys_come_from_the_whole_project(string body)
    {
        var (analyzer, offset) = At(body, ProjectResources);
        var labels = Features.Complete(analyzer, offset).Select(c => c.Label).ToList();
        Assert.Contains("AccentBrush", labels);
        Assert.Contains("FontSize.Body", labels);
    }

    [Fact]
    public void Resource_keys_in_the_same_document_without_a_provider()
    {
        var (analyzer, offset) = At("<UserControl.Resources><SolidColorBrush x:Key=\"Local\" Color=\"Red\"/></UserControl.Resources><TextBlock Foreground=\"{StaticResource |}\"/>");
        Assert.Contains(Features.Complete(analyzer, offset), c => c.Label == "Local");
    }

    [Fact]
    public void Resource_key_definition_and_hover()
    {
        var (analyzer, offset) = At("<TextBlock Foreground=\"{StaticResource Accent|Brush}\"/>", ProjectResources);
        var def = Assert.Single(Features.Definition(analyzer, offset));
        Assert.Equal(("/proj/GlobalStyles.axaml", 4), (def.FilePath, def.StartLine));
        Assert.Contains("#FF6600", Features.Hover(analyzer, offset)!.Markdown);
    }

    [Fact]
    public void Resource_index_reads_keys_and_values()
    {
        var entries = AxamlLs.Workspace.ResourceIndex.Scan("f.axaml", """
            <ResourceDictionary xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <x:Double x:Key="FontSize.Body">14</x:Double>
              <SolidColorBrush x:Key="Accent" Color="#FF6600"/>
            </ResourceDictionary>
            """);
        Assert.Equal(["FontSize.Body", "Accent"], entries.Select(e => e.Key));
        Assert.Equal("14", entries[0].ValuePreview);
        Assert.Equal(1, entries[0].Line);
    }

    [Fact]
    public void New_element_gets_a_closing_tag()
    {
        var (analyzer, offset) = At("<StackPanel><Butt|\n</StackPanel>");
        var button = Features.Complete(analyzer, offset).Single(c => c.Label == "Button");
        Assert.True(button.IsSnippet);
        Assert.Equal("Button $1>\n</Button>", button.InsertText);
    }

    [Fact]
    public void Renaming_an_existing_tag_inserts_only_the_name()
    {
        var (analyzer, offset) = At("<StackPanel><Butt| Content=\"x\"/></StackPanel>");
        var button = Features.Complete(analyzer, offset).Single(c => c.Label == "Button");
        Assert.False(button.IsSnippet);
        Assert.Equal("Button", button.InsertText);
    }
}

[Collection("sample")]
public class ThemeResourceTests(SampleProjectFixture sample)
{
    private const string Header = """
        <UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     xmlns:vm="using:SampleApp.ViewModels" x:DataType="vm:MainViewModel">
        """;

    private List<Features.Completion> CompleteAt(string body)
    {
        var text = Header + body.Replace("|", "") + "\n</UserControl>";
        var analyzer = sample.Analyze(text);
        analyzer.ResourceProvider = () => [new("AccentBrush", "SolidColorBrush", "/p/Styles.axaml", 1, 1, null), new("Body", "FontFamily", "/p/Styles.axaml", 2, 1, null)];
        return Features.Complete(analyzer, Header.Length + body.IndexOf('|'));
    }

    [Fact]
    public void Fluent_theme_keys_are_read_from_the_compiled_assembly()
    {
        var keys = sample.Project.LibraryResources.Select(r => r.Key).ToHashSet();
        Assert.True(keys.Count > 300, $"only {keys.Count} keys");
        Assert.Contains("SystemAccentColor", keys);
        Assert.Contains("SystemControlBackgroundAccentBrush", keys);
        Assert.Contains("ButtonBackground", keys);
        Assert.DoesNotContain(keys, k => k.Contains(' '));
    }

    [Fact]
    public void Plain_value_offers_literals_and_only_openers_for_resources()
    {
        var items = CompleteAt("<Button Background=\"|\"/>");
        Assert.Contains(items, c => c.Label == "Red");
        Assert.DoesNotContain(items, c => c.Label is "AccentBrush" or "SystemControlBackgroundAccentBrush");
        var opener = items.Single(c => c.Label == "{DynamicResource …}");
        Assert.Equal("{DynamicResource $1}$0", opener.InsertText);
        Assert.True(items.Where(c => !c.Label.StartsWith('{')).All(c => string.CompareOrdinal(c.SortText ?? c.Label, opener.SortText) < 0));
    }

    [Fact]
    public void Resource_keys_that_fit_the_property_rank_first()
    {
        var items = CompleteAt("<Button Background=\"{DynamicResource |}\"/>");
        var brush = items.Single(c => c.Label == "SystemControlBackgroundAccentBrush");
        var color = items.Single(c => c.Label == "SystemAccentColor");
        Assert.True(string.CompareOrdinal(brush.SortText, color.SortText) < 0);
        Assert.Contains(items, c => c.Label == "Body"); // everything is still offered
    }

    [Fact]
    public void Setter_value_uses_the_styled_controls_property()
    {
        var labels = CompleteAt("<UserControl.Styles><Style Selector=\"Button.Card:pointerover\"><Setter Property=\"Background\" Value=\"|\"/></Style></UserControl.Styles>")
            .Select(c => c.Label).ToList();
        Assert.Contains("Red", labels);
        var ranked = CompleteAt("<UserControl.Styles><Style Selector=\"Button\"><Setter Property=\"Background\" Value=\"{DynamicResource |}\"/></Style></UserControl.Styles>");
        Assert.True(string.CompareOrdinal(ranked.Single(c => c.Label == "SystemControlBackgroundAccentBrush").SortText, ranked.Single(c => c.Label == "SystemAccentColor").SortText) < 0);
    }

    [Fact]
    public void Enum_and_font_family_values()
    {
        Assert.Contains(CompleteAt("<TextBlock FontWeight=\"|\"/>"), c => c.Label == "Bold");
        Assert.Contains(CompleteAt("<TextBlock FontFamily=\"{StaticResource |}\"/>"), c => c.Label == "Body");
    }
}

[Collection("sample")]
public class ColorAndOpenerTests(SampleProjectFixture sample)
{
    private const string Header = """
        <UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     xmlns:vm="using:SampleApp.ViewModels" x:DataType="vm:MainViewModel">
        """;

    private XamlAnalyzer Analyze(string body, out int offset)
    {
        offset = Header.Length + body.IndexOf('|');
        var analyzer = sample.Analyze(Header + body.Replace("|", "") + "\n</UserControl>");
        analyzer.ResourceProvider = () => [new("Accent", "SolidColorBrush", "/p/S.axaml", 1, 1, "Color=\"#FF6600\""), new("Body", "FontFamily", "/p/S.axaml", 2, 1, null)];
        return analyzer;
    }

    private List<Features.Completion> CompleteAt(string body) => Features.Complete(Analyze(body, out var o), o);

    [Fact]
    public void Named_colors_carry_their_value_for_a_swatch()
    {
        var red = CompleteAt("<Button Background=\"|\"/>").Single(c => c.Label == "Red");
        Assert.Equal(Features.ItemKind.Color, red.Kind);
        Assert.Equal("#FF0000", red.ColorHex);
        Assert.Equal("#00FFFFFF", CompleteAt("<Button Background=\"|\"/>").Single(c => c.Label == "Transparent").ColorHex);
    }

    [Fact]
    public void Color_resources_carry_their_value()
    {
        var accent = CompleteAt("<Button Background=\"{StaticResource |}\"/>").Single(c => c.Label == "Accent");
        Assert.Equal("#FF6600", accent.ColorHex);
    }

    [Fact]
    public void Openers_come_last_and_fit_the_property()
    {
        var items = CompleteAt("<Button Background=\"|\"/>");
        var openers = items.Where(c => c.Label.StartsWith('{')).Select(c => c.Label).ToList();
        Assert.Equal(["{Binding …}", "{DynamicResource …}", "{StaticResource …}", "{x:Null}"], openers);
        Assert.Equal("{Binding $1}$0", items.Single(c => c.Label == "{Binding …}").InsertText);
        var maxLiteral = items.Where(c => !c.Label.StartsWith('{')).Max(c => c.SortText ?? c.Label);
        Assert.All(items.Where(c => c.Label.StartsWith('{')), c => Assert.True(string.CompareOrdinal(c.SortText, maxLiteral) > 0));

        // A bool has no resources here and is a value type: only {Binding …}.
        Assert.Equal(["{Binding …}"], CompleteAt("<Button IsEnabled=\"|\"/>").Where(c => c.Label.StartsWith('{')).Select(c => c.Label));
    }

    [Fact]
    public void Template_binding_opener_inside_a_control_template()
    {
        var items = CompleteAt("<UserControl.Template><ControlTemplate TargetType=\"UserControl\"><Border Background=\"|\"/></ControlTemplate></UserControl.Template>");
        Assert.Contains(items, c => c.Label == "{TemplateBinding …}");
    }

    [Fact]
    public void Color_format_templates_follow_the_names_and_replace_the_whole_value()
    {
        var items = CompleteAt("<Button Background=\"#12|\"/>");
        var templates = items.Where(c => c.Kind == Features.ItemKind.Snippet && !c.Label.StartsWith('{')).ToList();
        Assert.Equal(["#RRGGBB", "#AARRGGBB", "#RGB", "#ARGB", "rgb(r,g,b)", "rgba(r,g,b,a)", "hsl(h,s%,l%)", "hsla(h,s%,l%,a)", "hsv(h,s%,v%)", "hsva(h,s%,v%,a)"],
            templates.Select(c => c.Label));
        Assert.Equal("hsl(${1:0},${2:0}%,${3:0}%)", templates.Single(c => c.Label == "hsl(h,s%,l%)").InsertText);
        var hsl = templates[6];
        Assert.Equal(3, hsl.ReplaceEnd - hsl.ReplaceStart); // all of "#12"
        var red = items.Single(c => c.Label == "Red");
        Assert.True(string.CompareOrdinal(red.SortText, hsl.SortText) < 0);
        Assert.All(items.Where(c => c.Label.StartsWith('{')), c => Assert.True(string.CompareOrdinal(c.SortText, hsl.SortText) > 0));
        // Not offered for non-color properties.
        Assert.DoesNotContain(CompleteAt("<Button Width=\"|\"/>"), c => c.Label == "#RRGGBB");
    }

    [Theory]
    [InlineData("rgb(255,0,0)", "#FF0000")]
    [InlineData("RGB(100%, 0%, 0%)", "#FF0000")]
    [InlineData("rgba(255,0,0,0.5)", "#80FF0000")]
    [InlineData("rgba(255,0,0,50%)", "#80FF0000")]
    [InlineData("rgba(255,0,0,128)", "#80FF0000")]
    [InlineData("hsl(120,100%,50%)", "#00FF00")]
    [InlineData("hsl(0,1,0.5)", "#FF0000")]
    [InlineData("hsla(240,100%,50%,0.5)", "#800000FF")]
    [InlineData("hsv(0,100%,100%)", "#FF0000")]
    [InlineData("hsva(60,100%,100%,1)", "#FFFF00")]
    [InlineData("#ABC", "#AABBCC")]
    public void Functional_color_syntaxes_parse(string text, string hex)
    {
        Assert.True(ColorSupport.TryParse(text, new Dictionary<string, uint>(), out var argb));
        Assert.Equal(hex, ColorSupport.Hex(argb));
    }

    [Theory]
    [InlineData("rgb(255 0 0)")]
    [InlineData("hwb(0,0%,0%)")]
    [InlineData("rgb(1,2)")]
    public void Unsupported_color_syntaxes_do_not_parse(string text) =>
        Assert.False(ColorSupport.TryParse(text, new Dictionary<string, uint>(), out _));

    [Fact]
    public void Color_picker_offers_hex_rgb_and_hsl()
    {
        Assert.Equal(["#FF0000", "rgb(255,0,0)", "hsl(0,100%,50%)"], ColorSupport.Presentations(0xFFFF0000));
        Assert.Equal(["#800000FF", "rgba(0,0,255,0.5)", "hsla(240,100%,50%,0.5)"], ColorSupport.Presentations(0x800000FF));
    }

    [Fact]
    public void Document_colors_only_where_a_color_is_meant()
    {
        var analyzer = Analyze("<StackPanel Background=\"#FF6600\"><TextBlock Text=\"Red\" Foreground=\"Red\"/><Border BorderBrush=\"#80000000\"/></StackPanel>|", out _);
        var colors = Features.DocumentColors(analyzer).Select(c => ColorSupport.Hex(c.Argb)).ToList();
        Assert.Equal(["#FF6600", "#FF0000", "#80000000"], colors);
    }

    [Theory]
    [InlineData("#F60", 0xFFFF6600u)]
    [InlineData("#8F60", 0x88FF6600u)]
    [InlineData("#ff6600", 0xFFFF6600u)]
    [InlineData("#80FF6600", 0x80FF6600u)]
    public void Hex_color_forms(string text, uint argb)
    {
        Assert.True(ColorSupport.TryParse(text, new Dictionary<string, uint>(), out var v));
        Assert.Equal(argb, v);
    }
}

[Collection("sample")]
public class SelectorTests(SampleProjectFixture sample)
{
    private const string Header = """
        <UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     xmlns:vm="using:SampleApp.ViewModels" xmlns:loc="using:SampleApp.Localization" x:DataType="vm:MainViewModel">
        """;

    private List<Features.Completion> CompleteAt(string body, params string[] usedClasses)
    {
        var offset = Header.Length + body.IndexOf('|');
        var analyzer = sample.Analyze(Header + body.Replace("|", "") + "\n</UserControl>");
        analyzer.StyleProvider = () =>
        {
            var styles = AxamlLs.Workspace.ClassIndex.ScanText("", analyzer.Result.Doc.Text);
            styles.UsedClasses.UnionWith(usedClasses);
            return styles;
        };
        return Features.Complete(analyzer, offset);
    }

    private static string Styled(string selector) => $"<UserControl.Styles><Style Selector=\"{selector}\"><Setter Property=\"Opacity\" Value=\"1\"/></Style></UserControl.Styles>";

    [Fact]
    public void Tokenizes_a_selector()
    {
        var tokens = Selectors.Tokenize("Button.Expand:pointerover /template/ ContentPresenter#PART_ContentPresenter, ^:not(.x) > local|Foo:nth-child(2n+1)");
        Assert.Equal(
            ["Type:Button", "Class:.Expand", "PseudoClass::pointerover", "Template:/template/", "Type:ContentPresenter", "Name:#PART_ContentPresenter",
             "Nesting:^", "PseudoClass::not", "Class:.x", "Type:local|Foo", "PseudoClass::nth-child"],
            tokens.Select(t => $"{t.Kind}:{t.Text}"));
    }

    [Fact]
    public void Selector_types_not_markup_openers()
    {
        var items = CompleteAt(Styled("Spl|"));
        Assert.Contains(items, c => c.Label == "SplitView");
        Assert.Contains(items, c => c.Label == "Button");
        Assert.DoesNotContain(items, c => c.Label.StartsWith('{'));
        Assert.DoesNotContain(items, c => c.Label == "MainViewModel"); // not a control
    }

    [Fact]
    public void Template_keyword_after_a_type()
    {
        Assert.Contains(CompleteAt(Styled("SplitView |")), c => c.Label == "/template/");
        var slash = CompleteAt(Styled("SplitView /tem|")).Single(c => c.Label == "/template/");
        Assert.Equal("/template/ ", slash.InsertText);
    }

    [Fact]
    public void Pseudo_classes_of_the_type()
    {
        var items = CompleteAt(Styled("Button.Expand:|"));
        Assert.Contains(items, c => c.Label == "pointerover");
        Assert.Contains(items, c => c.Label == "pressed");
        Assert.Contains(items, c => c.Label == "not(…)");
        Assert.DoesNotContain(CompleteAt(Styled("TextBlock:|")), c => c.Label == "pressed");
    }

    [Fact]
    public void Classes_and_template_parts()
    {
        Assert.Contains(CompleteAt(Styled("Button.|"), "Expand", "Card"), c => c.Label == "Card");
        var parts = CompleteAt(Styled("Button /template/ ContentPresenter#|"));
        Assert.Contains(parts, c => c.Label == "PART_ContentPresenter");
    }

    [Fact]
    public void Unused_classes_are_hinted()
    {
        var analyzer = sample.Analyze(Header + Styled("Button.Used, Button.Unused:pointerover") + "\n</UserControl>");
        var hints = Selectors.UnusedClasses(analyzer.Result.Doc, new HashSet<string> { "Used" }).ToList();
        var hint = Assert.Single(hints);
        Assert.Equal(Codes.UnusedClass, hint.Code);
        Assert.Equal("Unused", analyzer.Result.Doc.Text[hint.Start..hint.End]);
    }

    [Fact]
    public void Class_index_reads_axaml_and_code()
    {
        Assert.Equal(["a", "b", "c"], AxamlLs.Workspace.ClassIndex.Scan("x.axaml", "<Border xmlns=\"https://github.com/avaloniaui\" Classes=\"a b\" Classes.c=\"{Binding X}\"/>").Order());
        Assert.Contains("Glow", AxamlLs.Workspace.ClassIndex.Scan("x.cs", "button.Classes.Add(\"Glow\");"));
    }

    [Fact]
    public void Parent_binding_offers_control_types()
    {
        var items = CompleteAt("<TextBlock Width=\"{Binding $parent[Win|].Width}\"/>");
        var window = items.Single(c => c.Label == "Window");
        Assert.DoesNotContain(items, c => c.Label == "Width");
    }

    [Fact]
    public void Custom_markup_extensions_and_their_properties()
    {
        var names = CompleteAt("<TextBlock Text=\"{loc|}\"/>");
        Assert.Contains(names, c => c.Label == "loc:Localize");
        var keys = CompleteAt("<TextBlock Text=\"{loc:Localize hello, |}\"/>");
        Assert.Contains(keys, c => c.Label == "Fallback" && c.InsertText == "Fallback=");
    }

    [Fact]
    public void Custom_markup_extension_is_not_an_error()
    {
        var analyzer = sample.Analyze(Header + "<TextBlock Text=\"{loc:Localize hello}\"/>\n</UserControl>");
        Assert.Empty(analyzer.Result.Diagnostics);
    }

    [Fact]
    public void Template_parts_come_from_the_controls_own_theme()
    {
        var button = CompleteAt(Styled("Button /template/ ContentPresenter#|"));
        Assert.Equal(["PART_ContentPresenter"], button.Select(c => c.Label));
        Assert.Contains("ContentPresenter in Button's template", button[0].Detail);

        var split = CompleteAt(Styled("SplitView /template/ #|")).Select(c => c.Label).ToList();
        Assert.Contains("PART_PaneRoot", split);
        Assert.DoesNotContain("PART_ContentPresenter2", split);
        Assert.DoesNotContain("PART_PeriodHost", split); // a DatePicker part

        // Only parts of the type written before '#'.
        var panels = CompleteAt(Styled("SplitView /template/ Panel#|")).Select(c => c.Label).ToList();
        Assert.Contains("PART_PaneRoot", panels);
        Assert.DoesNotContain(CompleteAt(Styled("SplitView /template/ Border#|")), c => c.Label == "PART_PaneRoot" && !c.Detail!.Contains("Border"));
    }

    [Fact]
    public void Template_parts_of_the_projects_own_control_theme()
    {
        var items = CompleteAt("""
            <UserControl.Resources><ControlTheme x:Key="T" TargetType="TextBox"><Setter Property="Template"><ControlTemplate>
              <Border Name="PART_Frame"><TextPresenter x:Name="PART_TextPresenter"/></Border>
            </ControlTemplate></Setter></ControlTheme></UserControl.Resources>
            """ + Styled("TextBox /template/ Border#|"));
        Assert.Contains(items, c => c.Label == "PART_Frame");
        Assert.DoesNotContain(items, c => c.Label == "PART_TextPresenter"); // not a Border
    }

    [Fact]
    public void Classes_value_offers_classes_styled_for_the_control()
    {
        var items = CompleteAt(Styled("Button.Expand") + "<Button Classes=\"Card |\"/>", "Card", "Glow");
        var expand = items.Single(c => c.Label == "Expand");
        Assert.Equal("Styled for Button".ToLowerInvariant(), expand.Detail!.ToLowerInvariant());
        Assert.Contains(items, c => c.Label == "accent"); // Fluent's Button theme styles ^.accent
        Assert.Contains(items, c => c.Label == "Glow");
        Assert.DoesNotContain(items, c => c.Label == "Card"); // already there
        Assert.True(string.CompareOrdinal(expand.SortText, items.Single(c => c.Label == "Glow").SortText) < 0);
        Assert.DoesNotContain(items, c => c.Label.StartsWith('{'));
    }

    [Fact]
    public void Conditional_classes_attribute_names()
    {
        var items = CompleteAt(Styled("Border.Highlight") + "<Border Classes.|/>");
        var item = items.Single(c => c.Label == "Classes.Highlight");
        Assert.Equal("Classes.Highlight=\"{Binding $1}\"$0", item.InsertText);
        Assert.DoesNotContain(items, c => c.Label == "ContextMenu");
    }

    [Theory]
    [InlineData("<Button A=\"1\"/|></Button>", "<Button A=\"1\"/>")]
    [InlineData("<Button/|>\n    </Button>\n<X/>", "<Button/>\n<X/>")]
    [InlineData("<local:Foo Sel=\"a > b\"/|></local:Foo>", "<local:Foo Sel=\"a > b\"/>")]
    [InlineData("<Button/|><TextBlock/></Button>", null)]
    [InlineData("<Button/|></Border>", null)]
    public void Typing_slash_removes_the_empty_end_tag(string input, string? expected)
    {
        var offset = input.IndexOf('|');
        var text = input.Replace("|", "");
        var edit = Features.SelfClosingEdit(text, offset);
        Assert.Equal(expected, edit is { } e ? text[..e.Start] + text[e.End..] : null);
    }
}

[Collection("sample")]
public class SetterAndAssetTests(SampleProjectFixture sample)
{
    private const string Header = """
        <UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     xmlns:vm="using:SampleApp.ViewModels" x:DataType="vm:MainViewModel">
        """;

    private XamlAnalyzer Analyze(string text)
    {
        var analyzer = sample.Analyze(text);
        analyzer.ProjectDirectory = SampleProjectFixture.SampleDir;
        analyzer.DocumentPath = Path.Combine(SampleProjectFixture.SampleDir, "Views", "Test.axaml");
        return analyzer;
    }

    private List<Features.Completion> CompleteAt(string body)
    {
        var offset = Header.Length + body.IndexOf('|');
        return Features.Complete(Analyze(Header + body.Replace("|", "") + "\n</UserControl>"), offset);
    }

    [Fact]
    public void Setter_property_names_of_the_styled_type()
    {
        var items = CompleteAt("<UserControl.Styles><Style Selector=\"Button.Actions\"><Setter Property=\"Fo|\" Value=\"1\"/></Style></UserControl.Styles>");
        var fontSize = items.Single(c => c.Label == "FontSize");
        Assert.Equal("double", fontSize.Detail);
        Assert.Contains(items, c => c.Label == "Background");
        Assert.Contains(items, c => c.Label == "Command"); // Button's own
        Assert.DoesNotContain(items, c => c.Label.StartsWith('{'));
        Assert.DoesNotContain(items, c => c.Label == "Bounds"); // a direct property: styles cannot set it

        var attached = CompleteAt("<UserControl.Styles><Style Selector=\"Button\"><Setter Property=\"Grid.|\" Value=\"1\"/></Style></UserControl.Styles>");
        Assert.Contains(attached, c => c.Label == "Grid.Row");
        Assert.Contains(attached, c => c.Label == "Grid.ColumnSpan");
    }

    [Fact]
    public void Asset_paths_walk_folders()
    {
        var root = CompleteAt("<Image Source=\"|\"/>");
        // Folders insert without the slash: typing "/" (a trigger character) reopens the menu in Zed.
        Assert.Contains(root, c => c.Label == "/Assets/" && c.Kind == Features.ItemKind.Folder && c.InsertText == "/Assets");
        Assert.DoesNotContain(root, c => c.Label.Contains("bin") || c.Label.Contains("obj/"));

        var assets = CompleteAt("<Image Source=\"/Assets/|\"/>");
        Assert.Contains(assets, c => c.Label == "Icons/");
        Assert.Contains(assets, c => c.Label == "logo.png" && c.Kind == Features.ItemKind.File);

        // Images only, no svg for a plain Image; any file for a Uri.
        Assert.DoesNotContain(CompleteAt("<Image Source=\"/Assets/Icons/|\"/>"), c => c.Label.EndsWith(".svg"));
        Assert.Contains(CompleteAt("<StyleInclude Source=\"../|\"/>"), c => c.Label == "MainView.axaml" || c.Label == "Views/");
    }

    [Fact]
    public void Typing_a_name_finds_files_deeper_down()
    {
        var items = CompleteAt("<Image Source=\"/Assets/hou|\"/>");
        Assert.Contains(items, c => c.Label == "Icons/house.png");
        Assert.DoesNotContain(items, c => c.Label == "Icons/house_outline.svg"); // not an image an Image can show
        Assert.DoesNotContain(items, c => c.Label == "Icons/home.svg");
    }

    [Fact]
    public void Avares_scheme_and_missing_files()
    {
        Assert.Contains(CompleteAt("<Image Source=\"ava|\"/>"), c => c.Label == "avares://SampleApp/");
        Assert.Contains(CompleteAt("<Image Source=\"avares://SampleApp/Assets/|\"/>"), c => c.Label == "logo.png");

        var analyzer = Analyze(Header + """
            <StackPanel>
              <Image Source="/Assets/logo.png"/>
              <Image Source="avares://SampleApp/Assets/missing.png"/>
              <Image Source="../Assets/logo.png"/>
              <Image Source="avares://Other.Lib/x.png"/>
            </StackPanel>
            </UserControl>
            """);
        var missing = Assert.Single(Features.AssetDiagnostics(analyzer));
        Assert.Equal(Codes.MissingAsset, missing.Code);
        Assert.Contains("missing.png", missing.Message);

        var at = analyzer.Result.Doc.Text.IndexOf("/Assets/logo.png", StringComparison.Ordinal) + 3;
        var target = Assert.Single(Features.Definition(analyzer, at));
        Assert.EndsWith("Assets/logo.png", target.FilePath);
    }

    [Fact]
    public void One_letter_finds_whole_paths_below()
    {
        var items = CompleteAt("<Image Source=\"/Assets/h|\"/>");
        var house = items.Single(c => c.Label == "Icons/house.png");
        Assert.Equal("Icons/house.png", house.InsertText);
        Assert.NotNull(house.PreviewPath);
    }

    [Fact]
    public void Previews_are_data_uris_with_a_visible_svg()
    {
        var png = Assets.PreviewMarkdown(Path.Combine(SampleProjectFixture.SampleDir, "Assets", "logo.png"), SampleProjectFixture.SampleDir)!;
        Assert.StartsWith("![logo.png](data:image/png;base64,iVBOR", png);
        Assert.Contains("`/Assets/logo.png`", png);

        var svgMarkdown = Assets.PreviewMarkdown(Path.Combine(SampleProjectFixture.SampleDir, "Assets", "Icons", "home.svg"), null)!;
        var base64 = svgMarkdown[(svgMarkdown.IndexOf("base64,", StringComparison.Ordinal) + 7)..svgMarkdown.IndexOf(')')];
        var svg = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        Assert.Contains("fill=\"#9a9a9a\"", svg);
        Assert.Contains("width=\"96\"", svg);

        var currentColor = Path.Combine(Path.GetTempPath(), "axaml-ls-currentcolor.svg");
        File.WriteAllText(currentColor, "<svg fill=\"currentColor\" width=\"20\" height=\"20\"><path d=\"M0 0h20v20z\"/></svg>");
        var md = Assets.PreviewMarkdown(currentColor, null)!;
        var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(md[(md.IndexOf("base64,", StringComparison.Ordinal) + 7)..md.IndexOf(')')]));
        Assert.DoesNotContain("currentColor", decoded);
        Assert.Contains("viewBox=\"0 0 20 20\"", decoded);
        File.Delete(currentColor);
    }
}

[Collection("sample")]
public class InlayHintTests(SampleProjectFixture sample)
{
    private const string Header = """
        <UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     xmlns:vm="using:SampleApp.ViewModels" x:DataType="vm:MainViewModel">
        """;

    /// <summary>The value with each hint written in brackets before the piece it names.</summary>
    private string Hinted(string body, string attribute)
    {
        var text = Header + body + "\n</UserControl>";
        var analyzer = sample.Analyze(text);
        var hints = Features.InlayHintsIn(analyzer, 0, text.Length);
        var attrStart = text.IndexOf(attribute + "=\"", StringComparison.Ordinal) + attribute.Length + 2;
        var attrEnd = text.IndexOf('"', attrStart);
        var result = new System.Text.StringBuilder();
        for (var i = attrStart; i < attrEnd; i++)
        {
            foreach (var h in hints.Where(h => h.Offset == i)) result.Append('[').Append(h.Label).Append(']');
            result.Append(text[i]);
        }
        return result.ToString();
    }

    [Theory]
    [InlineData("Padding", "7", "[all:]7")]
    [InlineData("Padding", "7 2", "[lr:]7 [tb:]2")]
    [InlineData("Margin", "0,4,8,0", "[l:]0,[t:]4,[r:]8,[b:]0")]
    [InlineData("Padding", "1 2 3", "1 2 3")] // invalid for Avalonia: no guessing
    [InlineData("CornerRadius", "9", "[all:]9")]
    [InlineData("CornerRadius", "9 4", "[top:]9 [bottom:]4")]
    [InlineData("CornerRadius", "1 2 3 4", "[tl:]1 [tr:]2 [br:]3 [bl:]4")]
    [InlineData("BoxShadow", "1 2 4 0 #66000000", "[x:]1 [y:]2 [blur:]4 [spread:]0 #66000000")]
    [InlineData("BoxShadow", "inset 1 2 Red, 0 0 10 rgb(0,0,0)", "inset [x:]1 [y:]2 Red, [x:]0 [y:]0 [blur:]10 rgb(0,0,0)")]
    public void Names_the_parts_of_compact_values(string property, string value, string expected) =>
        Assert.Equal(expected, Hinted($"<Border {property}=\"{value}\"/>", property));

    [Fact]
    public void In_setter_values_and_markup_extensions()
    {
        Assert.Equal("[lr:]4 [tb:]0", Hinted("<UserControl.Styles><Style Selector=\"Border\"><Setter Property=\"Padding\" Value=\"4 0\"/></Style></UserControl.Styles>", "Value"));
        Assert.Equal("{Binding [Path=][(MainViewModel).]Title, Mode=OneWay}", Hinted("<TextBlock Text=\"{Binding Title, Mode=OneWay}\"/>", "Text"));
        Assert.Equal("{StaticResource [ResourceKey=]Accent}", Hinted("<Border Background=\"{StaticResource Accent}\"/>", "Background"));
        Assert.Equal("{Binding [Path=]Title, Source={x:Static [Member=]vm:MainViewModel.Instance}}",
            Hinted("<TextBlock Text=\"{Binding Title, Source={x:Static vm:MainViewModel.Instance}}\"/>", "Text"));
        Assert.Equal("Hello 1 2", Hinted("<TextBlock Text=\"Hello 1 2\"/>", "Text")); // a string: nothing
    }

    [Fact]
    public void Bindings_show_the_type_their_path_starts_from()
    {
        Assert.Equal("{Binding [Path=][(MainViewModel).]Title}", Hinted("<TextBlock Text=\"{Binding Title}\"/>", "Text"));
        Assert.Equal("{Binding Path=[(MainViewModel).]Title}", Hinted("<TextBlock Text=\"{Binding Path=Title}\"/>", "Text"));
        Assert.Equal("{CompiledBinding [Path=][(MainViewModel).]Selected.Name}", Hinted("<TextBlock Text=\"{CompiledBinding Selected.Name}\"/>", "Text"));
        // The path starts from an item type inside a data template, and from the named element for ElementName.
        Assert.Equal("{Binding [Path=][(ItemViewModel).]Name}", Hinted(
            "<ItemsControl ItemsSource=\"{Binding Items}\"><ItemsControl.ItemTemplate><DataTemplate><TextBlock Text=\"{Binding Name}\"/></DataTemplate></ItemsControl.ItemTemplate></ItemsControl>", "Text"));
        Assert.Equal("{Binding [Path=][(TextBlock).]Text, ElementName=Header}", Hinted("<StackPanel><TextBlock x:Name=\"Header\"/><TextBlock Text=\"{Binding Text, ElementName=Header}\"/></StackPanel>", "Text"));
    }

    [Theory]
    [InlineData("{Binding $parent[UserControl].DataContext}")]
    [InlineData("{Binding $self.Text}")]
    [InlineData("{Binding #Header.Text}")]
    [InlineData("{Binding !IsBusy}")]
    [InlineData("{Binding (Grid.Row)}")]
    [InlineData("{Binding}")]
    [InlineData("{Binding Title, Source={x:Static vm:MainViewModel.Instance}}")]
    public void No_source_type_hint_when_the_path_names_its_own_start(string binding)
    {
        var text = Header + $"<StackPanel><TextBlock x:Name=\"Header\"/><TextBlock Text=\"{binding}\"/></StackPanel></UserControl>";
        Assert.DoesNotContain(Features.InlayHintsIn(sample.Analyze(text), 0, text.Length), h => h.Label.StartsWith('(') && h.Label.EndsWith(")."));
    }

    [Fact]
    public void The_source_type_hint_can_be_switched_off()
    {
        var text = Header + "<TextBlock Text=\"{Binding Title}\"/></UserControl>";
        var hints = Features.InlayHintsIn(sample.Analyze(text), 0, text.Length, new InlayHintOptions(BindingSource: false));
        Assert.Equal(["Path="], hints.Select(h => h.Label));
    }

    [Fact]
    public void Grid_definitions_are_numbered_and_the_index_shows_its_definition()
    {
        Assert.Equal("[0:]Auto,[1:]*,[2:]2*", Hinted("<Grid ColumnDefinitions=\"Auto,*,2*\"/>", "ColumnDefinitions"));
        Assert.Equal("[0:]Auto, [1:]*", Hinted("<Grid RowDefinitions=\"Auto, *\"/>", "RowDefinitions"));
        var text = Header + "<Grid ColumnDefinitions=\"Auto,*,2*\"><Button Grid.Column=\"2\"/><Button Grid.Column=\"7\"/></Grid></UserControl>";
        var hint = Assert.Single(Features.InlayHintsIn(sample.Analyze(text), 0, text.Length), h => h.Label.StartsWith('→'));
        Assert.Equal("→ 2*", hint.Label);
        Assert.Equal("Grid.Column=\"2\"", text[(text.LastIndexOf("Grid.Column=\"2\"", StringComparison.Ordinal))..hint.Offset]);
    }

    [Fact]
    public void Box_shadow_colors_get_swatches()
    {
        var text = Header + "<StackPanel><Border BoxShadow=\"1 2 4 0 #66000000\"/><Border BoxShadow=\"inset 0 0 Red, 0 0 10 2 rgb(0,0,255)\"/>"
                   + "<Border><Border.Styles><Style Selector=\"Border\"><Setter Property=\"BoxShadow\" Value=\"3 3 hsl(0,100%,50%)\"/></Style></Border.Styles></Border></StackPanel>\n</UserControl>";
        var colors = Features.DocumentColors(sample.Analyze(text));
        Assert.Equal(["#66000000", "#FF0000", "#0000FF", "#FF0000"], colors.Select(c => ColorSupport.Hex(c.Argb)));
        Assert.Equal("#66000000", text[colors[0].Start..colors[0].End]);
        Assert.Equal("rgb(0,0,255)", text[colors[2].Start..colors[2].End]);
    }
}
