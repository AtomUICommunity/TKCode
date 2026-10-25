using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TKCode.Desktop.Services;

public sealed record ExplorerOperationContext(string Root, Func<bool> IsCurrent);

public interface IExplorerFileOperations
{
    Task<string> CreateAsync(ExplorerOperationContext context, string parent, string name, bool isDirectory);
    Task<string> RenameAsync(ExplorerOperationContext context, string path, string name, bool isDirectory);
    Task TrashAsync(ExplorerOperationContext context, string path, bool isDirectory);
}

/// <summary>A failed case-only rename whose original name could not be restored.</summary>
public sealed class ExplorerRenameException : IOException
{
    public string ActualPath { get; }

    internal ExplorerRenameException(string actualPath, Exception failure, Exception rollbackFailure)
        : base($"The rename failed and the original name could not be restored. The item is now at '{actualPath}'. " +
               $"{failure.Message} Rollback: {rollbackFailure.Message}", new AggregateException(failure, rollbackFailure))
    {
        ActualPath = actualPath;
    }
}

/// <summary>
/// Workspace-scoped, non-overwriting mutations. Links may be renamed/trashed, but must never
/// be traversed. Checks are repeated on the worker immediately before a mutation; they do not
/// claim to protect against another process replacing directories concurrently with a syscall.
/// </summary>
public sealed partial class ExplorerFileOperations : IExplorerFileOperations
{
    private readonly ITrashService _trash;
    private readonly Action<string, string> _move;

    public ExplorerFileOperations(ITrashService trash) : this(trash, MoveExclusive) { }

    internal ExplorerFileOperations(ITrashService trash, Action<string, string> move)
    {
        _trash = trash;
        _move = move;
    }

    public Task<string> CreateAsync(ExplorerOperationContext context, string parent, string name, bool isDirectory) => Task.Run(() =>
    {
        ValidateName(name);
        parent = ValidateParent(context, parent);
        var destination = Path.Combine(parent, name);
        EnsureCurrent(context);
        if (isDirectory)
        {
            CreateDirectoryExclusive(destination);
        }
        else
        {
            using var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        return destination;
    });

    public Task<string> RenameAsync(ExplorerOperationContext context, string path, string name, bool isDirectory) => Task.Run(() =>
    {
        ValidateName(name);
        path = ValidateLeaf(context, path, isDirectory);
        var parent = Path.GetDirectoryName(path)!;
        var destination = Path.Combine(parent, name);
        if (string.Equals(path, destination, StringComparison.Ordinal))
        {
            return path;
        }
        if (!string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase))
        {
            ValidateParent(context, parent);
            _move(path, destination);
            return destination;
        }

        // On a case-insensitive volume Exists(destination) also finds the source. Check the
        // actual directory entries instead, so case-distinct files on sensitive volumes survive.
        EnsureCurrent(context);
        if (Directory.EnumerateFileSystemEntries(parent).Any(entry =>
                string.Equals(Path.GetFileName(entry), name, StringComparison.Ordinal)))
        {
            throw new IOException($"An item named '{name}' already exists.");
        }
        var temporary = Path.Combine(parent, $".tkcode-rename-{Guid.NewGuid():N}");
        ValidateParent(context, parent);
        _move(path, temporary);
        try
        {
            ValidateParent(context, parent);
            _move(temporary, destination);
        }
        catch (Exception failure)
        {
            try
            {
                // Root switching must not prevent compensating for our first move. Still refuse
                // to follow a parent link, and never overwrite an item placed at the old name.
                ValidateParent(context with { IsCurrent = static () => true }, parent);
                _move(temporary, path);
            }
            catch (Exception rollbackFailure)
            {
                throw new ExplorerRenameException(temporary, failure, rollbackFailure);
            }
            throw;
        }
        return destination;
    });

    public Task TrashAsync(ExplorerOperationContext context, string path, bool isDirectory) => Task.Run(async () =>
    {
        path = ValidateLeaf(context, path, isDirectory);
        ValidateParent(context, Path.GetDirectoryName(path)!);
        await _trash.TrashAsync(path, () =>
        {
            // Native services can queue another worker (or an STA). Revalidate on that worker
            // immediately before native IO, not only before submitting the trash request.
            ValidateLeaf(context, path, isDirectory);
            ValidateParent(context, Path.GetDirectoryName(path)!);
        }).ConfigureAwait(false);
    });

    internal static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ') ||
            name.Any(c => c < ' ' || c is '/' or '\\' or '<' or '>' or ':' or '"' or '|' or '?' or '*'))
        {
            throw new ArgumentException("Enter a single file or folder name without separators or invalid characters.", nameof(name));
        }
        var stem = name.Split('.')[0].TrimEnd(' ');
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase) ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                                  stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
             (stem[3] is >= '1' and <= '9' or '¹' or '²' or '³')))
        {
            throw new ArgumentException("This name is reserved by Windows. Choose another name.", nameof(name));
        }
    }

    private static string ValidateLeaf(ExplorerOperationContext context, string path, bool isDirectory)
    {
        EnsureCurrent(context);
        path = AbsolutePath(path);
        var root = AbsolutePath(context.Root);
        EnsureInside(root, path, allowRoot: false);
        ValidateParent(context, Path.GetDirectoryName(path)!);
        var attributes = File.GetAttributes(path); // Includes dangling links; Exists does not.
        if ((attributes & FileAttributes.ReparsePoint) == 0 &&
            ((attributes & FileAttributes.Directory) != 0) != isDirectory)
        {
            throw new IOException("The item type has changed. Refresh the explorer and try again.");
        }
        EnsureCurrent(context);
        return path;
    }

    private static string ValidateParent(ExplorerOperationContext context, string parent)
    {
        EnsureCurrent(context);
        var root = AbsolutePath(context.Root);
        parent = AbsolutePath(parent);
        EnsureInside(root, parent, allowRoot: true);
        // Ancestors above the captured workspace root are intentionally not inspected: e.g.
        // macOS /var is normally a system symlink. The workspace root itself must not be a link.
        CheckDirectory(root);
        var relative = Path.GetRelativePath(root, parent);
        var current = root;
        if (relative != ".")
        {
            foreach (var component in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, component);
                CheckDirectory(current);
            }
        }
        EnsureCurrent(context);
        return parent;
    }

    private static void CheckDirectory(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"Cannot operate through the linked directory '{path}'.");
        }
        if ((attributes & FileAttributes.Directory) == 0)
        {
            throw new IOException($"'{path}' is not a directory.");
        }
    }

    private static string AbsolutePath(string path)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("Explorer operations require an absolute path.", nameof(path));
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static void EnsureInside(string root, string path, bool allowRoot)
    {
        // GetRelativePath uses case-insensitive comparison on macOS even on case-sensitive
        // volumes. Enforce an exact Unix root/prefix before calling it, or a case-distinct
        // sibling workspace could incorrectly be treated as an in-root path.
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var isRoot = string.Equals(root, path, comparison);
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if ((isRoot && !allowRoot) || (!isRoot && !path.StartsWith(prefix, comparison)))
        {
            throw new IOException("The item is outside the current workspace, or is the workspace root itself.");
        }
        var relative = Path.GetRelativePath(root, path);
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            throw new IOException("The item is outside the current workspace, or is the workspace root itself.");
        }
    }

    private static void EnsureCurrent(ExplorerOperationContext context)
    {
        if (!context.IsCurrent())
        {
            throw new InvalidOperationException("The workspace has changed. Try the operation again in the current workspace.");
        }
    }

    private static void CreateDirectoryExclusive(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!CreateDirectoryWindows(path, IntPtr.Zero))
            {
                ThrowNativeError("create directory", path);
            }
        }
        else if (Mkdir(path, 0x1FF) != 0) // 0777, reduced by the user's umask.
        {
            ThrowNativeError("create directory", path);
        }
    }

    private static void MoveExclusive(string source, string destination)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!MoveFileWindows(source, destination))
            {
                ThrowNativeError("rename", destination);
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            if (RenameMac(source, destination, 0x00000004) != 0) // RENAME_EXCL
            {
                ThrowNativeError("rename", destination);
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            if (RenameLinux(-100, source, -100, destination, 1) != 0) // AT_FDCWD, RENAME_NOREPLACE
            {
                ThrowNativeError("rename", destination);
            }
        }
        else
        {
            throw new PlatformNotSupportedException("Safe non-overwriting rename is unavailable on this platform.");
        }
    }

    private static void ThrowNativeError(string operation, string path)
    {
        var error = Marshal.GetLastPInvokeError();
        throw new IOException($"Could not {operation} '{path}': {new Win32Exception(error).Message}", new Win32Exception(error));
    }

    [LibraryImport("libc", EntryPoint = "mkdir", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Mkdir(string path, uint mode);

    [LibraryImport("libc", EntryPoint = "renamex_np", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int RenameMac(string source, string destination, uint flags);

    [LibraryImport("libc", EntryPoint = "renameat2", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int RenameLinux(int sourceDirectory, string source, int destinationDirectory, string destination, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateDirectoryW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateDirectoryWindows(string path, IntPtr securityAttributes);

    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveFileWindows(string source, string destination);
}
