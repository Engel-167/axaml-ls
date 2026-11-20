using System.Collections.Concurrent;
using AxamlLs.Telemetry;

namespace AxamlLs.Workspace;

/// <summary>
/// What a scanner found in each file of a project, cached:
/// <list type="bullet">
/// <item>a file on disk is re-read only when its timestamp changed, and its timestamp is only checked after the
/// <see cref="FileIndex"/> saw something change (so a quiet project costs no I/O at all);</item>
/// <item>an open buffer is re-scanned only when its text changed;</item>
/// <item>a re-scan that finds the same facts keeps the previous object, so callers can merge results and compare them
/// by reference.</item>
/// </list>
/// </summary>
/// <param name="scan">Reads a file's facts from its path and text.</param>
/// <param name="same">Whether two scans found the same facts (then the older object is kept).</param>
/// <param name="metrics">Counts the files actually read and scanned (cache misses), when given.</param>
internal sealed class FileFactsCache<T>(Func<string, string, T> scan, Func<T, T, bool> same, ServerMetrics? metrics = null) where T : class
{
    private sealed record DiskEntry(DateTime Stamp, long Generation, T Facts);

    private sealed record BufferEntry(string Text, T Facts);

    private readonly ConcurrentDictionary<string, DiskEntry> _disk = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, BufferEntry> _buffers = new(StringComparer.Ordinal);

    /// <summary>The facts of every listed file, then of open buffers below the index's root that it does not list yet.</summary>
    public T[] Collect(FileIndex index, string[] files, IReadOnlyDictionary<string, string> openBuffers, params string[] bufferExtensions)
    {
        var generation = index.Generation;
        var result = new List<T>(files.Length + 4);
        var scanned = 0;
        foreach (var file in files)
        {
            if (Get(file, openBuffers, generation, ref scanned) is { } facts) result.Add(facts);
        }
        var prefix = index.Root + Path.DirectorySeparatorChar;
        foreach (var path in openBuffers.Keys)
        {
            if (!path.StartsWith(prefix, StringComparison.Ordinal) || !bufferExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase))) continue;
            if (Array.BinarySearch(files, path, StringComparer.Ordinal) >= 0) continue; // already collected
            if (Get(path, openBuffers, generation, ref scanned) is { } facts) result.Add(facts);
        }
        metrics?.FilesScanned(scanned);
        if (_buffers.Count > openBuffers.Count)
        {
            foreach (var closed in _buffers.Keys.Where(k => !openBuffers.ContainsKey(k)).ToList()) _buffers.TryRemove(closed, out _);
        }
        return [.. result];
    }

    /// <summary>Whether two collections hold the same fact objects in the same order (no deep comparison).</summary>
    public static bool SameObjects(T[] a, T[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
        {
            if (!ReferenceEquals(a[i], b[i])) return false;
        }
        return true;
    }

    private T? Get(string path, IReadOnlyDictionary<string, string> openBuffers, long generation, ref int scanned)
    {
        if (openBuffers.TryGetValue(path, out var text))
        {
            _buffers.TryGetValue(path, out var cached);
            // string.Equals checks the reference first: an unchanged buffer is the same string object.
            if (cached != null && string.Equals(cached.Text, text, StringComparison.Ordinal)) return cached.Facts;
            var fresh = scan(path, text);
            scanned++;
            if (cached != null && same(cached.Facts, fresh)) fresh = cached.Facts;
            _buffers[path] = new BufferEntry(text, fresh);
            return fresh;
        }

        _disk.TryGetValue(path, out var entry);
        if (entry != null && entry.Generation == generation) return entry.Facts;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                _disk.TryRemove(path, out _);
                return null;
            }
            var stamp = info.LastWriteTimeUtc;
            if (entry != null && entry.Stamp == stamp)
            {
                _disk[path] = entry with { Generation = generation };
                return entry.Facts;
            }
            var facts = scan(path, File.ReadAllText(path));
            scanned++;
            if (entry != null && same(entry.Facts, facts)) facts = entry.Facts;
            _disk[path] = new DiskEntry(stamp, generation, facts);
            return facts;
        }
        catch (IOException)
        {
            return entry?.Facts;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
