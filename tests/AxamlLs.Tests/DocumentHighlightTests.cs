using AxamlLs.Analysis;

namespace AxamlLs.Tests;

/// <summary>Highlights of the other uses in the file (features plan F2).</summary>
[Collection("sample")]
public class DocumentHighlightTests(SampleProjectFixture sample)
{
    private readonly ProjectTestContext _p = new(sample);

    /// <summary>"text:Kind" for each highlight at <paramref name="anchor"/> + <paramref name="delta"/> in MainView.axaml.</summary>
    private string[] At(string anchor, int delta = 0)
    {
        var file = _p.File("MainView.axaml");
        var at = file.Text.IndexOf(anchor, StringComparison.Ordinal);
        Assert.True(at >= 0, anchor);
        return [.. DocumentHighlights.For(file, at + delta).Select(h => $"{file.Text[h.Start..h.End]}:{h.Kind}")];
    }

    [Fact]
    public void Element_name_declaration_is_a_write_and_references_are_reads()
    {
        var found = At("x:Name=\"Header\"", 9);
        Assert.Equal(["Header:Write", "Header:Read", "Header:Read"], found);
        Assert.Equal(found, At("#Header.Text", 1));
    }

    [Fact]
    public void Resource_key_declaration_and_uses()
    {
        Assert.Equal(["Accent:Write", "Accent:Read", "Accent:Read"], At("{StaticResource Accent}", 17));
    }

    [Fact]
    public void Style_class_in_a_selector_defines_it()
    {
        Assert.Equal(["Card:Write", "Card:Read"], At("Classes=\"Card\"", 10));
    }

    [Fact]
    public void Binding_member_uses_in_the_file()
    {
        var found = At("{Binding Title}", 10);
        Assert.NotEmpty(found);
        Assert.All(found, h => Assert.Equal("Title:Read", h));
    }

    [Fact]
    public void Tag_names_highlight_their_pair_and_whitespace_nothing()
    {
        var file = _p.File("MainView.axaml");
        var close = file.Text.LastIndexOf("</", StringComparison.Ordinal);
        var name = file.Text[(close + 2)..file.Text.IndexOf('>', close)];
        Assert.Equal([$"{name}:Text", $"{name}:Text"], At("</" + name + ">", 3));
        Assert.Empty(DocumentHighlights.For(file, file.Text.IndexOf("\n    ", StringComparison.Ordinal) + 2));
    }
}
