using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.LocalHttpsProxy;
using BirkNext.ApiReview;
using Microsoft.Extensions.Options;

namespace BirkNext.Api.Services.ApiQuality;

/// <summary>Backend configuration of active API testing (section <c>ApiActiveTesting</c>).</summary>
public sealed class ApiActiveTestingOptions
{
    public const string SectionName = "ApiActiveTesting";
    /// <summary>Master switch for safe fuzzing on this BirkNext instance. Error probes follow the environment decision only.</summary>
    public bool FuzzingEnabled { get; set; } = true;
    /// <summary>Hosts (exact or <c>*.suffix</c>) never actively tested, whatever the classification.</summary>
    public List<string> BlockedHosts { get; set; } = [];
}

public interface IApiEnvironmentSafetyPolicy
{
    ApiEnvironmentSafetyDecision Evaluate(ApiReviewRunRequest request);
    ApiEnvironmentSafetyDecision EvaluateForFuzzing(ApiReviewRunRequest request);
}

/// <summary>
/// The backend authority for active API testing. The client's <c>IsProduction</c> flag and environment type are inputs, never the
/// decision: a production claim, a production host marker on any selected target or the environment URL, a disagreement with the
/// server-held Local HTTPS proxy context of the same Target Environment, an unknown type or a type outside
/// <see cref="ApiActiveTestingEnvironments.Allowed"/> blocks active testing. Unknown fails closed.
/// </summary>
public sealed class ApiEnvironmentSafetyPolicy(ILocalHttpsProxyStatusQuery? proxy = null, IOptions<ApiActiveTestingOptions>? options = null) : IApiEnvironmentSafetyPolicy
{
    private static readonly string[] KnownTypes = ["Local", "Development", "QA", "Test", "RC", "Production", "Custom"];

    /// <summary>Policy without server-held proxy state (unit tests, callers outside DI).</summary>
    public static ApiEnvironmentSafetyPolicy Default { get; } = new();

    public ApiEnvironmentSafetyDecision Evaluate(ApiReviewRunRequest request)
    {
        var settings = options?.Value ?? new ApiActiveTestingOptions();
        var environment = request.Environment;
        var claimed = string.IsNullOrWhiteSpace(environment.EnvironmentType) ? null : environment.EnvironmentType.Trim();
        var evidence = new List<string> { $"Target Environment classification: {claimed ?? "not set"}" };

        var hosts = request.Targets.Where(t => t.Selected).Select(t => t.Host)
            .Append(Uri.TryCreate(environment.TargetUrl, UriKind.Absolute, out var targetUri) ? targetUri.Host : null)
            .Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h!.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList();
        var markerHost = hosts.FirstOrDefault(h => !IsLoopback(h) && ActiveEventPolicy.LooksLikeProduction(h));
        var blockedHost = hosts.FirstOrDefault(h => settings.BlockedHosts.Any(p => HostMatches(h, p)));
        evidence.Add(markerHost is null ? $"Host production markers: none on {hosts.Count} host(s)" : $"Host production marker: {markerHost}");

        string? serverType = null;
        if (proxy is not null && !string.IsNullOrWhiteSpace(request.Identity.ProfileId) && !string.IsNullOrWhiteSpace(request.Identity.ContextFingerprint))
        {
            serverType = proxy.StatusForProfile(request.Identity.ProfileId!, request.Identity.ContextFingerprint!)?.EnvironmentType?.Trim();
            evidence.Add(serverType is null ? "Server-held proxy context: none for this Target Environment" : $"Server-held proxy context classification: {serverType}");
        }
        else evidence.Add("Server-held proxy context: not used (no proxy identity)");

        var productionClaim = environment.IsProduction || Is(claimed, "Production") || Is(serverType, "Production");
        var productionLike = productionClaim || markerHost is not null;
        ApiEnvironmentSafetyDecision Decide(ApiEnvironmentSafetyState state, string reason) => new()
        {
            State = state, EnvironmentType = claimed, ProductionLike = productionLike, Reason = reason, Evidence = evidence,
        };

        if (productionClaim) return Decide(ApiEnvironmentSafetyState.ProductionBlocked, "Production environments are never actively tested: passive, read-only review only.");
        if (markerHost is not null) return Decide(ApiEnvironmentSafetyState.ProductionMarkerBlocked, $"The host {markerHost} carries a production marker; active testing is blocked whatever the environment classification.");
        if (blockedHost is not null) return Decide(ApiEnvironmentSafetyState.NotPermittedBlocked, $"The host {blockedHost} is blocked for active API testing in backend configuration ({ApiActiveTestingOptions.SectionName}:BlockedHosts).");
        if (serverType is not null && !Is(claimed, serverType))
            return Decide(ApiEnvironmentSafetyState.ConflictBlocked, $"The review claims {claimed ?? "no classification"} but the server-held proxy context of this Target Environment is {serverType}; environment safety could not be established.");
        if (claimed is null || !KnownTypes.Contains(claimed, StringComparer.OrdinalIgnoreCase))
            return Decide(ApiEnvironmentSafetyState.UnknownBlocked, "Environment safety could not be established: the environment type is unknown. Classify the Target Environment as Local, Development, QA or Test.");
        if (!ApiActiveTestingEnvironments.IsAllowed(claimed))
            return Decide(ApiEnvironmentSafetyState.NotPermittedBlocked, $"Active API testing runs only against Local, Development, QA or Test environments, not {claimed}.");
        return Decide(ApiEnvironmentSafetyState.Allowed, $"{claimed} environment: bounded active API testing is permitted.");
    }

    /// <summary>Fuzzing additionally needs the instance switch. Error probes only need the environment decision.</summary>
    public ApiEnvironmentSafetyDecision EvaluateForFuzzing(ApiReviewRunRequest request)
    {
        var decision = Evaluate(request);
        if (decision.ActiveTestingAllowed && !(options?.Value ?? new ApiActiveTestingOptions()).FuzzingEnabled)
            return decision with { State = ApiEnvironmentSafetyState.Disabled, Reason = $"Safe fuzzing is disabled for this BirkNext instance ({ApiActiveTestingOptions.SectionName}:FuzzingEnabled is false)." };
        return decision;
    }

    private static bool Is(string? value, string expected) => value is not null && string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

    private static bool IsLoopback(string host) =>
        host is "localhost" or "127.0.0.1" or "::1" or "[::1]" || host.EndsWith(".localhost", StringComparison.Ordinal) || host.StartsWith("127.", StringComparison.Ordinal);

    private static bool HostMatches(string host, string pattern)
    {
        var p = pattern.Trim().ToLowerInvariant();
        return p.StartsWith("*.", StringComparison.Ordinal) ? host.EndsWith(p[1..], StringComparison.Ordinal) : host == p;
    }
}
