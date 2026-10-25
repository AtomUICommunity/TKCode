using System.Runtime.InteropServices;

namespace TKCode.Desktop.Services;

internal sealed class LinuxTrashService(ILinuxTrashNative native) : ITrashService
{
    public LinuxTrashService() : this(new LinuxTrashNative()) { }

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
            throw new PlatformNotSupportedException("The Linux native trash backend is unavailable (GIO/GLib is required).", ex);
        }
    }

    private void TrashCore(string path, Action validateBeforeStart)
    {
        nint file = 0;
        nint error = 0;
        try
        {
            file = native.CreateFile(path);
            if (file == 0)
                throw new IOException("GIO could not create the native trash file reference.");
            // g_file_trash does not fall back to g_file_delete when a mount has no trash support.
            validateBeforeStart();
            if (native.Trash(file, out error) == 0)
                throw new IOException(error == 0
                    ? "GIO could not move the item to the Trash (no native error was supplied)."
                    : $"GIO could not move the item to the Trash: {native.DescribeError(error)}");
        }
        finally
        {
            try
            {
                if (error != 0)
                    native.FreeError(error);
            }
            finally
            {
                if (file != 0)
                    native.Unref(file);
            }
        }
    }
}

internal interface ILinuxTrashNative
{
    nint CreateFile(string path);
    int Trash(nint file, out nint error);
    string DescribeError(nint error);
    void FreeError(nint error);
    void Unref(nint file);
}

internal sealed partial class LinuxTrashNative : ILinuxTrashNative
{
    public nint CreateFile(string path) => g_file_new_for_path(path);
    public int Trash(nint file, out nint error) => g_file_trash(file, 0, out error);
    public void FreeError(nint error) => g_error_free(error);
    public void Unref(nint file) => g_object_unref(file);

    public string DescribeError(nint error)
    {
        var value = Marshal.PtrToStructure<GError>(error);
        return Marshal.PtrToStringUTF8(value.Message) ?? $"Native error {value.Domain}:{value.Code}";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GError
    {
        public uint Domain;
        public int Code;
        public nint Message;
    }

    [LibraryImport("libgio-2.0.so.0", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint g_file_new_for_path(string path);
    [LibraryImport("libgio-2.0.so.0")]
    private static partial int g_file_trash(nint file, nint cancellable, out nint error);
    [LibraryImport("libglib-2.0.so.0")]
    private static partial void g_error_free(nint error);
    [LibraryImport("libgobject-2.0.so.0")]
    private static partial void g_object_unref(nint instance);
}
