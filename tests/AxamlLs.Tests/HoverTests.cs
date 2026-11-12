using AxamlLs.Analysis;
using AxamlLs.Workspace;

namespace AxamlLs.Tests;

[Collection("sample")]
public class HoverTests(SampleProjectFixture sample)
{
    private const string Header = """
        <UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     xmlns:vm="using:SampleApp.ViewModels" x:DataType="vm:MainViewModel">
        """;

    /// <summary>Hover at the '|' in the body, in an analyzer wired like the server's (project-wide resources, styles and files).</summary>
    private string? HoverAt(string body)
    {
        var offset = Header.Length + 1 + body.IndexOf('|');
        var text = Header + "\n" + body.Replace("|", "") + "\n</UserControl>";
        var analyzer = sample.Analyze(text);
        var xaml = new ProjectXaml();
        var empty = new Dictionary<string, string>();
        ProjectXaml.Wire(analyzer, Path.Combine(SampleProjectFixture.SampleDir, "Views", "Scratch.axaml"), sample.Host, () => empty, () => xaml.Files(sample.Host, empty));
        return Features.Hover(analyzer, offset)?.Markdown;
    }

    [Fact]
    public void Color_resource_has_a_swatch()
    {
        var hover = HoverAt("<Button Background=\"{StaticResource Acc|ent}\"/>");
        Assert.Contains("![](data:image/svg+xml;base64,", hover);
        Assert.Contains("#FF6600", hover);
        Assert.Contains("MainView.axaml", hover);
    }

    [Fact]
    public void The_x_key_of_a_color_resource_has_the_same_hover()
    {
        var hover = HoverAt("<SolidColorBrush x:Key=\"Acc|ent\" Color=\"#FF6600\"/>");
        Assert.Contains("data:image/svg+xml", hover);
    }

    [Fact]
    public void Theme_key_names_its_assembly()
    {
        var key = sample.Project.LibraryResources.First(r => r.Key.StartsWith("SystemAccentColor", StringComparison.Ordinal));
        var hover = HoverAt($"<Button Background=\"{{StaticResource {key.Key}|}}\"/>");
        Assert.Contains("compiled theme/library", hover);
        Assert.Contains(key.Assembly!, hover);
    }

    [Fact]
    public void Selector_type_shows_the_class()
    {
        var hover = HoverAt("<Style Selector=\"Bu|tton.Card\"/>");
        Assert.Contains("class Avalonia.Controls.Button", hover);
    }

    [Fact]
    public void Selector_class_says_where_it_is_set()
    {
        var hover = HoverAt("<Style Selector=\"Button.Ca|rd\"/>");
        Assert.Contains("set on 4 elements", hover);
        Assert.Contains("`Second.axaml` line", hover);
        Assert.Contains("`CodeStyles.cs` line", hover);
    }

    [Fact]
    public void Selector_pseudo_class_says_what_it_means_and_who_declares_it()
    {
        var hover = HoverAt("<Style Selector=\"Button:pointer|over\"/>");
        Assert.Contains("the pointer is over the control", hover);
        Assert.Contains("Declared by", hover);
        Assert.Contains("Set by code in this project", HoverAt("<Style Selector=\"Button:cust|om\"/>"));
    }

    [Fact]
    public void Template_parts_and_nesting_are_explained()
    {
        Assert.Contains("inside the template of the control before it", HoverAt("<Style Selector=\"Button /temp|late/ Border\"/>"));
        var part = HoverAt("<Style Selector=\"Button /template/ ContentPresenter#PART_Content|Presenter\"/>");
        Assert.Contains("ContentPresenter", part);
        Assert.Contains("template of `Button`", part);
        var nested = HoverAt("<Style Selector=\"Button\"><Style Selector=\"|^:pressed\"/></Style>");
        Assert.Contains("enclosing style: `Button`", nested);
    }

    [Fact]
    public void Compact_values_become_a_table()
    {
        Assert.Contains("Left 7 · Top 2 · Right 7 · Bottom 2", HoverAt("<Border Padding=\"|7 2\"/>"));
        Assert.Contains("Left 5 · Top 5 · Right 5 · Bottom 5", HoverAt("<Border Padding=\"|5\"/>"));
        Assert.Contains("TopLeft 9 · TopRight 9 · BottomRight 4 · BottomLeft 4", HoverAt("<Border CornerRadius=\"9| 4\"/>"));
        Assert.Contains("x 1 · y 2 · blur 4 · spread 0 · color #66000000", HoverAt("<Border BoxShadow=\"1 2 |4 0 #66000000\"/>"));
        Assert.Null(HoverAt("<Border Padding=\"|1 2 3\"/>"));
    }

    [Fact]
    public void Color_literal_has_a_swatch_and_other_notations()
    {
        var hover = HoverAt("<Border Background=\"#FF6|600\"/>");
        Assert.Contains("data:image/svg+xml", hover);
        Assert.Contains("`#FF6600`", hover);
        Assert.Contains("`rgb(", hover);
    }

    [Fact]
    public void Symbol_hover_still_works() =>
        Assert.Contains("string MainViewModel.Title", HoverAt("<TextBlock Text=\"{Binding Ti|tle}\"/>"));
}
