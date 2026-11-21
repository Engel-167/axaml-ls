using System.Collections.Concurrent;
using AxamlLs.Analysis;
using AxamlLs.Text;

namespace AxamlLs.Workspace;

/// <summary>One analyzed .axaml file of the project.</summary>
public sealed record XamlFile(string Path, string Text, XamlAnalyzer Analyzer, LineIndex Lines);

/// <summary>
/// Every .axaml file of a project, analyzed (not only the open ones): find-references and rename need to see them all.
/// Results are cached per file, keyed by the text (open buffers) or the disk timestamp, and by the project snapshot,
/// so a second call with nothing changed returns the same analyzers.
/// </summary>
public sealed class ProjectXaml
{
    private sealed record Entry(bool FromBuffer, DateTime Stamp, long Generation, string Text, ProjectSnapshot? Project, XamlFile File);

    private readonly ConcurrentDictionary<string, Entry> _cache = new(StringComparer.Ordinal);

    /// <param name="host">The project whose .axaml files are listed.</param>
    /// <param name="openBuffers">Unsaved editor contents by path; they win over the disk.</param>
    /// <param name="mentioning">When given, only files whose words may include one of these names are analysed and
    /// returned (see <see cref="ProjectIndexes.MayMention"/>); the others are not even read when their words are cached.</param>
    /// <param name="ct">Checked before each file: a cancelled search stops without analysing the rest.</param>
    public IReadOnlyList<XamlFile> Files(ProjectHost host, IReadOnlyDictionary<string, string> openBuffers, IReadOnlyCollection<string>? mentioning = null,
        CancellationToken ct = default)
    {
        var project = host.Current;
        var index = host.Indexes.Files;
        var generation = index.Generation;
        var paths = new HashSet<string>(index.WithExtensions(".axaml"), StringComparer.Ordinal);
        foreach (var k in openBuffers.Keys)
        {
            if (k.EndsWith(".axaml", StringComparison.Ordinal) && k.StartsWith(host.ProjectDirectory, StringComparison.Ordinal)) paths.Add(k);
        }

        var result = new List<XamlFile>();
        foreach (var path in paths.Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (mentioning != null && !host.Indexes.MayMention(path, openBuffers, mentioning)) continue;
            try
            {
                if (File(path, project, host, openBuffers, generation) is { } file) result.Add(file);
            }
            catch (IOException)
            {
            }
        }
        foreach (var stale in _cache.Keys.Where(k => !paths.Contains(k)).ToList()) _cache.TryRemove(stale, out _);
        return result;
    }

    private XamlFile? File(string path, ProjectSnapshot? project, ProjectHost host, IReadOnlyDictionary<string, string> openBuffers, long generation)
    {
        var fromBuffer = openBuffers.TryGetValue(path, out var text);
        _cache.TryGetValue(path, out var previous);
        var e = previous != null && ReferenceEquals(previous.Project, project) && previous.FromBuffer == fromBuffer ? previous : null;
        DateTime stamp = default;
        if (fromBuffer)
        {
            if (e != null && string.Equals(e.Text, text, StringComparison.Ordinal)) return e.File;
        }
        else
        {
            // Nothing changed on disk since this entry was checked: no need to look at the file at all.
            if (e != null && e.Generation == generation) return e.File;
            if (!System.IO.File.Exists(path)) return null;
            stamp = System.IO.File.GetLastWriteTimeUtc(path);
            if (e != null && e.Stamp == stamp)
            {
                _cache[path] = e with { Generation = generation };
                return e.File;
            }
        }

        // Analyzed against another project snapshot but the text is the same: keep the text (no re-read) and its XML check.
        var sameText = previous != null && previous.FromBuffer == fromBuffer
            && (fromBuffer ? string.Equals(previous.File.Text, text, StringComparison.Ordinal) : previous.Stamp == stamp);
        if (sameText) text = previous!.File.Text;
        text ??= System.IO.File.ReadAllText(path);
        var context = ProjectContext.For(path, host, host.Indexes, () => openBuffers, () => Files(host, openBuffers));
        var analyzer = XamlAnalyzer.Analyze(text, project, host.CompiledBindingsDefault, context, wellFormed: sameText ? previous!.File.Analyzer.WellFormed : null);
        var file = new XamlFile(path, text, analyzer, analyzer.Lines);
        _cache[path] = new Entry(fromBuffer, stamp, generation, fromBuffer ? text : "", project, file);
        return file;
    }
}
