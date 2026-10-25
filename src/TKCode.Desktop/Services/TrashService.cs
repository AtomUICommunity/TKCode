namespace TKCode.Desktop.Services;

/// <summary>Moves an item to the operating system's trash. Failure never falls back to permanent deletion.</summary>
public interface ITrashService
{
    Task TrashAsync(string path);

    /// <summary>Revalidates the caller's context at the last safe point before native trash starts.</summary>
    /// <remarks>
    /// Backends that queue work must override this and validate inside their worker.
    /// The callback must be worker-thread safe and throw to cancel the operation.
    /// </remarks>
    Task TrashAsync(string path, Action validateBeforeStart)
    {
        ArgumentNullException.ThrowIfNull(validateBeforeStart);
        validateBeforeStart();
        return TrashAsync(path);
    }
}

public static class TrashService
{
    public static ITrashService Create()
    {
        if (OperatingSystem.IsMacOS())
            return new MacTrashService();
        if (OperatingSystem.IsLinux())
            return new LinuxTrashService();
        if (OperatingSystem.IsWindows())
            return new WindowsTrashService();
        return new UnavailableTrashService();
    }

    internal static string GetFullPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Contains('\0'))
            throw new ArgumentException("A trash path cannot contain a NUL character.", nameof(path));
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (string.Equals(fullPath, Path.GetPathRoot(fullPath), StringComparison.OrdinalIgnoreCase))
            throw new IOException("A filesystem root cannot be moved to the trash.");
        // Do not resolve links or require File.Exists: broken links can themselves be trashed.
        return fullPath;
    }

    private sealed class UnavailableTrashService : ITrashService
    {
        public Task TrashAsync(string path) => Task.FromException(
            new PlatformNotSupportedException("Native trash is unavailable on this operating system."));
    }
}
