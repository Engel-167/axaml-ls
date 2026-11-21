using AxamlLs.Analysis;
using AxamlLs.Lsp;
using AxamlLs.Workspace;
using AxamlLs.Xaml;

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

/// <summary>
/// Runs a refactoring on a view of the sample project and checks what every refactoring must keep (features plan F10):
/// the result is well formed and its analysis reports nothing new.
/// </summary>
internal sealed class RefactoringTestSupport(SampleProjectFixture sample)
{
    public const string Header = """
        <UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     xmlns:vm="using:SampleApp.ViewModels" x:DataType="vm:MainViewModel">
        """;

    private static readonly string ViewPath = Path.Combine(SampleProjectFixture.SampleDir, "Views", "Test.axaml");

    /// <summary>The file for a view's text; the cursor (and selection) is written as | (twice for a selection), and removed.</summary>
    public (XamlFile File, int Start, int End) Open(string text)
    {
        var start = text.IndexOf('|');
        var end = text.IndexOf('|', start + 1);
        var clean = text.Replace("|", "", StringComparison.Ordinal);
        var analyzer = sample.Analyze(clean, new ProjectContext(SampleProjectFixture.SampleDir, ViewPath, FileIndex: sample.Indexes.Files));
        return (new XamlFile(ViewPath, clean, analyzer, new AxamlLs.Text.LineIndex(clean)), start, end < 0 ? start : end - 1);
    }

    public string? Title(AxamlLs.Analysis.Refactorings.IRefactoring refactoring, string text)
    {
        var (file, start, end) = Open(text);
        return refactoring.TitleAt(file, start, end, AxamlLs.Analysis.Refactorings.RefactoringSettings.Default);
    }

    /// <summary>The text after the refactoring, checked: offered, edits only in this file, well formed, no new problems.</summary>
    public string Apply(AxamlLs.Analysis.Refactorings.IRefactoring refactoring, string text, AxamlLs.Analysis.Refactorings.RefactoringSettings? settings = null)
    {
        var (file, start, end) = Open(text);
        settings ??= AxamlLs.Analysis.Refactorings.RefactoringSettings.Default;
        Assert.NotNull(refactoring.TitleAt(file, start, end, settings));
        var edits = refactoring.Edits(file, start, end, settings);
        Assert.NotEmpty(edits);
        Assert.All(edits, e => Assert.Equal(file.Path, e.Path));
        var after = Edits.Apply(file.Text, edits);
        Assert.Null(WellFormedness.Check(after));
        var before = Problems(file.Analyzer);
        var now = Problems(sample.Analyze(after, file.Analyzer.Context));
        foreach (var p in before) now.Remove(p);
        Assert.Empty(now);
        return after;
    }

    private static List<string> Problems(XamlAnalyzer analyzer) => [.. analyzer.Result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")];
}
