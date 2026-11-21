using AxamlLs.Analysis.Refactorings;

namespace AxamlLs.Tests;

[Collection("sample")]
public class ExtractToStyleTests(SampleProjectFixture sample)
{
    private readonly RefactoringTestSupport _r = new(sample);
    private readonly ExtractToStyle _extract = new();

    private static string View(string body) => RefactoringTestSupport.Header + "\n    " + body + "\n</UserControl>";

    [Fact]
    public void Styled_values_move_bindings_names_and_attached_stay()
    {
        var after = _r.Apply(_extract, View("<Grid>\n        <But|ton x:Name=\"Save\" Grid.Row=\"1\" Background=\"Red\" Command=\"{Binding SaveCommand}\" Margin=\"4\"/>\n    </Grid>"));
        Assert.Contains("<Button Classes=\"newClass\" x:Name=\"Save\" Grid.Row=\"1\" Command=\"{Binding SaveCommand}\"/>", after);
        Assert.Contains(
            "<UserControl.Styles>\n        <Style Selector=\"Button.newClass\">\n            <Setter Property=\"Background\" Value=\"Red\" />\n            <Setter Property=\"Margin\" Value=\"4\" />\n        </Style>\n    </UserControl.Styles>",
            after);
    }

    [Fact]
    public void An_existing_class_list_gets_the_new_class()
    {
        var after = _r.Apply(_extract, View("<UserControl.Styles>\n        <Style Selector=\"Button.newClass\"/>\n    </UserControl.Styles>\n    <But|ton Classes=\"Card\" Width=\"80\"/>"));
        Assert.Contains("<Button Classes=\"Card newClass2\"/>", after);
        Assert.Contains("<Style Selector=\"Button.newClass2\">", after);
    }

    [Fact]
    public void Not_offered_without_styled_values_or_off_the_tag_name()
    {
        Assert.Null(_r.Title(_extract, View("<But|ton Command=\"{Binding SaveCommand}\" Grid.Row=\"1\"/>")));
        Assert.Null(_r.Title(_extract, View("<Button Width=\"8|0\"/>")));
    }
}
