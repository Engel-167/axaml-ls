using System.Collections.Concurrent;
using System.IO.Enumeration;

namespace AxamlLs.Workspace;

/// <summary>
/// The files of one project folder, listed once and then kept current by a single <see cref="FileSystemWatcher"/>
/// (the "virtual file system" approach of rust-analyzer and VS Code): every index, completion and diagnostic reads
/// this list instead of walking the disk again.
/// <list type="bullet">
/// <item>Build output, VCS and tool folders (<c>bin</c>, <c>obj</c>, <c>node_modules</c>, <c>.git</c>, any dot-folder)
/// are pruned during the walk, so they are never descended into (MSBuild's default globs skip the same ones).</item>
/// <item><see cref="Generation"/> changes whenever a file below the root is created, changed, deleted or renamed.
/// Caches keyed by it skip even the per-file timestamp checks while nothing changed.</item>
/// <item>When the watcher cannot be created (inotify limit) or overflows, the index falls back to treating its
/// contents as valid for one second at a time, so results are never stale for long.</item>
/// </list>
/// </summary>
public sealed class FileIndex : IDisposable
{
    private static readonly ConcurrentDictionary<string, FileIndex> Indexes = new(StringComparer.Ordinal);
    private static readonly HashSet<string> PrunedNames = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", "node_modules" };
    private const long FallbackTtlMs = 1000;

    private readonly object _gate = new();
    private readonly FileSystemWatcher? _watcher;
    private readonly ConcurrentDictionary<string, string[]> _byExtension = new(StringComparer.OrdinalIgnoreCase);
    private long _generation;
    private long _structureGeneration;
    private string[] _files = [];
    private HashSet<string> _set = new(StringComparer.Ordinal);
    private long _listedGeneration = -1;

    public string Root { get; }

    /// <summary>Raised (on a watcher thread) for each change to a file outside the pruned folders.</summary>
    public event Action<FileSystemEventArgs>? Changed;

    private FileIndex(string root)
    {
        Root = root;
        try
        {
            _watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            _watcher.Changed += (_, e) => OnEvent(e, structural: false);
            _watcher.Created += (_, e) => OnEvent(e, structural: true);
            _watcher.Deleted += (_, e) => OnEvent(e, structural: true);
            _watcher.Renamed += (_, e) =>
            {
                OnEvent(new FileSystemEventArgs(WatcherChangeTypes.Deleted, Path.GetDirectoryName(e.OldFullPath)!, Path.GetFileName(e.OldFullPath)), structural: true);
                OnEvent(e, structural: true);
            };
            _watcher.Error += (_, e) =>
            {
                // Overflow: events were lost, so nothing cached can be trusted.
                Log.Info($"File watcher for {root} lost events ({e.GetException().Message}); rescanning");
                Interlocked.Increment(ref _generation);
                Interlocked.Increment(ref _structureGeneration);
            };
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            Log.Error($"Cannot watch {root}; file lists will be refreshed every second instead", ex);
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    /// <summary>The index of a folder (created, listed lazily and watched on first use).</summary>
    public static FileIndex For(string root)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return Indexes.GetOrAdd(root, r => new FileIndex(r));
    }

    /// <summary>Changes whenever anything below the root changes. Without a watcher it changes every second.</summary>
    public long Generation => _watcher == null ? Volatile.Read(ref _generation) + Environment.TickCount64 / FallbackTtlMs : Volatile.Read(ref _generation);

    private long StructureGeneration => _watcher == null ? Environment.TickCount64 / FallbackTtlMs : Volatile.Read(ref _structureGeneration);

    /// <summary>Every file below the root, outside pruned folders, sorted ordinally.</summary>
    public IReadOnlyList<string> Files => Snapshot().Files;

    /// <summary>The files with one of the given extensions (".axaml", ".cs"), cached until the next structural change.</summary>
    public string[] WithExtensions(params string[] extensions)
    {
        var (files, generation) = Snapshot();
        var key = generation + "|" + string.Join('|', extensions);
        if (_byExtension.TryGetValue(key, out var hit)) return hit;
        var result = files.Where(f => extensions.Any(e => f.EndsWith(e, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (_byExtension.Count > 64) _byExtension.Clear();
        _byExtension[key] = result;
        return result;
    }

    /// <summary>Whether a file exists, answered from the index for paths below the root (no disk access).</summary>
    public bool Contains(string path) => IsUnderRoot(path) && !IsPruned(path) ? SnapshotSet().Contains(path) : File.Exists(path);

    /// <summary>True for paths inside a folder the index never lists.</summary>
    public bool IsPruned(string path)
    {
        if (!IsUnderRoot(path)) return false;
        var rel = path.AsSpan(Root.Length + 1);
        foreach (var range in rel.Split(Path.DirectorySeparatorChar))
        {
            var segment = rel[range];
            // The last segment is the file itself: only folders are pruned.
            if (range.End.GetOffset(rel.Length) == rel.Length) break;
            if (IsPrunedName(segment)) return true;
        }
        return false;
    }

    public static bool IsPrunedName(ReadOnlySpan<char> name)
    {
        if (name.Length == 0) return false;
        if (name[0] == '.') return true;
        foreach (var p in PrunedNames)
        {
            if (name.Equals(p, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private bool IsUnderRoot(string path) =>
        path.Length > Root.Length + 1 && path[Root.Length] == Path.DirectorySeparatorChar && path.StartsWith(Root, StringComparison.Ordinal);

    private (string[] Files, long Generation) Snapshot()
    {
        var generation = StructureGeneration;
        lock (_gate)
        {
            if (_listedGeneration != generation) List(generation);
            return (_files, _listedGeneration);
        }
    }

    private HashSet<string> SnapshotSet()
    {
        Snapshot();
        lock (_gate) return _set;
    }

    private void List(long generation)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
        };
        string[] files;
        try
        {
            files = new FileSystemEnumerable<string>(Root, static (ref FileSystemEntry e) => e.ToFullPath(), options)
            {
                ShouldIncludePredicate = static (ref FileSystemEntry e) => !e.IsDirectory,
                // Pruning here means bin/obj/.git are never opened, unlike filtering a full recursive listing afterwards.
                ShouldRecursePredicate = static (ref FileSystemEntry e) =>
                    !IsPrunedName(e.FileName) && (e.Attributes & FileAttributes.ReparsePoint) == 0,
            }.ToArray();
        }
        catch (Exception ex)
        {
            Log.Error($"Could not list {Root}", ex);
            files = [];
        }
        Array.Sort(files, StringComparer.Ordinal);
        _files = files;
        _set = new HashSet<string>(files, StringComparer.Ordinal);
        _listedGeneration = generation;
        Log.Debug($"Indexed {files.Length} files under {Root}");
    }

    private void OnEvent(FileSystemEventArgs e, bool structural)
    {
        if (IsPruned(e.FullPath)) return;
        Interlocked.Increment(ref _generation);
        if (structural) Interlocked.Increment(ref _structureGeneration);
        try
        {
            Changed?.Invoke(e);
        }
        catch (Exception ex)
        {
            Log.Error("File change handler failed", ex);
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        Indexes.TryRemove(new KeyValuePair<string, FileIndex>(Root, this));
    }
}
