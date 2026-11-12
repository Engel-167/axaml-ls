using AxamlLs.Analysis;
using AxamlLs.Lsp;
using AxamlLs.Workspace;

namespace AxamlLs.Tests;

[Collection("sample")]
public class QuickFixTests(SampleProjectFixture sample)
{
    private const string Xmlns = "xmlns=\"https://github.com/avaloniaui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";

    private static string ViewPath => Path.Combine(SampleProjectFixture.SampleDir, "Views", "Scratch.axaml");

    /// <summary>The fixes for the diagnostic with this code, each as (title, the resulting text of the file it edits).</summary>
    private List<(string Title, string Result, QuickFix Fix)> Fixes(string xaml, string code)
    {
        var analyzer = sample.Analyze(xaml);
        ProjectXaml.Wire(analyzer, ViewPath, sample.Host, () => new Dictionary<string, string>());
        var d = LspServer.CollectDiagnostics(analyzer).Select(x => x.Diagnostic).First(x => x.Code == code);
        return QuickFixes.For(analyzer, d).Select(f =>
        {
            var edited = f.Edits.Select(e => e.Path).Distinct().Single(); // these fixes edit one file
            var text = edited == ViewPath ? xaml : File.ReadAllText(edited);
            return (f.Title, Edits.Apply(text, f.Edits), f);
        }).ToList();
    }

    [Fact]
    public void Unknown_property_offers_the_closest_properties()
    {
        var fixes = Fixes($"<Button {Xmlns} Backgroun=\"Red\"/>", Codes.UnknownProperty);
        Assert.Equal("Change to 'Background'", fixes[0].Title);
        Assert.Equal($"<Button {Xmlns} Background=\"Red\"/>", fixes[0].Result);
        Assert.True(fixes[0].Fix.Preferred);
        Assert.InRange(fixes.Count, 1, 3);
    }

    [Fact]
    public void Unknown_binding_member_offers_members_of_the_view_model()
    {
        const string xaml = $"<UserControl {Xmlns} xmlns:vm=\"using:SampleApp.ViewModels\" x:DataType=\"vm:MainViewModel\"><TextBlock Text=\"{{Binding Titel}}\"/></UserControl>";
        var fixes = Fixes(xaml, Codes.UnknownBindingMember);
        Assert.Equal("Change to 'Title'", fixes[0].Title);
        Assert.Contains("{Binding Title}", fixes[0].Result);
    }

    [Fact]
    public void Unknown_resource_offers_close_keys()
    {
        var fixes = Fixes($"<Button {Xmlns} Background=\"{{StaticResource Acent}}\"/>", Codes.UnknownResource);
        Assert.Equal("Change to 'Accent'", fixes[0].Title);
        Assert.Contains("{StaticResource Accent}", fixes[0].Result);
    }

    [Fact]
    public void Unknown_markup_extension_offers_known_ones()
    {
        var fixes = Fixes($"<Button {Xmlns} Background=\"{{StaticResourc Accent}}\"/>", Codes.UnknownMarkupExtension);
        Assert.Equal("Change to 'StaticResource'", fixes[0].Title);
    }

    [Fact]
    public void Unknown_type_imports_its_namespace_on_the_root_and_prefixes_both_tags()
    {
        const string xaml = $"<UserControl {Xmlns}>\n  <Second></Second>\n</UserControl>";
        var fix = Fixes(xaml, Codes.UnknownType).Single(f => f.Title.StartsWith("Import", StringComparison.Ordinal));
        Assert.Equal("Import 'SampleApp.Views' as xmlns:views", fix.Title);
        Assert.Equal($"<UserControl {Xmlns} xmlns:views=\"using:SampleApp.Views\">\n  <views:Second></views:Second>\n</UserControl>", fix.Result);
    }

    [Fact]
    public void Unknown_type_with_a_declared_prefix_only_prefixes_the_name()
    {
        const string xaml = $"<UserControl {Xmlns} xmlns:v=\"using:SampleApp.Views\">\n  <Second/>\n</UserControl>";
        var fix = Fixes(xaml, Codes.UnknownType).First();
        Assert.Equal("Change to 'v:Second'", fix.Title);
        Assert.Contains("<v:Second/>", fix.Result);
        Assert.DoesNotContain("xmlns:views", fix.Result);
    }

    [Fact]
    public void Unknown_element_typo_offers_the_real_control()
    {
        var fixes = Fixes($"<StackPanel {Xmlns}><TextBlok/></StackPanel>", Codes.UnknownType);
        Assert.Contains(fixes, f => f.Title == "Change to 'TextBlock'" && f.Result.Contains("<TextBlock/>"));
    }

    [Fact]
    public void Missing_data_type_offers_the_matching_view_model()
    {
        const string xaml = $"<UserControl {Xmlns}\n             x:Class=\"SampleApp.Views.MainView\">\n  <TextBlock Text=\"{{Binding Title}}\"/>\n</UserControl>";
        var fixes = Fixes(xaml, Codes.MissingDataType);
        Assert.Equal("Add x:DataType=\"viewmodels:MainViewModel\"", fixes[0].Title);
        Assert.Contains("xmlns:viewmodels=\"using:SampleApp.ViewModels\"", fixes[0].Result);
        Assert.Contains("x:DataType=\"viewmodels:MainViewModel\">", fixes[0].Result);
        Assert.InRange(fixes.Count, 1, 5);
    }

    [Fact]
    public void Missing_data_type_reuses_an_existing_prefix()
    {
        const string xaml = $"<UserControl {Xmlns} xmlns:vm=\"using:SampleApp.ViewModels\" x:Class=\"SampleApp.Views.MainView\"><TextBlock Text=\"{{Binding Title}}\"/></UserControl>";
        var fix = Fixes(xaml, Codes.MissingDataType)[0];
        Assert.Equal("Add x:DataType=\"vm:MainViewModel\"", fix.Title);
        Assert.Contains("x:Class=\"SampleApp.Views.MainView\" x:DataType=\"vm:MainViewModel\">", fix.Result);
    }

    [Fact]
    public void Unknown_handler_is_created_in_the_code_behind()
    {
        const string xaml = $"<UserControl {Xmlns} x:Class=\"SampleApp.Views.MainView\"><Button Click=\"OnSave\"/></UserControl>";
        var fix = Fixes(xaml, Codes.UnknownHandler).Single();
        Assert.Equal("Create handler 'OnSave' in MainView.axaml.cs", fix.Title);
        Assert.Contains("    private void OnSave(object? sender, RoutedEventArgs e)\n    {\n    }\n}", fix.Result);
        Assert.DoesNotContain("using Avalonia.Interactivity;\nusing Avalonia.Interactivity;", fix.Result);
    }

    [Fact]
    public void Unknown_handler_adds_the_using_the_event_args_need()
    {
        const string xaml = $"<UserControl {Xmlns} x:Class=\"SampleApp.Views.Second\"><Button Click=\"OnSave\"/></UserControl>";
        var fix = Fixes(xaml, Codes.UnknownHandler).Single();
        Assert.StartsWith("using Avalonia.Controls;\nusing Avalonia.Interactivity;\n", fix.Result);
        Assert.Contains("private void OnSave(object? sender, RoutedEventArgs e)", fix.Result);
    }

    [Fact]
    public void Missing_file_offers_the_closest_file_in_the_same_folder()
    {
        var fix = Fixes($"<Image {Xmlns} Source=\"/Assets/logo.pnj\"/>", Codes.MissingAsset).Single();
        Assert.Equal("Change to '/Assets/logo.png'", fix.Title);
        Assert.Contains("Source=\"/Assets/logo.png\"", fix.Result);
    }
}
