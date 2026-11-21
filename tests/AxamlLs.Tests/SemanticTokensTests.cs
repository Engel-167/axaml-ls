using System.Text.Json.Nodes;
using AxamlLs.Analysis;
using AxamlLs.Lsp;
using AxamlLs.Telemetry;
using AxamlLs.Text;

namespace AxamlLs.Tests;

/// <summary>Range and delta semantic tokens (features plan F8).</summary>
public class SemanticTokensTests
{
    private const string Styles = """
        <Styles xmlns="https://github.com/avaloniaui">
          <Style Selector="Button.Primary:pointerover /template/ ContentPresenter#PART_Content">
            <Setter Property="Background" Value="Red"/>
          </Style>
          <Style Selector="TextBlock.Title, TextBlock.Subtitle">
            <Style Selector="^:disabled">
              <Setter Property="Opacity" Value="0.5"/>
            </Style>
          </Style>
          <Style Selector="ListBoxItem:selected > Border.Card">
            <Setter Property="Margin" Value="4"/>
          </Style>
        </Styles>
        """;

    private static int[] Full(string text) => SemanticTokens.Encode(XamlAnalyzer.Analyze(text, project: null, compiledDefault: true), new LineIndex(text));

    /// <summary>Line, column, length and type of each token: the encoding with the deltas undone.</summary>
    private static List<(int Line, int Char, int Length, int Type)> Decode(int[] data)
    {
        var tokens = new List<(int, int, int, int)>();
        var (line, ch) = (0, 0);
        for (var i = 0; i < data.Length; i += 5)
        {
            ch = data[i] == 0 ? ch + data[i + 1] : data[i + 1];
            line += data[i];
            tokens.Add((line, ch, data[i + 2], data[i + 3]));
        }
        return tokens;
    }

    [Fact]
    public void A_range_answers_the_slice_of_the_full_tokens()
    {
        var full = Decode(Full(Styles));
        Assert.True(full.Count > 10, $"{full.Count} tokens");
        var lines = new LineIndex(Styles);
        var analyzer = XamlAnalyzer.Analyze(Styles, project: null, compiledDefault: true);
        foreach (var (fromLine, toLine) in new[] { (0, 2), (4, 7), (9, 10), (0, 99) })
        {
            var start = lines.GetOffset(fromLine, 0);
            var end = lines.GetOffset(Math.Min(toLine, Styles.Count(c => c == '\n')), 0);
            var expected = full.Where(t => t.Line >= fromLine && t.Line < toLine).ToList();
            if (toLine == 99) expected = full.Where(t => t.Line >= fromLine && t.Line < Styles.Count(c => c == '\n')).ToList();
            Assert.Equal(expected, Decode(SemanticTokens.Encode(analyzer, lines, start, end)));
        }
    }

    /// <summary>200 random one-character edits of a styles file (fixed seed): the delta applied to the old array is the new full array.</summary>
    [Fact]
    public void Applying_the_delta_gives_the_new_full_tokens()
    {
        var random = new Random(20261120);
        const string alphabet = "abcXYZ.:#^> /-\n\"";
        var text = Styles;
        var old = Full(text);
        for (var i = 0; i < 200; i++)
        {
            var at = random.Next(text.Length);
            text = random.Next(3) switch
            {
                0 => text.Remove(at, 1),
                1 => text.Insert(at, alphabet[random.Next(alphabet.Length)].ToString()),
                _ => text.Remove(at, 1).Insert(at, alphabet[random.Next(alphabet.Length)].ToString()),
            };
            var now = Full(text);
            var edits = SemanticTokens.Diff(old, now);
            Assert.Equal(now, SemanticTokens.Apply(old, edits));
            Assert.All(edits, e => Assert.True(e.Start % 5 == 0 && e.DeleteCount % 5 == 0 && e.Data.Length % 5 == 0));
            if (old.AsSpan().SequenceEqual(now)) Assert.Empty(edits);
            old = now;
        }
    }

    [Fact]
    public async Task The_server_answers_a_delta_against_its_last_result()
    {
        using var metrics = new ServerMetrics();
        await using var client = new LspTestClient(metrics);
        var uri = Protocol.PathToUri(Path.Combine(Path.GetTempPath(), "axaml-ls-tokens-" + Guid.NewGuid().ToString("N"), "Styles.axaml"));
        await client.OpenAsync(uri, Styles);
        var doc = new JsonObject { ["uri"] = uri };
        var first = (await client.RequestAsync("textDocument/semanticTokens/full", new JsonObject { ["textDocument"] = doc.DeepClone() }))["result"]!;
        var oldData = first["data"]!.AsArray().Select(n => (int)n!).ToArray();

        var changed = Styles.Replace("Border.Card", "Border.Card.Big", StringComparison.Ordinal);
        await client.NotifyAsync("textDocument/didChange", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri, ["version"] = 2 },
            ["contentChanges"] = new JsonArray(new JsonObject { ["text"] = changed }),
        });
        var delta = (await client.RequestAsync("textDocument/semanticTokens/full/delta",
            new JsonObject { ["textDocument"] = doc.DeepClone(), ["previousResultId"] = (string?)first["resultId"] }))["result"]!;
        Assert.NotEqual((string?)first["resultId"], (string?)delta["resultId"]);
        var edits = delta["edits"]!.AsArray()
            .Select(e => new TokenEdit((int)e!["start"]!, (int)e["deleteCount"]!, [.. e["data"]!.AsArray().Select(n => (int)n!)])).ToList();
        Assert.Single(edits);

        // An unknown previous result gets the full array, which the delta must reproduce. (The server marks unused
        // classes with their own type, so its answer is the oracle, not Full.)
        var unknown = (await client.RequestAsync("textDocument/semanticTokens/full/delta",
            new JsonObject { ["textDocument"] = doc.DeepClone(), ["previousResultId"] = "nope" }))["result"]!;
        var fullNow = unknown["data"]!.AsArray().Select(n => (int)n!).ToArray();
        Assert.Equal(Full(changed).Length, fullNow.Length);
        Assert.Equal(fullNow, SemanticTokens.Apply(oldData, edits));
    }
}
