using System.Diagnostics;

namespace TKCode.Desktop.Services;

public interface IExplorerPlatformActions
{
    Task ShowAsync(string path, bool isDirectory);
    Task OpenDirectoryAsync(string path);
}

/// <summary>Launches file managers, never a command shell or the default application for a file.</summary>
public sealed class ExplorerPlatformActions : IExplorerPlatformActions
{
    private readonly ExplorerPlatform _platform;
    private readonly Func<ProcessStartInfo, Task> _launch;

    public ExplorerPlatformActions() : this(CurrentPlatform(), LaunchAsync) { }

    internal ExplorerPlatformActions(ExplorerPlatform platform, Func<ProcessStartInfo, Task> launch)
    {
        _platform = platform;
        _launch = launch;
    }

    public Task ShowAsync(string path, bool isDirectory) => Task.Run(async () =>
    {
        path = ValidatePath(path, isDirectory);
        var start = NewStart(_platform switch
        {
            ExplorerPlatform.MacOS => "open",
            ExplorerPlatform.Windows => "explorer.exe",
            ExplorerPlatform.Linux => "xdg-open",
            _ => throw new PlatformNotSupportedException("File manager actions are unavailable on this platform.")
        });
        if (_platform == ExplorerPlatform.MacOS)
        {
            start.ArgumentList.Add("-R");
            start.ArgumentList.Add("--");
            start.ArgumentList.Add(path);
        }
        else if (_platform == ExplorerPlatform.Windows)
        {
            start.ArgumentList.Add("/select,");
            start.ArgumentList.Add(path);
        }
        else
        {
            // xdg-open has no standard select/reveal switch. Open the containing directory,
            // including when revealing a directory, rather than executing/opening the item.
            start.ArgumentList.Add(Path.GetDirectoryName(path) ?? path);
        }
        await _launch(start).ConfigureAwait(false);
    });

    public Task OpenDirectoryAsync(string path) => Task.Run(async () =>
    {
        path = ValidatePath(path, isDirectory: true, requireAccessibleDirectory: true);
        var start = NewStart(_platform switch
        {
            ExplorerPlatform.MacOS => "open",
            ExplorerPlatform.Windows => "explorer.exe",
            ExplorerPlatform.Linux => "xdg-open",
            _ => throw new PlatformNotSupportedException("File manager actions are unavailable on this platform.")
        });
        if (_platform == ExplorerPlatform.MacOS)
        {
            start.ArgumentList.Add("--");
        }
        start.ArgumentList.Add(path);
        await _launch(start).ConfigureAwait(false);
    });

    private static ExplorerPlatform CurrentPlatform()
    {
        if (OperatingSystem.IsMacOS())
        {
            return ExplorerPlatform.MacOS;
        }
        if (OperatingSystem.IsWindows())
        {
            return ExplorerPlatform.Windows;
        }
        if (OperatingSystem.IsLinux())
        {
            return ExplorerPlatform.Linux;
        }
        throw new PlatformNotSupportedException("File manager actions are unavailable on this platform.");
    }

    private static string ValidatePath(string path, bool isDirectory, bool requireAccessibleDirectory = false)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("File manager actions require an absolute path.", nameof(path));
        }
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var attributes = File.GetAttributes(path);
        if (((attributes & FileAttributes.Directory) != 0) != isDirectory &&
            (attributes & FileAttributes.ReparsePoint) == 0)
        {
            throw new IOException("The item type has changed. Refresh the explorer and try again.");
        }
        // Revealing a dangling link is fine, but the directory-opening action must never
        // invoke the default application for a linked file or an inaccessible target.
        if (requireAccessibleDirectory && !Directory.Exists(path))
        {
            throw new IOException($"'{path}' is not an accessible directory.");
        }
        return path;
    }

    private ProcessStartInfo NewStart(string executable) => new(executable)
    {
        UseShellExecute = false,
        RedirectStandardError = _platform != ExplorerPlatform.Windows,
        RedirectStandardOutput = _platform != ExplorerPlatform.Windows,
        CreateNoWindow = true
    };

    private static async Task LaunchAsync(ProcessStartInfo start)
    {
        using var process = Process.Start(start) ?? throw new IOException($"Could not start '{start.FileName}'.");
        // Explorer may keep the newly launched process alive for the file-manager window.
        // Starting it is the complete request; do not block the UI operation until it closes.
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        var detail = await error.ConfigureAwait(false);
        await output.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new IOException($"'{start.FileName}' failed (exit {process.ExitCode}). {detail.Trim()}");
        }
    }
}

internal enum ExplorerPlatform
{
    MacOS,
    Windows,
    Linux
}
