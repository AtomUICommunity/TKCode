namespace TKCode.Desktop.Services;

internal sealed class WindowsTrashService : ITrashService
{
    public Task TrashAsync(string path)
    {
        _ = TrashService.GetFullPath(path);
        // Do not queue any deletion or claim success while the recycle-only contract is unverified.
        return Task.FromException(WindowsTrashInterop.CreateUnavailableException());
    }
}
