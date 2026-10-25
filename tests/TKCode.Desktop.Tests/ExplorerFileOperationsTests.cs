using System.Diagnostics;
using TKCode.Desktop.Services;
using Xunit;

namespace TKCode.Desktop.Tests;

public sealed class ExplorerFileOperationsTests
{
    [Fact]
    public async Task Create_CreatesSingleFileAndDirectory_AndNeverOverwrites()
    {
        using var workspace = new Workspace();
        var file = await workspace.Operations.CreateAsync(workspace.Context, workspace.Root, "hello.txt", false);
        Assert.Equal(Path.Combine(workspace.Root, "hello.txt"), file);
        Assert.Equal(string.Empty, await File.ReadAllTextAsync(file));
        await File.WriteAllTextAsync(file, "keep me");
        await Assert.ThrowsAsync<IOException>(() => workspace.Operations.CreateAsync(workspace.Context, workspace.Root, "hello.txt", false));
        await Assert.ThrowsAsync<IOException>(() => workspace.Operations.CreateAsync(workspace.Context, workspace.Root, "hello.txt", true));
        Assert.Equal("keep me", await File.ReadAllTextAsync(file));

        var directory = await workspace.Operations.CreateAsync(workspace.Context, workspace.Root, "new folder", true);
        Assert.True(Directory.Exists(directory));
        await Assert.ThrowsAsync<IOException>(() => workspace.Operations.CreateAsync(workspace.Context, workspace.Root, "new folder", true));
        await Assert.ThrowsAsync<IOException>(() => workspace.Operations.CreateAsync(workspace.Context, workspace.Root, "new folder", false));
    }

    [Fact]
    public async Task CreateDirectory_IsExclusiveEvenForConcurrentRequests()
    {
        using var workspace = new Workspace();
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ =>
        {
            try
            {
                await workspace.Operations.CreateAsync(workspace.Context, workspace.Root, "winner", true);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
        }));
        Assert.Equal(1, results.Count(success => success));
        Assert.True(Directory.Exists(Path.Combine(workspace.Root, "winner")));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("/rooted")]
    [InlineData("C:drive")]
    [InlineData("C:\\rooted")]
    [InlineData("bad\0name")]
    [InlineData("bad\nname")]
    [InlineData("bad<name")]
    [InlineData("bad>name")]
    [InlineData("bad\"name")]
    [InlineData("bad|name")]
    [InlineData("bad?name")]
    [InlineData("bad*name")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("PRN.log")]
    [InlineData("AUX")]
    [InlineData("COM1.txt")]
    [InlineData("lpt9")]
    [InlineData("COM¹")]
    [InlineData("LPT².txt")]
    [InlineData("CONIN$")]
    [InlineData("CONOUT$")]
    [InlineData("CON .txt")]
    public async Task Names_AreSingleComponentsAndWindowsSafe_OnEveryPlatform(string name)
    {
        using var workspace = new Workspace();
        var source = workspace.File("source.txt");
        await Assert.ThrowsAnyAsync<ArgumentException>(() => workspace.Operations.CreateAsync(workspace.Context, workspace.Root, name, false));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => workspace.Operations.RenameAsync(workspace.Context, source, name, false));
        Assert.Equal([source], Directory.EnumerateFileSystemEntries(workspace.Root));
    }

    [Theory]
    [InlineData(".env")]
    [InlineData("hello world.txt")]
    [InlineData("你好.cs")]
    [InlineData("COM10.txt")]
    [InlineData("null.txt")]
    [InlineData("semi;dollar$'bracket[1].txt")]
    public async Task Names_PreserveValidComponents(string name)
    {
        using var workspace = new Workspace();
        var path = await workspace.Operations.CreateAsync(workspace.Context, workspace.Root, name, false);
        Assert.Equal(name, Path.GetFileName(path));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Bounds_RejectOutsideSiblingPrefixTraversalAndRootMutation()
    {
        using var workspace = new Workspace();
        var outside = Path.Combine(workspace.Base, "workspace-sibling");
        Directory.CreateDirectory(outside);
        var outsideFile = Path.Combine(outside, "source.txt");
        await File.WriteAllTextAsync(outsideFile, "outside");
        var operations = workspace.Operations;
        foreach (var parent in new[] { outside, Path.Combine(workspace.Root, "..", "workspace-sibling") })
        {
            await Assert.ThrowsAsync<IOException>(() => operations.CreateAsync(workspace.Context, parent, "bad.txt", false));
        }
        await Assert.ThrowsAsync<IOException>(() => operations.RenameAsync(workspace.Context, outsideFile, "renamed.txt", false));
        await Assert.ThrowsAsync<IOException>(() => operations.TrashAsync(workspace.Context, outsideFile, false));
        await Assert.ThrowsAsync<IOException>(() => operations.RenameAsync(workspace.Context, workspace.Root, "other", true));
        await Assert.ThrowsAsync<IOException>(() => operations.TrashAsync(workspace.Context, workspace.Root, true));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => operations.CreateAsync(workspace.Context, ".", "bad.txt", false));
        Assert.Equal("outside", await File.ReadAllTextAsync(outsideFile));
        Assert.Empty(workspace.Trash.Paths);
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.Root));
    }

    [Fact]
    public async Task Bounds_RequireExactUnixRootCasing_EvenOnCaseInsensitiveVolumes()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var workspace = new Workspace();
        var source = workspace.File("source.txt", "keep");
        var differentlyCasedRoot = Path.Combine(workspace.Base, "WORKSPACE");
        var differentlyCasedSource = Path.Combine(differentlyCasedRoot, "source.txt");
        await Assert.ThrowsAsync<IOException>(() => workspace.Operations.CreateAsync(workspace.Context, differentlyCasedRoot, "bad.txt", false));
        await Assert.ThrowsAsync<IOException>(() => workspace.Operations.RenameAsync(workspace.Context, differentlyCasedSource, "bad.txt", false));
        await Assert.ThrowsAsync<IOException>(() => workspace.Operations.TrashAsync(workspace.Context, differentlyCasedSource, false));
        Assert.Equal("keep", await File.ReadAllTextAsync(source));
        Assert.Equal([source], Directory.EnumerateFileSystemEntries(workspace.Root));
        Assert.Empty(workspace.Trash.Paths);
    }

    [Fact]
    public async Task Bounds_RejectCaseDistinctSiblingRoots_WhenVolumeSupportsThem()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var workspace = new Workspace();
        var sibling = Path.Combine(workspace.Base, "WORKSPACE");
        Directory.CreateDirectory(sibling);
        var entries = Directory.EnumerateDirectories(workspace.Base).Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
        if (!entries.Contains("workspace") || !entries.Contains("WORKSPACE"))
        {
            return; // macOS can host both case-sensitive and case-insensitive volumes.
        }
        var siblingSource = Path.Combine(sibling, "source.txt");
        await File.WriteAllTextAsync(siblingSource, "outside");
        await Assert.ThrowsAsync<IOException>(() => workspace.Operations.CreateAsync(workspace.Context, sibling, "bad.txt", false));
        await Assert.ThrowsAsync<IOException>(() => workspace.Operations.RenameAsync(workspace.Context, siblingSource, "bad.txt", false));
        await Assert.ThrowsAsync<IOException>(() => workspace.Operations.TrashAsync(workspace.Context, siblingSource, false));
        await Assert.ThrowsAsync<IOException>(() => workspace.Operations.TrashAsync(workspace.Context, sibling, true));
        Assert.Equal("outside", await File.ReadAllTextAsync(siblingSource));
        Assert.Equal([siblingSource], Directory.EnumerateFileSystemEntries(sibling));
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.Root));
        Assert.Empty(workspace.Trash.Paths);
    }

    [Fact]
    public async Task StaleContext_StopsAllOperations_AndChecksAgainImmediatelyBeforeCreation()
    {
        using var workspace = new Workspace();
        var source = workspace.File("source.txt");
        var stale = workspace.Context with { IsCurrent = static () => false };
        await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.Operations.CreateAsync(stale, workspace.Root, "new.txt", false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.Operations.RenameAsync(stale, source, "new.txt", false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.Operations.TrashAsync(stale, source, false));
        var checks = 0;
        var switchesDuringValidation = workspace.Context with { IsCurrent = () => Interlocked.Increment(ref checks) < 3 };
        await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.Operations.CreateAsync(switchesDuringValidation, workspace.Root, "new.txt", false));
        Assert.False(File.Exists(Path.Combine(workspace.Root, "new.txt")));
        Assert.True(File.Exists(source));
        Assert.Empty(workspace.Trash.Paths);
    }

    [Fact]
    public async Task Rename_PreservesContents_AndNeverOverwritesFilesOrDirectories()
    {
        using var workspace = new Workspace();
        var source = workspace.File("source.txt", "source contents");
        var occupied = workspace.File("occupied.txt", "existing contents");
        await Assert.ThrowsAsync<IOException>(() => workspace.Operations.RenameAsync(workspace.Context, source, "occupied.txt", false));
        Assert.Equal("source contents", await File.ReadAllTextAsync(source));
        Assert.Equal("existing contents", await File.ReadAllTextAsync(occupied));
        var renamed = await workspace.Operations.RenameAsync(workspace.Context, source, "renamed.txt", false);
        Assert.False(File.Exists(source));
        Assert.Equal("source contents", await File.ReadAllTextAsync(renamed));

        var sourceDirectory = workspace.Directory("source-directory");
        var occupiedDirectory = workspace.Directory("occupied-directory");
        workspace.File("source-directory/child.txt");
        await Assert.ThrowsAsync<IOException>(() => workspace.Operations.RenameAsync(workspace.Context, sourceDirectory, "occupied-directory", true));
        Assert.True(Directory.Exists(occupiedDirectory));
        Assert.True(File.Exists(Path.Combine(sourceDirectory, "child.txt")));
        var renamedDirectory = await workspace.Operations.RenameAsync(workspace.Context, sourceDirectory, "renamed-directory", true);
        Assert.True(File.Exists(Path.Combine(renamedDirectory, "child.txt")));
        Assert.Equal(renamedDirectory, await workspace.Operations.RenameAsync(workspace.Context, renamedDirectory, "renamed-directory", true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaseOnlyRename_WorksAndLeavesNoTemporaryEntry(bool isDirectory)
    {
        using var workspace = new Workspace();
        var source = isDirectory ? workspace.Directory("Name") : workspace.File("Name", "contents");
        var destination = await workspace.Operations.RenameAsync(workspace.Context, source, "NAME", isDirectory);
        Assert.Equal(Path.Combine(workspace.Root, "NAME"), destination);
        Assert.Equal(["NAME"], Directory.EnumerateFileSystemEntries(workspace.Root).Select(Path.GetFileName));
        if (!isDirectory)
        {
            Assert.Equal("contents", await File.ReadAllTextAsync(destination));
        }
    }

    [Fact]
    public async Task CaseOnlyRename_DoesNotReplaceDistinctCaseSensitiveEntry()
    {
        using var workspace = new Workspace();
        var lower = workspace.File("name", "lower");
        var upper = Path.Combine(workspace.Root, "NAME");
        try
        {
            using var stream = new FileStream(upper, FileMode.CreateNew);
        }
        catch (IOException)
        {
            return; // Probe this volume, not OS-wide case assumptions.
        }
        await File.WriteAllTextAsync(upper, "upper");
        await Assert.ThrowsAsync<IOException>(() => workspace.Operations.RenameAsync(workspace.Context, lower, "NAME", false));
        Assert.Equal("lower", await File.ReadAllTextAsync(lower));
        Assert.Equal("upper", await File.ReadAllTextAsync(upper));
    }

    [Fact]
    public async Task CaseOnlyRename_RollsBackOnFailure()
    {
        using var workspace = new Workspace();
        var source = workspace.File("name", "keep me");
        var calls = 0;
        var operations = new ExplorerFileOperations(workspace.Trash, (from, to) =>
        {
            if (++calls == 2)
            {
                throw new IOException("Simulated destination failure");
            }
            File.Move(from, to, overwrite: false);
        });
        var failure = await Assert.ThrowsAsync<IOException>(() => operations.RenameAsync(workspace.Context, source, "NAME", false));
        Assert.Contains("Simulated destination failure", failure.Message);
        Assert.Equal(3, calls);
        Assert.Equal("keep me", await File.ReadAllTextAsync(source));
        Assert.Equal([source], Directory.EnumerateFileSystemEntries(workspace.Root));
    }

    [Fact]
    public async Task CaseOnlyRename_RollsBackEvenIfWorkspaceSwitchesAfterFirstMove()
    {
        using var workspace = new Workspace();
        var source = workspace.File("name");
        var current = true;
        var context = workspace.Context with { IsCurrent = () => current };
        var operations = new ExplorerFileOperations(workspace.Trash, (from, to) =>
        {
            File.Move(from, to, overwrite: false);
            current = false;
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => operations.RenameAsync(context, source, "NAME", false));
        Assert.Equal([source], Directory.EnumerateFileSystemEntries(workspace.Root));
    }

    [Fact]
    public async Task CaseOnlyRename_ReportsActualTemporaryPathIfRollbackFails()
    {
        using var workspace = new Workspace();
        var source = workspace.File("name", "recoverable");
        var calls = 0;
        var operations = new ExplorerFileOperations(workspace.Trash, (from, to) =>
        {
            if (++calls > 1)
            {
                throw new IOException("Simulated failure");
            }
            File.Move(from, to, overwrite: false);
        });
        var failure = await Assert.ThrowsAsync<ExplorerRenameException>(() => operations.RenameAsync(workspace.Context, source, "NAME", false));
        Assert.Equal(3, calls);
        Assert.Contains(failure.ActualPath, failure.Message);
        Assert.StartsWith(".tkcode-rename-", Path.GetFileName(failure.ActualPath));
        Assert.Equal("recoverable", await File.ReadAllTextAsync(failure.ActualPath));
        Assert.Equal([failure.ActualPath], Directory.EnumerateFileSystemEntries(workspace.Root));
    }

    [Fact]
    public async Task LinkAncestors_AndLinkedWorkspaceRoot_AreRejected()
    {
        using var workspace = new Workspace();
        var outside = Path.Combine(workspace.Base, "outside");
        Directory.CreateDirectory(Path.Combine(outside, "nested"));
        var outsideFile = Path.Combine(outside, "nested", "keep.txt");
        await File.WriteAllTextAsync(outsideFile, "outside");
        var link = Path.Combine(workspace.Root, "link");
        if (!TryDirectoryLink(link, outside))
        {
            return;
        }
        var operations = workspace.Operations;
        await Assert.ThrowsAsync<IOException>(() => operations.CreateAsync(workspace.Context, link, "new.txt", false));
        await Assert.ThrowsAsync<IOException>(() => operations.CreateAsync(workspace.Context, Path.Combine(link, "nested"), "new.txt", false));
        var linkedFile = Path.Combine(link, "nested", "keep.txt");
        await Assert.ThrowsAsync<IOException>(() => operations.RenameAsync(workspace.Context, linkedFile, "renamed.txt", false));
        await Assert.ThrowsAsync<IOException>(() => operations.TrashAsync(workspace.Context, linkedFile, false));
        var linkedRootContext = new ExplorerOperationContext(link, static () => true);
        await Assert.ThrowsAsync<IOException>(() => operations.CreateAsync(linkedRootContext, link, "new.txt", false));
        Assert.Equal("outside", await File.ReadAllTextAsync(outsideFile));
        Assert.Empty(workspace.Trash.Paths);
        Assert.False(File.Exists(Path.Combine(outside, "new.txt")));
    }

    [Fact]
    public async Task LeafDirectoryLinks_CanBeRenamedAndTrashed_WithoutTouchingTarget()
    {
        using var workspace = new Workspace();
        var outside = Path.Combine(workspace.Base, "outside");
        Directory.CreateDirectory(outside);
        var outsideFile = Path.Combine(outside, "keep.txt");
        await File.WriteAllTextAsync(outsideFile, "outside");
        var link = Path.Combine(workspace.Root, "link");
        if (!TryDirectoryLink(link, outside))
        {
            return;
        }
        var renamed = await workspace.Operations.RenameAsync(workspace.Context, link, "renamed-link", true);
        Assert.True((File.GetAttributes(renamed) & FileAttributes.ReparsePoint) != 0);
        await workspace.Operations.TrashAsync(workspace.Context, renamed, true);
        Assert.Equal([renamed], workspace.Trash.Paths);
        Assert.Equal("outside", await File.ReadAllTextAsync(outsideFile));
    }

    [Fact]
    public async Task DanglingLeafLinks_CanBeRenamedAndTrashed_ButNotOverwritten()
    {
        using var workspace = new Workspace();
        var link = Path.Combine(workspace.Root, "dangling");
        try
        {
            File.CreateSymbolicLink(link, Path.Combine(workspace.Base, "missing"));
        }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
        {
            return;
        }
        await Assert.ThrowsAsync<IOException>(() => workspace.Operations.CreateAsync(workspace.Context, workspace.Root, "dangling", false));
        await Assert.ThrowsAsync<IOException>(() => workspace.Operations.CreateAsync(workspace.Context, workspace.Root, "dangling", true));
        var renamed = await workspace.Operations.RenameAsync(workspace.Context, link, "renamed-link", false);
        await workspace.Operations.TrashAsync(workspace.Context, renamed, false);
        Assert.Equal([renamed], workspace.Trash.Paths);
        Assert.True((File.GetAttributes(renamed) & FileAttributes.ReparsePoint) != 0);
        Assert.False(File.Exists(Path.Combine(workspace.Base, "missing")));
    }

    [Fact]
    public async Task Trash_UsesInjectedService_AndPropagatesFailureWithoutDeleting()
    {
        using var workspace = new Workspace();
        var source = workspace.File("keep.txt", "keep");
        workspace.Trash.Failure = new IOException("Trash unavailable");
        var error = await Assert.ThrowsAsync<IOException>(() => workspace.Operations.TrashAsync(workspace.Context, source, false));
        Assert.Equal("Trash unavailable", error.Message);
        Assert.Equal([source], workspace.Trash.Paths);
        Assert.Equal("keep", await File.ReadAllTextAsync(source));
    }

    [Fact]
    public async Task Trash_RevalidatesContextAtActualStart_AfterNativeServiceQueuesWorker()
    {
        using var workspace = new Workspace();
        var source = workspace.File("keep.txt", "keep");
        var trash = new DelayedStartTrash();
        var operations = new ExplorerFileOperations(trash);
        var current = 1;
        var context = workspace.Context with { IsCurrent = () => Volatile.Read(ref current) != 0 };
        var operation = operations.TrashAsync(context, source, false);
        await trash.Queued.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Volatile.Write(ref current, 0);
        trash.Start.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => operation);
        Assert.Empty(trash.Mutations);
        Assert.Equal("keep", await File.ReadAllTextAsync(source));
    }

    [Fact]
    public async Task Trash_RevalidatesLeafTypeAtActualStart_AfterNativeServiceQueuesWorker()
    {
        using var workspace = new Workspace();
        var source = workspace.File("changed");
        var trash = new DelayedStartTrash();
        var operations = new ExplorerFileOperations(trash);
        var operation = operations.TrashAsync(workspace.Context, source, false);
        await trash.Queued.Task.WaitAsync(TimeSpan.FromSeconds(10));
        File.Delete(source);
        Directory.CreateDirectory(source);
        trash.Start.SetResult();
        await Assert.ThrowsAsync<IOException>(() => operation);
        Assert.Empty(trash.Mutations);
        Assert.True(Directory.Exists(source));
    }

    [Fact]
    public async Task MissingOrChangedItems_FailWithoutCallingTrash()
    {
        using var workspace = new Workspace();
        var source = workspace.File("file");
        await Assert.ThrowsAsync<IOException>(() => workspace.Operations.RenameAsync(workspace.Context, source, "new-name", true));
        await Assert.ThrowsAsync<IOException>(() => workspace.Operations.TrashAsync(workspace.Context, source, true));
        var missing = Path.Combine(workspace.Root, "missing");
        await Assert.ThrowsAnyAsync<IOException>(() => workspace.Operations.RenameAsync(workspace.Context, missing, "new-name", false));
        await Assert.ThrowsAnyAsync<IOException>(() => workspace.Operations.TrashAsync(workspace.Context, missing, false));
        Assert.Empty(workspace.Trash.Paths);
        Assert.True(File.Exists(source));
    }

    [Theory]
    [InlineData(0, "open")]
    [InlineData(1, "explorer.exe")]
    [InlineData(2, "xdg-open")]
    public async Task PlatformActions_UseStructuredArguments_AndNeverOpenAFileAsAnApplication(int platformValue, string executable)
    {
        using var workspace = new Workspace();
        var file = workspace.File("spaces ;$' [你好].txt");
        var directory = workspace.Directory("folder ;$' [你好]");
        var starts = new List<ProcessStartInfo>();
        var platform = (ExplorerPlatform)platformValue;
        IExplorerPlatformActions actions = new ExplorerPlatformActions(platform, start =>
        {
            starts.Add(start);
            return Task.CompletedTask;
        });
        await actions.ShowAsync(file, false);
        await actions.ShowAsync(directory, true);
        await actions.OpenDirectoryAsync(directory);
        Assert.Equal(3, starts.Count);
        Assert.All(starts, start =>
        {
            Assert.Equal(executable, start.FileName);
            Assert.False(start.UseShellExecute);
            Assert.Equal(string.Empty, start.Arguments);
        });
        if (platform == ExplorerPlatform.MacOS)
        {
            Assert.Equal(["-R", "--", file], starts[0].ArgumentList);
            Assert.Equal(["-R", "--", directory], starts[1].ArgumentList);
            Assert.Equal(["--", directory], starts[2].ArgumentList);
        }
        else if (platform == ExplorerPlatform.Windows)
        {
            Assert.Equal(["/select,", file], starts[0].ArgumentList);
            Assert.Equal(["/select,", directory], starts[1].ArgumentList);
            Assert.Equal([directory], starts[2].ArgumentList);
        }
        else
        {
            Assert.Equal([workspace.Root], starts[0].ArgumentList);
            Assert.Equal([workspace.Root], starts[1].ArgumentList);
            Assert.Equal([directory], starts[2].ArgumentList);
        }
    }

    [Fact]
    public async Task PlatformActions_PropagateLaunchErrors_AndRefuseToOpenFilesAsDirectories()
    {
        using var workspace = new Workspace();
        var file = workspace.File("not-a-directory.txt");
        var calls = 0;
        var actions = new ExplorerPlatformActions(ExplorerPlatform.MacOS, _ =>
        {
            ++calls;
            throw new IOException("File manager unavailable");
        });
        await Assert.ThrowsAsync<IOException>(() => actions.OpenDirectoryAsync(file));
        Assert.Equal(0, calls);
        var error = await Assert.ThrowsAsync<IOException>(() => actions.ShowAsync(file, false));
        Assert.Equal("File manager unavailable", error.Message);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task PlatformActions_RejectUnsupportedPlatforms_WithoutLaunchingLinuxTools()
    {
        using var workspace = new Workspace();
        var file = workspace.File("file.txt");
        var calls = 0;
        var actions = new ExplorerPlatformActions((ExplorerPlatform)int.MaxValue, _ =>
        {
            ++calls;
            return Task.CompletedTask;
        });
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => actions.ShowAsync(file, false));
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => actions.OpenDirectoryAsync(workspace.Root));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task PlatformActions_CanRevealDanglingLinks_ButCannotOpenThemAsDirectories()
    {
        using var workspace = new Workspace();
        var link = Path.Combine(workspace.Root, "dangling-directory");
        if (!TryDirectoryLink(link, Path.Combine(workspace.Base, "missing")))
        {
            return;
        }
        var calls = 0;
        var actions = new ExplorerPlatformActions(ExplorerPlatform.MacOS, _ =>
        {
            ++calls;
            return Task.CompletedTask;
        });
        await actions.ShowAsync(link, true);
        await Assert.ThrowsAsync<IOException>(() => actions.OpenDirectoryAsync(link));
        Assert.Equal(1, calls);
    }

    private static bool TryDirectoryLink(string path, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(path, target);
            return true;
        }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
        {
            return false;
        }
    }

    private sealed class FakeTrash : ITrashService
    {
        public List<string> Paths { get; } = [];
        public Exception? Failure { get; set; }

        public Task TrashAsync(string path)
        {
            Paths.Add(path);
            return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
        }
    }

    private sealed class DelayedStartTrash : ITrashService
    {
        public TaskCompletionSource Queued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Start { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Mutations { get; } = [];

        public Task TrashAsync(string path) => throw new InvalidOperationException("The actual-start validator is required.");

        public async Task TrashAsync(string path, Action validateBeforeStart)
        {
            Queued.SetResult();
            await Start.Task;
            validateBeforeStart();
            Mutations.Add(path); // Simulates the native mutation; never invokes the real trash.
        }
    }

    private sealed class Workspace : IDisposable
    {
        public string Base { get; } = Path.Combine(Path.GetTempPath(), $"tkcode-file-ops-{Guid.NewGuid():N}");
        public string Root { get; }
        public FakeTrash Trash { get; } = new();
        public ExplorerFileOperations Operations { get; }
        public ExplorerOperationContext Context => new(Root, static () => true);

        public Workspace()
        {
            Root = Path.Combine(Base, "workspace");
            System.IO.Directory.CreateDirectory(Root);
            Operations = new ExplorerFileOperations(Trash);
        }

        public string File(string relative, string contents = "")
        {
            var path = Path.Combine(Root, relative);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, contents);
            return path;
        }

        public string Directory(string relative)
        {
            var path = Path.Combine(Root, relative);
            System.IO.Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose() => System.IO.Directory.Delete(Base, recursive: true);
    }
}
