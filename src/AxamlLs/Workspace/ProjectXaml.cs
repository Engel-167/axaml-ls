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
    private sealed record Entry(bool FromBuffer, DateTime Stamp, string Text, ProjectSnapshot? Project, XamlFile File);

    private readonly ConcurrentDictionary<string, Entry> _cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Gives an analyzer the same providers wherever it is created (open document or project scan), so they cannot drift apart.
    /// </summary>
    public static void Wire(XamlAnalyzer analyzer, string path, ProjectHost? host, Func<IReadOnlyDictionary<string, string>> openBuffers, Func<IReadOnlyList<XamlFile>>? projectFiles = null)
    {
        var root = host?.ProjectDirectory ?? System.IO.Path.GetDirectoryName(path)!;
        analyzer.ResourceProvider = () => ResourceIndex.ForDirectory(root, openBuffers());
        analyzer.StyleProvider = () => ClassIndex.ForDirectory(root, openBuffers());
        analyzer.DocumentPath = path;
        analyzer.ProjectFiles = projectFiles;
        analyzer.ProjectDirectory = host?.ProjectDirectory;
    }

    /// <param name="openBuffers">Unsaved editor contents by path; they win over the disk.</param>
    public IReadOnlyList<XamlFile> Files(ProjectHost host, IReadOnlyDictionary<string, string> openBuffers)
    {
        var project = host.Current;
        var paths = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var f in Directory.EnumerateFiles(host.ProjectDirectory, "*.axaml", SearchOption.AllDirectories))
            {
                if (!ProjectFiles.IsBuildOutput(f)) paths.Add(f);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Could not list the .axaml files of {host.ProjectDirectory}", ex);
        }
        foreach (var k in openBuffers.Keys)
        {
            if (k.EndsWith(".axaml", StringComparison.Ordinal) && k.StartsWith(host.ProjectDirectory, StringComparison.Ordinal)) paths.Add(k);
        }

        var result = new List<XamlFile>();
        foreach (var path in paths.Order(StringComparer.Ordinal))
        {
            try
            {
                if (File(path, project, host, openBuffers) is { } file) result.Add(file);
            }
            catch (IOException)
            {
            }
        }
        foreach (var stale in _cache.Keys.Where(k => !paths.Contains(k)).ToList()) _cache.TryRemove(stale, out _);
        return result;
    }

    private XamlFile? File(string path, ProjectSnapshot? project, ProjectHost host, IReadOnlyDictionary<string, string> openBuffers)
    {
        var fromBuffer = openBuffers.TryGetValue(path, out var text);
        DateTime stamp = default;
        if (!fromBuffer)
        {
            if (!System.IO.File.Exists(path)) return null;
            stamp = System.IO.File.GetLastWriteTimeUtc(path);
        }
        if (_cache.TryGetValue(path, out var e) && ReferenceEquals(e.Project, project) && e.FromBuffer == fromBuffer &&
            (fromBuffer ? string.Equals(e.Text, text, StringComparison.Ordinal) : e.Stamp == stamp))
            return e.File;

        text ??= System.IO.File.ReadAllText(path);
        var analyzer = XamlAnalyzer.Analyze(text, project, host.CompiledBindingsDefault);
        Wire(analyzer, path, host, () => openBuffers, () => Files(host, openBuffers));
        var file = new XamlFile(path, text, analyzer, new LineIndex(text));
        _cache[path] = new Entry(fromBuffer, stamp, fromBuffer ? text : "", project, file);
        return file;
    }
}
