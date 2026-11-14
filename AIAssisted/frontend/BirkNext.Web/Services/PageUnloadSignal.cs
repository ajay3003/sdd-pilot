using Microsoft.JSInterop;

namespace BirkNext.Web.Services;

/// <summary>
/// Whether the browser page is being unloaded (reload, close, full navigation). A fetch aborted by the unload surfaces in .NET as
/// <c>HttpRequestException: TypeError: Failed to fetch</c> — the same exception as a real network failure — so the save pipeline asks this
/// signal to tell an expected interruption from a failure while the page stays.
/// </summary>
public interface IPageUnloadSignal
{
    bool IsUnloading { get; }
}

/// <summary>Reads <c>birknextSaveGuard.isUnloading()</c> (wwwroot/js/storage.js, set on <c>pagehide</c>) synchronously; false when it cannot be read.</summary>
public sealed class JsPageUnloadSignal(IJSRuntime js) : IPageUnloadSignal
{
    public bool IsUnloading
    {
        get
        {
            try { return js is IJSInProcessRuntime inProcess && inProcess.Invoke<bool>("birknextSaveGuard.isUnloading"); }
            catch (Exception) { return false; }
        }
    }
}
