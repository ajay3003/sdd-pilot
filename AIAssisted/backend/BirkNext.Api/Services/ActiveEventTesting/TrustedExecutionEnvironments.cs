using BirkNext.Integrations;

namespace BirkNext.Api.Services.ActiveEventTesting;

/// <summary>
/// One backend-owned trusted execution environment. It is the only authority for an environment's type, target URL and whether it may
/// execute active events, and it enrolls the exact integrations that may be used. <see cref="EnvironmentId"/> is the stable id the
/// integration catalog is keyed by; the browser's Target Environment profile only selects it — its type, URL and labels are UI state.
/// </summary>
public sealed record TrustedExecutionEnvironment(
    string EnvironmentId,
    string DisplayName,
    string EnvironmentType,
    bool ExecutionAllowed,
    string? TargetUrl,
    IReadOnlyList<string> IntegrationIds,
    IReadOnlyDictionary<string, string> MonitoringReferences)
{
    /// <summary>The components (services) that run in this environment and where (configuration only; read by Integration Topology).</summary>
    public IReadOnlyList<TrustedComponent> Components { get; init; } = [];
}

/// <summary>
/// One component of a trusted environment (<c>TargetEnvironments:Trusted:n:Components:m</c>): its name, base address and, optionally, the
/// relative path of its OpenAPI document. Where it runs, not what it exposes: interfaces come from discovery.
/// </summary>
public sealed record TrustedComponent(string Name, string? BaseUrl, string? OpenApiPath);

public interface ITrustedExecutionEnvironmentRegistry
{
    /// <summary>The single trusted record for the id, or null with the reason (missing, duplicated or invalid records fail closed).</summary>
    TrustedExecutionEnvironment? Resolve(string environmentId, out string reason);
    ActiveEventEnvironmentTrust Describe(string environmentId);
}

/// <summary>
/// Reads <c>TargetEnvironments:Trusted</c> from backend configuration (never from a request). Nothing here is writable through the API, so
/// a browser cannot declare itself QA, change a target URL or enroll an integration.
/// </summary>
public sealed class TrustedExecutionEnvironmentRegistry(IReadOnlyList<TrustedExecutionEnvironment> environments) : ITrustedExecutionEnvironmentRegistry
{
    public static TrustedExecutionEnvironmentRegistry From(IConfiguration configuration) => new(configuration.GetSection("TargetEnvironments:Trusted").GetChildren()
        .Select(section => new TrustedExecutionEnvironment(
            (section["EnvironmentId"] ?? "").Trim(),
            (section["DisplayName"] ?? "").Trim(),
            (section["EnvironmentType"] ?? "Unknown").Trim(),
            bool.TryParse(section["ExecutionAllowed"], out var allowed) && allowed,
            string.IsNullOrWhiteSpace(section["TargetUrl"]) ? null : section["TargetUrl"]!.Trim(),
            section.GetSection("IntegrationIds").GetChildren().Select(item => (item.Value ?? "").Trim()).Where(item => item.Length > 0).ToArray(),
            section.GetSection("MonitoringReferences").GetChildren().Where(item => !string.IsNullOrWhiteSpace(item.Value))
                .ToDictionary(item => item.Key, item => item.Value!, StringComparer.Ordinal))
        {
            Components = [.. section.GetSection("Components").GetChildren().Where(item => !string.IsNullOrWhiteSpace(item["Name"]))
                .Select(item => new TrustedComponent(item["Name"]!.Trim(), item["BaseUrl"]?.Trim(), item["OpenApiPath"]?.Trim()))],
        })
        .ToArray());

    public TrustedExecutionEnvironment? Resolve(string environmentId, out string reason)
    {
        var matches = environments.Where(item => item.EnvironmentId.Length > 0 && string.Equals(item.EnvironmentId, environmentId, StringComparison.Ordinal)).Take(2).ToArray();
        if (matches.Length == 0)
        {
            reason = "This environment is not a trusted execution environment in backend configuration (TargetEnvironments:Trusted).";
            return null;
        }
        if (matches.Length > 1)
        {
            reason = "This environment id is configured more than once in TargetEnvironments:Trusted; ambiguous trust is refused.";
            return null;
        }
        var match = matches[0];
        if (match.DisplayName.Length == 0)
        {
            reason = "The trusted environment record has no display name; incomplete records are refused.";
            return null;
        }
        reason = $"Backend-owned trusted environment {match.DisplayName} ({match.EnvironmentType}).";
        return match;
    }

    public ActiveEventEnvironmentTrust Describe(string environmentId)
    {
        var trusted = Resolve(environmentId, out var reason);
        return trusted is null
            ? new ActiveEventEnvironmentTrust { EnvironmentId = environmentId, Trusted = false, Detail = reason }
            : new ActiveEventEnvironmentTrust
            {
                EnvironmentId = environmentId, Trusted = true, DisplayName = trusted.DisplayName, EnvironmentType = trusted.EnvironmentType,
                ExecutionAllowed = trusted.ExecutionAllowed, IntegrationIds = trusted.IntegrationIds,
                Detail = !trusted.ExecutionAllowed ? "Trusted, but active execution is not allowed for this environment."
                    : ActiveEventPolicy.EnvironmentBlock(trusted.EnvironmentType) ?? reason,
            };
    }
}
