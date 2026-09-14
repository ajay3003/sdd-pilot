using System.Text.Json.Serialization;
using BirkNext.LocalHttpsProxy;

namespace BirkNext.Web.Models;

/// <summary>
/// Safe, per-Target-Environment persistence of page-oriented endpoint discovery. Holds only non-secret network metadata: no bearer
/// token, Authorization value, cookie, request body, response body or sensitive query string, and no transient proxy session id.
/// Persisted in the separate <c>birknext:endpoint-discovery</c> store, never in the Target Environment configuration.
/// </summary>
public sealed class EndpointDiscoverySnapshot
{
    [JsonPropertyName("pages")] public List<PageAnalysis> Pages { get; set; } = [];
    /// <summary>Endpoints that could not be safely correlated to one page (background poll, shared config, telemetry, global auth).</summary>
    [JsonPropertyName("shared")] public List<ObservedNetworkEndpoint> Shared { get; set; } = [];
    [JsonPropertyName("updatedAt")] public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One analyzed application page and the endpoints it was observed to communicate with. Relationship-oriented: an endpoint used by two pages is a separate row under each page, so deleting one page never removes the other page's evidence.</summary>
public sealed class PageAnalysis
{
    [JsonPropertyName("origin")] public string PageOrigin { get; set; } = "";
    [JsonPropertyName("path")] public string PagePath { get; set; } = "";
    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
    [JsonPropertyName("firstObservedAt")] public DateTimeOffset FirstObservedAt { get; set; }
    [JsonPropertyName("lastObservedAt")] public DateTimeOffset LastObservedAt { get; set; }
    [JsonPropertyName("endpoints")] public List<ObservedNetworkEndpoint> Endpoints { get; set; } = [];

    /// <summary>Stable identity: scheme+host+normalized path, no query string or credentials.</summary>
    [JsonIgnore] public string Identity => $"{PageOrigin}{PagePath}";
    [JsonIgnore] public string Title => string.IsNullOrWhiteSpace(DisplayName) ? (PagePath.Length == 0 ? "/" : PagePath) : DisplayName!;
}
