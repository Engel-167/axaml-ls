using System.Collections.Concurrent;
using System.IO.Enumeration;

namespace AxamlLs.Workspace;

/// <summary>
/// The files of one project folder (owned by its <see cref="ProjectIndexes"/>), listed once and then kept current by an <see cref="IFileChangeSource"/> (a
/// <see cref="FileSystemWatcher"/> in the server: the "virtual file system" approach of rust-analyzer and VS Code):
/// every index, completion and diagnostic reads this list instead of walking the disk again.
/// <list type="bullet">
/// <item>Build output, VCS and tool folders (<c>bin</c>, <c>obj</c>, <c>node_modules</c>, <c>.git</c>, any dot-folder)
/// are pruned during the walk, so they are never descended into (MSBuild's default globs skip the same ones).</item>
/// <item><see cref="Generation"/> changes whenever a file below the root is created, changed, deleted or renamed.
/// Caches keyed by it skip even the per-file timestamp checks while nothing changed.</item>
/// <item>When the change source is not reliable (no watcher: inotify limit), the index treats its contents as valid
/// for one second at a time, so results are never stale for long. When it overflows, everything is relisted.</item>
/// </list>
/// </summary>
public sealed class FileIndex : IDisposable
{
    private static readonly HashSet<string> PrunedNames = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", "node_modules" };
    private const long FallbackTtlMs = 1000;

    private readonly object _gate = new();
    private readonly IFileChangeSource _changes;
    private readonly TimeProvider _time;
    private readonly long _created;
    private readonly ConcurrentDictionary<string, string[]> _byExtension = new(StringComparer.OrdinalIgnoreCase);
    private long _generation;
    private long _fallbackTick = -1;
    private long _structureGeneration;
    private string[] _files = [];
    private HashSet<string> _set = new(StringComparer.Ordinal);
    private long _listedGeneration = -1;

    public string Root { get; }

    /// <summary>Raised (on the change source's thread) with the changes to files outside the pruned folders.</summary>
    public event Action<IReadOnlyList<FileChange>>? Changed;

    /// <summary>Raised when changes may have been lost; listeners must revalidate what they derived from files.</summary>
    public event Action? Overflowed;

    /// <param name="root">The folder, as a full path without a trailing separator.</param>
    /// <param name="changes">Reports changes below <paramref name="root"/>; the index disposes it.</param>
    /// <param name="time">The clock for the fallback refresh when <paramref name="changes"/> is not reliable.</param>
    internal FileIndex(string root, IFileChangeSource changes, TimeProvider time)
    {
        Root = root;
        _changes = changes;
        _time = time;
        _created = time.GetTimestamp();
        changes.Changed += OnChanges;
        changes.Overflowed += OnOverflow;
    }

    /// <summary>Changes whenever anything below the root changes. Without a reliable change source it changes every second.</summary>
    public long Generation
    {
        get
        {
            AdvanceIfUnreliable();
            return Volatile.Read(ref _generation);
        }
    }

    private long StructureGeneration
    {
        get
        {
            AdvanceIfUnreliable();
            return Volatile.Read(ref _structureGeneration);
        }
    }

    /// <summary>
    /// While changes can be missed, both generations move on once per second. They only ever increase, so a source
    /// that becomes reliable later (the editor's registration answered) can never bring back an old generation that a
    /// cache entry still holds.
    /// </summary>
    private void AdvanceIfUnreliable()
    {
        if (_changes.Reliable) return;
        var tick = (long)_time.GetElapsedTime(_created).TotalMilliseconds / FallbackTtlMs;
        if (Interlocked.Exchange(ref _fallbackTick, tick) == tick) return;
        Interlocked.Increment(ref _generation);
        Interlocked.Increment(ref _structureGeneration);
    }

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

    private void OnChanges(IReadOnlyList<FileChange> changes)
    {
        var relevant = changes.Where(c => !IsPruned(c.Path)).ToList();
        if (relevant.Count == 0) return;
        Interlocked.Increment(ref _generation);
        if (relevant.Any(c => c.Kind != FileChangeKind.Changed)) Interlocked.Increment(ref _structureGeneration);
        try
        {
            Changed?.Invoke(relevant);
        }
        catch (Exception ex)
        {
            Log.Error("File change handler failed", ex);
        }
    }

    private void OnOverflow()
    {
        // Events were lost, so nothing cached can be trusted.
        Interlocked.Increment(ref _generation);
        Interlocked.Increment(ref _structureGeneration);
        try
        {
            Overflowed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("File overflow handler failed", ex);
        }
    }

    public void Dispose()
    {
        _changes.Changed -= OnChanges;
        _changes.Overflowed -= OnOverflow;
        _changes.Dispose();
    }
}
