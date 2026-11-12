using AxamlLs.Analysis;

namespace AxamlLs.Tests;

[Collection("sample")]
public class ReferencesTests(SampleProjectFixture sample)
{
    private readonly ProjectTestContext _p = new(sample);

    [Fact]
    public async Task Class_from_a_selector_finds_selectors_attributes_and_code()
    {
        var found = _p.Describe(await _p.Occurrences("MainView.axaml", "Button.Card", delta: 8));
        Assert.Equal(["CodeStyles.cs:Card", "MainView.axaml:Card", "MainView.axaml:Card", "Second.axaml:Card", "Second.axaml:Card"], found);
    }

    [Fact]
    public async Task Resource_key_finds_the_declaration_and_every_use()
    {
        var occurrences = await _p.Occurrences("MainView.axaml", "{StaticResource Accent}", delta: 18);
        var found = _p.Describe(occurrences);
        Assert.Equal(["MainView.axaml:Accent", "MainView.axaml:Accent", "MainView.axaml:Accent", "Second.axaml:Accent", "Second.axaml:Accent"], found);
        Assert.Single(occurrences, o => o.IsDeclaration);
        // From the x:Key itself the result is the same.
        Assert.Equal(found, _p.Describe(await _p.Occurrences("MainView.axaml", "x:Key=\"Accent\"", delta: 8)));
    }

    [Fact]
    public async Task Resource_key_set_in_code_is_found()
    {
        var f = _p.File("Second.axaml");
        var analyzer = f.Analyzer;
        Assert.Contains("CodeStyles.cs:FromCode", _p.Describe(await Targets.OccurrencesAsync(new ResourceKeyTarget("FromCode"), f, _p.Files, sample.Host)));
        Assert.NotNull(analyzer);
    }

    [Fact]
    public async Task View_model_property_finds_bindings_in_both_views_and_the_csharp_declaration()
    {
        var occurrences = await _p.Occurrences("Second.axaml", "{Binding Title}", delta: 10);
        var found = _p.Describe(occurrences);
        Assert.Equal(2, found.Count(x => x == "Second.axaml:Title"));
        Assert.Contains("MainView.axaml:Title", found);
        var declaration = Assert.Single(occurrences, o => o.IsDeclaration);
        Assert.EndsWith("MainViewModel.cs", declaration.Path);
        Assert.Equal("_title", _p.Describe([declaration]).Single().Split(':')[1]);
    }

    [Fact]
    public async Task Element_name_in_a_binding_finds_the_x_name_and_the_use()
    {
        var occurrences = await _p.Occurrences("MainView.axaml", "{Binding #SaveButton.Content}", delta: 12);
        Assert.Equal(["MainView.axaml:SaveButton", "MainView.axaml:SaveButton"], _p.Describe(occurrences));
        Assert.Single(occurrences, o => o.IsDeclaration);
    }
}
