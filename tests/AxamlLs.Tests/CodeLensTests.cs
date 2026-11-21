using System.Text.Json.Nodes;
using AxamlLs.Analysis;
using AxamlLs.Lsp;
using AxamlLs.Telemetry;

namespace AxamlLs.Tests;

/// <summary>"N references" lenses on x:Key (features plan F11).</summary>
[Collection("sample")]
public class CodeLensTests(SampleProjectFixture sample)
{
    private readonly ProjectTestContext _p = new(sample);

    [Fact]
    public async Task A_key_used_in_several_files_counts_every_use_and_jumps_to_another_file()
    {
        var occurrences = await _p.Occurrences("MainView.axaml", "x:Key=\"Accent\"", 8);
        Assert.Equal("4 references", ResourceLenses.Title(ResourceLenses.Uses(occurrences)));
        var jump = ResourceLenses.Jump(occurrences, _p.File("MainView.axaml").Path)!;
        Assert.EndsWith("Second.axaml", jump.Path);
        Assert.False(jump.IsDeclaration);
    }

    [Fact]
    public void Titles_and_jumps_for_zero_and_one_use()
    {
        Assert.Equal("unused", ResourceLenses.Title(0));
        Assert.Equal("1 reference", ResourceLenses.Title(1));
        var declaration = new Occurrence("/p/A.axaml", 10, 15, IsDeclaration: true);
        var sameFile = new Occurrence("/p/A.axaml", 40, 45);
        Assert.Equal(declaration, ResourceLenses.Jump([declaration], "/p/A.axaml"));
        Assert.Equal(sameFile, ResourceLenses.Jump([declaration, sameFile], "/p/A.axaml"));
        Assert.Null(ResourceLenses.Jump([], "/p/A.axaml"));
    }
}

/// <summary>Key lenses through the server: unresolved first, then counted by resolve with the range unchanged.</summary>
public class CodeLensRequestTests
{
    private const string View = """
        <UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <UserControl.Resources>
            <SolidColorBrush x:Key="Unused" Color="Red"/>
            <SolidColorBrush x:Key="Once" Color="Blue"/>
          </UserControl.Resources>
          <Border Background="{StaticResource Once}"/>
        </UserControl>
        """;

    [Fact]
    public async Task Resolve_counts_uses_and_keeps_the_range()
    {
        using var metrics = new ServerMetrics();
        await using var client = new LspTestClient(metrics);
        var uri = Protocol.PathToUri(Path.Combine(Path.GetTempPath(), "axaml-ls-lens-" + Guid.NewGuid().ToString("N"), "View.axaml"));
        await client.OpenAsync(uri, View);
        var lenses = (await client.RequestAsync("textDocument/codeLens", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } }))["result"]!.AsArray();
        var keyLenses = lenses.Where(l => l!["data"] != null).ToList();
        Assert.Equal(2, keyLenses.Count);
        Assert.All(keyLenses, l => Assert.Null(l!["command"]));

        var titles = new List<string?>();
        foreach (var lens in keyLenses)
        {
            var resolved = (await client.RequestAsync("codeLens/resolve", lens!.DeepClone().AsObject()))["result"]!;
            Assert.True(JsonNode.DeepEquals(lens["range"], resolved["range"]));
            titles.Add((string?)resolved["command"]!["title"]);
        }
        Assert.Equal(["unused", "1 reference"], titles);
    }
}
