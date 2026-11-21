using AxamlLs.Analysis.Refactorings;

namespace AxamlLs.Tests;

[Collection("sample")]
public class SortAttributesTests(SampleProjectFixture sample)
{
    private readonly RefactoringTestSupport _r = new(sample);
    private readonly SortAttributes _sort = new();

    private string Sort(string tag, RefactoringSettings? settings = null) =>
        Body(_r.Apply(_sort, RefactoringTestSupport.Header + "<Grid>" + tag + "</Grid>\n</UserControl>", settings));

    private static string Body(string text) => text[(text.IndexOf("<Grid>", StringComparison.Ordinal) + 6)..text.IndexOf("</Grid>", StringComparison.Ordinal)];

    [Fact]
    public void Names_layout_the_rest_by_name_then_events()
    {
        Assert.Equal(
            "<Button x:Name=\"Save\" Grid.Row=\"1\" Width=\"80\" Margin=\"4\" Content=\"Save\" IsEnabled=\"True\" Click=\"OnClick\"/>",
            Sort("<B|utton Click=\"OnClick\" IsEnabled=\"True\" Content=\"Save\" Margin=\"4\" Grid.Row=\"1\" Width=\"80\" x:Name=\"Save\"/>"));
    }

    [Fact]
    public void Line_breaks_stay_where_they_were()
    {
        Assert.Equal("<Button Width=\"80\"\n        Content=\"Save\"/>", Sort("<Button| Content=\"Save\"\n        Width=\"80\"/>"));
    }

    [Fact]
    public void The_order_can_be_configured()
    {
        Assert.Equal("<Button Content=\"Save\" Width=\"80\"/>", Sort("<Button| Width=\"80\" Content=\"Save\"/>", new RefactoringSettings(["Content", "*"])));
    }

    [Fact]
    public void Not_offered_when_already_sorted_or_off_a_tag()
    {
        Assert.Null(_r.Title(_sort, RefactoringTestSupport.Header + "<Grid><Button| Width=\"80\" Content=\"Save\"/></Grid>\n</UserControl>"));
        Assert.Null(_r.Title(_sort, RefactoringTestSupport.Header + "<Grid> | <Button Content=\"Save\" Width=\"80\"/></Grid>\n</UserControl>"));
    }
}
