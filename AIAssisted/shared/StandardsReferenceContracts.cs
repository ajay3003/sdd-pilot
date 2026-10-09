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
    // Keys are the rule ids the engines actually emit (finding RuleId / SourceRuleId). A test scans the engines for emitted ids and fails
    // on any key here that no engine produces, so a renamed rule can no longer leave a silent dead mapping behind.
    private static readonly StandardReference SecurityMisconfigurationFrontend = Owasp("A05:2021", "Security Misconfiguration", StandardsMappingType.Related, StandardsEvidenceScope.FrontendRuntime);
    private static readonly StandardReference SecurityMisconfigurationApi = Owasp("A05:2021", "Security Misconfiguration", StandardsMappingType.Related, StandardsEvidenceScope.ApiRuntime);

    private static readonly Dictionary<string, StandardReference[]> FrontendRules = new(StringComparer.Ordinal)
    {
        ["std-csp-missing"] = [SecurityMisconfigurationFrontend],
        ["std-hsts-missing"] = [SecurityMisconfigurationFrontend],
        // Static Security's own X-Content-Type-Options finding (scanner rule id; no derived "std-" finding exists for it).
        ["HDR-MISSING-X-CONTENT-TYPE-OPTIONS"] = [SecurityMisconfigurationFrontend],
    };

    private static readonly Dictionary<string, StandardReference[]> ApiRules = new(StringComparer.Ordinal)
    {
        ["sec-no-hsts"] = [SecurityMisconfigurationApi],
        ["sec-no-xcto"] = [SecurityMisconfigurationApi],
        ["sec-server-disclosure"] = [SecurityMisconfigurationApi],
        ["cors-wildcard-credentials"] = [SecurityMisconfigurationApi],
        ["cors-wildcard-authenticated"] = [SecurityMisconfigurationApi],
        ["sec-no-tls"] = [Owasp("A02:2021", "Cryptographic Failures", StandardsMappingType.Related, StandardsEvidenceScope.ApiRuntime)],
        ["errors-leak"] = [SecurityMisconfigurationApi],
        ["gql-error-leak"] = [SecurityMisconfigurationApi],
        ["gql-leak"] = [SecurityMisconfigurationApi],
        ["fuzz-information-leak"] = [SecurityMisconfigurationApi],
        ["rest-unexpectedly-public"] = [Owasp("A07:2021", "Identification and Authentication Failures", StandardsMappingType.Related, StandardsEvidenceScope.ApiRuntime)],
        ["gql-unexpectedly-public"] = [Owasp("A07:2021", "Identification and Authentication Failures", StandardsMappingType.Related, StandardsEvidenceScope.ApiRuntime)],
        ["rest-slow"] = [Iso("Performance efficiency", StandardsEvidenceScope.ApiRuntime)],
        ["gql-slow"] = [Iso("Performance efficiency", StandardsEvidenceScope.ApiRuntime)],
        ["gql-operation-incompatible"] = [Iso("Compatibility", StandardsEvidenceScope.ApiContract)],
        ["contract-type-mismatch"] = [Iso("Compatibility", StandardsEvidenceScope.ApiContract)],
        ["contract-undocumented-operation"] = [Iso("Compatibility", StandardsEvidenceScope.ApiContract)],
    };

    private static readonly Dictionary<string, StandardReference[]> IntegrationRules = new(StringComparer.Ordinal)
    {
        ["contract-incompatible"] = [Iso("Compatibility", StandardsEvidenceScope.IntegrationContract)],
        ["consumer-progress-stale"] = [Iso("Reliability", StandardsEvidenceScope.IntegrationRuntime)],
    };

    /// <summary>The mapped rule ids per engine family, for the dead-mapping guard test and documentation.</summary>
    public static IReadOnlyCollection<string> FrontendRuleIds => FrontendRules.Keys;
    public static IReadOnlyCollection<string> ApiRuleIds => ApiRules.Keys;
    public static IReadOnlyCollection<string> IntegrationRuleIds => IntegrationRules.Keys;

    public static IReadOnlyList<StandardReference> ForFrontendRule(string ruleId) => FrontendRules.TryGetValue(ruleId, out var r) ? r : [];
    public static IReadOnlyList<StandardReference> ForApiRule(string ruleId) => ApiRules.TryGetValue(ruleId, out var r) ? r : [];
    public static IReadOnlyList<StandardReference> ForIntegrationRule(string ruleId) => IntegrationRules.TryGetValue(ruleId, out var r) ? r : [];

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
