using AxamlLs.Xaml;

namespace AxamlLs.Tests;

public class ParserTests
{
    [Fact]
    public void Xml_tolerates_unfinished_tags()
    {
        var doc = XmlDoc.Parse("<Grid><TextBlock Text=\"{Binding Fo\n<Button/></Grid>");
        Assert.Equal(["Grid", "TextBlock", "Button"], doc.AllElements.Select(e => e.Name));
        var attr = doc.AllElements[1].Attributes.Single();
        Assert.False(attr.ValueTerminated);
        Assert.Equal("{Binding Fo\n", attr.Value);
    }

    [Fact]
    public void Xml_decodes_entities_and_keeps_offsets()
    {
        const string text = "<A B=\"x &amp;&amp; y\"/>";
        var attr = XmlDoc.Parse(text).Root!.Attributes.Single();
        Assert.Equal("x && y", attr.Value);
        Assert.Equal(text.IndexOf('y'), attr.Map[attr.Value.IndexOf('y')]);
    }

    [Fact]
    public void Xml_namespaces_are_scoped()
    {
        var doc = XmlDoc.Parse("<A xmlns=\"d\" xmlns:vm=\"using:X\"><B xmlns:vm=\"using:Y\"/><C/></A>");
        Assert.Equal("using:Y", doc.AllElements[1].LookupNamespace("vm"));
        Assert.Equal("using:X", doc.AllElements[2].LookupNamespace("vm"));
        Assert.Equal("d", doc.AllElements[2].LookupNamespace(""));
    }

    [Fact]
    public void Markup_extension_with_nested_and_quoted_args()
    {
        const string value = "{Binding Price, StringFormat='{}{0:C}', RelativeSource={RelativeSource AncestorType=Window}}";
        var ext = MarkupExtension.Parse(new MappedText(value, [.. Enumerable.Range(100, value.Length + 1)]))!;
        Assert.Equal("Binding", ext.Name);
        Assert.Equal(100, ext.Start);
        Assert.Equal("Price", ext.Positional(0)!.Value!.Text);
        Assert.Equal("{}{0:C}", ext.Named("StringFormat")!.Value!.Text);
        var rs = ext.Named("RelativeSource")!.Nested!;
        Assert.Equal("Window", rs.Named("AncestorType")!.Value!.Text);
        Assert.True(ext.Closed);
        Assert.Equal(100 + value.IndexOf("Price", StringComparison.Ordinal), ext.Positional(0)!.Value!.Start);
    }

    [Fact]
    public void Markup_extension_reports_missing_brace()
    {
        var ext = MarkupExtension.Parse(new MappedText("{Binding Foo", [.. Enumerable.Range(0, 13)]))!;
        Assert.False(ext.Closed);
        Assert.Single(ext.Errors);
    }

    [Theory]
    [InlineData("Title", "Property:Title")]
    [InlineData("!IsBusy", "Not|Property:IsBusy")]
    [InlineData("#Header.Text", "Element:Header|Property:Text")]
    [InlineData("$parent[Window;2].DataContext", "Parent:$parent|Property:DataContext")]
    [InlineData("Items[0].Name", "Property:Items|Indexer:0|Property:Name")]
    [InlineData("(Grid.Row)", "Attached:Row")]
    [InlineData("Task^.Length", "Property:Task|Stream|Property:Length")]
    [InlineData("#w.((vm:Vm)DataContext).Title", "Element:w|Property:DataContext|Cast|Property:Title")]
    public void Binding_paths(string text, string expected)
    {
        var path = BindingPath.Parse(text);
        Assert.Empty(path.Errors);
        var actual = string.Join("|", path.Segments.Select(s => s.Name.Length > 0 && s.Kind is not PathSegmentKind.Cast ? $"{s.Kind}:{s.Name}" : s.Kind.ToString()));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Binding_path_parent_level()
    {
        var seg = BindingPath.Parse("$parent[vm:Window;2]").Segments.Single();
        Assert.Equal("vm:Window", seg.TypeName);
        Assert.Equal(2, seg.Level);
    }

    [Theory]
    [InlineData("Title..Length")]
    [InlineData("Title Name")]
    [InlineData("Items[0")]
    public void Binding_path_errors(string text) => Assert.NotEmpty(BindingPath.Parse(text).Errors);

    [Fact]
    public void Binding_path_trailing_dot_expects_member()
    {
        Assert.True(BindingPath.Parse("Selected.").EndsExpectingMember);
        Assert.False(BindingPath.Parse("Selected").EndsExpectingMember);
    }
}
