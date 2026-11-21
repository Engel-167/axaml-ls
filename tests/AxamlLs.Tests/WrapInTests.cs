using AxamlLs.Analysis.Refactorings;

namespace AxamlLs.Tests;

[Collection("sample")]
public class WrapInTests(SampleProjectFixture sample)
{
    private readonly RefactoringTestSupport _r = new(sample);

    private static string View(string body) => RefactoringTestSupport.Header + "\n    " + body + "\n</UserControl>";

    [Fact]
    public void Wraps_and_indents_the_element_and_moves_attached_layout()
    {
        var after = _r.Apply(new WrapIn("Border"), View("<Grid>\n        <Text|Block Grid.Row=\"1\" Text=\"a\">\n            <ToolTip.Tip>hi</ToolTip.Tip>\n        </TextBlock>\n    </Grid>"));
        Assert.Contains(
            "    <Grid>\n        <Border Grid.Row=\"1\">\n            <TextBlock Text=\"a\">\n                <ToolTip.Tip>hi</ToolTip.Tip>\n            </TextBlock>\n        </Border>\n    </Grid>",
            after);
    }

    [Fact]
    public void A_selected_element_can_be_wrapped_in_each_container()
    {
        foreach (var container in WrapIn.Containers)
        {
            var after = _r.Apply(new WrapIn(container), View("<StackPanel>\n        |<Button Content=\"Save\"/>|\n    </StackPanel>"));
            Assert.Contains($"<{container}>\n            <Button Content=\"Save\"/>\n        </{container}>", after);
        }
    }

    [Fact]
    public void Not_offered_for_the_root_non_controls_or_preserved_whitespace()
    {
        var wrap = new WrapIn("Border");
        Assert.Null(_r.Title(wrap, "<User|Control xmlns=\"https://github.com/avaloniaui\"/>"));
        Assert.Null(_r.Title(wrap, View("<Grid>\n        <Grid.RowDefinitions><Row|Definition/></Grid.RowDefinitions>\n    </Grid>")));
        Assert.Null(_r.Title(wrap, View("<StackPanel>\n        <Text|Block xml:space=\"preserve\"> a </TextBlock>\n    </StackPanel>")));
    }
}
