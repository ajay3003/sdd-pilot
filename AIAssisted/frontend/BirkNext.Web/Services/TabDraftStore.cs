using System.Text.Json;
using Microsoft.JSInterop;

namespace BirkNext.Web.Services;

/// <summary>
/// Unsaved, per-tab work that should survive a reload of the page (a scenario selection, export field values not yet saved as the project's
/// settings). Kept in the browser tab's sessionStorage only: it is never shared, never sent to the API and is gone when the tab closes. Storage
/// can be unavailable (private window, blocked site data, prerendering); every read and write then quietly does nothing and the page works as
/// before.
/// </summary>
public static class TabDraftStore
{
    public const string Prefix = "birknext.tab.";

    public static string SelectionKey(string projectKey) => $"{Prefix}tps-selection.{projectKey}";

    public static string ExportSettingsKey(string projectKey) => $"{Prefix}adx-settings.{projectKey}";

    /// <summary>Finn og forstå's search, filters and work queue for this tab and project (kept when the tester comes back from Plan &amp; prepare).</summary>
    public static string DiscoverFilterKey(string projectKey) => $"{Prefix}mtd-filter.{projectKey}";

    /// <summary>Finn og forstå's working context for this tab and project: the user story group, the open scenario and the tab (restored on return).</summary>
    public static string DiscoverContextKey(string projectKey) => $"{Prefix}mtd-context.{projectKey}";

    public static async Task<T?> ReadAsync<T>(IJSRuntime js, string key)
    {
        try
        {
            var raw = await js.InvokeAsync<string?>("sessionStorage.getItem", key);
            return string.IsNullOrWhiteSpace(raw) ? default : JsonSerializer.Deserialize<T>(raw);
        }
        catch (Exception e) when (e is JSException or JsonException or InvalidOperationException or TaskCanceledException or NotSupportedException) { return default; }
    }

    public static async Task WriteAsync<T>(IJSRuntime js, string key, T value)
    {
        try { await js.InvokeVoidAsync("sessionStorage.setItem", key, JsonSerializer.Serialize(value)); }
        catch (Exception e) when (e is JSException or InvalidOperationException or TaskCanceledException or NotSupportedException) { }
    }

    public static async Task RemoveAsync(IJSRuntime js, string key)
    {
        try { await js.InvokeVoidAsync("sessionStorage.removeItem", key); }
        catch (Exception e) when (e is JSException or InvalidOperationException or TaskCanceledException or NotSupportedException) { }
    }
}
