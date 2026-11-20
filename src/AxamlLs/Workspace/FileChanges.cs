namespace AxamlLs.Workspace;

/// <summary>A change to a file below a project folder, from whichever source watches it.</summary>
public readonly record struct FileChange(string Path, FileChangeKind Kind);

public enum FileChangeKind
{
    Created,
    Changed,
    Deleted,
}

/// <summary>
/// Something that reports file changes below a folder: a <see cref="FileSystemWatcher"/>, the editor
/// (<c>workspace/didChangeWatchedFiles</c>, performance plan P1) or a test.
/// </summary>
public interface IFileChangeSource : IDisposable
{
    /// <summary>A batch of changes; a rename arrives as a delete and a create in the same batch.</summary>
    event Action<IReadOnlyList<FileChange>>? Changed;

    /// <summary>Changes may have been lost (watcher overflow): every cache must revalidate.</summary>
    event Action? Overflowed;

    /// <summary>
    /// False when changes can be missed at any time (no watcher could be created): users then trust what they cached for
    /// a short time only.
    /// </summary>
    bool Reliable { get; }
}

/// <summary>Changes below a folder from one recursive <see cref="FileSystemWatcher"/>.</summary>
public sealed class FileSystemChangeSource : IFileChangeSource
{
    private readonly FileSystemWatcher? _watcher;

    public FileSystemChangeSource(string root)
    {
        try
        {
            _watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            _watcher.Changed += (_, e) => Raise(new FileChange(e.FullPath, FileChangeKind.Changed));
            _watcher.Created += (_, e) => Raise(new FileChange(e.FullPath, FileChangeKind.Created));
            _watcher.Deleted += (_, e) => Raise(new FileChange(e.FullPath, FileChangeKind.Deleted));
            _watcher.Renamed += (_, e) => Raise(new FileChange(e.OldFullPath, FileChangeKind.Deleted), new FileChange(e.FullPath, FileChangeKind.Created));
            _watcher.Error += (_, e) =>
            {
                Log.Info($"File watcher for {root} lost events ({e.GetException().Message}); caches will revalidate");
                Overflowed?.Invoke();
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

    public event Action<IReadOnlyList<FileChange>>? Changed;

    public event Action? Overflowed;

    public bool Reliable => _watcher != null;

    private void Raise(params FileChange[] changes) => Changed?.Invoke(changes);

    public void Dispose() => _watcher?.Dispose();
}
