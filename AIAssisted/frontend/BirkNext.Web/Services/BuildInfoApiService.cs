using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BirkNext.BuildInfo;

namespace BirkNext.Web.Services;

/// <summary>The running backend's build information (GET api/system/build-info). Read-only.</summary>
public interface IBuildInfoApiService
{
    Task<(BuildInfoResponse? Info, string? Error)> GetAsync(CancellationToken ct = default);

    /// <summary>The runtime profile and what this installation runs on (GET api/system/runtime). Read-only.</summary>
    Task<(RuntimeEnvironmentInfo? Info, string? Error)> GetRuntimeAsync(CancellationToken ct = default) =>
        Task.FromResult<(RuntimeEnvironmentInfo?, string?)>((null, "The runtime information is not available in this host."));
}

public sealed class BuildInfoApiService(HttpClient http) : IBuildInfoApiService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    // One read per short window, shared by every card on the page (Overview, Application information, Runtime & Environment): the typed client
    // is created per component, so the coalescing is static. Results stay a few seconds only; Refresh invalidates explicitly.
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(5);
    private static readonly object Gate = new();
    private static (Task<(BuildInfoResponse?, string?)> Task, DateTimeOffset At)? _build;
    private static (Task<(RuntimeEnvironmentInfo?, string?)> Task, DateTimeOffset At)? _runtime;

    /// <summary>Forget the shared results: the next read asks the backend again (the Overview's Refresh).</summary>
    public static void Invalidate() { lock (Gate) { _build = null; _runtime = null; } }

    public Task<(BuildInfoResponse? Info, string? Error)> GetAsync(CancellationToken ct = default)
    {
        lock (Gate)
        {
            if (_build is { } b && DateTimeOffset.UtcNow - b.At < Window) return b.Task;
            var task = ReadBuildAsync(CancellationToken.None); // shared: one caller leaving never cancels it for the others
            _build = (task, DateTimeOffset.UtcNow);
            return task;
        }
    }

    public Task<(RuntimeEnvironmentInfo? Info, string? Error)> GetRuntimeAsync(CancellationToken ct = default)
    {
        lock (Gate)
        {
            if (_runtime is { } r && DateTimeOffset.UtcNow - r.At < Window) return r.Task;
            var task = ReadRuntimeAsync(CancellationToken.None);
            _runtime = (task, DateTimeOffset.UtcNow);
            return task;
        }
    }

    private async Task<(BuildInfoResponse? Info, string? Error)> ReadBuildAsync(CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync("api/system/build-info", ct);
            if (!response.IsSuccessStatusCode) return (null, $"The backend did not return its build information (HTTP {(int)response.StatusCode}).");
            return (await response.Content.ReadFromJsonAsync<BuildInfoResponse>(Json, ct), null);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException or NotSupportedException)
        {
            return (null, "The backend's build information is not reachable.");
        }
    }

    private async Task<(RuntimeEnvironmentInfo? Info, string? Error)> ReadRuntimeAsync(CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync("api/system/runtime", ct);
            if (!response.IsSuccessStatusCode) return (null, $"The backend did not return its runtime information (HTTP {(int)response.StatusCode}).");
            return (await response.Content.ReadFromJsonAsync<RuntimeEnvironmentInfo>(Json, ct), null);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException or NotSupportedException)
        {
            return (null, "The backend's runtime information is not reachable.");
        }
    }
}

/// <summary>The identity compiled into THIS frontend (BirkNext.Web) — never read from the backend.</summary>
public static class FrontendBuildIdentity
{
    private static readonly Lazy<BuildIdentity> Identity = new(() => BuildIdentityReader.FromAssembly(typeof(FrontendBuildIdentity).Assembly, "BirkNext.Web"));
    public static BuildIdentity Current => Identity.Value;
}
