using AxamlLs.Analysis;
using AxamlLs.Workspace;

namespace AxamlLs.Tests;

[Collection("sample")]
public class SelectorValidationTests(SampleProjectFixture sample)
{
    private List<XamlDiagnostic> Check(string selector, out string text)
    {
        text = $"""
            <UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         xmlns:local="using:SampleApp.Controls">
              <UserControl.Styles>
                <Style Selector="{selector}"/>
              </UserControl.Styles>
            </UserControl>
            """;
        var analyzer = sample.Analyze(text);
        ProjectXaml.Wire(analyzer, Path.Combine(SampleProjectFixture.SampleDir, "Views", "Scratch.axaml"), sample.Host, () => new Dictionary<string, string>());
        return Selectors.Validate(analyzer).ToList();
    }

    private List<XamlDiagnostic> Check(string selector) => Check(selector, out _);

    [Fact]
    public void Unknown_type_is_an_error_on_the_type()
    {
        var d = Assert.Single(Check("Buton.accent", out var text));
        Assert.Equal(Codes.UnknownSelectorType, d.Code);
        Assert.Equal(Severity.Error, d.Severity);
        Assert.Equal("Buton", text[d.Start..d.End]);
        Assert.Single(Check("local|Missing"), x => x.Code == Codes.UnknownSelectorType);
        Assert.Single(Check("nope|Button"), x => x.Code == Codes.UnknownSelectorType && x.Message.Contains("Undeclared"));
    }

    [Fact]
    public void Known_types_are_fine()
    {
        Assert.Empty(Check("Button.accent"));
        Assert.Empty(Check("local|MyControl"));
        Assert.Empty(Check("Button, TextBlock.h1 > Border"));
    }

    [Theory]
    [InlineData("Button:pointerover")]
    [InlineData("Button:pressed")]
    [InlineData("Button:flyout-open")] // declared by the theme
    [InlineData("TextBox:error")]       // set by data validation, styled by the theme
    [InlineData("local|MyControl:custom")] // set in CodeStyles.cs
    [InlineData("Button:not(.a)")]
    [InlineData("ListBoxItem:nth-child(2n+1)")]
    public void Known_pseudo_classes_are_fine(string selector) => Assert.Empty(Check(selector));

    [Fact]
    public void Unknown_pseudo_class_is_a_warning_on_the_name()
    {
        var d = Assert.Single(Check("Button:pressd", out var text));
        Assert.Equal(Codes.UnknownPseudoClass, d.Code);
        Assert.Equal(Severity.Warning, d.Severity);
        Assert.Equal("pressd", text[d.Start..d.End]);
    }

    [Fact]
    public void Known_template_parts_are_fine() => Assert.Empty(Check("Button /template/ ContentPresenter#PART_ContentPresenter"));

    [Fact]
    public void Unknown_template_part_is_a_warning()
    {
        var d = Assert.Single(Check("Button /template/ ContentPresenter#PART_Nope", out var text));
        Assert.Equal(Codes.UnknownTemplatePart, d.Code);
        Assert.Equal("PART_Nope", text[d.Start..d.End]);
    }

    private List<XamlDiagnostic> CheckStyles(string styles, out string text)
    {
        text = $"""
            <Styles xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
            {styles}
            </Styles>
            """;
        var analyzer = sample.Analyze(text);
        // The document is open in the editor, so the project scan reads it from the open buffers.
        var path = Path.Combine(SampleProjectFixture.SampleDir, "Views", "Scratch.axaml");
        var buffers = new Dictionary<string, string> { [path] = text };
        ProjectXaml.Wire(analyzer, path, sample.Host, () => buffers);
        return Selectors.Validate(analyzer).Where(d => d.Code == Codes.UnknownTemplatePart).ToList();
    }

    private const string SegmentTemplate = """
          <Style Selector="RadioButton.segment">
            <Setter Property="Template">
              <ControlTemplate>
                <Border Name="Pill"><ContentPresenter Content="{TemplateBinding Content}" /></Border>
              </ControlTemplate>
            </Setter>
          </Style>
        """;

    [Fact]
    public void Parts_of_a_template_set_by_a_style_are_known()
    {
        Assert.Empty(CheckStyles(SegmentTemplate + """
              <Style Selector="RadioButton.segment:checked /template/ Border#Pill" />
              <Style Selector="RadioButton.segment:pointerover /template/ Border#RootBorder" />
            """, out _));
        var d = Assert.Single(CheckStyles(SegmentTemplate + """<Style Selector="RadioButton.segment /template/ Border#Pil" />""", out var text));
        Assert.Equal("Pil", text[d.Start..d.End]);
    }

    [Fact]
    public void Parts_of_a_template_in_a_nested_style_belong_to_the_outer_target()
    {
        Assert.Empty(CheckStyles("""
              <Style Selector="ToggleButton.chip">
                <Style Selector="^:checked">
                  <Setter Property="Template"><ControlTemplate><Border Name="Chip" /></ControlTemplate></Setter>
                </Style>
              </Style>
              <Style Selector="ToggleButton.chip /template/ Border#Chip" />
            """, out _));
    }

    [Theory]
    [InlineData("RadioButton.segment:checked", "RadioButton")]
    [InlineData("Window > Button", "Button")]
    [InlineData(":is(Window)", null)]
    [InlineData("Button:not(Border) /template/ ContentPresenter", "ContentPresenter")]
    [InlineData("Button /template/ #PART_X", null)]
    public void Target_type_is_the_last_compound(string selector, string? type) =>
        Assert.Equal(type, Assert.Single(Selectors.TargetTypes(selector)).TypeText);

    [Fact]
    public void Each_branch_has_its_target_and_nesting_is_noted()
    {
        var targets = Selectors.TargetTypes("Button.a, ^:pressed, TextBlock:is(.x, .y)");
        Assert.Equal([("Button", false), (null, true), ("TextBlock", false)], targets);
    }

    [Fact]
    public void A_control_without_a_theme_has_unknown_parts_so_nothing_is_said() =>
        Assert.Empty(Check("local|MyControl /template/ Border#PART_Anything"));

    [Fact]
    public void Unknown_property_in_a_property_selector_is_a_warning()
    {
        var d = Assert.Single(Check("Button[Nope=1]", out var text));
        Assert.Equal(Codes.UnknownSelectorProperty, d.Code);
        Assert.Equal("Nope", text[d.Start..d.End]);
        Assert.Empty(Check("Button[IsEnabled=True]"));
        Assert.Empty(Check("Button[(Grid.Row)=1]"));
    }

    [Theory]
    [InlineData("Button:not(.a", "Unclosed")]
    [InlineData("Button:not(.a))", "Unmatched")]
    [InlineData("Button >", "after '>'")]
    [InlineData("Button /template/", "after '/template/'")]
    public void Syntax_errors_are_reported(string selector, string message)
    {
        var d = Assert.Single(Check(selector), x => x.Code == Codes.SelectorSyntax);
        Assert.Equal(Severity.Error, d.Severity);
        Assert.Contains(message, d.Message);
    }
}
