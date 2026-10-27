using System.Runtime.InteropServices;
using Xunit;

namespace TKCode.Terminal.Protocol.Tests;

/// <summary>Desktop 与 host 用同一规则找 libghostty-vt，否则构建 ID 不一致（build mismatch）。</summary>
public sealed class GhosttyLibraryLocatorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ghostty-locator").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void FindsTheLibraryInTheRuntimesLayoutOfAPackage()
    {
        var native = Path.Combine(_dir, "runtimes", RuntimeInformation.RuntimeIdentifier, "native");
        Directory.CreateDirectory(native);
        var lib = Path.Combine(native, "libghostty-vt.so");
        File.WriteAllText(lib, "x");

        Assert.Equal(lib, PtyHostPaths.FindGhosttyLibrary(_dir));
    }

    [Fact]
    public void PrefersTheLibraryNextToTheExecutable()
    {
        var flat = Path.Combine(_dir, "ghostty-vt.dll");
        File.WriteAllText(flat, "x");
        var native = Path.Combine(_dir, "runtimes", RuntimeInformation.RuntimeIdentifier, "native");
        Directory.CreateDirectory(native);
        File.WriteAllText(Path.Combine(native, "ghostty-vt.dll"), "y");

        Assert.Equal(flat, PtyHostPaths.FindGhosttyLibrary(_dir));
    }

    [Fact]
    public void ReturnsNullWhenThereIsNone() => Assert.Null(PtyHostPaths.FindGhosttyLibrary(_dir));
}
