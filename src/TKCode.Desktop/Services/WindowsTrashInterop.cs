namespace TKCode.Desktop.Services;

/// <summary>The Windows backend deliberately fails closed until recycling can be guaranteed.</summary>
internal static class WindowsTrashInterop
{
    // FOF_ALLOWUNDO alone permits permanent deletion. A future IFileOperation implementation must
    // verify FOFX_RECYCLEONDELETE and the PreDeleteItem transfer-flag/veto contract against an
    // authoritative Windows SDK, reject every non-recycling operation BEFORE deletion, run on its
    // own STA, and inspect item results and GetAnyOperationsAborted. A successful PerformOperations
    // HRESULT alone is not success. Neither SHFileOperation nor File.Delete is a safe fallback.
    // No unverified COM vtable/flag definitions are shipped here.
    internal static PlatformNotSupportedException CreateUnavailableException() => new(
        "Windows recycle-only trash is unavailable: the native pre-delete recycling safety contract " +
        "has not been verified. The item has not been deleted.");
}
