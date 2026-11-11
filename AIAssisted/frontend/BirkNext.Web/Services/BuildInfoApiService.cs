using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BirkNext.BuildInfo;

namespace BirkNext.Web.Services;

/// <summary>The running backend's build information (GET api/system/build-info). Read-only.</summary>
public interface IBuildInfoApiService
{
    Task<(BuildInfoResponse? Info, string? Error)> GetAsync(CancellationToken ct = default);
}

public sealed class BuildInfoApiService(HttpClient http) : IBuildInfoApiService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public async Task<(BuildInfoResponse? Info, string? Error)> GetAsync(CancellationToken ct = default)
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
}

/// <summary>The identity compiled into THIS frontend (BirkNext.Web) — never read from the backend.</summary>
public static class FrontendBuildIdentity
{
    private static readonly Lazy<BuildIdentity> Identity = new(() => BuildIdentityReader.FromAssembly(typeof(FrontendBuildIdentity).Assembly, "BirkNext.Web"));
    public static BuildIdentity Current => Identity.Value;
}
