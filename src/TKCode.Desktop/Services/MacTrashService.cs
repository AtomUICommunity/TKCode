using System.Runtime.InteropServices;

namespace TKCode.Desktop.Services;

internal sealed class MacTrashService(IMacTrashNative native) : ITrashService
{
    public MacTrashService() : this(new MacTrashNative()) { }

    public Task TrashAsync(string path) => TrashAsync(path, static () => { });

    public Task TrashAsync(string path, Action validateBeforeStart)
    {
        ArgumentNullException.ThrowIfNull(validateBeforeStart);
        var fullPath = TrashService.GetFullPath(path);
        return Task.Run(() => Trash(fullPath, validateBeforeStart));
    }

    private void Trash(string path, Action validateBeforeStart)
    {
        try
        {
            TrashCore(path, validateBeforeStart);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw new PlatformNotSupportedException("The macOS native trash backend is unavailable.", ex);
        }
    }

    private void TrashCore(string path, Action validateBeforeStart)
    {
        nint pool = 0;
        try
        {
            pool = native.CreateAutoreleasePool();
            if (pool == 0)
                throw new IOException("Could not create a native trash autorelease pool.");
            var manager = native.GetFileManager();
            var url = native.CreateFileUrl(path);
            if (manager == 0 || url == 0)
                throw new IOException("Could not create the native trash file URL or file manager.");

            // Foundation's BOOL is one byte, not Win32 BOOL. Both out objects are borrowed,
            // autoreleased references; copy NSError's text before draining our worker's pool.
            validateBeforeStart();
            if (native.TrashItem(manager, url, out _, out var error) == 0)
                throw new IOException(error == 0
                    ? "macOS could not move the item to the Trash (no native error was supplied)."
                    : $"macOS could not move the item to the Trash: {native.DescribeError(error)}");
        }
        finally
        {
            if (pool != 0)
                native.DrainAutoreleasePool(pool);
        }
    }
}

internal interface IMacTrashNative
{
    nint CreateAutoreleasePool();
    void DrainAutoreleasePool(nint pool);
    nint GetFileManager();
    nint CreateFileUrl(string path);
    byte TrashItem(nint manager, nint url, out nint resultingUrl, out nint error);
    string DescribeError(nint error);
}

internal sealed partial class MacTrashNative : IMacTrashNative
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string Foundation = "/System/Library/Frameworks/Foundation.framework/Foundation";

    public nint CreateAutoreleasePool()
    {
        // The desktop normally loads Foundation already, but this backend must also stand alone.
        // Keep the framework loaded for the lifetime of the process (Objective-C class ownership).
        _ = FoundationHandle.Value;
        return Send(Send(Class("NSAutoreleasePool"), Sel("alloc")), Sel("init"));
    }

    private static readonly Lazy<nint> FoundationHandle = new(() => NativeLibrary.Load(Foundation));

    public void DrainAutoreleasePool(nint pool) => SendVoid(pool, Sel("drain"));
    public nint GetFileManager() => Send(Class("NSFileManager"), Sel("defaultManager"));

    public nint CreateFileUrl(string path)
    {
        var text = SendUtf8(Class("NSString"), Sel("stringWithUTF8String:"), path);
        return text == 0 ? 0 : SendObject(Class("NSURL"), Sel("fileURLWithPath:"), text);
    }

    // NSFileManager.h: YES means moved to a Trash; NO means not moved. Unlike removeItemAtURL:,
    // this API has no permanent-deletion fallback.
    public byte TrashItem(nint manager, nint url, out nint resultingUrl, out nint error) =>
        SendTrash(manager, Sel("trashItemAtURL:resultingItemURL:error:"), url, out resultingUrl, out error);

    public string DescribeError(nint error)
    {
        var description = Send(error, Sel("localizedDescription"));
        var utf8 = description == 0 ? 0 : Send(description, Sel("UTF8String"));
        return Marshal.PtrToStringUTF8(utf8) ?? "Unknown native error";
    }

    private static nint Class(string name) => objc_getClass(name);
    private static nint Sel(string name) => sel_registerName(name);

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint objc_getClass(string name);
    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint sel_registerName(string name);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint Send(nint receiver, nint selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial void SendVoid(nint receiver, nint selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint SendObject(nint receiver, nint selector, nint argument);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint SendUtf8(nint receiver, nint selector, string argument);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial byte SendTrash(nint receiver, nint selector, nint url, out nint resultingUrl, out nint error);
}
