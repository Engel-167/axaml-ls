using AxamlLs.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace AxamlLs.Tests;

/// <summary>A rename done in C# (and saved) makes the AXAML follow.</summary>
[Collection("sample")]
public class CodeRenameTests(SampleProjectFixture sample)
{
    private readonly ProjectTestContext _p = new(sample);

    private Document ViewModel() =>
        sample.Host.Solution!.Projects.SelectMany(p => p.Documents).First(d => d.FilePath!.EndsWith("MainViewModel.cs", StringComparison.Ordinal));

    private async Task<List<CodeRename>> Detect(Func<string, string> change)
    {
        var doc = ViewModel();
        var text = (await doc.GetTextAsync()).ToString();
        return await CodeRenames.DetectAsync(doc, SourceText.From(change(text)));
    }

    private static string[] Describe(IEnumerable<CodeRename> renames) => renames.Select(r => $"{r.OldSymbol.Name}->{r.NewName}").Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public async Task Observable_field_rename_renames_the_generated_property()
    {
        var renames = await Detect(t => t.Replace("_title", "_heading"));
        Assert.Equal(["Title->Heading", "_title->_heading"], Describe(renames));

        var changed = _p.Apply(CodeRenames.AxamlEdits(renames, _p.Files));
        Assert.Contains("{Binding Heading}", changed["MainView.axaml"]);
        Assert.DoesNotContain("Binding Title", changed["Second.axaml"]);
    }

    [Fact]
    public async Task Relay_command_method_rename_renames_the_command()
    {
        var renames = await Detect(t => t.Replace("void Save()", "void Store()"));
        Assert.Equal(["Save->Store", "SaveCommand->StoreCommand"], Describe(renames));

        var changed = _p.Apply(CodeRenames.AxamlEdits(renames, _p.Files));
        Assert.Contains("{Binding StoreCommand}", changed["Second.axaml"]);
    }

    [Fact]
    public async Task Partial_property_rename_is_followed()
    {
        var renames = await Detect(t => t.Replace("bool IsBusy", "bool IsWorking"));
        Assert.Contains("IsBusy->IsWorking", Describe(renames));
    }

    [Fact]
    public async Task Other_edits_are_not_renames()
    {
        Assert.Empty(await Detect(t => t.Replace("_title = \"Sample\"", "_titl = \"Sample\"").Replace("public void Reset()", "public void Reset(int x)")));
        Assert.Empty(await Detect(t => t.Replace("\"Sample\"", "\"Other\"")));
        Assert.Empty(await Detect(t => t.Replace("/// <summary>The window title.</summary>", "/// <summary>The title.</summary>")));
        // A use renamed without its declaration is not a rename of anything.
        Assert.Empty(await Detect(t => t.Replace("Task.FromResult", "Task.FromResults")));
    }

    [Fact]
    public void Swaps_must_be_consistent()
    {
        var a = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText("class A { int x; int y => x; }").GetRoot();
        var good = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText("class A { int z; int y => z; }").GetRoot();
        var mixed = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText("class A { int z; int y => w; }").GetRoot();
        Assert.Equal(2, CodeRenames.SwappedIdentifiers(a, good)!.Count);
        Assert.Null(CodeRenames.SwappedIdentifiers(a, mixed));
    }
}
