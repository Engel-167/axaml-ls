using AxamlLs.Analysis;
using AxamlLs.Workspace;

namespace AxamlLs.Tests;

[Collection("sample")]
public class ResourceKeyTests(SampleProjectFixture sample)
{
    private const string Header = """
        <UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     xmlns:vm="using:SampleApp.ViewModels" x:DataType="vm:MainViewModel">
        """;

    private List<XamlDiagnostic> Check(string body, out string text)
    {
        text = Header + "\n" + body + "\n</UserControl>";
        var analyzer = sample.Analyze(text);
        analyzer.ResourceProvider = () => [new("Accent", "SolidColorBrush", "/p/S.axaml", 1, 1, "Color=\"#FF6600\""), new("Converter", "FooConverter", "/p/S.axaml", 2, 1, null)];
        analyzer.StyleProvider = () => ClassIndex.ScanText("/p/Code.cs", "app.Resources[\"FromCode\"] = x;");
        return ResourceCheck.Unknown(analyzer).ToList();
    }

    [Fact]
    public void Missing_static_key_is_a_warning_on_the_key()
    {
        var d = Assert.Single(Check("<Button Background=\"{StaticResource Missing}\"/>", out var text));
        Assert.Equal(Codes.UnknownResource, d.Code);
        Assert.Equal(Severity.Warning, d.Severity);
        Assert.Equal("Missing", text[d.Start..d.End]);
        Assert.Contains("'Missing' is not declared", d.Message);
    }

    [Fact]
    public void Existing_project_key_is_fine() => Assert.Empty(Check("<Button Background=\"{StaticResource Accent}\"/>", out _));

    [Fact]
    public void Theme_keys_are_fine()
    {
        // Pick a key the compiled Fluent theme really declares.
        var key = sample.Project.LibraryResources.First(r => r.Key.StartsWith("SystemAccentColor", StringComparison.Ordinal)).Key;
        Assert.Empty(Check($"<Button Background=\"{{StaticResource {key}}}\"/>", out _));
    }

    [Fact]
    public void Missing_dynamic_key_is_information()
    {
        var d = Assert.Single(Check("<Button Background=\"{DynamicResource Missing}\"/>", out _));
        Assert.Equal(Severity.Information, d.Severity);
    }

    [Fact]
    public void Nested_converter_keys_are_checked()
    {
        var d = Assert.Single(Check("<TextBlock Text=\"{Binding Title, Converter={StaticResource NoConverter}}\"/>", out var text));
        Assert.Equal("NoConverter", text[d.Start..d.End]);
        Assert.Empty(Check("<TextBlock Text=\"{Binding Title, Converter={StaticResource Converter}}\"/>", out _));
    }

    [Fact]
    public void Keys_that_are_markup_are_skipped() =>
        Assert.Empty(Check("<Button Background=\"{StaticResource {x:Static vm:MainViewModel.Foo}}\"/>", out _));

    [Fact]
    public void Keys_set_in_csharp_count_as_declared() =>
        Assert.Empty(Check("<Button Background=\"{StaticResource FromCode}\"/>", out _));

    [Fact]
    public void Suggests_a_close_key()
    {
        var d = Assert.Single(Check("<Button Background=\"{StaticResource Acent}\"/>", out _));
        Assert.EndsWith("Did you mean 'Accent'?", d.Message);
    }

    [Fact]
    public void Nothing_is_reported_without_a_project()
    {
        var analyzer = XamlAnalyzer.Analyze("<Button xmlns=\"https://github.com/avaloniaui\" Background=\"{StaticResource Nope}\"/>", null, false);
        Assert.Empty(ResourceCheck.Unknown(analyzer));
    }

    [Fact]
    public void The_sample_views_have_no_unknown_resources()
    {
        // Wired like the server does it: keys come from the whole sample project.
        foreach (var name in new[] { "Views/MainView.axaml", "Views/Second.axaml" })
        {
            var analyzer = sample.AnalyzeFile(name);
            ProjectXaml.Wire(analyzer, Path.Combine(SampleProjectFixture.SampleDir, name), sample.Host, () => new Dictionary<string, string>());
            Assert.Empty(ResourceCheck.Unknown(analyzer));
        }
    }
}
