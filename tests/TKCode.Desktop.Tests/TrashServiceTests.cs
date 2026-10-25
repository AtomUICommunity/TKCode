using TKCode.Desktop.Services;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>Native contracts use fakes; real trash is only exercised by an explicit, isolated opt-in.</summary>
public sealed class TrashServiceTests
{
    [Fact]
    public void Factory_SelectsTheCurrentPlatform()
    {
        var service = TrashService.Create();
        if (OperatingSystem.IsMacOS())
            Assert.IsType<MacTrashService>(service);
        else if (OperatingSystem.IsLinux())
            Assert.IsType<LinuxTrashService>(service);
        else if (OperatingSystem.IsWindows())
            Assert.IsType<WindowsTrashService>(service);
        else
            Assert.NotNull(service);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\0path")]
    public async Task InvalidPaths_AreRejectedBeforeNativeCalls(string path)
    {
        var mac = new FakeMac();
        var linux = new FakeLinux();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => new MacTrashService(mac).TrashAsync(path));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => new LinuxTrashService(linux).TrashAsync(path));
        Assert.Empty(mac.Calls);
        Assert.Empty(linux.Calls);
    }

    [Fact]
    public async Task FilesystemRoot_IsRejectedBeforeNativeCalls()
    {
        var mac = new FakeMac();
        await Assert.ThrowsAsync<IOException>(() => new MacTrashService(mac).TrashAsync(Path.GetPathRoot(Path.GetFullPath("."))!));
        Assert.Empty(mac.Calls);
    }

    [Fact]
    public async Task Mac_SuccessUsesFileUrlAndDrainsPool_WithoutReleasingBorrowedObjects()
    {
        var native = new FakeMac { Result = 0xff };
        var path = Path.Combine(Path.GetTempPath(), "space 中文 # link");
        await new MacTrashService(native).TrashAsync(path + Path.DirectorySeparatorChar);
        Assert.Equal(Path.GetFullPath(path), native.Path);
        Assert.Equal(["pool", "manager", "url", "trash", "drain"], native.Calls);
    }

    [Fact]
    public async Task Mac_FailureCopiesNSErrorBeforePoolDrains()
    {
        var native = new FakeMac { Result = 0, Error = 99 };
        var error = await Assert.ThrowsAsync<IOException>(() => new MacTrashService(native).TrashAsync("file"));
        Assert.Contains("native permission denied", error.Message);
        Assert.Equal(["pool", "manager", "url", "trash", "describe", "drain"], native.Calls);
    }

    [Fact]
    public async Task Mac_FailureWithoutNSErrorStillThrowsAndDrains()
    {
        var native = new FakeMac { Result = 0 };
        var error = await Assert.ThrowsAsync<IOException>(() => new MacTrashService(native).TrashAsync("file"));
        Assert.Contains("no native error", error.Message);
        Assert.Equal("drain", native.Calls[^1]);
    }

    [Fact]
    public async Task Mac_NullUrlNeverCallsTrash()
    {
        var native = new FakeMac { Url = 0 };
        await Assert.ThrowsAsync<IOException>(() => new MacTrashService(native).TrashAsync("file"));
        Assert.Equal(["pool", "manager", "url", "drain"], native.Calls);
    }

    [Fact]
    public async Task Mac_MissingBackendIsExplicit()
    {
        var native = new FakeMac { PoolException = new DllNotFoundException("Foundation") };
        var error = await Assert.ThrowsAsync<PlatformNotSupportedException>(() => new MacTrashService(native).TrashAsync("file"));
        Assert.Contains("unavailable", error.Message);
        Assert.IsType<DllNotFoundException>(error.InnerException);
        Assert.Equal(["pool"], native.Calls);
    }

    [Fact]
    public async Task Mac_ManagedFailureStillDrainsPool()
    {
        var native = new FakeMac { TrashException = new InvalidOperationException("native shim") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new MacTrashService(native).TrashAsync("file"));
        Assert.Equal("drain", native.Calls[^1]);
    }

    [Fact]
    public async Task Linux_SuccessUsesGFileAndUnrefsIt()
    {
        var native = new FakeLinux();
        var path = Path.Combine(Path.GetTempPath(), "space 中文 # link");
        await new LinuxTrashService(native).TrashAsync(path);
        Assert.Equal(Path.GetFullPath(path), native.Path);
        Assert.Equal(["file", "trash", "unref"], native.Calls);
    }

    [Fact]
    public async Task Linux_FailureCopiesAndFreesGErrorAndUnrefsFile()
    {
        var native = new FakeLinux { Result = 0, Error = 99 };
        var error = await Assert.ThrowsAsync<IOException>(() => new LinuxTrashService(native).TrashAsync("file"));
        Assert.Contains("native permission denied", error.Message);
        Assert.Equal(["file", "trash", "describe", "free-error", "unref"], native.Calls);
    }

    [Fact]
    public async Task Linux_ErrorIsFreedEvenIfNativeUnexpectedlyReturnsSuccessWithError()
    {
        var native = new FakeLinux { Error = 99 };
        await new LinuxTrashService(native).TrashAsync("file");
        Assert.Equal(["file", "trash", "free-error", "unref"], native.Calls);
    }

    [Fact]
    public async Task Linux_FailureWithoutGErrorStillThrowsAndUnrefs()
    {
        var native = new FakeLinux { Result = 0 };
        var error = await Assert.ThrowsAsync<IOException>(() => new LinuxTrashService(native).TrashAsync("file"));
        Assert.Contains("no native error", error.Message);
        Assert.Equal(["file", "trash", "unref"], native.Calls);
    }

    [Fact]
    public async Task Linux_NullFileNeverCallsTrashOrUnref()
    {
        var native = new FakeLinux { File = 0 };
        await Assert.ThrowsAsync<IOException>(() => new LinuxTrashService(native).TrashAsync("file"));
        Assert.Equal(["file"], native.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Linux_MissingBackendIsExplicit_AndUnrefsAlreadyCreatedFile(bool missingEntryPoint)
    {
        var native = new FakeLinux
        {
            TrashException = missingEntryPoint ? new EntryPointNotFoundException("g_file_trash") : new DllNotFoundException("GIO")
        };
        var error = await Assert.ThrowsAsync<PlatformNotSupportedException>(() => new LinuxTrashService(native).TrashAsync("file"));
        Assert.Contains("GIO/GLib", error.Message);
        Assert.Equal(["file", "trash", "unref"], native.Calls);
    }

    [Fact]
    public async Task Linux_ManagedFailureStillUnrefsFile()
    {
        var native = new FakeLinux { TrashException = new InvalidOperationException("native shim") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new LinuxTrashService(native).TrashAsync("file"));
        Assert.Equal("unref", native.Calls[^1]);
    }

    [Fact]
    public async Task Mac_ValidatesAfterNativeSetupImmediatelyBeforeTrash()
    {
        var native = new FakeMac();
        await new MacTrashService(native).TrashAsync("file", () => native.Calls.Add("validate"));
        Assert.Equal(["pool", "manager", "url", "validate", "trash", "drain"], native.Calls);
    }

    [Fact]
    public async Task Mac_StaleValidationPreventsNativeTrashAndDrainsPool()
    {
        var native = new FakeMac();
        var stale = new InvalidOperationException("The explorer context is stale.");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new MacTrashService(native).TrashAsync("file", () =>
        {
            native.Calls.Add("validate");
            throw stale;
        }));
        Assert.Same(stale, error);
        Assert.Equal(["pool", "manager", "url", "validate", "drain"], native.Calls);
    }

    [Fact]
    public async Task Linux_ValidatesAfterNativeSetupImmediatelyBeforeTrash()
    {
        var native = new FakeLinux();
        await new LinuxTrashService(native).TrashAsync("file", () => native.Calls.Add("validate"));
        Assert.Equal(["file", "validate", "trash", "unref"], native.Calls);
    }

    [Fact]
    public async Task Linux_StaleValidationPreventsNativeTrashAndUnrefsFile()
    {
        var native = new FakeLinux();
        var stale = new InvalidOperationException("The explorer context is stale.");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new LinuxTrashService(native).TrashAsync("file", () =>
        {
            native.Calls.Add("validate");
            throw stale;
        }));
        Assert.Same(stale, error);
        Assert.Equal(["file", "validate", "unref"], native.Calls);
    }

    [Fact]
    public async Task DefaultValidationOverloadSupportsExistingImplementations()
    {
        var native = new LegacyTrash();
        ITrashService service = native;
        await service.TrashAsync("file", () => native.Calls.Add("validate"));
        Assert.Equal(["validate", "trash"], native.Calls);
    }

    [Fact]
    public async Task DefaultValidationOverloadRejectsStaleContextBeforeCallingExistingImplementation()
    {
        var native = new LegacyTrash();
        ITrashService service = native;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.TrashAsync("file", () =>
            throw new InvalidOperationException("The explorer context is stale.")));
        Assert.Empty(native.Calls);
    }

    [Fact]
    public async Task NullValidationIsRejectedBeforeNativeSetup()
    {
        var mac = new FakeMac();
        var linux = new FakeLinux();
        await Assert.ThrowsAsync<ArgumentNullException>(() => new MacTrashService(mac).TrashAsync("file", null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => new LinuxTrashService(linux).TrashAsync("file", null!));
        Assert.Empty(mac.Calls);
        Assert.Empty(linux.Calls);
    }

    [Fact]
    public async Task FailedBackendsNeverDeleteTheSource()
    {
        var directory = Directory.CreateTempSubdirectory("tkcode-trash-failure-").FullName;
        try
        {
            var path = Path.Combine(directory, "retain.txt");
            await File.WriteAllTextAsync(path, "retain me");
            await Assert.ThrowsAsync<IOException>(() => new MacTrashService(new FakeMac { Result = 0 }).TrashAsync(path));
            await Assert.ThrowsAsync<IOException>(() => new LinuxTrashService(new FakeLinux { Result = 0 }).TrashAsync(path));
            var error = await Assert.ThrowsAsync<PlatformNotSupportedException>(() => new WindowsTrashService().TrashAsync(path));
            Assert.Contains("recycle-only", error.Message);
            Assert.Equal("retain me", await File.ReadAllTextAsync(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task NativeTrash_IsolatedOptInOnly()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("TKCODE_TEST_NATIVE_TRASH") == "1",
            "Set TKCODE_TEST_NATIVE_TRASH=1 to trash only newly created isolated test items.");
        Assert.SkipUnless(OperatingSystem.IsMacOS() || OperatingSystem.IsLinux(), "Windows recycle-only backend is disabled.");
        var directory = Directory.CreateTempSubdirectory("tkcode-native-trash-").FullName;
        try
        {
            var path = Path.Combine(directory, "trash me 中文.txt");
            await File.WriteAllTextAsync(path, "isolated native trash test");
            await TrashService.Create().TrashAsync(path);
            Assert.False(File.Exists(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class LegacyTrash : ITrashService
    {
        public List<string> Calls { get; } = [];
        public Task TrashAsync(string path)
        {
            Calls.Add("trash");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeMac : IMacTrashNative
    {
        public List<string> Calls { get; } = [];
        public string? Path { get; private set; }
        public byte Result { get; init; } = 1;
        public nint Error { get; init; }
        public nint Url { get; init; } = 3;
        public Exception? PoolException { get; init; }
        public Exception? TrashException { get; init; }

        public nint CreateAutoreleasePool()
        {
            Calls.Add("pool");
            if (PoolException is not null)
                throw PoolException;
            return 1;
        }
        public void DrainAutoreleasePool(nint pool)
        {
            Assert.Equal((nint)1, pool);
            Calls.Add("drain");
        }
        public nint GetFileManager() { Calls.Add("manager"); return 2; }
        public nint CreateFileUrl(string path) { Calls.Add("url"); Path = path; return Url; }
        public byte TrashItem(nint manager, nint url, out nint resultingUrl, out nint error)
        {
            Assert.Equal((nint)2, manager);
            Assert.Equal(Url, url);
            Calls.Add("trash");
            resultingUrl = 4;
            error = Error;
            if (TrashException is not null)
                throw TrashException;
            return Result;
        }
        public string DescribeError(nint error)
        {
            Assert.Equal(Error, error);
            Assert.DoesNotContain("drain", Calls);
            Calls.Add("describe");
            return "native permission denied";
        }
    }

    private sealed class FakeLinux : ILinuxTrashNative
    {
        public List<string> Calls { get; } = [];
        public string? Path { get; private set; }
        public int Result { get; init; } = 1;
        public nint Error { get; init; }
        public nint File { get; init; } = 10;
        public Exception? TrashException { get; init; }

        public nint CreateFile(string path) { Calls.Add("file"); Path = path; return File; }
        public int Trash(nint file, out nint error)
        {
            Assert.Equal(File, file);
            Calls.Add("trash");
            error = Error;
            if (TrashException is not null)
                throw TrashException;
            return Result;
        }
        public string DescribeError(nint error)
        {
            Assert.Equal(Error, error);
            Assert.DoesNotContain("free-error", Calls);
            Calls.Add("describe");
            return "native permission denied";
        }
        public void FreeError(nint error) { Assert.Equal(Error, error); Calls.Add("free-error"); }
        public void Unref(nint file) { Assert.Equal(File, file); Calls.Add("unref"); }
    }
}
