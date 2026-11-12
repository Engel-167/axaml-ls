using AxamlLs.Analysis;
using AxamlLs.Text;

namespace AxamlLs.Tests;

[Collection("sample")]
public class NavigationTests(SampleProjectFixture sample)
{
    private const string Folds = """
        <UserControl xmlns="https://github.com/avaloniaui"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <!-- #region Buttons -->
          <StackPanel>
            <Button/>
            <Button/>
          </StackPanel>
          <!-- #endregion -->
          <!--
            a long
            comment
          -->
          <Border></Border>
          <Grid>
          </Grid>
        </UserControl>
        """;

    [Fact]
    public void Folding_covers_elements_start_tags_comments_and_regions()
    {
        var ranges = Folding.Compute(sample.Analyze(Folds), new LineIndex(Folds));
        Assert.Equal(
            [
                new FoldRange(0, 1, null),       // the root's start tag spans two lines
                new FoldRange(1, 14, null),      // the root's content
                new FoldRange(2, 7, "region"),
                new FoldRange(3, 5, null),       // StackPanel's content
                new FoldRange(8, 11, "comment"),
            ],
            ranges);
    }

    [Fact]
    public void Multi_line_self_closing_tags_fold_to_the_closing_bracket()
    {
        const string text = "<UserControl xmlns=\"https://github.com/avaloniaui\">\n<Button\n  Content=\"a\"\n  Tag=\"b\"/>\n</UserControl>";
        Assert.Contains(new FoldRange(1, 3, null), Folding.Compute(sample.Analyze(text), new LineIndex(text)));
    }

    [Fact]
    public void Type_definition_from_a_plain_element_goes_to_the_view_model()
    {
        var text = File.ReadAllText(Path.Combine(SampleProjectFixture.SampleDir, "Views", "MainView.axaml"));
        var analyzer = sample.Analyze(text);
        var offset = text.IndexOf("<TextBlock x:Name=\"Header\"", StringComparison.Ordinal) + 3;
        var target = Assert.Single(Navigation.TypeDefinition(analyzer, offset));
        Assert.EndsWith("MainViewModel.cs", target.FilePath);
        Assert.Equal("public partial class MainViewModel", ReadLine(target).Trim().Split(" : ")[0]);
    }

    [Fact]
    public void Type_definition_from_a_binding_goes_to_the_type_of_the_value()
    {
        var text = File.ReadAllText(Path.Combine(SampleProjectFixture.SampleDir, "Views", "MainView.axaml"));
        var analyzer = sample.Analyze(text);
        // Selected is an ItemViewModel?
        var target = Assert.Single(Navigation.TypeDefinition(analyzer, text.IndexOf("Selected.Name", StringComparison.Ordinal) + 2));
        Assert.Contains("class ItemViewModel", ReadLine(target));
    }

    [Fact]
    public void Implementation_goes_to_the_code_behind()
    {
        var text = File.ReadAllText(Path.Combine(SampleProjectFixture.SampleDir, "Views", "MainView.axaml"));
        var analyzer = sample.Analyze(text);
        var target = Assert.Single(Navigation.Implementation(analyzer, text.IndexOf("<StackPanel", StringComparison.Ordinal) + 3));
        Assert.EndsWith("MainView.axaml.cs", target.FilePath);
        Assert.Contains("class MainView", ReadLine(target));
    }

    [Fact]
    public void Implementation_on_an_event_handler_goes_to_the_method()
    {
        var text = File.ReadAllText(Path.Combine(SampleProjectFixture.SampleDir, "Views", "MainView.axaml"));
        var analyzer = sample.Analyze(text);
        var target = Assert.Single(Navigation.Implementation(analyzer, text.IndexOf("Click=\"OnClick\"", StringComparison.Ordinal) + 9));
        Assert.EndsWith("MainView.axaml.cs", target.FilePath);
        Assert.Contains("void OnClick", ReadLine(target));
    }

    [Fact]
    public void The_lens_targets_are_the_view_model_and_the_code_behind()
    {
        var analyzer = sample.AnalyzeFile("Views/MainView.axaml");
        Assert.EndsWith("MainViewModel.cs", Navigation.ViewModel(analyzer).First().FilePath);
        Assert.EndsWith("MainView.axaml.cs", Navigation.CodeBehind(analyzer).First().FilePath);
    }

    private static string ReadLine(DefinitionTarget t) => File.ReadAllLines(t.FilePath!)[t.StartLine];
}
