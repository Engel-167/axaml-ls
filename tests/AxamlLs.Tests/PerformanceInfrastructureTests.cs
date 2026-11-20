using System.Text.Json.Nodes;
using AxamlLs.Analysis;
using AxamlLs.Lsp;
using AxamlLs.Workspace;
using Microsoft.Extensions.Time.Testing;

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
        Assert.Equal("<!-- top -->\n<Grid>\n  <TextBlock Text=\"hi\"/>\n</Grid>", Protocol.ApplyContentChanges(text, changes));
    }

    [Fact]
    public void A_change_without_range_replaces_the_text()
    {
        var changes = new JsonArray(new JsonObject { ["text"] = "<A/>" }, Change(0, 1, 0, 2, "B"));
        Assert.Equal("<B/>", Protocol.ApplyContentChanges("<Grid/>", changes));
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

    /// <summary>Raises file changes when the test says so (no watcher, no waiting).</summary>
    private sealed class TestFileChangeSource(bool reliable = true) : IFileChangeSource
    {
        public event Action<IReadOnlyList<FileChange>>? Changed;
        public event Action? Overflowed;
        public bool Reliable => reliable;
        public void Raise(params FileChange[] changes) => Changed?.Invoke(changes);
        public void Overflow() => Overflowed?.Invoke();
        public void Dispose()
        {
        }
    }

    /// <summary>A temp folder with Views/A.axaml, obj/Debug/B.axaml and .git/C.axaml.</summary>
    private static string MakeFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "axaml-ls-index-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Views"));
        Directory.CreateDirectory(Path.Combine(root, "obj", "Debug"));
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        File.WriteAllText(Path.Combine(root, "Views", "A.axaml"), "<A/>");
        File.WriteAllText(Path.Combine(root, "obj", "Debug", "B.axaml"), "<B/>");
        File.WriteAllText(Path.Combine(root, ".git", "C.axaml"), "<C/>");
        return root;
    }

    [Fact]
    public void File_index_prunes_build_folders_and_sees_new_files()
    {
        var root = MakeFolder();
        var changes = new TestFileChangeSource();
        using var index = new FileIndex(root, changes, new FakeTimeProvider());
        try
        {
            Assert.Equal([Path.Combine(root, "Views", "A.axaml")], index.WithExtensions(".axaml"));
            Assert.True(index.IsPruned(Path.Combine(root, "obj", "Debug", "B.axaml")));
            Assert.False(index.IsPruned(Path.Combine(root, "Views", "A.axaml")));

            var before = index.Generation;
            var added = Path.Combine(root, "Views", "D.axaml");
            File.WriteAllText(added, "<D/>");
            Assert.False(index.Contains(added)); // not reported yet: the listing is trusted
            changes.Raise(new FileChange(added, FileChangeKind.Created));
            Assert.True(index.Contains(added));
            Assert.NotEqual(before, index.Generation);
            Assert.Contains(added, index.WithExtensions(".axaml"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void File_index_ignores_changes_in_pruned_folders_and_forwards_the_rest()
    {
        var root = MakeFolder();
        var changes = new TestFileChangeSource();
        using var index = new FileIndex(root, changes, new FakeTimeProvider());
        var forwarded = new List<FileChange>();
        index.Changed += forwarded.AddRange;
        try
        {
            var before = index.Generation;
            changes.Raise(new FileChange(Path.Combine(root, "obj", "Debug", "B.axaml"), FileChangeKind.Changed));
            Assert.Equal(before, index.Generation);
            Assert.Empty(forwarded);

            var edited = new FileChange(Path.Combine(root, "Views", "A.axaml"), FileChangeKind.Changed);
            changes.Raise(edited);
            Assert.NotEqual(before, index.Generation);
            Assert.Equal([edited], forwarded);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void File_index_relists_after_an_overflow()
    {
        var root = MakeFolder();
        var changes = new TestFileChangeSource();
        using var index = new FileIndex(root, changes, new FakeTimeProvider());
        var overflowed = false;
        index.Overflowed += () => overflowed = true;
        try
        {
            Assert.Single(index.WithExtensions(".axaml"));
            var lost = Path.Combine(root, "Views", "Lost.axaml");
            File.WriteAllText(lost, "<Lost/>"); // its event was lost
            changes.Overflow();
            Assert.True(overflowed);
            Assert.True(index.Contains(lost));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Without_a_reliable_source_the_listing_is_trusted_for_one_second()
    {
        var root = MakeFolder();
        var time = new FakeTimeProvider();
        using var index = new FileIndex(root, new TestFileChangeSource(reliable: false), time);
        try
        {
            Assert.Single(index.WithExtensions(".axaml"));
            var added = Path.Combine(root, "Views", "New.axaml");
            File.WriteAllText(added, "<New/>");
            time.Advance(TimeSpan.FromMilliseconds(500));
            Assert.False(index.Contains(added));
            time.Advance(TimeSpan.FromMilliseconds(600));
            Assert.True(index.Contains(added));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task File_system_source_reports_a_new_file_and_a_rename_as_delete_plus_create()
    {
        var root = MakeFolder();
        using var source = new FileSystemChangeSource(root);
        Assert.True(source.Reliable);
        var seen = new List<FileChange>();
        var renamed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var to = Path.Combine(root, "Views", "Renamed.axaml");
        source.Changed += batch =>
        {
            lock (seen)
            {
                seen.AddRange(batch);
                if (seen.Any(c => c.Path == to && c.Kind == FileChangeKind.Created)) renamed.TrySetResult();
            }
        };
        try
        {
            var from = Path.Combine(root, "Views", "A.axaml");
            File.Move(from, to);
            await renamed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            lock (seen) Assert.Contains(new FileChange(from, FileChangeKind.Deleted), seen);
        }
        finally
        {
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
