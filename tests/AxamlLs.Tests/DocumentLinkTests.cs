using AxamlLs.Analysis;
using AxamlLs.Workspace;

namespace AxamlLs.Tests;

/// <summary>Clickable paths (features plan F7).</summary>
[Collection("sample")]
public class DocumentLinkTests(SampleProjectFixture sample)
{
    private (string Text, IReadOnlyList<DocumentLink> Links) Links(string body)
    {
        var text = $"""
            <UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <StackPanel>
            {body}
              </StackPanel>
            </UserControl>
            """;
        var analyzer = sample.Analyze(text, new ProjectContext(SampleProjectFixture.SampleDir, Path.Combine(SampleProjectFixture.SampleDir, "Views", "Test.axaml"),
            FileIndex: sample.Indexes.Files));
        return (text, DocumentLinks.Find(analyzer));
    }

    [Fact]
    public void Absolute_avares_and_relative_paths_link_to_their_file()
    {
        var (text, links) = Links("""
                <Image Source="/Assets/logo.png"/>
                <Image Source="avares://SampleApp/Assets/Icons/house.png"/>
                <Image Source=" ../Assets/logo.png "/>
            """);
        Assert.Equal(["/Assets/logo.png", "avares://SampleApp/Assets/Icons/house.png", "../Assets/logo.png"], links.Select(l => text[l.Start..l.End]));
        Assert.Equal(["Assets/logo.png", "Assets/Icons/house.png", "Assets/logo.png"], links.Select(l => l.Tooltip));
        Assert.All(links, l => Assert.True(File.Exists(l.Path)));
    }

    [Fact]
    public void Markup_extension_paths_link_too()
    {
        var (text, links) = Links("""    <Image Source="{SvgImage /Assets/Icons/home.svg}"/>""");
        Assert.Equal("/Assets/Icons/home.svg", text[links.Single().Start..links.Single().End]);
    }

    [Fact]
    public void Missing_files_urls_and_other_assemblies_are_not_links()
    {
        var (_, links) = Links("""
                <Image Source="/Assets/missing.png"/>
                <Image Source="https://example.com/logo.png"/>
                <Image Source="avares://Other.Lib/Assets/logo.png"/>
                <TextBlock Text="/Assets/logo.png"/>
            """);
        Assert.Empty(links);
    }
}
