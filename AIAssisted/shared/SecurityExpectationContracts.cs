using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BirkNext.SourceArchitecture;

namespace BirkNext.SecurityExpectations;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SecurityExpectationField { Authority, TenantId, ClientId, RedirectUrl, BackendDomain, RestHost, GraphQlHost, CdnHost, SecurityHeader }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SecurityCandidateState { Detected, Suggested, Conflict, Stale, Rejected, Accepted, NeedsReview, MatchesSource }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SecurityExpectationOrigin { Existing, Default, Manual, AcceptedFromSource }

public sealed record SecurityExpectationCandidate
{
    public List<string> SourceOccurrenceIds { get; init; } = [];
    public List<SecurityExpectationEvidence> SupportingEvidence { get; init; } = [];
    public int SupportingEvidenceCount => SupportingEvidence.Count == 0 ? 1 : SupportingEvidence.Count;
    public List<Guid> SourceSnapshotIds => SupportingEvidence.Count == 0 ? [SourceSnapshotId] : SupportingEvidence.Select(e => e.SourceSnapshotId).Distinct().ToList();
    public string NormalizationRule { get; init; } = "Conservative field-specific normalization";
    public string Id { get; init; } = "";
    public SecurityExpectationField FieldType { get; init; }
    public string Value { get; init; } = "";
    public string NormalizedValue { get; init; } = "";
    public string FormatState { get; init; } = "Valid";
    public string EnvironmentScope { get; init; } = "";
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

public sealed record SecurityExpectationEvidence(Guid SourceSnapshotId, string Repository, string FilePath,
    int Line, string SymbolOrKey, string EvidenceKind, string RawValue, string Confidence, ArchitectureEvidenceState State, string SourceFingerprint = "", string EnvironmentScope = "");
public sealed record SecurityExpectationSourceScope(Guid PrimarySourceSnapshotId, List<Guid> RelatedSourceSnapshotIds);
public sealed record SecurityExpectationFieldDefinition(SecurityExpectationField Field, bool IsSingleton, string ValueType, string NormalizationRule);

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
    public List<SecurityCandidateDecision> ReviewDecisions { get; init; } = [];
    public Dictionary<Guid, string> SourceFingerprints { get; init; } = [];
    public SecurityExpectationSourceScope? SourceScope { get; init; }
    public string SourceDisplayName { get; init; } = "";
    public DateTimeOffset? SourceAnalyzedAt { get; init; }
    public int SupportingEvidenceCount => Candidates.Sum(c => c.SupportingEvidenceCount);
    public int NeedsReviewCount => Candidates.Where(c => c.IsCurrent && c.CandidateState is SecurityCandidateState.Detected or SecurityCandidateState.Suggested or SecurityCandidateState.NeedsReview or SecurityCandidateState.Conflict).Select(c => c.FieldType).Distinct().Count();
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
    Guid? SourceSnapshotId = null, string? SourceFingerprint = null, DateTimeOffset? AcceptedAt = null, string? SourceCandidateId = null, string? Scope = null);

/// <summary>Approved Target Environment configuration only. Discovery candidates never belong here.</summary>
public class ApprovedSecurityExpectations
{
    [JsonPropertyName("expectedAuthority")] public string? ExpectedAuthority { get; set; }
    [JsonPropertyName("expectedTenant")] public string? ExpectedTenant { get; set; }
    /// <summary>Legacy project-wide Client ID approval. Kept as-is; it is never redistributed to component scopes automatically.</summary>
    [JsonPropertyName("expectedClientId")] public string? ExpectedClientId { get; set; }
    /// <summary>Client/Application IDs approved per component client registration (component · configuration section).</summary>
    [JsonPropertyName("scopedClientIds")] public List<ScopedSecurityValue> ScopedClientIds { get; set; } = [];
    [JsonPropertyName("allowedRedirectUrls")] public List<string> AllowedRedirectUrls { get; set; } = [];
    [JsonPropertyName("allowedBackendDomains")] public List<string> AllowedBackendDomains { get; set; } = [];
    [JsonPropertyName("allowedRestHosts")] public List<string> AllowedRestHosts { get; set; } = [];
    [JsonPropertyName("allowedGraphQlHosts")] public List<string> AllowedGraphQlHosts { get; set; } = [];
    [JsonPropertyName("allowedCdnHosts")] public List<string> AllowedCdnHosts { get; set; } = [];
    [JsonPropertyName("expectedSecurityHeaders")] public List<string> ExpectedSecurityHeaders { get; set; } = [.. DefaultHeaders];
    [JsonPropertyName("origins")] public List<SecurityExpectationProvenance> Origins { get; set; } = [];
    public static readonly string[] DefaultHeaders = ["Content-Security-Policy", "X-Content-Type-Options", "Referrer-Policy", "Permissions-Policy", "Strict-Transport-Security"];
}

public sealed record SecurityDiscoveryRequest(Guid SourceSnapshotId, ApprovedSecurityExpectations Approved, List<Guid>? RelatedSourceSnapshotIds = null);
/// <summary>Scope: for a component-scoped expectation (Client/Application IDs) the component scope the approval applies to.</summary>
public sealed record SecurityCandidateReviewRequest(Guid DiscoveryId, string CandidateId, int Revision, ApprovedSecurityExpectations Approved, bool Replace = false, string? Scope = null);
public sealed record SecurityCandidateReviewResponse(SecurityExpectationDiscoveryResult Discovery, ApprovedSecurityExpectations Approved);
public sealed record SecurityCandidateDecision(string CandidateId, SecurityCandidateState State, DateTimeOffset At);

/// <summary>Conservative comparison and approval projection shared by API and UI; no network or inference.</summary>
public static class SecurityExpectationValues
{
    public static SecurityExpectationFieldDefinition Definition(SecurityExpectationField field) => new(field, Singleton(field),
        field is SecurityExpectationField.Authority or SecurityExpectationField.RedirectUrl ? "URL" :
        field is SecurityExpectationField.TenantId or SecurityExpectationField.ClientId ? "Identifier" : field == SecurityExpectationField.SecurityHeader ? "Header name" : "Host and port",
        field switch {
            SecurityExpectationField.Authority => "URL scheme/host normalization; preserve authority path and version; ignore one final slash",
            SecurityExpectationField.RedirectUrl => "URL scheme/host normalization; preserve path case and trailing slash",
            SecurityExpectationField.TenantId => "GUID formatting or domain case; no GUID/domain alias mapping",
            SecurityExpectationField.ClientId => "GUID formatting; exact comparison for legacy public identifiers",
            SecurityExpectationField.SecurityHeader => "Allow-listed case-insensitive header name; no header policies stored",
            _ => "Host case and DNS terminal dot; preserve non-default port; endpoint path is outside host expectation" });
    public static SecurityCandidateState DeriveState(SecurityExpectationCandidate candidate, ApprovedSecurityExpectations approved, int uniqueFieldCandidates)
    {
        if (!candidate.IsCurrent) return SecurityCandidateState.Stale;
        if (candidate.FormatState != "Valid") return SecurityCandidateState.NeedsReview;
        if (candidate.CandidateState == SecurityCandidateState.Rejected) return SecurityCandidateState.Rejected;
        if (Matches(approved, candidate)) return SecurityCandidateState.MatchesSource;
        if (Singleton(candidate.FieldType))
        {
            if (Values(approved, candidate.FieldType).Any()) return SecurityCandidateState.Conflict;
            if (uniqueFieldCandidates > 1) return SecurityCandidateState.NeedsReview;
        }
        return candidate.EvidenceState == ArchitectureEvidenceState.Inferred ? SecurityCandidateState.Suggested : SecurityCandidateState.Detected;
    }
    public static string AcceptAction(SecurityExpectationCandidate candidate, ApprovedSecurityExpectations approved, int uniqueFieldCandidates)
        => DeriveState(candidate, approved, uniqueFieldCandidates) == SecurityCandidateState.Conflict ? "Replace with detected" :
            !Singleton(candidate.FieldType) ? "Add" : uniqueFieldCandidates > 1 ? "Choose this value" : "Accept";
    public static List<SecurityExpectationCandidate> Group(IEnumerable<SecurityExpectationCandidate> observations)
        => observations.Where(c => !string.IsNullOrWhiteSpace(c.NormalizedValue))
        .GroupBy(c => (c.FieldType, c.EnvironmentScope, c.NormalizedValue)).OrderBy(g => g.Key.FieldType).ThenBy(g => g.Key.EnvironmentScope, StringComparer.Ordinal).ThenBy(g => g.Key.NormalizedValue, StringComparer.Ordinal)
        .Select(g => {
            var first = g.OrderBy(c => c.SourceComponent, StringComparer.Ordinal).ThenBy(c => c.SourceFile, StringComparer.Ordinal).ThenBy(c => c.SourceSymbol, StringComparer.Ordinal).First();
            var evidence = g.SelectMany(c => c.SupportingEvidence.Count > 0 ? c.SupportingEvidence : [new SecurityExpectationEvidence(c.SourceSnapshotId,c.SourceComponent,c.SourceFile,c.SourceLine,c.SourceSymbol,c.EvidenceType,c.Value,c.Confidence,c.EvidenceState, EnvironmentScope:c.EnvironmentScope)])
                .OrderBy(EvidenceIdentity,StringComparer.Ordinal).ThenBy(e => e.State).ThenBy(e => e.Confidence,StringComparer.Ordinal)
                .DistinctBy(EvidenceIdentity).OrderBy(e => EvidencePriority(e.State)).ThenBy(e => e.Repository,StringComparer.Ordinal)
                .ThenBy(e => e.FilePath,StringComparer.Ordinal).ThenBy(e => e.SymbolOrKey,StringComparer.Ordinal).ThenBy(e => e.Line).ToList();
            var key = $"{g.Key.FieldType}|{g.Key.EnvironmentScope}|{g.Key.NormalizedValue}";
            var id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));
            var source = evidence.FirstOrDefault();
            return first with { Id = id, Value = first.FormatState == "Valid" ? g.Key.NormalizedValue : first.Value, EnvironmentScope = g.Key.EnvironmentScope, SupportingEvidence = evidence,
                SourceSnapshotId = source?.SourceSnapshotId ?? first.SourceSnapshotId,
                SourceFile = source?.FilePath ?? first.SourceFile,
                SourceLine = source?.Line ?? first.SourceLine, SourceSymbol = source?.SymbolOrKey ?? first.SourceSymbol,
                SourceOccurrenceIds = g.SelectMany(c => c.SourceOccurrenceIds.Count > 0 ? c.SourceOccurrenceIds : [c.Id]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
                NormalizationRule = Definition(first.FieldType).NormalizationRule };
        }).ToList();
    private static string EvidenceIdentity(SecurityExpectationEvidence e) => string.Concat(new[] { e.SourceSnapshotId.ToString("N"),
        e.SourceFingerprint, e.EnvironmentScope, e.Repository, e.FilePath, e.Line.ToString(System.Globalization.CultureInfo.InvariantCulture), e.SymbolOrKey, e.EvidenceKind, e.RawValue }
        .Select(part => $"{part.Length}:{part}"));
    private static int EvidencePriority(ArchitectureEvidenceState state) => state switch {
        ArchitectureEvidenceState.Confirmed => 0, ArchitectureEvidenceState.StronglySupported => 1,
        ArchitectureEvidenceState.Inferred => 2, ArchitectureEvidenceState.Unresolved => 3, _ => 4 };
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
    public static bool IsPlaceholder(string value) => Regex.IsMatch(value.Trim(), @"^(\$\{[A-Za-z0-9_.:-]+\}|__[A-Za-z0-9_.:-]+__|<[A-Za-z0-9_.:-]+>)$", RegexOptions.CultureInvariant);
    public static string? Normalize(SecurityExpectationField field, string value)
    {
        value = value.Trim();
        if (value.Length >= 2 && value[0] == value[^1] && value[0] is '\'' or '"') value = value[1..^1].Trim();
        if (value.Length is 0 or > 500 || SecretShaped(value) || value.Any(char.IsControl)) return null;
        if (field == SecurityExpectationField.ClientId) return Guid.TryParse(value, out var client) ? client.ToString("D")
            : Regex.IsMatch(value, @"^[A-Za-z0-9][A-Za-z0-9._-]{1,127}$") ? value : null;
        if (field == SecurityExpectationField.TenantId) return Guid.TryParse(value, out var tenant) ? tenant.ToString("D")
            : Regex.IsMatch(value, @"^[A-Za-z0-9][A-Za-z0-9.-]*\.[A-Za-z]{2,}$") ? value.ToLowerInvariant() : null;
        if (field == SecurityExpectationField.SecurityHeader)
            return new[] { "Content-Security-Policy", "X-Content-Type-Options", "Referrer-Policy", "Permissions-Policy", "Strict-Transport-Security", "X-Frame-Options" }
                .FirstOrDefault(h => h.Equals(value, StringComparison.OrdinalIgnoreCase));
        var urlField = field is SecurityExpectationField.Authority or SecurityExpectationField.RedirectUrl;
        var wildcardHost = !urlField && value.StartsWith("*.", StringComparison.Ordinal);
        if (value.Contains('*') && !wildcardHost) return null;
        var hostValue = wildcardHost ? value[2..] : value;
        var input = urlField || hostValue.Contains("://") ? hostValue : "https://" + hostValue;
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 ||
            uri.Fragment.Length > 0 || SecretShaped(uri.Query) || Uri.CheckHostName(uri.IdnHost.TrimEnd('.')) == UriHostNameType.Unknown) return null;
        var name = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        var host = (uri.HostNameType == UriHostNameType.IPv6 ? "[" + name.Trim('[', ']') + "]" : name) + (uri.IsDefaultPort ? "" : ":" + uri.Port);
        if (!urlField) return uri.Query.Length == 0 && (hostValue.Contains("://") || uri.AbsolutePath == "/") ? (wildcardHost ? "*." : "") + host : null;
        if (field == SecurityExpectationField.Authority && uri.Query.Length > 0) return null;
        var path = uri.AbsolutePath;
        if (field == SecurityExpectationField.Authority && path.EndsWith("/", StringComparison.Ordinal)) path = path[..^1];
        return uri.Scheme.ToLowerInvariant() + "://" + host + path + uri.Query;
    }
    public static bool Matches(ApprovedSecurityExpectations settings, SecurityExpectationCandidate candidate) =>
        Values(settings, candidate.FieldType).Any(v => Normalize(candidate.FieldType, v) == candidate.NormalizedValue) ||
        candidate.FieldType == SecurityExpectationField.ClientId && settings.ScopedClientIds.Any(s => Normalize(candidate.FieldType, s.Value) == candidate.NormalizedValue);
    public static SecurityExpectationOrigin Origin(ApprovedSecurityExpectations settings, SecurityExpectationField field, string value) =>
        settings.Origins.LastOrDefault(p => p.FieldType == field && p.NormalizedValue == Normalize(field, value))?.Origin ?? SecurityExpectationOrigin.Existing;
    public static ApprovedSecurityExpectations Copy(ApprovedSecurityExpectations s) => new() {
        ExpectedAuthority = s.ExpectedAuthority, ExpectedTenant = s.ExpectedTenant, ExpectedClientId = s.ExpectedClientId,
        AllowedRedirectUrls = [.. s.AllowedRedirectUrls], AllowedBackendDomains = [.. s.AllowedBackendDomains], AllowedRestHosts = [.. s.AllowedRestHosts],
        AllowedGraphQlHosts = [.. s.AllowedGraphQlHosts], AllowedCdnHosts = [.. s.AllowedCdnHosts], ExpectedSecurityHeaders = [.. s.ExpectedSecurityHeaders], Origins = [.. s.Origins],
        ScopedClientIds = [.. s.ScopedClientIds] };
    /// <summary>Approves a Client/Application ID for one component scope; other scopes and the legacy project-wide value are unchanged.</summary>
    public static ApprovedSecurityExpectations AcceptScoped(ApprovedSecurityExpectations current, SecurityExpectationCandidate candidate, string scope, string fingerprint, DateTimeOffset at)
    {
        if (candidate.FieldType != SecurityExpectationField.ClientId || string.IsNullOrWhiteSpace(scope))
            throw new InvalidOperationException("Only Client/Application IDs are approved per component scope.");
        if (!candidate.IsCurrent || candidate.EvidenceState is ArchitectureEvidenceState.Unresolved or ArchitectureEvidenceState.Conflict ||
            Normalize(candidate.FieldType, candidate.Value) != candidate.NormalizedValue || SecurityConfigurationReviewEngine.IsPlaceholder(candidate.FieldType, candidate.NormalizedValue, candidate.FormatState))
            throw new InvalidOperationException("Candidate is stale, unresolved or a placeholder; refresh the review or correct the source.");
        var next = Copy(current);
        next.ScopedClientIds.RemoveAll(s => s.Scope == scope);
        next.ScopedClientIds.Add(new(scope, candidate.NormalizedValue));
        next.Origins.RemoveAll(p => p.FieldType == candidate.FieldType && p.Scope == scope);
        next.Origins.Add(new(candidate.FieldType, candidate.NormalizedValue, SecurityExpectationOrigin.AcceptedFromSource, candidate.SourceSnapshotId, fingerprint, at, candidate.Id, scope));
        return next;
    }
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
