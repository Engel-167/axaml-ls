using AxamlLs.Analysis.Refactorings;

namespace AxamlLs.Tests;

[Collection("sample")]
public class ExtractToResourceTests(SampleProjectFixture sample)
{
    private readonly RefactoringTestSupport _r = new(sample);
    private readonly ExtractToResource _extract = new();

    private static string View(string body) => RefactoringTestSupport.Header + "\n    " + body + "\n</UserControl>";

    [Fact]
    public void A_color_becomes_a_brush_in_new_resources()
    {
        var after = _r.Apply(_extract, View("<Border Background=\"R|ed\"/>"));
        Assert.Contains("<Border Background=\"{StaticResource BorderBackgroundBrush}\"/>", after);
        Assert.Contains("<UserControl.Resources>\n        <SolidColorBrush x:Key=\"BorderBackgroundBrush\" Color=\"Red\" />\n    </UserControl.Resources>", after);
    }

    [Fact]
    public void An_existing_resources_section_gets_it_with_a_unique_key()
    {
        var after = _r.Apply(_extract, View("<UserControl.Resources>\n        <Thickness x:Key=\"BorderMargin\">2</Thickness>\n    </UserControl.Resources>\n    <Border Margin=\"|4\"/>"));
        Assert.Contains("<Thickness x:Key=\"BorderMargin\">2</Thickness>\n        <Thickness x:Key=\"BorderMargin2\">4</Thickness>\n    </UserControl.Resources>", after);
        Assert.Contains("Margin=\"{StaticResource BorderMargin2}\"", after);
    }

    [Fact]
    public void Numbers_and_strings_use_x_types()
    {
        Assert.Contains("<x:Double x:Key=\"BorderWidth\">80</x:Double>", _r.Apply(_extract, View("<Border Width=\"8|0\"/>")));
        Assert.Contains("<x:String x:Key=\"TextBlockText\">Hello</x:String>", _r.Apply(_extract, View("<TextBlock Text=\"Hel|lo\"/>")));
    }

    [Fact]
    public void Not_offered_for_markup_unknown_types_or_a_brush_that_isnt_a_color()
    {
        Assert.Null(_r.Title(_extract, View("<TextBlock Text=\"{Binding Ti|tle}\"/>")));
        Assert.Null(_r.Title(_extract, View("<Border Tag=\"ab|c\"/>")));
        Assert.Null(_r.Title(_extract, View("<Border Background=\"linear-gr|adient(red, blue)\"/>")));
    }
}
