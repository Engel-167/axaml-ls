using AxamlLs.Analysis;
using AxamlLs.Xaml;

namespace AxamlLs.Tests;

public class LinkedTagsTests
{
    private static string[]? At(string marked)
    {
        var offset = marked.IndexOf('|');
        var text = marked.Remove(offset, 1);
        if (LinkedTags.At(XmlDoc.Parse(text), text, offset) is not var (open, close)) return null;
        return [text[open.Start..open.End], text[close.Start..close.End], text[(close.Start - 2)..close.Start]];
    }

    [Fact]
    public void Start_and_end_tag_names_are_linked() =>
        Assert.Equal(["Grid", "Grid", "</"], At("<Window><Gr|id Margin=\"4\"><Button/></Grid></Window>"));

    [Fact]
    public void Works_from_the_end_tag_and_with_prefixes() =>
        Assert.Equal(["local:Card", "local:Card", "</"], At("<Window><local:Card></local:Ca|rd></Window>"));

    [Fact]
    public void Edges_of_the_name_count() =>
        Assert.Equal(["Grid", "Grid", "</"], At("<Window><Grid|></Grid></Window>"));

    [Fact]
    public void Nothing_for_self_closing_tags_attributes_or_text()
    {
        Assert.Null(At("<Window><Button|/></Window>"));
        Assert.Null(At("<Window><Grid Mar|gin=\"4\"></Grid></Window>"));
        Assert.Null(At("<Window>te|xt</Window>"));
        Assert.Null(At("<Window><Grid>|</Window>"));
    }
}
