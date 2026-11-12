using AxamlLs.Lsp;
using AxamlLs.Text;
using AxamlLs.Workspace;

namespace AxamlLs.Tests;

[Collection("sample")]
public class ProjectXamlTests(SampleProjectFixture sample)
{
    private static readonly IReadOnlyDictionary<string, string> NoBuffers = new Dictionary<string, string>();

    [Fact]
    public void Finds_every_axaml_file_of_the_project()
    {
        var files = new ProjectXaml().Files(sample.Host, NoBuffers);
        Assert.Contains(files, f => f.Path.EndsWith("MainView.axaml"));
        Assert.Contains(files, f => f.Path.EndsWith("Broken.axaml"));
        Assert.Contains(files, f => f.Path.EndsWith("Second.axaml"));
        Assert.DoesNotContain(files, f => f.Path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));
    }

    [Fact]
    public void An_open_buffer_wins_over_the_disk()
    {
        var path = Path.Combine(SampleProjectFixture.SampleDir, "Views", "Second.axaml");
        var buffers = new Dictionary<string, string> { [path] = "<UserControl xmlns=\"https://github.com/avaloniaui\"><Button Name=\"Unsaved\"/></UserControl>" };
        var file = new ProjectXaml().Files(sample.Host, buffers).Single(f => f.Path == path);
        Assert.Contains("Unsaved", file.Text);
        Assert.True(file.Analyzer.Result.Names.ContainsKey("Unsaved"));
    }

    [Fact]
    public void A_second_call_with_unchanged_files_hits_the_cache()
    {
        var index = new ProjectXaml();
        var first = index.Files(sample.Host, NoBuffers);
        var second = index.Files(sample.Host, NoBuffers);
        Assert.Equal(first.Count, second.Count);
        for (var i = 0; i < first.Count; i++) Assert.Same(first[i].Analyzer, second[i].Analyzer);
    }
}

public class EditsTests
{
    private static readonly string A = Path.Combine(Path.GetTempPath(), "edits-a.axaml");
    private static readonly string B = Path.Combine(Path.GetTempPath(), "edits-b.axaml");

    private static (string, LineIndex) TextOf(string text) => (text, new LineIndex(text));

    [Fact]
    public void Groups_by_file_and_converts_to_ranges()
    {
        var edit = Edits.ToWorkspaceEdit([new FileEdit(A, 4, 7, "XYZ"), new FileEdit(B, 0, 1, "q")], _ => TextOf("abc\ndefgh"));
        var changes = edit["changes"]!.AsObject();
        Assert.Equal(2, changes.Count);
        var first = changes[LspServer.PathToUri(A)]!.AsArray().Single()!;
        Assert.Equal(1, (int)first["range"]!["start"]!["line"]!);
        Assert.Equal(0, (int)first["range"]!["start"]!["character"]!);
        Assert.Equal(3, (int)first["range"]!["end"]!["character"]!);
        Assert.Equal("XYZ", (string?)first["newText"]);
    }

    [Fact]
    public void Drops_duplicates_and_overlaps()
    {
        var edit = Edits.ToWorkspaceEdit([new FileEdit(A, 0, 3, "x"), new FileEdit(A, 0, 3, "x"), new FileEdit(A, 2, 5, "y"), new FileEdit(A, 6, 7, "z")], _ => TextOf("0123456789"));
        var list = edit["changes"]![LspServer.PathToUri(A)]!.AsArray();
        Assert.Equal(["x", "z"], list.Select(e => (string?)e!["newText"]));
    }

    [Fact]
    public void Positions_are_utf16()
    {
        // "😀" is two UTF-16 units: the edit after it starts at column 3.
        const string text = "a😀bc";
        var edit = Edits.ToWorkspaceEdit([new FileEdit(A, 3, 4, "B")], _ => TextOf(text));
        var range = edit["changes"]![LspServer.PathToUri(A)]![0]!["range"]!;
        Assert.Equal(3, (int)range["start"]!["character"]!);
        Assert.Equal(4, (int)range["end"]!["character"]!);
    }

    [Fact]
    public void Apply_replaces_back_to_front()
    {
        Assert.Equal("aXcYe", Edits.Apply("abcde", [new FileEdit(A, 1, 2, "X"), new FileEdit(A, 3, 4, "Y")]));
    }
}
