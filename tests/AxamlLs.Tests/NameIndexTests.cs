using AxamlLs.Analysis;
using AxamlLs.Workspace;

namespace AxamlLs.Tests;

/// <summary>The word pre-filter for references and rename (performance plan P3).</summary>
[Collection("sample")]
public class NameIndexTests(SampleProjectFixture sample)
{
    private static readonly Dictionary<string, string> NoBuffers = [];

    [Fact]
    public void Words_include_dotted_and_hyphenated_parts_and_names_inside_markup()
    {
        var words = FileWords.Scan("""
            <Grid Grid.Row="1" Background="{StaticResource AccentBrush}">
              <Style Selector="Button.accent-big:pointerover /template/ ContentPresenter#PART_Border"/>
              <TextBlock Text="{Binding Path=Item.Title}" x:Name="Header" Classes="btn-primary,wide"/>
            </Grid>
            """);
        foreach (var w in (string[])["Grid.Row", "Grid", "Row", "AccentBrush", "StaticResource", "Button.accent-big", "Button", "accent-big", "accent", "big",
                     "pointerover", "PART_Border", "Item.Title", "Item", "Title", "Header", "btn-primary", "btn", "primary", "wide", "1"])
            Assert.True(words.Words.Contains(w), w);
        Assert.False(words.Anything);
        Assert.True(words.MayMention(["Title"]));
        Assert.False(words.MayMention(["Subtitle", "Footer"]));
    }

    [Fact]
    public void Names_that_are_not_one_word_and_character_references_are_never_filtered_out()
    {
        var words = FileWords.Scan("<Grid/>");
        Assert.True(words.MayMention(["My Key"]));
        Assert.True(words.MayMention(["sys:Key"]));
        Assert.False(words.MayMention(["Accent"]));
        // &#65;ccent is "Accent": the file can mention any name.
        Assert.True(FileWords.Scan("""<Border Background="{StaticResource &#65;ccent}"/>""").MayMention(["Accent"]));
    }

    [Fact]
    public void Only_files_that_may_mention_a_name_are_analysed()
    {
        var all = new ProjectXaml().Files(sample.Host, NoBuffers);
        var filtered = new ProjectXaml().Files(sample.Host, NoBuffers, ["Card"]);
        Assert.Equal(
            all.Where(f => FileWords.Scan(f.Text).Words.Contains("Card")).Select(f => f.Path),
            filtered.Select(f => f.Path));
        Assert.NotEmpty(filtered);
        Assert.True(filtered.Count < all.Count);
        Assert.Empty(new ProjectXaml().Files(sample.Host, NoBuffers, []));
    }

    /// <summary>
    /// Differential: for every target in the sample project (each position where a word starts), the occurrences
    /// found in the filtered files equal those found in all files.
    /// </summary>
    [Fact]
    public void Filtered_search_finds_exactly_what_the_full_search_finds()
    {
        var all = new ProjectXaml().Files(sample.Host, NoBuffers);
        var filteredXaml = new ProjectXaml();
        var seen = new HashSet<(string, string)>();
        var kinds = new Dictionary<string, int>();
        foreach (var origin in all)
        {
            var text = origin.Text;
            for (var offset = 0; offset < text.Length; offset++)
            {
                if (offset > 0 && (char.IsLetterOrDigit(text[offset - 1]) || text[offset - 1] == '_')) continue;
                if (Targets.Find(origin.Analyzer, offset) is not { } found) continue;
                var key = found.Target is SymbolTarget s ? $"symbol {s.Symbol.ToDisplayString()}" : found.Target.ToString()!;
                if (!seen.Add((origin.Path, key))) continue;
                kinds[found.Target.GetType().Name] = kinds.GetValueOrDefault(found.Target.GetType().Name) + 1;

                var expected = Targets.TextOccurrences(found.Target, origin, all);
                var candidates = filteredXaml.Files(sample.Host, NoBuffers, Targets.CandidateNames(found, text));
                var actual = Targets.TextOccurrences(found.Target, origin, candidates);
                Assert.True(expected.SequenceEqual(actual),
                    $"{key} from {Path.GetFileName(origin.Path)}@{offset}: expected {expected.Count} occurrences, found {actual.Count} " +
                    $"(candidates {string.Join(", ", Targets.CandidateNames(found, text))})");
            }
        }
        // Every kind of target was exercised.
        Assert.All((string[])[nameof(ElementNameTarget), nameof(ResourceKeyTarget), nameof(StyleClassTarget), nameof(SymbolTarget)],
            kind => Assert.True(kinds.GetValueOrDefault(kind) > 0, $"no {kind} in the samples"));
    }

    [Fact]
    public void Code_literals_are_cached_per_file()
    {
        using var indexes = new ProjectIndexes(SampleProjectFixture.SampleDir);
        var first = indexes.CodeLiterals(NoBuffers);
        Assert.Contains(first, f => f.Classes.Any(c => c.Name == "Card"));
        Assert.Contains(first, f => f.ResourceKeys.Any(k => k.Name == "FromCode"));
        var again = indexes.CodeLiterals(NoBuffers);
        Assert.True(first.Zip(again).All(p => ReferenceEquals(p.First, p.Second)));
    }
}
