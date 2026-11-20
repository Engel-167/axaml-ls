using AxamlLs.Analysis;
using AxamlLs.Xaml;

namespace AxamlLs.Tests;

/// <summary>Expand selection (features plan F5): each chain innermost first, as the selected text.</summary>
public class SelectionRangeTests
{
    private const string Doc = """
        <UserControl xmlns="https://github.com/avaloniaui">
          <Grid>
            <Grid.RowDefinitions>
              <RowDefinition Height="Auto"/>
            </Grid.RowDefinitions>
            <!-- the title -->
            <TextBlock Text="{Binding Title, Converter={StaticResource Upper}}" Margin="4"/>
          </Grid>
        </UserControl>
        """;

    private static string[] Chain(string anchor, int delta)
    {
        var at = Doc.IndexOf(anchor, StringComparison.Ordinal) + delta;
        return [.. SelectionRanges.At(XmlDoc.Parse(Doc), at).Select(r => Doc[r.Start..r.End])];
    }

    private static string Element(string startsWith) => Doc[Doc.IndexOf(startsWith, StringComparison.Ordinal)..];

    [Fact]
    public void A_binding_inside_a_nested_extension_grows_through_each_extension()
    {
        var chain = Chain("Upper", 2);
        // The argument value equals the inner extension, and the attribute value the outer one: each appears once.
        Assert.Equal(
            ["Upper", "{StaticResource Upper}", "Converter={StaticResource Upper}", "{Binding Title, Converter={StaticResource Upper}}",
             "Text=\"{Binding Title, Converter={StaticResource Upper}}\""],
            chain[..5]);
        Assert.StartsWith("<TextBlock Text=", chain[5]);
        Assert.EndsWith("Margin=\"4\"/>", chain[5]);
        Assert.Equal(Doc, chain[^1]);
        Assert.All(chain.Zip(chain.Skip(1)), pair => Assert.Contains(pair.First, pair.Second));
    }

    [Fact]
    public void A_plain_attribute_value_then_the_attribute_then_the_tag()
    {
        var chain = Chain("Margin=\"4\"", 8);
        Assert.Equal(["4", "Margin=\"4\""], chain[..2]);
        Assert.StartsWith("<TextBlock", chain[2]);
    }

    [Fact]
    public void An_element_in_a_property_element_grows_to_its_parents()
    {
        var chain = Chain("RowDefinition Height", 3);
        Assert.Equal("RowDefinition", chain[0]);
        Assert.Equal("<RowDefinition Height=\"Auto\"/>", chain[1]);
        var property = Element("<Grid.RowDefinitions>");
        Assert.Equal(property[..(property.IndexOf("</Grid.RowDefinitions>", StringComparison.Ordinal) + "</Grid.RowDefinitions>".Length)], chain[3]);
        Assert.StartsWith("<Grid>", chain.First(c => c.StartsWith("<Grid>", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_comment_selects_its_word_then_the_comment()
    {
        var chain = Chain("title -->", 2);
        Assert.Equal(["title", "<!-- the title -->"], chain[..2]);
        Assert.Equal(Doc, chain[^1]);
    }
}

/// <summary>The request answers one nested chain per position, innermost range first.</summary>
public class SelectionRangeRequestTests
{
    [Fact]
    public async Task Each_position_gets_a_chain_with_parents()
    {
        using var metrics = new AxamlLs.Telemetry.ServerMetrics();
        await using var client = new LspTestClient(metrics);
        var uri = AxamlLs.Lsp.Protocol.PathToUri(Path.Combine(Path.GetTempPath(), "axaml-ls-selection-" + Guid.NewGuid().ToString("N"), "View.axaml"));
        await client.OpenAsync(uri, "<Grid>\n  <Border Margin=\"4\"/>\n</Grid>");
        var position = new System.Text.Json.Nodes.JsonObject { ["line"] = 1, ["character"] = 18 };
        var result = (await client.RequestAsync("textDocument/selectionRange", new System.Text.Json.Nodes.JsonObject
        {
            ["textDocument"] = new System.Text.Json.Nodes.JsonObject { ["uri"] = uri },
            ["positions"] = new System.Text.Json.Nodes.JsonArray(position, position.DeepClone()),
        }))["result"]!.AsArray();
        Assert.Equal(2, result.Count);
        var depth = 0;
        for (var node = result[0]; node != null; node = node["parent"]) depth++;
        Assert.True(depth >= 4, $"depth {depth}");
        Assert.Equal(18, (int?)result[0]!["range"]!["start"]!["character"]); // the value "4", after the quote at 17
    }
}
