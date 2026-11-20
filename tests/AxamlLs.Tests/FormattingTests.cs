using System.Text.Json.Nodes;
using AxamlLs.Analysis;
using AxamlLs.Analysis.Formatting;
using AxamlLs.Lsp;
using AxamlLs.Telemetry;
using AxamlLs.Xaml;

namespace AxamlLs.Tests;

/// <summary>The whitespace-only formatter (features plan F1).</summary>
public class FormattingTests
{
    private static string Format(string text, FormattingSettings? settings = null, int tabSize = 4, bool spaces = true, int start = 0, int end = int.MaxValue)
    {
        Assert.Null(WellFormedness.Check(text));
        var changes = XamlLayout.Format(XmlDoc.Parse(text), new FormattingOptions(tabSize, spaces, settings ?? FormattingSettings.Default), start, end);
        return Apply(text, changes);
    }

    private static string Apply(string text, IReadOnlyList<TextChange> changes)
    {
        foreach (var c in changes.OrderByDescending(c => c.Start)) text = text[..c.Start] + c.NewText + text[c.End..];
        return text;
    }

    private static string Lines(params string[] lines) => string.Join("\n", lines);

    [Fact]
    public void Indents_children_and_puts_each_element_on_its_own_line()
    {
        Assert.Equal(
            Lines("<Grid>", "    <StackPanel>", "        <Button />", "        <Button />", "    </StackPanel>", "</Grid>"),
            Format("<Grid><StackPanel>\n<Button/>   <Button/></StackPanel>\n  </Grid>"));
    }

    [Fact]
    public void Tabs_and_tab_size_come_from_the_request()
    {
        Assert.Equal("<Grid>\n\t<Border />\n</Grid>", Format("<Grid><Border/></Grid>", spaces: false));
        Assert.Equal("<Grid>\n  <Border />\n</Grid>", Format("<Grid><Border/></Grid>", tabSize: 2));
    }

    [Fact]
    public void Attributes_break_when_the_tag_is_long_or_has_many_of_them()
    {
        // Three attributes fit on one line; whitespace inside the tag is normalized.
        Assert.Equal("<Button Content=\"Save\" Width=\"80\" Height=\"30\" />", Format("<Button   Content = \"Save\"\n Width=\"80\" Height=\"30\"/>"));
        // Four are more than the threshold: aligned under the first.
        Assert.Equal(
            Lines("<Button Content=\"Save\"", "        Width=\"80\"", "        Height=\"30\"", "        Margin=\"4\" />"),
            Format("<Button Content=\"Save\" Width=\"80\" Height=\"30\" Margin=\"4\"/>"));
        // Longer than the limit.
        Assert.Equal(
            Lines("<TextBlock Text=\"A long text value\"", "           Foreground=\"Red\" />"),
            Format("<TextBlock Text=\"A long text value\" Foreground=\"Red\"/>", FormattingSettings.Default with { MaxLineLength = 40 }));
    }

    [Fact]
    public void Attribute_layout_options()
    {
        const string tag = "<Button Content=\"Save\" Width=\"80\"/>";
        Assert.Equal(Lines("<Button Content=\"Save\"", "        Width=\"80\" />"), Format(tag, FormattingSettings.Default with { AttributesOnSeparateLines = AttributeLayout.Always }));
        Assert.Equal(
            "<Button Content=\"Save\" Width=\"80\" Height=\"30\" Margin=\"4\" />",
            Format("<Button Content=\"Save\"\n Width=\"80\"\n Height=\"30\"\n Margin=\"4\"/>", FormattingSettings.Default with { AttributesOnSeparateLines = AttributeLayout.Never }));
        Assert.Equal(
            Lines("<Button", "    Content=\"Save\"", "    Width=\"80\" />"),
            Format(tag, FormattingSettings.Default with { AttributesOnSeparateLines = AttributeLayout.Always, FirstAttributeOnSameLine = false }));
        Assert.Equal(
            Lines("<Button Content=\"Save\"", "    Width=\"80\" />"),
            Format(tag, FormattingSettings.Default with { AttributesOnSeparateLines = AttributeLayout.Always, IndentAttributesBy = AttributeIndent.Indent }));
        Assert.Equal("<Button Content=\"Save\" Width=\"80\"/>", Format(tag, FormattingSettings.Default with { SpaceBeforeSelfClosingSlash = false }));
        Assert.Equal(
            Lines("<Button Content=\"Save\" Width=\"80\" Height=\"30\" Margin=\"4\" />"),
            Format("<Button Content=\"Save\" Width=\"80\" Height=\"30\" Margin=\"4\"/>", FormattingSettings.Default with { AttributesOnSeparateLinesThreshold = 4 }));
    }

    [Fact]
    public void Blank_lines_between_elements_shrink_to_the_setting()
    {
        const string text = "<StackPanel>\n\n\n\n    <A />\n\n\n    <B />\n\n</StackPanel>";
        Assert.Equal("<StackPanel>\n    <A />\n\n    <B />\n</StackPanel>", Format(text));
        Assert.Equal("<StackPanel>\n    <A />\n\n\n    <B />\n</StackPanel>", Format(text, FormattingSettings.Default with { KeepBlankLines = 2 }));
        Assert.Equal("<StackPanel>\n    <A />\n    <B />\n</StackPanel>", Format(text, FormattingSettings.Default with { KeepBlankLines = 0 }));
    }

    [Fact]
    public void Property_elements_nest_like_any_other()
    {
        Assert.Equal(
            Lines("<Grid>", "    <Grid.RowDefinitions>", "        <RowDefinition Height=\"Auto\" />", "    </Grid.RowDefinitions>", "</Grid>"),
            Format("<Grid>\n<Grid.RowDefinitions><RowDefinition Height=\"Auto\"/></Grid.RowDefinitions></Grid>"));
    }

    [Fact]
    public void Multi_line_markup_extension_values_are_left_as_written()
    {
        const string value = "{Binding Title,\n                 Mode=TwoWay}";
        var formatted = Format($"<Grid><TextBox Text=\"{value}\" Width=\"5\"/></Grid>");
        Assert.Contains($"Text=\"{value}\"", formatted);
        Assert.Contains("Width=\"5\" />", formatted);
    }

    [Fact]
    public void Comments_keep_their_line_and_their_text()
    {
        const string text = "<StackPanel>\n<!-- #region Header -->\n<A/> <!-- trailing   note -->\n  <!--\n   multi\n     line\n  -->\n<B/>\n<!-- #endregion -->\n</StackPanel>";
        Assert.Equal(
            "<StackPanel>\n    <!-- #region Header -->\n    <A /> <!-- trailing   note -->\n    <!--\n   multi\n     line\n  -->\n    <B />\n    <!-- #endregion -->\n</StackPanel>",
            Format(text));
    }

    [Fact]
    public void Text_inline_and_preserved_content_is_untouched()
    {
        Assert.Equal("<Grid>\n    <TextBlock>Hello   world</TextBlock>\n</Grid>", Format("<Grid><TextBlock>Hello   world</TextBlock></Grid>"));
        const string runs = "<TextBlock><Run Text=\"a\"/><Run Text=\"b\"/></TextBlock>";
        Assert.Equal(runs, Format(runs)); // nothing inside an inline container moves, not even inside its tags
        const string preserved = "<Grid xml:space=\"preserve\">  <A/>   <B/> </Grid>";
        Assert.Equal(preserved, Format(preserved));
        Assert.Equal("<Grid></Grid>", Format("<Grid></Grid>"));
    }

    [Fact]
    public void Range_formatting_touches_only_the_range()
    {
        const string text = "<Grid>\n<A   X=\"1\"/>\n<B   Y=\"2\"/>\n</Grid>";
        var b = text.IndexOf("<B", StringComparison.Ordinal);
        Assert.Equal("<Grid>\n<A   X=\"1\"/>\n    <B Y=\"2\" />\n</Grid>", Format(text, start: b, end: text.IndexOf("</Grid>", StringComparison.Ordinal) - 1));
    }

    [Fact]
    public void Crlf_documents_stay_crlf()
    {
        Assert.Equal("<Grid>\r\n    <A />\r\n</Grid>", Format("<Grid>\r\n<A/></Grid>"));
    }

    public static TheoryData<string> RealFiles()
    {
        var data = new TheoryData<string>();
        var roots = new[] { SampleProjectFixture.SampleDir, Path.GetFullPath(Path.Combine(SampleProjectFixture.SampleDir, "..", "..", "..", "Orion_POS", "Orion_POS")) };
        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var f in Directory.EnumerateFiles(root, "*.axaml", SearchOption.AllDirectories).Where(f => !f.Contains("/bin/") && !f.Contains("/obj/")).Order())
                data.Add(f);
        }
        return data;
    }

    /// <summary>
    /// Properties over every .axaml in samples/ and the read-only Orion folder (read, formatted in memory, never
    /// written): only whitespace changes, formatting twice changes nothing more, the result is well formed, and its
    /// analysis reports the same problems.
    /// </summary>
    [Theory]
    [MemberData(nameof(RealFiles))]
    public void Formatting_a_real_file_only_moves_whitespace(string path)
    {
        var text = File.ReadAllText(path);
        if (WellFormedness.Check(text) != null) return; // never formatted
        foreach (var settings in new[] { FormattingSettings.Default, FormattingSettings.Default with { AttributesOnSeparateLines = AttributeLayout.Always, FirstAttributeOnSameLine = false } })
        {
            var changes = XamlLayout.Format(XmlDoc.Parse(text), new FormattingOptions(4, true, settings));
            Assert.All(changes, c => Assert.True(string.IsNullOrWhiteSpace(text[c.Start..c.End]) && string.IsNullOrWhiteSpace(c.NewText)));
            var once = Apply(text, changes);

            Assert.Equal(Strip(text), Strip(once));
            Assert.Null(WellFormedness.Check(once));
            Assert.Empty(XamlLayout.Format(XmlDoc.Parse(once), new FormattingOptions(4, true, settings)));

            var docBefore = XmlDoc.Parse(text);
            var docAfter = XmlDoc.Parse(once);
            Assert.Equal(
                docBefore.AllElements.Select(e => (e.Name, string.Join("|", e.Attributes.Select(a => a.Name + "=" + a.Value)))),
                docAfter.AllElements.Select(e => (e.Name, string.Join("|", e.Attributes.Select(a => a.Name + "=" + a.Value)))));
            Assert.Equal(Problems(text), Problems(once));
        }
    }

    private static string Strip(string s) => string.Concat(s.Where(c => !char.IsWhiteSpace(c)));

    private static string[] Problems(string text) =>
        [.. XamlAnalyzer.Analyze(text, project: null, compiledDefault: true).Result.Diagnostics.Select(d => $"{d.Code}: {d.Message}").Order()];
}

/// <summary>Formatting through the server: edits for a well-formed document, none for a broken one.</summary>
public class FormattingRequestTests
{
    [Fact]
    public async Task The_server_formats_well_formed_documents_only()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "axaml-ls-format-" + Guid.NewGuid().ToString("N")));
        using var metrics = new ServerMetrics();
        try
        {
            await using var client = new LspTestClient(metrics);
            var uri = Protocol.PathToUri(Path.Combine(dir.FullName, "View.axaml"));
            await client.OpenAsync(uri, "<Grid><Border/></Grid>");
            var parameters = new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = uri },
                ["options"] = new JsonObject { ["tabSize"] = 2, ["insertSpaces"] = true },
            };
            var edits = (await client.RequestAsync("textDocument/formatting", parameters))["result"]!.AsArray();
            Assert.Equal(["\n  ", " ", "\n"], edits.Select(e => (string?)e!["newText"]));

            await client.NotifyAsync("textDocument/didChange", new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = uri, ["version"] = 2 },
                ["contentChanges"] = new JsonArray(new JsonObject { ["text"] = "<Grid><Border></Grid>" }),
            });
            Assert.Empty((await client.RequestAsync("textDocument/formatting", parameters.DeepClone()))["result"]!.AsArray());
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
