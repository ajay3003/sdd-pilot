using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BirkNext.SourceArchitecture;

namespace BirkNext.SecurityExpectations;

// Security Configuration Review: a deterministic comparison of source-declared security configuration with the approved Security Expectations
// of one Target Environment. It projects the existing discovery (logical candidates + supporting evidence) — it does not extract, store or
// verify anything at runtime. See docs/security-expectations-review.md.

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SecurityConfigurationAssessmentState { Consistent, NeedsReview, Conflict, Partial, NotAssessed, NotApplicable }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SecurityConfigurationFindingType
{
    ConfigurationConflict, UnexpectedValue, UnapprovedValue, PlaceholderValue, EnvironmentMismatch, MissingExpectedValue, StaleApprovedValue,
    UnsupportedAssessment, AmbiguousScope, InvalidValue,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SecurityValueCardinality { Single, Multiple }

/// <summary>Which values must agree: one value per selected environment, or one per component client registration.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SecurityExpectationScope { Environment, EnvironmentAndComponent }

/// <summary>How a declared value relates to the selected environment.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SecurityValueApplicability
{
    /// <summary>Declared in the selected environment's override file (e.g. appsettings.Development.json).</summary>
    SelectedEnvironment,
    /// <summary>Declared in shared/base configuration and not overridden for the selected environment in the same component and key.</summary>
    SharedConfiguration,
    /// <summary>Base value replaced by the selected environment's override in the same component and key (file layering only).</summary>
    Overridden,
    /// <summary>Declared only for another explicit environment.</summary>
    OtherEnvironment,
}

/// <summary>Scoped approval of a value whose expectation is per component (Client/Application IDs).</summary>
public sealed record ScopedSecurityValue(string Scope, string Value);

/// <summary>Expectation metadata: value type, cardinality and scope. Normalization stays in <see cref="SecurityExpectationValues.Normalize"/>.</summary>
public sealed record SecurityExpectationMetadata(SecurityExpectationField Field, string Name, string ValueType, SecurityValueCardinality Cardinality,
    SecurityExpectationScope Scope, bool AssessedFromSource = true)
{
    public static SecurityExpectationMetadata For(SecurityExpectationField field) => field switch
    {
        SecurityExpectationField.Authority => new(field, "Authority", "Authority URL", SecurityValueCardinality.Single, SecurityExpectationScope.Environment),
        SecurityExpectationField.TenantId => new(field, "Tenant", "GUID or tenant domain", SecurityValueCardinality.Single, SecurityExpectationScope.Environment),
        SecurityExpectationField.ClientId => new(field, "Client / Application IDs", "GUID or client identifier", SecurityValueCardinality.Single, SecurityExpectationScope.EnvironmentAndComponent),
        SecurityExpectationField.RedirectUrl => new(field, "Redirect URLs", "Redirect URI", SecurityValueCardinality.Multiple, SecurityExpectationScope.Environment),
        SecurityExpectationField.BackendDomain => new(field, "Backend domains", "Host", SecurityValueCardinality.Multiple, SecurityExpectationScope.Environment),
        SecurityExpectationField.RestHost => new(field, "REST hosts", "Host", SecurityValueCardinality.Multiple, SecurityExpectationScope.Environment),
        SecurityExpectationField.GraphQlHost => new(field, "GraphQL hosts", "Host", SecurityValueCardinality.Multiple, SecurityExpectationScope.Environment),
        SecurityExpectationField.CdnHost => new(field, "CDN hosts", "Host", SecurityValueCardinality.Multiple, SecurityExpectationScope.Environment),
        // Header presence is a runtime property; source declarations are listed, not assessed.
        _ => new(field, "Security headers", "Header name", SecurityValueCardinality.Multiple, SecurityExpectationScope.Environment, AssessedFromSource: false),
    };
}

public sealed record SecurityConfigurationFinding(SecurityConfigurationFindingType Type, string Message, bool RequiresAttention, string Scope = "", string? ValueGroupId = null);

/// <summary>One occurrence of a value in source (provenance). Never an issue on its own.</summary>
public sealed record SecurityValueOccurrence(string Component, string File, int Line, string Key, string RawValue, string Environment,
    SecurityValueApplicability Applicability, string SourceRole, Guid SourceSnapshotId, string EvidenceKind, string Confidence, ArchitectureEvidenceState State);

/// <summary>One logical value (field + normalized value + scope) with its source diversity. The main review shows these, not occurrences.</summary>
public sealed record SecurityValueGroup
{
    public string Id { get; init; } = "";
    public string Scope { get; init; } = "";
    public string Value { get; init; } = "";
    /// <summary>Approved, Not approved, Ignored, Placeholder, Invalid format, Other environment, Overridden.</summary>
    public string Status { get; init; } = "";
    public bool IsApproved { get; init; }
    public bool IsIgnored { get; init; }
    public bool IsPlaceholder { get; init; }
    public bool IsInvalid { get; init; }
    public bool AppliesToSelectedEnvironment { get; init; }
    public List<string> Environments { get; init; } = [];
    public int Occurrences { get; init; }
    public int Components { get; init; }
    public int Files { get; init; }
    /// <summary>Discovery candidates behind this group (approve/ignore act on them; evidence stays with them).</summary>
    public List<string> CandidateIds { get; init; } = [];
    /// <summary>The candidate an approval uses (current, valid, in the selected scope when possible); null when the value cannot be approved.</summary>
    public string? ApprovalCandidateId { get; init; }
    public List<SecurityValueOccurrence> Evidence { get; init; } = [];
}

public sealed record SecurityConfigurationCheckResult
{
    public string Id { get; init; } = "";
    public SecurityExpectationField Field { get; init; }
    public string Name { get; init; } = "";
    public SecurityExpectationMetadata Metadata { get; init; } = SecurityExpectationMetadata.For(SecurityExpectationField.Authority);
    public SecurityConfigurationAssessmentState Assessment { get; init; }
    /// <summary>One sentence: why the check has its state.</summary>
    public string Summary { get; init; } = "";
    public List<string> ApprovedValues { get; init; } = [];
    /// <summary>Scopes with values (component · configuration section) for component-scoped expectations; empty otherwise.</summary>
    public List<string> Scopes { get; init; } = [];
    public List<SecurityValueGroup> ValueGroups { get; init; } = [];
    public List<SecurityConfigurationFinding> Findings { get; init; } = [];
    public int Occurrences { get; init; }
    public int Components { get; init; }
    public int Files { get; init; }
    public List<string> Limitations { get; init; } = [];
}

public sealed record SecurityConfigurationAttentionItem(string CheckId, string Title, string Summary, SecurityConfigurationAssessmentState State);

public sealed record SecurityConfigurationReview
{
    public SecurityConfigurationAssessmentState Assessment { get; init; }
    public string Summary { get; init; } = "";
    /// <summary>The ASP.NET environment name whose override files apply to the selected Target Environment; null when the type has no mapping.</summary>
    public string? SourceEnvironment { get; init; }
    public Guid? SourceSnapshotId { get; init; }
    public bool HasSource { get; init; }
    public List<SecurityConfigurationCheckResult> Checks { get; init; } = [];
    public List<SecurityConfigurationAttentionItem> Attention { get; init; } = [];
    public int Consistent { get; init; }
    public int NeedsReview { get; init; }
    public int Conflicts { get; init; }
    public int Placeholders { get; init; }
    public int EnvironmentMismatches { get; init; }
    public int NotAssessed { get; init; }
    public int ApprovedValues { get; init; }
    public List<string> Limitations { get; init; } = [];
}

/// <summary>Input context of a review: which Target Environment, which source and how Source Analysis completed.</summary>
public sealed record SecurityConfigurationReviewContext(string EnvironmentType, Guid? SelectedSnapshotId = null, string? SourceAnalysisStatus = null);

public static class SecurityConfigurationReviewEngine
{
    public static readonly SecurityExpectationField[] ReviewedFields =
    [
        SecurityExpectationField.Authority, SecurityExpectationField.TenantId, SecurityExpectationField.ClientId, SecurityExpectationField.RedirectUrl,
        SecurityExpectationField.BackendDomain, SecurityExpectationField.RestHost, SecurityExpectationField.GraphQlHost, SecurityExpectationField.CdnHost,
        SecurityExpectationField.SecurityHeader,
    ];

    /// <summary>ASP.NET environment name loaded for a Target Environment type (appsettings.{Name}.json). Custom types have no deterministic mapping.</summary>
    public static string? SourceEnvironmentFor(string environmentType) => environmentType switch
    {
        "Local" => "Local", "Development" => "Development", "QA" => "QA", "Test" => "Test", "Production" => "Production", _ => null,
    };

    private static readonly string[] KnownEnvironments = ["Development", "Local", "Test", "QA", "Staging", "Production"];

    /// <summary>Design-time files that never configure a deployed environment: launch profiles and GraphQL code-generation settings.</summary>
    public const string Tooling = "Tooling";

    /// <summary>Environment of a source file: "" for shared/base configuration, the canonical name for a known appsettings.{Name}.json, the
    /// literal name for any other suffix (e.g. "Dev"), and <see cref="Tooling"/> for launchSettings.json / .graphqlrc.json.
    /// Never guessed from similarity: "Dev" is not "Development".</summary>
    public static string FileEnvironment(string path)
    {
        var fileName = path.Replace('\\', '/').Split('/')[^1];
        if (fileName.Equals("launchSettings.json", StringComparison.OrdinalIgnoreCase) || fileName.Equals(".graphqlrc.json", StringComparison.OrdinalIgnoreCase)) return Tooling;
        var match = Regex.Match(path.Replace('\\', '/'), @"(?:^|/)appsettings\.([A-Za-z0-9_-]+)\.json$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return "";
        var name = match.Groups[1].Value;
        return KnownEnvironments.FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? name;
    }

    /// <summary>Deterministic placeholders only: recognized templates, the all-zero GUID for identifiers.</summary>
    public static bool IsPlaceholder(SecurityExpectationField field, string value, string formatState = "Valid") =>
        formatState == "Placeholder" || SecurityExpectationValues.IsPlaceholder(value) ||
        field is SecurityExpectationField.TenantId or SecurityExpectationField.ClientId && Guid.TryParse(value.Trim().Trim('{', '}'), out var g) && g == Guid.Empty;

    private static readonly Regex Loopback = new(@"^(https?://)?(localhost|127(\.\d{1,3}){3}|\[::1\])(:\d+)?(/|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public static bool IsLoopback(string normalized) => Loopback.IsMatch(normalized);

    private static readonly string[] EntraAuthorityHosts = ["login.microsoftonline.com", "login.microsoftonline.us", "login.partner.microsoftonline.cn", "login.windows.net", "sts.windows.net"];
    private static bool IsEntraAuthority(string normalized) => Uri.TryCreate(normalized, UriKind.Absolute, out var uri) &&
        (EntraAuthorityHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase) || uri.Host.EndsWith(".ciamlogin.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".b2clogin.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>Component-scope of an occurrence for per-component expectations: component plus configuration section ("AzureAd:Schemes:person").</summary>
    public static string ComponentScope(string component, string key)
    {
        var parts = key.Split(':');
        var section = parts.Length > 1 ? string.Join(':', parts[..^1]) : "";
        return component.Length == 0 ? "" : section.Length == 0 ? component : $"{component} · {section}";
    }

    private static string Component(SecurityExpectationEvidence e)
    {
        var at = e.Repository.IndexOf(" / ", StringComparison.Ordinal);
        return at < 0 ? e.Repository : e.Repository[(at + 3)..];
    }

    public static SecurityConfigurationReview Review(SecurityExpectationDiscoveryResult? discovery, ApprovedSecurityExpectations approved, SecurityConfigurationReviewContext context)
    {
        var sourceEnvironment = SourceEnvironmentFor(context.EnvironmentType);
        var approvedCount = ReviewedFields.Sum(f => SecurityExpectationValues.Values(approved, f).Count()) + approved.ScopedClientIds.Count;
        var limitations = new List<string> { "Runtime effective configuration is not verified: values are source declarations, not what the deployed environment uses." };
        if (discovery is null || discovery.Status == ArchitectureStatus.Unsupported && discovery.Candidates.Count == 0 ||
            context.SelectedSnapshotId is { } selected && discovery.SourceSnapshotId != selected || !discovery.IsCurrent)
        {
            var reason = discovery is null ? "No source security configuration has been reviewed for this environment."
                : discovery.Status == ArchitectureStatus.Unsupported && discovery.Candidates.Count == 0 ? "The selected snapshot has no security configuration evidence. Analyze the source again in Source Analysis."
                : "The last review is for another source snapshot. Refresh the review for the selected snapshot.";
            var notAssessed = ReviewedFields.Select(f => new SecurityConfigurationCheckResult
            {
                Id = f.ToString(), Field = f, Name = SecurityExpectationMetadata.For(f).Name, Metadata = SecurityExpectationMetadata.For(f),
                Assessment = SecurityConfigurationAssessmentState.NotAssessed, Summary = reason, ApprovedValues = Approved(approved, f),
            }).ToList();
            return new()
            {
                Assessment = SecurityConfigurationAssessmentState.NotAssessed, Summary = reason, SourceEnvironment = sourceEnvironment, SourceSnapshotId = discovery?.SourceSnapshotId,
                HasSource = false, Checks = notAssessed, NotAssessed = notAssessed.Count, ApprovedValues = approvedCount, Limitations = limitations,
            };
        }

        if (sourceEnvironment is null)
            limitations.Add($"The Target Environment type {context.EnvironmentType} has no deterministic source environment name, so only shared configuration applies; environment-specific files are listed but not assessed.");
        if (context.SourceAnalysisStatus is "Partial" || discovery.Status == ArchitectureStatus.Partial || discovery.UnsupportedEvidence.Count > 0)
            limitations.Add("Source Analysis is partial: absence of evidence is not proof of absence.");
        limitations.AddRange(discovery.Diagnostics.Where(d => d.Contains("Re-analyze", StringComparison.OrdinalIgnoreCase) || d.Contains("historical", StringComparison.OrdinalIgnoreCase)));

        var occurrences = Occurrences(discovery, sourceEnvironment);
        var unmapped = occurrences.Select(o => o.Occurrence).Where(o => o.Applicability == SecurityValueApplicability.OtherEnvironment && !KnownEnvironments.Contains(o.Environment) && o.Environment != Tooling)
            .Select(o => o.Environment).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (unmapped.Count > 0)
            limitations.Add($"Environment-specific files for {string.Join(", ", unmapped.Select(n => $"\"{n}\""))} do not map to the {context.EnvironmentType} Target Environment; their values are listed but not assessed for it.");
        if (occurrences.Any(o => o.Candidate.FieldType == SecurityExpectationField.ClientId && o.Occurrence.Component.Length == 0))
            limitations.Add("Component scope is unresolved for some Client IDs.");

        var authorities = occurrences.Where(o => o.Candidate.FieldType == SecurityExpectationField.Authority && o.Valid).Select(o => o.Candidate.NormalizedValue).Distinct().ToList();
        var checks = ReviewedFields.Select(f => Check(f, occurrences.Where(o => o.Candidate.FieldType == f).ToList(), approved, context, sourceEnvironment, authorities)).ToList();
        var attention = checks.Where(c => c.Assessment is SecurityConfigurationAssessmentState.NeedsReview or SecurityConfigurationAssessmentState.Conflict)
            .Select(c => new SecurityConfigurationAttentionItem(c.Id, c.Name, AttentionSummary(c), c.Assessment)).ToList();
        var conflicts = checks.Count(c => c.Assessment == SecurityConfigurationAssessmentState.Conflict);
        var needsReview = checks.Count(c => c.Assessment == SecurityConfigurationAssessmentState.NeedsReview);
        var consistent = checks.Count(c => c.Assessment == SecurityConfigurationAssessmentState.Consistent);
        var state = conflicts > 0 ? SecurityConfigurationAssessmentState.Conflict : needsReview > 0 ? SecurityConfigurationAssessmentState.NeedsReview
            : consistent == 0 ? SecurityConfigurationAssessmentState.NotAssessed
            : checks.Any(c => c.Assessment == SecurityConfigurationAssessmentState.NotAssessed && c.Metadata.AssessedFromSource && c.Occurrences > 0) ? SecurityConfigurationAssessmentState.Partial
            : SecurityConfigurationAssessmentState.Consistent;
        return new()
        {
            Assessment = state,
            Summary = state switch
            {
                SecurityConfigurationAssessmentState.Conflict => $"{attention.Count} item(s) need attention, {conflicts} with conflicting values.",
                SecurityConfigurationAssessmentState.NeedsReview => $"{attention.Count} item(s) need review.",
                SecurityConfigurationAssessmentState.Partial => "Assessed checks are consistent; some declared values could not be assessed for this environment.",
                SecurityConfigurationAssessmentState.Consistent => "Source-declared security configuration is consistent with the approved expectations.",
                _ => "No security configuration could be assessed for this environment.",
            },
            SourceEnvironment = sourceEnvironment, SourceSnapshotId = discovery.SourceSnapshotId, HasSource = true, Checks = checks, Attention = attention,
            Consistent = consistent, NeedsReview = needsReview, Conflicts = conflicts,
            Placeholders = checks.Sum(c => c.Findings.Count(f => f.Type == SecurityConfigurationFindingType.PlaceholderValue && f.RequiresAttention)),
            EnvironmentMismatches = checks.Sum(c => c.Findings.Count(f => f.Type == SecurityConfigurationFindingType.EnvironmentMismatch)),
            NotAssessed = checks.Count(c => c.Assessment is SecurityConfigurationAssessmentState.NotAssessed), ApprovedValues = approvedCount, Limitations = limitations,
        };
    }

    private sealed record Located(SecurityExpectationCandidate Candidate, SecurityValueOccurrence Occurrence, bool Valid, bool Placeholder, bool Ignored);

    /// <summary>Every distinct evidence occurrence with its environment applicability. Exact duplicate extractions were removed by discovery grouping.</summary>
    private static List<Located> Occurrences(SecurityExpectationDiscoveryResult discovery, string? sourceEnvironment)
    {
        var raw = discovery.Candidates.SelectMany(c => (c.SupportingEvidence.Count > 0 ? c.SupportingEvidence
                : [new SecurityExpectationEvidence(c.SourceSnapshotId, c.SourceComponent, c.SourceFile, c.SourceLine, c.SourceSymbol, c.EvidenceType, c.Value, c.Confidence, c.EvidenceState)])
            .Select(e => (Candidate: c, Evidence: e, Component: Component(e), Environment: FileEnvironment(e.FilePath))))
            .DistinctBy(x => (x.Candidate.Id, x.Evidence.SourceSnapshotId, x.Evidence.Repository, x.Evidence.FilePath, x.Evidence.Line, x.Evidence.SymbolOrKey, x.Evidence.RawValue))
            .ToList();
        // File layering per component and key: the selected environment's file replaces the base file's value for the same key.
        var overriddenKeys = raw.Where(x => sourceEnvironment is not null && x.Environment == sourceEnvironment)
            .Select(x => (x.Evidence.SourceSnapshotId, x.Component, Key: x.Evidence.SymbolOrKey.ToLowerInvariant())).ToHashSet();
        return raw.Select(x =>
        {
            var applicability = x.Environment.Length == 0
                ? overriddenKeys.Contains((x.Evidence.SourceSnapshotId, x.Component, x.Evidence.SymbolOrKey.ToLowerInvariant())) ? SecurityValueApplicability.Overridden : SecurityValueApplicability.SharedConfiguration
                : x.Environment == sourceEnvironment ? SecurityValueApplicability.SelectedEnvironment : SecurityValueApplicability.OtherEnvironment;
            var valid = x.Candidate.FormatState == "Valid" && x.Candidate.EvidenceState is not (ArchitectureEvidenceState.Unresolved or ArchitectureEvidenceState.Conflict);
            var occurrence = new SecurityValueOccurrence(x.Component, x.Evidence.FilePath, x.Evidence.Line, x.Evidence.SymbolOrKey, x.Evidence.RawValue, x.Environment,
                applicability, x.Evidence.SourceSnapshotId == discovery.SourceSnapshotId ? "Primary" : "Related", x.Evidence.SourceSnapshotId, x.Evidence.EvidenceKind, x.Evidence.Confidence, x.Evidence.State);
            return new Located(x.Candidate, occurrence, valid, IsPlaceholder(x.Candidate.FieldType, valid ? x.Candidate.NormalizedValue : x.Candidate.Value, x.Candidate.FormatState),
                x.Candidate.CandidateState == SecurityCandidateState.Rejected);
        }).ToList();
    }

    private static bool Applies(SecurityValueApplicability a) => a is SecurityValueApplicability.SelectedEnvironment or SecurityValueApplicability.SharedConfiguration;

    private static List<string> Approved(ApprovedSecurityExpectations approved, SecurityExpectationField field) =>
        [.. SecurityExpectationValues.Values(approved, field).Where(v => v.Length > 0),
         .. field == SecurityExpectationField.ClientId ? approved.ScopedClientIds.Select(s => $"{s.Value} ({s.Scope})") : []];

    private static SecurityConfigurationCheckResult Check(SecurityExpectationField field, List<Located> all, ApprovedSecurityExpectations approved,
        SecurityConfigurationReviewContext context, string? sourceEnvironment, List<string> authorities)
    {
        var meta = SecurityExpectationMetadata.For(field);
        var environmentLabel = sourceEnvironment is null ? context.EnvironmentType : context.EnvironmentType == sourceEnvironment ? sourceEnvironment : $"{context.EnvironmentType} ({sourceEnvironment} files)";
        var legacyValues = SecurityExpectationValues.Values(approved, field).Where(v => v.Length > 0).Select(v => SecurityExpectationValues.Normalize(field, v) ?? v.Trim()).ToHashSet(StringComparer.Ordinal);
        var componentScoped = meta.Scope == SecurityExpectationScope.EnvironmentAndComponent;
        var scopedApprovals = componentScoped
            ? approved.ScopedClientIds.GroupBy(s => s.Scope, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Select(s => SecurityExpectationValues.Normalize(field, s.Value) ?? s.Value.Trim()).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal)
            : [];
        string ScopeOf(Located l) => componentScoped ? ComponentScope(l.Occurrence.Component, l.Occurrence.Key) : "";
        var scopes = all.Select(ScopeOf).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        // A legacy single project-wide approval is mapped to a scope only when exactly one scope applies; otherwise it is ambiguous, never redistributed.
        var appliedScopes = all.Where(l => Applies(l.Occurrence.Applicability)).Select(ScopeOf).Distinct(StringComparer.Ordinal).ToList();
        var legacyAmbiguous = componentScoped && legacyValues.Count > 0 && appliedScopes.Count > 1;
        HashSet<string> ApprovedIn(string scope) => !componentScoped ? legacyValues
            : scopedApprovals.TryGetValue(scope, out var s) ? s : appliedScopes.Count <= 1 ? legacyValues : [];

        var groups = new List<SecurityValueGroup>();
        var findings = new List<SecurityConfigurationFinding>();
        foreach (var scope in scopes)
        {
            var inScope = all.Where(l => ScopeOf(l) == scope).ToList();
            var approvedHere = ApprovedIn(scope);
            foreach (var g in inScope.GroupBy(l => l.Valid ? l.Candidate.NormalizedValue : "raw:" + l.Candidate.Value, StringComparer.Ordinal))
            {
                var items = g.ToList();
                var first = items[0];
                var applies = items.Any(l => Applies(l.Occurrence.Applicability));
                var isApproved = first.Valid && approvedHere.Contains(first.Candidate.NormalizedValue);
                var ignored = !isApproved && items.Where(l => Applies(l.Occurrence.Applicability) || !applies).All(l => l.Ignored);
                var environments = items.Select(l => l.Occurrence.Environment.Length == 0 ? "Shared" : l.Occurrence.Environment).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
                var approvalCandidate = items.Where(l => l.Valid && !l.Placeholder && l.Candidate.IsCurrent && l.Candidate.CandidateState != SecurityCandidateState.Stale)
                    .OrderBy(l => Applies(l.Occurrence.Applicability) ? 0 : 1).ThenBy(l => l.Candidate.Id, StringComparer.Ordinal).Select(l => l.Candidate.Id).FirstOrDefault();
                var id = $"{field}|{scope}|{g.Key}";
                groups.Add(new()
                {
                    Id = id, Scope = scope, Value = first.Valid ? first.Candidate.NormalizedValue : first.Candidate.Value,
                    Status = isApproved ? "Approved" : first.Placeholder ? "Placeholder" : !first.Valid ? "Invalid format" : ignored ? "Ignored"
                        : applies ? "Not approved" : items.Any(l => l.Occurrence.Applicability == SecurityValueApplicability.Overridden) ? $"Overridden for {environmentLabel}"
                        : items.All(l => l.Occurrence.Environment == Tooling) ? "Design-time tooling" : "Other environment",
                    IsApproved = isApproved, IsIgnored = ignored, IsPlaceholder = first.Placeholder, IsInvalid = !first.Valid, AppliesToSelectedEnvironment = applies,
                    Environments = environments, Occurrences = items.Count, Components = items.Select(l => l.Occurrence.Component).Distinct(StringComparer.Ordinal).Count(),
                    Files = items.Select(l => (l.Occurrence.Component, l.Occurrence.File)).Distinct().Count(),
                    CandidateIds = items.Select(l => l.Candidate.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
                    ApprovalCandidateId = first.Placeholder || !first.Valid || isApproved ? null : approvalCandidate,
                    Evidence = items.Select(l => l.Occurrence).OrderBy(o => o.Component, StringComparer.Ordinal).ThenBy(o => o.File, StringComparer.Ordinal).ThenBy(o => o.Line).ThenBy(o => o.Key, StringComparer.Ordinal).ToList(),
                });
            }
            if (!meta.AssessedFromSource) continue;
            var scopeGroups = groups.Where(gr => gr.Scope == scope).ToList();
            var scopeLabel = scope.Length == 0 ? "" : $" for {scope}";
            var effective = scopeGroups.Where(gr => gr.AppliesToSelectedEnvironment && !gr.IsPlaceholder && !gr.IsInvalid).ToList();
            foreach (var gr in scopeGroups.Where(gr => gr.IsPlaceholder))
                findings.Add(new(SecurityConfigurationFindingType.PlaceholderValue, gr.AppliesToSelectedEnvironment
                    ? $"Placeholder/default value {gr.Value}{scopeLabel} applies to {environmentLabel} source configuration."
                    : $"Placeholder/default value {gr.Value}{scopeLabel} in {string.Join(", ", gr.Environments)} configuration; it does not apply to {environmentLabel}.",
                    gr.AppliesToSelectedEnvironment, scope, gr.Id));
            foreach (var gr in scopeGroups.Where(gr => gr.IsInvalid && gr.AppliesToSelectedEnvironment))
                findings.Add(new(SecurityConfigurationFindingType.InvalidValue, $"A declared value{scopeLabel} has an invalid format and cannot be compared.", true, scope, gr.Id));
            var mismatches = effective.Where(gr => !gr.IsApproved && !gr.IsIgnored && sourceEnvironment != "Local" && IsLoopback(gr.Value) && field is not (SecurityExpectationField.Authority or SecurityExpectationField.TenantId or SecurityExpectationField.ClientId)).ToList();
            foreach (var gr in mismatches)
                findings.Add(new(SecurityConfigurationFindingType.EnvironmentMismatch, $"Loopback value {gr.Value} is declared for {environmentLabel}{scopeLabel} and is not approved.", true, scope, gr.Id));
            if (meta.Cardinality == SecurityValueCardinality.Single)
            {
                var distinct = effective.Select(gr => gr.Value).Distinct(StringComparer.Ordinal).ToList();
                if (distinct.Count > 1)
                    findings.Add(new(SecurityConfigurationFindingType.ConfigurationConflict, $"{distinct.Count} different values are declared{scopeLabel} for {environmentLabel}; one is expected.", true, scope));
                else if (distinct.Count == 1 && approvedHere.Count > 0 && !approvedHere.Contains(distinct[0]))
                    findings.Add(new(SecurityConfigurationFindingType.ConfigurationConflict, $"Source declares {distinct[0]}{scopeLabel}; the approved value is {string.Join(", ", approvedHere)}.", true, scope, effective[0].Id));
                else if (distinct.Count == 1 && approvedHere.Count == 0 && !effective[0].IsIgnored)
                    findings.Add(componentScoped && legacyAmbiguous && legacyValues.Contains(distinct[0])
                        ? new(SecurityConfigurationFindingType.AmbiguousScope, $"Matches the legacy project-wide approval; confirm it{scopeLabel}.", true, scope, effective[0].Id)
                        : new(SecurityConfigurationFindingType.UnapprovedValue, $"{distinct[0]}{scopeLabel} is declared but not yet approved.", true, scope, effective[0].Id));
                if (distinct.Count == 0 && field is SecurityExpectationField.Authority or SecurityExpectationField.TenantId or SecurityExpectationField.ClientId &&
                    scopeGroups.Any(gr => !gr.AppliesToSelectedEnvironment && !gr.IsPlaceholder && !gr.IsInvalid && gr.Status == "Other environment"))
                    findings.Add(new(SecurityConfigurationFindingType.MissingExpectedValue, $"Declared{scopeLabel} for {string.Join(", ", scopeGroups.SelectMany(gr => gr.Environments).Distinct().Order())} but not for {environmentLabel} or shared configuration.", true, scope));
            }
            else
            {
                foreach (var gr in effective.Where(gr => !gr.IsApproved && !gr.IsIgnored && !mismatches.Contains(gr)))
                    findings.Add(new(SecurityConfigurationFindingType.UnapprovedValue, $"{gr.Value}{scopeLabel} is declared for {environmentLabel} but not in the approved values.", true, scope, gr.Id));
            }
        }

        // Approved values with no current declaration for the selected environment are kept and flagged, never removed.
        if (meta.AssessedFromSource)
        {
            var declared = groups.Where(gr => gr.AppliesToSelectedEnvironment && !gr.IsInvalid).Select(gr => (gr.Scope, gr.Value)).ToHashSet();
            var approvedPairs = componentScoped
                ? scopedApprovals.SelectMany(p => p.Value.Select(v => (Scope: p.Key, Value: v))).Concat(legacyAmbiguous || appliedScopes.Count == 0 ? legacyValues.Select(v => (Scope: "", Value: v)) : appliedScopes.Count == 1 ? legacyValues.Select(v => (Scope: appliedScopes[0], Value: v)) : [])
                : legacyValues.Select(v => (Scope: "", Value: v));
            foreach (var (scope, value) in approvedPairs)
            {
                var stillDeclared = componentScoped && scope.Length == 0 ? declared.Any(d => d.Value == value) : declared.Contains((scope, value));
                if (stillDeclared) continue;
                if (meta.Cardinality == SecurityValueCardinality.Single && findings.Any(f => f.Type == SecurityConfigurationFindingType.ConfigurationConflict && f.Scope == scope)) continue;
                findings.Add(new(SecurityConfigurationFindingType.StaleApprovedValue, $"Approved {value}{(scope.Length == 0 ? "" : $" ({scope})")} is not declared for {environmentLabel} in the selected source. The approval is kept.", true, scope));
            }
            if (legacyAmbiguous)
                findings.Add(new(SecurityConfigurationFindingType.AmbiguousScope, $"The legacy project-wide Client ID approval cannot be assigned to one of {appliedScopes.Count} component scopes; approve each component's value.", true));
        }

        // Applicability: a tenant ID is specific to Microsoft Entra ID.
        var applicable = !(field == SecurityExpectationField.TenantId && all.Count == 0 && legacyValues.Count == 0 && authorities.Count > 0 && !authorities.Any(IsEntraAuthority));
        var attention = findings.Where(f => f.RequiresAttention).ToList();
        var assessment = !applicable ? SecurityConfigurationAssessmentState.NotApplicable
            : !meta.AssessedFromSource ? SecurityConfigurationAssessmentState.NotAssessed
            : attention.Any(f => f.Type == SecurityConfigurationFindingType.ConfigurationConflict) ? SecurityConfigurationAssessmentState.Conflict
            : attention.Count > 0 ? SecurityConfigurationAssessmentState.NeedsReview
            : groups.Any(gr => gr.AppliesToSelectedEnvironment && gr.IsApproved) ? SecurityConfigurationAssessmentState.Consistent
            : SecurityConfigurationAssessmentState.NotAssessed;
        var summary = assessment switch
        {
            SecurityConfigurationAssessmentState.NotApplicable => $"Not applicable: the declared authority is not Microsoft Entra ID ({string.Join(", ", authorities.Select(a => Uri.TryCreate(a, UriKind.Absolute, out var u) ? u.Host : a).Distinct())}).",
            SecurityConfigurationAssessmentState.NotAssessed when !meta.AssessedFromSource => "Header presence is a runtime property; source declarations are listed for reference only.",
            SecurityConfigurationAssessmentState.NotAssessed when all.Count == 0 => "No source declaration found. This is not evidence of a defect: the value may be configured outside the analyzed source.",
            SecurityConfigurationAssessmentState.NotAssessed when groups.All(gr => gr.IsIgnored || !gr.AppliesToSelectedEnvironment) => $"Nothing declared for {environmentLabel}; values exist only for other environments or were ignored.",
            SecurityConfigurationAssessmentState.NotAssessed => "No approved value to compare with.",
            SecurityConfigurationAssessmentState.Consistent => $"{environmentLabel} source configuration matches the approved {(meta.Cardinality == SecurityValueCardinality.Single ? "value" : "values")}.",
            _ => string.Join(" ", attention.Select(f => f.Type).Distinct().Select(t => FindingLabel(t, attention.Count(f => f.Type == t)))),
        };
        var applying = all.Where(l => Applies(l.Occurrence.Applicability)).ToList();
        return new()
        {
            Id = field.ToString(), Field = field, Name = meta.Name, Metadata = meta, Assessment = assessment, Summary = summary, ApprovedValues = Approved(approved, field),
            Scopes = componentScoped ? scopes.Where(s => s.Length > 0).ToList() : [],
            ValueGroups = groups.OrderBy(gr => gr.Scope, StringComparer.Ordinal).ThenBy(gr => gr.AppliesToSelectedEnvironment ? 0 : 1).ThenBy(gr => gr.IsApproved ? 0 : 1).ThenByDescending(gr => gr.Occurrences).ThenBy(gr => gr.Value, StringComparer.Ordinal).ToList(),
            Findings = findings, Occurrences = all.Count, Components = all.Select(l => l.Occurrence.Component).Distinct(StringComparer.Ordinal).Count(),
            Files = all.Select(l => (l.Occurrence.Component, l.Occurrence.File)).Distinct().Count(),
        };
    }

    public static string FindingLabel(SecurityConfigurationFindingType type, int count) => type switch
    {
        SecurityConfigurationFindingType.ConfigurationConflict => count == 1 ? "1 conflicting value." : $"{count} conflicts.",
        SecurityConfigurationFindingType.UnapprovedValue => count == 1 ? "1 value not yet approved." : $"{count} values not yet approved.",
        SecurityConfigurationFindingType.PlaceholderValue => count == 1 ? "1 placeholder/default value." : $"{count} placeholder/default values.",
        SecurityConfigurationFindingType.EnvironmentMismatch => count == 1 ? "1 loopback value for a non-local environment." : $"{count} loopback values for a non-local environment.",
        SecurityConfigurationFindingType.MissingExpectedValue => "Declared for other environments only.",
        SecurityConfigurationFindingType.StaleApprovedValue => count == 1 ? "1 approved value is no longer declared." : $"{count} approved values are no longer declared.",
        SecurityConfigurationFindingType.AmbiguousScope => "Approval scope is ambiguous.",
        SecurityConfigurationFindingType.InvalidValue => count == 1 ? "1 value has an invalid format." : $"{count} values have an invalid format.",
        _ => "Review needed.",
    };

    private static string AttentionSummary(SecurityConfigurationCheckResult check)
    {
        var attention = check.Findings.Where(f => f.RequiresAttention).ToList();
        if (check.Metadata.Scope == SecurityExpectationScope.EnvironmentAndComponent)
        {
            var scopes = attention.Select(f => f.Scope).Where(s => s.Length > 0).Distinct().Count();
            if (scopes > 1) return $"{scopes} component scopes need review: {check.Summary}";
        }
        return attention.Count == 1 ? attention[0].Message : check.Summary;
    }
}
