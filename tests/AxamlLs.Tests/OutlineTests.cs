using AxamlLs.Analysis;

namespace AxamlLs.Tests;

[Collection("sample")]
public class OutlineTests(SampleProjectFixture sample)
{
    private const string Xaml = """
        <UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
                     xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006" mc:Ignorable="d"
                     x:Class="SampleApp.Views.MainView">
          <UserControl.Resources>
            <SolidColorBrush x:Key="AccentBrush" Color="#FF6600"/>
          </UserControl.Resources>
          <UserControl.Styles>
            <Style Selector="Button.Actions:pointerover">
              <Setter Property="Background" Value="Red"/>
            </Style>
            <ControlTheme x:Key="{x:Type Button}" TargetType="Button"/>
          </UserControl.Styles>
          <Grid>
            <Grid.ColumnDefinitions>
              <ColumnDefinition Width="*"/>
            </Grid.ColumnDefinitions>
            <Button x:Name="SaveButton" Classes="accent big" Classes.wide="{Binding IsBusy}"/>
            <TextBlock d:DesignWidth="10"/>
            <d:Button/>
          </Grid>
        </UserControl>
        """;

    private static string Shape(IEnumerable<OutlineSymbol> symbols, int depth = 0) =>
        string.Concat(symbols.Select(s => $"{new string(' ', depth * 2)}{s.Name} [{s.Kind}]\n{Shape(s.Children, depth + 1)}"));

    [Fact]
    public void Tree_has_names_kinds_and_nesting_with_property_elements_flattened()
    {
        var outline = Outline.Build(sample.Analyze(Xaml));
        Assert.Equal("""
            UserControl MainView [5]
              SolidColorBrush [AccentBrush] [14]
              Style Button.Actions:pointerover [3]
                Setter Background [7]
              ControlTheme [Button] [3]
              Grid [19]
                ColumnDefinition [19]
                Button #SaveButton .accent .big .wide [8]
                TextBlock [19]

            """.Replace("\r\n", "\n"), Shape(outline));
    }

    [Fact]
    public void Symbols_have_a_detail_and_a_selection_range_on_the_tag_name()
    {
        var button = Outline.Build(sample.Analyze(Xaml))[0].Children.Single(c => c.Name == "Grid").Children.Single(c => c.Name.StartsWith("Button", StringComparison.Ordinal));
        Assert.Equal("Avalonia.Controls.Button", button.Detail);
        Assert.Equal("Button", Xaml[button.SelectionStart..button.SelectionEnd]);
        Assert.StartsWith("<Button", Xaml[button.Start..button.End]);
        Assert.True(button.Start <= button.SelectionStart && button.SelectionEnd <= button.End);
    }

    [Fact]
    public void Works_on_unfinished_documents()
    {
        var outline = Outline.Build(sample.Analyze("<UserControl xmlns=\"https://github.com/avaloniaui\"><Grid><Button Name=\"A\""));
        Assert.Equal("UserControl", outline[0].Name);
        Assert.NotEmpty(outline[0].Children);
    }
}
