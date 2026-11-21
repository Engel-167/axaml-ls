using AxamlLs.Workspace;

namespace AxamlLs.Tests;

/// <summary>Raises file changes when the test says so (no watcher, no waiting).</summary>
internal sealed class TestFileChangeSource(bool reliable = true) : IFileChangeSource
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
