using AxamlLs.Analysis;
using AxamlLs.Lsp;
using AxamlLs.Workspace;

namespace AxamlLs.Tests;

/// <summary>Helpers for tests that look at the whole sample project (references, rename, quick fixes).</summary>
public sealed class ProjectTestContext(SampleProjectFixture sample)
{
    public IReadOnlyList<XamlFile> Files { get; } = new ProjectXaml().Files(sample.Host, new Dictionary<string, string>());

    public XamlFile File(string name) => Files.Single(f => f.Path.EndsWith(name, StringComparison.Ordinal));

    /// <summary>The target at <paramref name="anchor"/> plus <paramref name="delta"/> characters, in one of the sample files.</summary>
    public (TargetAt Found, XamlFile File) TargetAt(string file, string anchor, int delta = 0)
    {
        var f = File(file);
        var at = f.Text.IndexOf(anchor, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{anchor}' not found in {file}");
        var found = Targets.Find(f.Analyzer, at + delta);
        Assert.NotNull(found);
        return (found, f);
    }

    public async Task<List<Occurrence>> Occurrences(string file, string anchor, int delta = 0)
    {
        var (found, f) = TargetAt(file, anchor, delta);
        return await Targets.OccurrencesAsync(found.Target, f, Files, sample.Host);
    }

    /// <summary>"File.axaml:text" for each occurrence, to assert on.</summary>
    public string[] Describe(IEnumerable<Occurrence> occurrences) => occurrences.Select(o =>
    {
        var text = Files.FirstOrDefault(f => f.Path == o.Path)?.Text ?? System.IO.File.ReadAllText(o.Path);
        return $"{System.IO.Path.GetFileName(o.Path)}:{text[o.Start..o.End]}";
    }).ToArray();

    /// <summary>The text of each changed file after applying edits (nothing is written to disk).</summary>
    public Dictionary<string, string> Apply(IEnumerable<FileEdit> edits) =>
        edits.GroupBy(e => e.Path).ToDictionary(g => System.IO.Path.GetFileName(g.Key),
            g => Edits.Apply(Files.FirstOrDefault(f => f.Path == g.Key)?.Text ?? System.IO.File.ReadAllText(g.Key), g));
}
