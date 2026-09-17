using System.Security.Cryptography;
using System.Text;

namespace BirkNext.Api.Services.IntegrationQuality;

/// <summary>
/// Phase 3, Checkpoint 5: deterministic historical identity for an integration.
///
/// IntegrationId is deliberately NOT used as the baseline key. It is assigned a random GUID the
/// first time an integration is seen, deduplicated by display name, and persisted in browser
/// local storage whose failures are swallowed. A rename, a cleared browser, a different machine
/// or a lost write therefore produces a new IntegrationId and would silently orphan history.
/// Public IntegrationId semantics are left untouched; this is an additional key.
///
/// The key is derived only from structural transport identity. Producer, consumer,
/// relationship source, runtime evidence, contract fingerprint, authentication state, findings,
/// timestamps and display name are all excluded on purpose: those must be able to change while
/// identity holds, otherwise they would mint a new identity instead of surfacing as history.
/// </summary>
public static class IntegrationBaselineIdentity
{
    /// <summary>
    /// Version of the identity algorithm. Any change to the inputs or canonicalisation rules must
    /// bump this so stored keys from an older algorithm remain distinguishable.
    /// </summary>
    public const int Version = 1;

    public static string Compute(string? environmentId, IntegrationConfigDto integration) =>
        Describe(environmentId, integration).Key;

    /// <summary>
    /// Returns the key together with the canonical fields it was derived from, so an operator can
    /// see what an opaque hash represents instead of having to reverse it.
    /// </summary>
    public static IntegrationStructuralIdentity Describe(
        string? environmentId,
        IntegrationConfigDto integration)
    {
        var environment = CanonicalEnvironment(environmentId);
        var endpoint = CanonicalEndpoint(integration.Endpoint);
        var resource = CanonicalResource(integration.Resource);

        // Field-separated with a character that cannot appear in the canonical forms, so
        // ("ab", "c") and ("a", "bc") cannot collide.
        var material = string.Join('',
            Version.ToString(),
            environment,
            integration.Type.ToString(),
            endpoint,
            resource);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));

        return new IntegrationStructuralIdentity
        {
            Version = Version,
            EnvironmentId = environment,
            Type = integration.Type,
            CanonicalEndpoint = endpoint,
            CanonicalResource = resource,
            Key = Convert.ToHexString(hash).ToLowerInvariant()[..32]
        };
    }

    /// <summary>
    /// Environment identity. ProfileId is preferred by callers; the value is trimmed and
    /// lowercased so incidental casing does not split one environment's history in two.
    /// </summary>
    private static string CanonicalEnvironment(string? environmentId) =>
        string.IsNullOrWhiteSpace(environmentId) ? "" : environmentId.Trim().ToLowerInvariant();

    /// <summary>
    /// Endpoint canonicalisation. A URL endpoint keeps scheme, host, non-default port and path,
    /// and drops user info, query and fragment. Host and scheme are lowercased because DNS and
    /// URI schemes are case-insensitive; the path is left as-is because it can be case-sensitive.
    /// A non-URL endpoint (an Event Hub namespace, a Kafka broker list) is a host-style value and
    /// is lowercased.
    ///
    /// This does not reuse DetectionStateComputer.NormalizeUrlForComparison: that helper is a
    /// private detail of staleness comparison and lowercases wholesale, which would destroy the
    /// case of path and resource identifiers that some brokers treat as significant.
    /// </summary>
    private static string CanonicalEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return "";

        var trimmed = endpoint.Trim();

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return trimmed.ToLowerInvariant().TrimEnd('/');

        var scheme = uri.Scheme.ToLowerInvariant();
        var host = uri.Host.ToLowerInvariant();
        var port = uri.IsDefaultPort ? "" : $":{uri.Port}";

        // Credentials must never contribute to, or be recoverable from, a persisted key.
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path == "/") path = "";

        return $"{scheme}://{host}{port}{path}";
    }

    /// <summary>
    /// Resource canonicalisation: trim only. Topic, queue, hub, exchange and subscription names
    /// are case-sensitive on several brokers (Kafka notably), so case is preserved.
    /// </summary>
    private static string CanonicalResource(string? resource) =>
        string.IsNullOrWhiteSpace(resource) ? "" : resource.Trim();
}

/// <summary>
/// The baseline key plus the canonical fields it was computed from, retained for diagnostics.
/// </summary>
public sealed class IntegrationStructuralIdentity
{
    public int Version { get; init; }
    public string EnvironmentId { get; init; } = "";
    public IntegrationType Type { get; init; }
    public string CanonicalEndpoint { get; init; } = "";
    public string CanonicalResource { get; init; } = "";
    public string Key { get; init; } = "";
}
