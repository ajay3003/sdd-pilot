using System.Text.Json.Serialization;

namespace BirkNext.Standards;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StandardsMappingType { Direct, Related, Informational }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StandardsEvidenceScope
{
    BrowserRuntime, FrontendRuntime, FrontendSource, FrontendConfiguration, ApiRuntime, ApiSource, ApiContract,
    IntegrationConfiguration, IntegrationContract, IntegrationRuntime, Manual
}

/// <summary>A reference attached to an existing finding; it is metadata and never a separate finding or verdict.</summary>
public sealed record StandardReference
{
    public string StandardId { get; init; } = "";
    public string StandardName { get; init; } = "";
    public string? Version { get; init; }
    public string ReferenceId { get; init; } = "";
    public string? Title { get; init; }
    public string? Url { get; init; }
    public StandardsMappingType MappingType { get; init; }
    public StandardsEvidenceScope EvidenceScope { get; init; }
    public string? Notes { get; init; }
}

/// <summary>Explicit, stable-rule mapping registry. Deliberately sparse: absence means no defensible reference.</summary>
public static class StandardsReferenceMappings
{
    public static IReadOnlyList<StandardReference> ForFrontendRule(string ruleId) => ruleId switch
    {
        "std-csp-missing" or "std-hsts-missing" or "std-xcontenttypeoptions-missing" =>
            [Owasp("A05:2021", "Security Misconfiguration", StandardsMappingType.Related, StandardsEvidenceScope.FrontendRuntime)],
        _ => [],
    };

    public static IReadOnlyList<StandardReference> ForApiRule(string ruleId) => ruleId switch
    {
        "sec-no-hsts" or "sec-no-xcto" or "sec-server-exposed" or "sec-cors-wildcard" =>
            [Owasp("A05:2021", "Security Misconfiguration", StandardsMappingType.Related, StandardsEvidenceScope.ApiRuntime)],
        "gql-error-leak" or "gql-leak" =>
            [Owasp("A05:2021", "Security Misconfiguration", StandardsMappingType.Related, StandardsEvidenceScope.ApiRuntime)],
        "rest-latency" or "gql-latency" or "gql-average-latency" =>
            [Iso("Performance efficiency", StandardsEvidenceScope.ApiRuntime)],
        "gql-compatibility" or "contract-shape" or "contract-operation" =>
            [Iso("Compatibility", StandardsEvidenceScope.ApiContract)],
        _ => [],
    };

    public static IReadOnlyList<StandardReference> ForIntegrationRule(string ruleId) => ruleId switch
    {
        "sec-tls" => [Owasp("A02:2021", "Cryptographic Failures", StandardsMappingType.Related, StandardsEvidenceScope.IntegrationRuntime)],
        "contract-incompatible" => [Iso("Compatibility", StandardsEvidenceScope.IntegrationContract)],
        "consumer-progress-stale" => [Iso("Reliability", StandardsEvidenceScope.IntegrationRuntime)],
        _ => [],
    };

    public static StandardReference Wcag(string criterionId, string? title, StandardsEvidenceScope scope) => new()
    {
        StandardId = "WCAG22", StandardName = "WCAG 2.2", Version = "2.2", ReferenceId = criterionId,
        Title = title, MappingType = StandardsMappingType.Direct, EvidenceScope = scope,
        Url = $"https://www.w3.org/TR/WCAG22/#{criterionId.Replace('.', '-')}",
        Notes = "A criterion reference for this observed finding; it does not establish overall WCAG conformance.",
    };

    private static StandardReference Owasp(string id, string title, StandardsMappingType type, StandardsEvidenceScope scope) => new()
    {
        StandardId = "OWASP-TOP10", StandardName = "OWASP Top 10", Version = "2021", ReferenceId = id,
        Title = title, MappingType = type, EvidenceScope = scope,
        Notes = "Related guidance reference only; this mapping does not establish OWASP compliance.",
    };

    private static StandardReference Iso(string title, StandardsEvidenceScope scope) => new()
    {
        StandardId = "ISO25010", StandardName = "ISO/IEC 25010", Version = null,
        ReferenceId = title, Title = title, MappingType = StandardsMappingType.Informational, EvidenceScope = scope,
        Notes = "Quality-characteristic context only; no certification or overall ISO assessment is implied.",
    };
}
