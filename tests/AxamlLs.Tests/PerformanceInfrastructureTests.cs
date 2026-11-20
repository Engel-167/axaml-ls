using System.Text.Json.Nodes;
using AxamlLs.Analysis;
using AxamlLs.Lsp;
using AxamlLs.Workspace;

namespace AxamlLs.Tests;

/// <summary>The caches and indexes behind the server's speed: they must stay correct while they save work.</summary>
public class PerformanceInfrastructureTests
{
    private static JsonObject Change(int sl, int sc, int el, int ec, string text) => new()
    {
        ["range"] = new JsonObject
        {
            ["start"] = new JsonObject { ["line"] = sl, ["character"] = sc },
            ["end"] = new JsonObject { ["line"] = el, ["character"] = ec },
        },
        ["text"] = text,
    };

    [Fact]
    public void Incremental_changes_apply_in_order()
    {
        var text = "<Grid>\n  <Button/>\n</Grid>";
        var changes = new JsonArray(
            Change(1, 3, 1, 9, "TextBlock"),          // Button → TextBlock
            Change(1, 12, 1, 12, " Text=\"hi\""),      // insert after the new name
            Change(0, 0, 0, 0, "<!-- top -->\n"));    // insert a line above everything
        Assert.Equal("<!-- top -->\n<Grid>\n  <TextBlock Text=\"hi\"/>\n</Grid>", LspServer.ApplyChanges(text, changes));
    }

    [Fact]
    public void A_change_without_range_replaces_the_text()
    {
        var changes = new JsonArray(new JsonObject { ["text"] = "<A/>" }, Change(0, 1, 0, 2, "B"));
        Assert.Equal("<B/>", LspServer.ApplyChanges("<Grid/>", changes));
    }

    [Fact]
    public void Fuzzy_score_prefers_word_starts_and_runs()
    {
        Assert.Null(Assets.FuzzyScore("Assets/Icons/home.svg", "xyz"));
        Assert.NotNull(Assets.FuzzyScore("Assets/Icons/home.svg", "aih"));
        // "home" as a whole word beats the same letters scattered.
        var word = Assets.FuzzyScore("Icons/home.svg", "home")!.Value;
        var scattered = Assets.FuzzyScore("Icons/hxoxmxe.svg", "home")!.Value;
        Assert.True(word > scattered, $"{word} vs {scattered}");
        // Matching at a segment start beats the middle of a word.
        Assert.True(Assets.FuzzyScore("Icons/logo.png", "lo") > Assets.FuzzyScore("Icons/hello.png", "lo"));
    }

    [Fact]
    public void File_index_prunes_build_folders_and_sees_new_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "axaml-ls-index-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Views"));
        Directory.CreateDirectory(Path.Combine(root, "obj", "Debug"));
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        File.WriteAllText(Path.Combine(root, "Views", "A.axaml"), "<A/>");
        File.WriteAllText(Path.Combine(root, "obj", "Debug", "B.axaml"), "<B/>");
        File.WriteAllText(Path.Combine(root, ".git", "C.axaml"), "<C/>");
        var index = FileIndex.For(root);
        try
        {
            Assert.Equal([Path.Combine(root, "Views", "A.axaml")], index.WithExtensions(".axaml"));
            Assert.True(index.IsPruned(Path.Combine(root, "obj", "Debug", "B.axaml")));
            Assert.False(index.IsPruned(Path.Combine(root, "Views", "A.axaml")));

            var before = index.Generation;
            var added = Path.Combine(root, "Views", "D.axaml");
            File.WriteAllText(added, "<D/>");
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!index.Contains(added) && DateTime.UtcNow < deadline) Thread.Sleep(20);
            Assert.True(index.Contains(added));
            Assert.NotEqual(before, index.Generation);
            Assert.Contains(added, index.WithExtensions(".axaml"));
        }
        finally
        {
            index.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Lru_cache_evicts_least_recently_used_and_respects_weight()
    {
        var cache = new LruCache<string, string>(maxEntries: 2, maxWeight: 10);
        cache.Add("a", "1", 1);
        cache.Add("b", "2", 1);
        Assert.True(cache.TryGet("a", out _)); // a is now the most recent
        cache.Add("c", "3", 1);                // evicts b
        Assert.False(cache.TryGet("b", out _));
        Assert.True(cache.TryGet("a", out _));
        cache.Add("big", "x", 9);              // weight 1 + 9 = 10 keeps a? a(1)+c(1)+big(9) > 10 → oldest goes
        Assert.True(cache.TryGet("big", out _));
        Assert.False(cache.TryGet("c", out _));
    }
}
