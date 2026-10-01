using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BirkNext.SourceArchitecture;

namespace BirkNext.SecurityExpectations;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SecurityExpectationField { Authority, TenantId, ClientId, RedirectUrl, BackendDomain, RestHost, GraphQlHost, CdnHost, SecurityHeader }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SecurityCandidateState { Detected, Suggested, Conflict, Stale, Rejected, Accepted }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SecurityExpectationOrigin { Existing, Default, Manual, AcceptedFromSource }

public sealed record SecurityExpectationCandidate
{
    public string Id { get; init; } = "";
    public SecurityExpectationField FieldType { get; init; }
    public string Value { get; init; } = "";
    public string NormalizedValue { get; init; } = "";
    public SecurityCandidateState CandidateState { get; init; }
    public ArchitectureEvidenceState EvidenceState { get; init; }
    public string Confidence { get; init; } = "";
    public Guid SourceSnapshotId { get; init; }
    public string SourceComponent { get; init; } = "";
    public string SourceFile { get; init; } = "";
    public int SourceLine { get; init; }
    public string SourceSymbol { get; init; } = "";
    public string EvidenceType { get; init; } = "";
    public string Explanation { get; init; } = "";
    public bool IsCurrent { get; init; } = true;
    public string? ConflictGroupId { get; init; }
    public string SuggestedAction { get; init; } = "Review";
}

public sealed record SecuritySourceEvidence
{
    public Guid SourceSnapshotId { get; init; }
    public string SourceFingerprint { get; init; } = "";
    public int AnalyzerVersion { get; init; } = 1;
    public List<SecurityExpectationCandidate> Candidates { get; init; } = [];
    public List<string> Diagnostics { get; init; } = [];
    public List<string> UnsupportedEvidence { get; init; } = [];
}

public sealed record SecurityExpectationDiscoveryResult
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string TargetEnvironmentId { get; init; } = "";
    public Guid SourceSnapshotId { get; init; }
    public string SourceFingerprint { get; init; } = "";
    public int AnalyzerVersion { get; init; } = 1;
    public DateTimeOffset ExtractedAt { get; init; }
    public List<SecurityExpectationCandidate> Candidates { get; init; } = [];
    public List<string> Conflicts { get; init; } = [];
    public List<string> Diagnostics { get; init; } = [];
    public List<string> UnsupportedEvidence { get; init; } = [];
    public ArchitectureStatus Status { get; init; }
    public bool IsCurrent { get; init; }
    public int Revision { get; init; }
}

public sealed record SecurityExpectationProvenance(SecurityExpectationField FieldType, string NormalizedValue, SecurityExpectationOrigin Origin,
    Guid? SourceSnapshotId = null, string? SourceFingerprint = null, DateTimeOffset? AcceptedAt = null, string? SourceCandidateId = null);

/// <summary>Approved Target Environment configuration only. Discovery candidates never belong here.</summary>
public class ApprovedSecurityExpectations
{
    [JsonPropertyName("expectedAuthority")] public string? ExpectedAuthority { get; set; }
    [JsonPropertyName("expectedTenant")] public string? ExpectedTenant { get; set; }
    [JsonPropertyName("expectedClientId")] public string? ExpectedClientId { get; set; }
    [JsonPropertyName("allowedRedirectUrls")] public List<string> AllowedRedirectUrls { get; set; } = [];
    [JsonPropertyName("allowedBackendDomains")] public List<string> AllowedBackendDomains { get; set; } = [];
    [JsonPropertyName("allowedRestHosts")] public List<string> AllowedRestHosts { get; set; } = [];
    [JsonPropertyName("allowedGraphQlHosts")] public List<string> AllowedGraphQlHosts { get; set; } = [];
    [JsonPropertyName("allowedCdnHosts")] public List<string> AllowedCdnHosts { get; set; } = [];
    [JsonPropertyName("expectedSecurityHeaders")] public List<string> ExpectedSecurityHeaders { get; set; } = [.. DefaultHeaders];
    [JsonPropertyName("origins")] public List<SecurityExpectationProvenance> Origins { get; set; } = [];
    public static readonly string[] DefaultHeaders = ["Content-Security-Policy", "X-Content-Type-Options", "Referrer-Policy", "Permissions-Policy", "Strict-Transport-Security"];
}

public sealed record SecurityDiscoveryRequest(Guid SourceSnapshotId, ApprovedSecurityExpectations Approved);
public sealed record SecurityCandidateReviewRequest(Guid DiscoveryId, string CandidateId, int Revision, ApprovedSecurityExpectations Approved, bool Replace = false);
public sealed record SecurityCandidateReviewResponse(SecurityExpectationDiscoveryResult Discovery, ApprovedSecurityExpectations Approved);
public sealed record SecurityCandidateDecision(string CandidateId, SecurityCandidateState State, DateTimeOffset At);

/// <summary>Conservative comparison and approval projection shared by API and UI; no network or inference.</summary>
public static class SecurityExpectationValues
{
    public static bool Singleton(SecurityExpectationField field) => field is SecurityExpectationField.Authority or SecurityExpectationField.TenantId or SecurityExpectationField.ClientId;
    public static string Label(SecurityExpectationField field) => field switch {
        SecurityExpectationField.Authority => "Expected Authority", SecurityExpectationField.TenantId => "Expected Tenant",
        SecurityExpectationField.ClientId => "Expected Client ID", SecurityExpectationField.RedirectUrl => "Allowed Redirect URLs",
        SecurityExpectationField.BackendDomain => "Allowed Backend Domains", SecurityExpectationField.RestHost => "Allowed REST Hosts",
        SecurityExpectationField.GraphQlHost => "Allowed GraphQL Hosts", SecurityExpectationField.CdnHost => "Allowed CDN Hosts", _ => "Expected Security Headers" };
    public static IEnumerable<string> Values(ApprovedSecurityExpectations settings, SecurityExpectationField field) => field switch {
        SecurityExpectationField.Authority => settings.ExpectedAuthority is { Length: > 0 } a ? [a] : [],
        SecurityExpectationField.TenantId => settings.ExpectedTenant is { Length: > 0 } t ? [t] : [],
        SecurityExpectationField.ClientId => settings.ExpectedClientId is { Length: > 0 } c ? [c] : [],
        SecurityExpectationField.RedirectUrl => settings.AllowedRedirectUrls, SecurityExpectationField.BackendDomain => settings.AllowedBackendDomains,
        SecurityExpectationField.RestHost => settings.AllowedRestHosts, SecurityExpectationField.GraphQlHost => settings.AllowedGraphQlHosts,
        SecurityExpectationField.CdnHost => settings.AllowedCdnHosts, _ => settings.ExpectedSecurityHeaders };
    public static bool SecretShaped(string value) => Regex.IsMatch(value, @"(?i)(secret|password|access.?token|refresh.?token|api.?key|sharedaccess|accountkey|sig=|bearer\s|connectionstring|eyJ[A-Za-z0-9_-]{10})");
    public static string? Normalize(SecurityExpectationField field, string value)
    {
        value = value.Trim();
        if (value.Length is 0 or > 500 || SecretShaped(value) || value.Contains('*') || value.Any(char.IsControl)) return null;
        if (field == SecurityExpectationField.ClientId) return Guid.TryParse(value, out var client) ? client.ToString("D")
            : Regex.IsMatch(value, @"^[A-Za-z0-9][A-Za-z0-9._-]{1,127}$") ? value : null;
        if (field == SecurityExpectationField.TenantId) return Guid.TryParse(value, out var tenant) ? tenant.ToString("D")
            : Regex.IsMatch(value, @"^[A-Za-z0-9][A-Za-z0-9.-]*\.[A-Za-z]{2,}$") ? value.ToLowerInvariant() : null;
        if (field == SecurityExpectationField.SecurityHeader)
            return new[] { "Content-Security-Policy", "X-Content-Type-Options", "Referrer-Policy", "Permissions-Policy", "Strict-Transport-Security", "X-Frame-Options" }
                .FirstOrDefault(h => h.Equals(value, StringComparison.OrdinalIgnoreCase));
        var urlField = field is SecurityExpectationField.Authority or SecurityExpectationField.RedirectUrl;
        var input = urlField || value.Contains("://") ? value : "https://" + value;
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 ||
            uri.Fragment.Length > 0 || SecretShaped(uri.Query) || Uri.CheckHostName(uri.IdnHost.TrimEnd('.')) == UriHostNameType.Unknown) return null;
        var name = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        var host = (uri.HostNameType == UriHostNameType.IPv6 ? "[" + name.Trim('[', ']') + "]" : name) + (uri.IsDefaultPort ? "" : ":" + uri.Port);
        if (!urlField) return uri.Query.Length == 0 && (value.Contains("://") || uri.AbsolutePath == "/") ? host : null;
        if (uri.Query.Length > 0) return null;
        return uri.Scheme.ToLowerInvariant() + "://" + host + uri.AbsolutePath + uri.Query;
    }
    public static bool Matches(ApprovedSecurityExpectations settings, SecurityExpectationCandidate candidate) =>
        Values(settings, candidate.FieldType).Any(v => Normalize(candidate.FieldType, v) == candidate.NormalizedValue);
    public static SecurityExpectationOrigin Origin(ApprovedSecurityExpectations settings, SecurityExpectationField field, string value) =>
        settings.Origins.LastOrDefault(p => p.FieldType == field && p.NormalizedValue == Normalize(field, value))?.Origin ?? SecurityExpectationOrigin.Existing;
    public static ApprovedSecurityExpectations Copy(ApprovedSecurityExpectations s) => new() {
        ExpectedAuthority = s.ExpectedAuthority, ExpectedTenant = s.ExpectedTenant, ExpectedClientId = s.ExpectedClientId,
        AllowedRedirectUrls = [.. s.AllowedRedirectUrls], AllowedBackendDomains = [.. s.AllowedBackendDomains], AllowedRestHosts = [.. s.AllowedRestHosts],
        AllowedGraphQlHosts = [.. s.AllowedGraphQlHosts], AllowedCdnHosts = [.. s.AllowedCdnHosts], ExpectedSecurityHeaders = [.. s.ExpectedSecurityHeaders], Origins = [.. s.Origins] };
    public static ApprovedSecurityExpectations Accept(ApprovedSecurityExpectations current, SecurityExpectationCandidate candidate, string fingerprint, bool replace, DateTimeOffset at)
    {
        if (!candidate.IsCurrent || candidate.EvidenceState is ArchitectureEvidenceState.Unresolved or ArchitectureEvidenceState.Conflict ||
            Normalize(candidate.FieldType, candidate.Value) != candidate.NormalizedValue)
            throw new InvalidOperationException("Candidate is stale or unresolved; refresh discovery or review source evidence.");
        if (Singleton(candidate.FieldType) && Values(current, candidate.FieldType).Any() && !Matches(current, candidate) && !replace)
            throw new InvalidOperationException("An approved value exists. Explicit replacement is required.");
        var next = Copy(current);
        if (candidate.FieldType == SecurityExpectationField.Authority) next.ExpectedAuthority = candidate.Value;
        else if (candidate.FieldType == SecurityExpectationField.TenantId) next.ExpectedTenant = candidate.Value;
        else if (candidate.FieldType == SecurityExpectationField.ClientId) next.ExpectedClientId = candidate.Value;
        else {
            var list = (List<string>)Values(next, candidate.FieldType);
            if (!Matches(next, candidate)) list.Add(candidate.Value);
        }
        next.Origins.RemoveAll(p => p.FieldType == candidate.FieldType && (Singleton(candidate.FieldType) || p.NormalizedValue == candidate.NormalizedValue));
        next.Origins.Add(new(candidate.FieldType, candidate.NormalizedValue, SecurityExpectationOrigin.AcceptedFromSource, candidate.SourceSnapshotId, fingerprint, at, candidate.Id));
        return next;
    }
}
