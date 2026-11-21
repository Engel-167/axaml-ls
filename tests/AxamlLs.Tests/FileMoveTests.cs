using System.Text.Json.Nodes;
using AxamlLs.Analysis;
using AxamlLs.Lsp;
using AxamlLs.Telemetry;
using AxamlLs.Workspace;

namespace AxamlLs.Tests;

/// <summary>Path values follow file renames and moves (features plan F4).</summary>
[Collection("sample")]
public class FileMoveTests(SampleProjectFixture sample)
{
    private static readonly string Root = SampleProjectFixture.SampleDir;
    private static readonly string ViewPath = Path.Combine(Root, "Views", "Test.axaml");

    private static string At(params string[] parts) => Path.Combine([Root, .. parts]);

    /// <summary>The view text after the moves' edits, applied to it.</summary>
    private string After(string body, params FileMove[] moves)
    {
        var text = $"""
            <UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <StackPanel>
            {body}
              </StackPanel>
            </UserControl>
            """;
        var analyzer = sample.Analyze(text, new ProjectContext(Root, ViewPath, FileIndex: sample.Indexes.Files));
        var edits = FileMoves.EditsFor(moves, Root, [(ViewPath, AssetReferences.Find(analyzer))]).ToList();
        Assert.All(edits, e => Assert.Equal(ViewPath, e.Path));
        return Edits.Apply(text, edits)[text.IndexOf("<StackPanel>", StringComparison.Ordinal)..];
    }

    [Fact]
    public void Each_written_form_is_kept()
    {
        var after = After("""
                <Image Source="/Assets/logo.png"/>
                <Image Source="avares://SampleApp/Assets/logo.png"/>
                <Image Source="../Assets/logo.png"/>
                <Image Source="/Assets/logo.png#frame"/>
            """, new FileMove(At("Assets", "logo.png"), At("Assets", "Images", "brand.png")));
        Assert.Contains("Source=\"/Assets/Images/brand.png\"", after);
        Assert.Contains("Source=\"avares://SampleApp/Assets/Images/brand.png\"", after);
        Assert.Contains("Source=\"../Assets/Images/brand.png\"", after);
        Assert.Contains("Source=\"/Assets/Images/brand.png#frame\"", after);
        Assert.DoesNotContain("logo.png", after);
    }

    [Fact]
    public void A_folder_move_moves_every_reference_under_it()
    {
        var after = After("""
                <Image Source="/Assets/Icons/house.png"/>
                <Image Source="{SvgImage /Assets/Icons/home.svg}"/>
                <Image Source="/Assets/logo.png"/>
            """, new FileMove(At("Assets", "Icons"), At("Assets", "Glyphs")));
        Assert.Contains("Source=\"/Assets/Glyphs/house.png\"", after);
        Assert.Contains("{SvgImage /Assets/Glyphs/home.svg}", after);
        Assert.Contains("Source=\"/Assets/logo.png\"", after);
    }

    [Fact]
    public void A_moved_view_rewrites_its_own_relative_references()
    {
        var after = After("""
                <Image Source="../Assets/logo.png"/>
                <Image Source="./Local.png"/>
                <Image Source="/Assets/logo.png"/>
            """, new FileMove(ViewPath, At("Views", "Sub", "Test.axaml")));
        Assert.Contains("Source=\"../../Assets/logo.png\"", after);
        Assert.Contains("Source=\"../Local.png\"", after);
        Assert.Contains("Source=\"/Assets/logo.png\"", after);
    }

    [Fact]
    public void Outside_the_project_other_assemblies_and_escapes_are_left_alone()
    {
        var outside = Path.Combine(Path.GetTempPath(), "elsewhere", "logo.png");
        const string body = """
                <Image Source="/Assets/logo.png"/>
                <Image Source="avares://Other.Lib/Assets/logo.png"/>
                <Image Source="https://example.com/Assets/logo.png"/>
            """;
        Assert.Equal(After(body), After(body, new FileMove(At("Assets", "logo.png"), outside)));
        Assert.Contains("avares://Other.Lib/Assets/logo.png", After(body, new FileMove(At("Assets", "logo.png"), At("Assets", "brand.png"))));
        Assert.Contains("/Assets/my%20logo.png", After("""    <Image Source="/Assets/my%20logo.png"/>""", new FileMove(At("Assets", "my logo.png"), At("Assets", "b.png"))));
    }

    [Fact]
    public void A_view_class_follows_only_when_its_code_behind_moves_too()
    {
        var view = new FileMove(At("Views", "Foo.axaml"), At("Views", "Bar.axaml"));
        var code = new FileMove(At("Views", "Foo.axaml.cs"), At("Views", "Bar.axaml.cs"));
        Assert.Equal([(view.Old, "Foo", "Bar")], FileMoves.RenamedViews([view, code]));
        Assert.Empty(FileMoves.RenamedViews([view]));
        Assert.Empty(FileMoves.RenamedViews([new FileMove(At("Views", "Foo.axaml"), At("Other", "Foo.axaml")), new FileMove(At("Views", "Foo.axaml.cs"), At("Other", "Foo.axaml.cs"))]));
    }
}

/// <summary>willRenameFiles through the server, on the real sample (edits are returned, never applied).</summary>
public class FileMoveRequestTests
{
    private static async Task<JsonNode?> RenameMainView(bool updateClass)
    {
        using var metrics = new ServerMetrics();
        await using var client = new LspTestClient(metrics);
        var view = Path.Combine(SampleProjectFixture.SampleDir, "Views", "MainView.axaml");
        await client.OpenAsync(Protocol.PathToUri(view), File.ReadAllText(view),
            initializationOptions: new JsonObject { ["renameFiles"] = new JsonObject { ["updateClass"] = updateClass } });
        JsonObject Move(string from, string to) => new() { ["oldUri"] = Protocol.PathToUri(from), ["newUri"] = Protocol.PathToUri(to) };
        var home = Path.Combine(SampleProjectFixture.SampleDir, "Views", "HomeView.axaml");
        return (await client.RequestAsync("workspace/willRenameFiles", new JsonObject
        {
            ["files"] = new JsonArray(Move(view, home), Move(view + ".cs", home + ".cs")),
        }))["result"];
    }

    [Fact]
    public async Task Renaming_a_view_and_its_code_behind_renames_the_class()
    {
        var changes = (await RenameMainView(updateClass: true))!["changes"]!.AsObject();
        var files = changes.Select(c => Path.GetFileName(Protocol.UriToPath(c.Key))).Order().ToList();
        Assert.Contains("MainView.axaml", files);
        Assert.Contains("MainView.axaml.cs", files);
        string Applied(string name)
        {
            var (uri, edits) = changes.Single(c => c.Key.EndsWith("/" + name, StringComparison.Ordinal));
            var text = File.ReadAllText(Protocol.UriToPath(uri));
            var lines = new AxamlLs.Text.LineIndex(text);
            foreach (var e in edits!.AsArray().OrderByDescending(e => Protocol.Offset(lines, e!["range"]!["start"]!)))
                text = text[..Protocol.Offset(lines, e!["range"]!["start"]!)] + (string)e["newText"]! + text[Protocol.Offset(lines, e["range"]!["end"]!)..];
            return text;
        }
        Assert.Contains("x:Class=\"SampleApp.Views.HomeView\"", Applied("MainView.axaml"));
        Assert.Contains("class HomeView", Applied("MainView.axaml.cs"));
        Assert.DoesNotContain("MainView", Applied("MainView.axaml.cs"));

        Assert.Null(await RenameMainView(updateClass: false));
    }
}
