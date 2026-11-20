using AxamlLs.Analysis;

namespace AxamlLs.Tests;

/// <summary>Markup extension argument hints (features plan F6).</summary>
[Collection("sample")]
public class SignatureHelpTests(SampleProjectFixture sample)
{
    private const string Header = """
        <UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     xmlns:vm="using:SampleApp.ViewModels" xmlns:loc="using:SampleApp.Localization" x:DataType="vm:MainViewModel">
        """;

    /// <summary>The active signature's label with the active parameter in [brackets], or null.</summary>
    private string? At(string body)
    {
        var offset = Header.Length + body.IndexOf('|');
        var analyzer = sample.Analyze(Header + body.Replace("|", "") + "\n</UserControl>");
        if (MarkupSignatures.At(analyzer, offset) is not { } help) return null;
        var signature = help.Signatures[help.ActiveSignature];
        var (label, spans) = signature.Render();
        if (help.ActiveParameter < 0) return label;
        var (start, end) = spans[help.ActiveParameter];
        return $"{label[..start]}[{label[start..end]}]{label[end..]}";
    }

    [Fact]
    public void A_binding_starts_at_its_path()
    {
        var shown = At("<TextBlock Text=\"{Binding |}\"/>");
        Assert.StartsWith("{Binding [Path], Path=…, Mode=…", shown);
    }

    [Fact]
    public void A_named_argument_is_the_active_parameter()
    {
        var shown = At("<TextBox Text=\"{Binding Title, Mode=|}\"/>")!;
        Assert.Contains("[Mode]=…", shown);
        Assert.StartsWith("{Binding Path,", shown); // the one-positional signature, since Title was given
        Assert.Contains("[Converter]=…", At("<TextBox Text=\"{Binding Title, Converter=|}\"/>"));
    }

    [Fact]
    public void A_resource_reference_names_its_key()
    {
        Assert.Equal("{StaticResource [ResourceKey]}", At("<Border Background=\"{StaticResource |}\"/>"));
        Assert.Equal("{x:Static [Member]}", At("<TextBlock Text=\"{x:Static |}\"/>"));
    }

    [Fact]
    public void A_custom_extension_shows_its_constructor_and_properties_with_docs()
    {
        var analyzer = sample.Analyze(Header + "<TextBlock Text=\"{loc:Localize hello, Fallback=x}\"/>\n</UserControl>");
        var text = analyzer.Result.Doc.Text;
        var help = MarkupSignatures.At(analyzer, text.IndexOf("hello", StringComparison.Ordinal) + 2)!;
        var signature = help.Signatures[help.ActiveSignature];
        Assert.Equal("{loc:Localize key, Key=…, Fallback=…}", signature.Render().Label);
        Assert.Equal(0, help.ActiveParameter);
        Assert.Equal("string", signature.Parameters[0].Type);
        Assert.Contains("Localize", signature.Doc);

        var onFallback = MarkupSignatures.At(analyzer, text.IndexOf("Fallback=x", StringComparison.Ordinal) + 9)!;
        Assert.Equal("Fallback", onFallback.Signatures[onFallback.ActiveSignature].Parameters[onFallback.ActiveParameter].Name);
    }

    [Fact]
    public void A_nested_extension_answers_for_itself()
    {
        Assert.Equal("{StaticResource [ResourceKey]}", At("<TextBlock Text=\"{Binding Title, Converter={StaticResource |}}\"/>"));
        Assert.Null(At("<TextBlock Text=\"plain|\"/>"));
        Assert.Null(At("<TextBlock Text=\"{Bind|ing Title}\"/>")); // on the name: completion's job
    }

    [Fact]
    public void Works_while_the_extension_is_still_being_typed()
    {
        Assert.Contains("[Mode]=…", At("<TextBox Text=\"{Binding Title, Mode=|\"/>"));
        Assert.Equal("{StaticResource [ResourceKey]}", At("<Border Background=\"{StaticResource |\"/>"));
    }
}

/// <summary>Signature help through the server: offsets as parameter labels, none outside markup.</summary>
public class SignatureHelpRequestTests
{
    [Fact]
    public async Task The_server_answers_with_label_offsets()
    {
        using var metrics = new AxamlLs.Telemetry.ServerMetrics();
        await using var client = new LspTestClient(metrics);
        var uri = AxamlLs.Lsp.Protocol.PathToUri(Path.Combine(Path.GetTempPath(), "axaml-ls-signature-" + Guid.NewGuid().ToString("N"), "View.axaml"));
        const string line = "<Border xmlns=\"https://github.com/avaloniaui\" Background=\"{StaticResource Accent}\"/>";
        await client.OpenAsync(uri, line);
        System.Text.Json.Nodes.JsonObject At(int character) => new()
        {
            ["textDocument"] = new System.Text.Json.Nodes.JsonObject { ["uri"] = uri },
            ["position"] = new System.Text.Json.Nodes.JsonObject { ["line"] = 0, ["character"] = character },
        };
        var result = (await client.RequestAsync("textDocument/signatureHelp", At(line.IndexOf("Accent", StringComparison.Ordinal) + 1)))["result"]!;
        var signature = result["signatures"]![0]!;
        var label = (string)signature["label"]!;
        var span = signature["parameters"]![0]!["label"]!.AsArray();
        Assert.Equal("ResourceKey", label[(int)span[0]!..(int)span[1]!]);
        Assert.Equal(0, (int)result["activeParameter"]!);
        Assert.Null((await client.RequestAsync("textDocument/signatureHelp", At(3)))["result"]);
    }
}
