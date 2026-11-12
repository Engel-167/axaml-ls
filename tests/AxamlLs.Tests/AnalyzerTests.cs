using System.Text.RegularExpressions;
using AxamlLs.Analysis;
using AxamlLs.Text;

namespace AxamlLs.Tests;

[Collection("sample")]
public class AnalyzerTests(SampleProjectFixture sample)
{
    [Fact]
    public void Valid_view_has_no_diagnostics()
    {
        // MainView.axaml compiles with Avalonia's own XAML compiler.
        var result = sample.AnalyzeFile("Views/MainView.axaml").Result;
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Broken_view_reports_exactly_the_expected_diagnostics()
    {
        var text = File.ReadAllText(Path.Combine(SampleProjectFixture.SampleDir, "Views/Broken.axaml"));
        var lines = text.Split('\n');
        var expected = new List<(int Line, string Code)>();
        for (var i = 0; i < lines.Length; i++)
        {
            var m = Regex.Match(lines[i], @"expect (AXN\d{4})");
            if (m.Success) expected.Add((i + 1, m.Groups[1].Value)); // the line after the comment (0-based i + 1)
        }

        var index = new LineIndex(text);
        var actual = sample.Analyze(text).Result.Diagnostics.Select(d => (index.GetPosition(d.Start).Line, d.Code)).OrderBy(x => x).ToList();
        Assert.Equal(expected.OrderBy(x => x).ToList(), actual);
    }

    [Fact]
    public void Generated_properties_resolve_and_navigate_to_the_field()
    {
        const string text = """
            <UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         xmlns:vm="using:SampleApp.ViewModels" x:DataType="vm:MainViewModel">
              <TextBlock Text="{Binding Title}"/>
            </UserControl>
            """;
        var analyzer = sample.Analyze(text);
        Assert.Empty(analyzer.Result.Diagnostics);
        var offset = text.IndexOf("Title}", StringComparison.Ordinal) + 2;
        var hover = Features.Hover(analyzer, offset);
        Assert.NotNull(hover);
        Assert.Contains("string MainViewModel.Title", hover.Markdown);
        var def = Assert.Single(Features.Definition(analyzer, offset));
        Assert.EndsWith("MainViewModel.cs", def.FilePath);
    }

    [Fact]
    public void A_bound_DataContext_sets_the_data_type_of_the_element_and_its_children()
    {
        const string text = """
            <UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         xmlns:vm="using:SampleApp.ViewModels" x:DataType="vm:MainViewModel">
              <StackPanel DataContext="{Binding Selected}" Tag="{Binding Name}">
                <TextBlock Text="{Binding Price}"/>
                <TextBlock Text="{Binding Title}"/>
              </StackPanel>
            </UserControl>
            """;
        var d = Assert.Single(sample.Analyze(text).Result.Diagnostics);
        Assert.Equal(Codes.UnknownBindingMember, d.Code);
        Assert.Equal(text.IndexOf("Title}", StringComparison.Ordinal), d.Start);
    }

    [Fact]
    public void An_object_DataContext_sets_the_data_type_and_an_unknown_one_checks_nothing()
    {
        const string text = """
            <UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         xmlns:vm="using:SampleApp.ViewModels" x:DataType="vm:MainViewModel">
              <StackPanel>
                <StackPanel>
                  <StackPanel.DataContext><vm:ItemViewModel/></StackPanel.DataContext>
                  <TextBlock Text="{Binding Name}"/>
                </StackPanel>
                <StackPanel DataContext="{Binding Nope}" Tag="{Binding Anything}">
                  <TextBlock Text="{Binding Whatever}"/>
                </StackPanel>
              </StackPanel>
            </UserControl>
            """;
        var d = Assert.Single(sample.Analyze(text).Result.Diagnostics);
        Assert.Equal(text.IndexOf("Nope}", StringComparison.Ordinal), d.Start);
    }

    [Fact]
    public void Reflection_bindings_are_not_checked_but_compiled_ones_are()
    {
        const string text = """
            <UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         xmlns:vm="using:SampleApp.ViewModels" x:DataType="vm:MainViewModel">
              <StackPanel>
                <TextBlock Text="{ReflectionBinding Nope}"/>
                <TextBlock Text="{CompiledBinding Nope}"/>
                <TextBlock x:CompileBindings="False" Text="{Binding Nope}"/>
              </StackPanel>
            </UserControl>
            """;
        var d = Assert.Single(sample.Analyze(text).Result.Diagnostics);
        Assert.Equal(Codes.UnknownBindingMember, d.Code);
        Assert.Equal(text.IndexOf("Nope}\"/>\n    <TextBlock x:Compile", StringComparison.Ordinal), d.Start);
    }

    [Fact]
    public void Works_without_a_project_for_xml_and_datatype_errors()
    {
        const string text = "<UserControl xmlns=\"https://github.com/avaloniaui\"><TextBlock Text=\"{Binding Title}\"/></UserControl";
        var diagnostics = XamlAnalyzer.Analyze(text, project: null, compiledDefault: true).Result.Diagnostics;
        Assert.Contains(diagnostics, d => d.Code == Codes.XmlError);
        Assert.Contains(diagnostics, d => d.Code == Codes.MissingDataType);
    }
}
