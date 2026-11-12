using AxamlLs.Analysis;

namespace AxamlLs.Tests;

[Collection("sample")]
public class RenameTests(SampleProjectFixture sample)
{
    private readonly ProjectTestContext _p = new(sample);

    private async Task<RenameResult> Rename(string file, string anchor, int delta, string newName)
    {
        var (found, f) = _p.TargetAt(file, anchor, delta);
        return await AxamlLs.Analysis.Rename.ComputeAsync(found.Target, newName, f, _p.Files, sample.Host);
    }

    [Fact]
    public async Task Class_is_renamed_in_selectors_attributes_and_code()
    {
        var result = await Rename("MainView.axaml", "Button.Card", 8, "Tile");
        Assert.Null(result.Error);
        var changed = _p.Apply(result.Edits);
        Assert.Contains("Selector=\"Button.Tile\"", changed["MainView.axaml"]);
        Assert.Contains("Classes=\"Tile\"", changed["MainView.axaml"]);
        Assert.Contains("Classes=\"Tile\"", changed["Second.axaml"]);
        Assert.Contains("Classes.Tile=", changed["Second.axaml"]);
        Assert.Contains("Classes.Add(\"Tile\")", changed["CodeStyles.cs"]);
        Assert.DoesNotContain("Card", changed["MainView.axaml"]);
    }

    [Fact]
    public async Task Resource_key_is_renamed_everywhere()
    {
        var result = await Rename("MainView.axaml", "x:Key=\"Accent\"", 8, "Brand");
        var changed = _p.Apply(result.Edits);
        Assert.DoesNotContain("Accent", changed["MainView.axaml"]);
        Assert.Contains("x:Key=\"Brand\"", changed["MainView.axaml"]);
        Assert.Contains("{StaticResource Brand}", changed["Second.axaml"]);
        Assert.Contains("{DynamicResource Brand}", changed["Second.axaml"]);
    }

    [Fact]
    public async Task Element_name_is_renamed_with_its_uses()
    {
        var result = await Rename("MainView.axaml", "x:Name=\"SaveButton\"", 9, "Store");
        var text = _p.Apply(result.Edits)["MainView.axaml"];
        Assert.Contains("x:Name=\"Store\"", text);
        Assert.Contains("{Binding #Store.Content}", text);
        Assert.DoesNotContain("SaveButton", text);
    }

    [Fact]
    public async Task Observable_property_renames_the_field_and_the_bindings()
    {
        var result = await Rename("Second.axaml", "{Binding Title}", 10, "Heading");
        Assert.Null(result.Error);
        var changed = _p.Apply(result.Edits);
        Assert.Contains("private string _heading", changed["MainViewModel.cs"]);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(changed["Second.axaml"], "Binding Heading").Count);
        Assert.Contains("{Binding Heading}", changed["MainView.axaml"]);
    }

    [Fact]
    public async Task Relay_command_renames_the_method_and_needs_the_command_suffix()
    {
        var refused = await Rename("Second.axaml", "{Binding SaveCommand}", 10, "Store");
        Assert.Contains("must end with 'Command'", refused.Error);
        var ok = await Rename("Second.axaml", "{Binding SaveCommand}", 10, "StoreCommand");
        Assert.Null(ok.Error);
        var changed = _p.Apply(ok.Edits);
        Assert.Contains("private void Store()", changed["MainViewModel.cs"]);
        Assert.Contains("{Binding StoreCommand}", changed["Second.axaml"]);
    }

    [Fact]
    public async Task Event_handler_is_renamed_in_code_and_xaml()
    {
        var result = await Rename("MainView.axaml", "Click=\"OnClick\"", 9, "OnPressed");
        var changed = _p.Apply(result.Edits);
        Assert.Contains("Click=\"OnPressed\"", changed["MainView.axaml"]);
        Assert.Contains("void OnPressed(", changed["MainView.axaml.cs"]);
    }

    [Fact]
    public void Library_properties_cannot_be_renamed()
    {
        var (found, _) = _p.TargetAt("Second.axaml", "<Border Classes.Card=\"{Binding IsBusy}\" Background", 45);
        Assert.IsType<SymbolTarget>(found.Target);
        Assert.False(AxamlLs.Analysis.Rename.CanRename(found.Target));
        Assert.True(AxamlLs.Analysis.Rename.CanRename(new StyleClassTarget("x")));
    }

    [Fact]
    public async Task Invalid_new_names_are_refused()
    {
        Assert.NotNull((await Rename("MainView.axaml", "x:Key=\"Accent\"", 8, "1bad name")).Error);
        Assert.NotNull((await Rename("Second.axaml", "{Binding Title}", 10, "not valid")).Error);
    }
}
