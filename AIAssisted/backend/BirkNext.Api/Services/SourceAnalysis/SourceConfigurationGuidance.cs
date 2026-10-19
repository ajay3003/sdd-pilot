using BirkNext.Integrations;
using BirkNext.SourceDomains;

namespace BirkNext.Api.Services.SourceAnalysis;

/// <summary>
/// Generic source guidance: for each declared configuration requirement, what the canonical (IQR-bound) Source Analysis evidence declares.
/// Only bound snapshots can suggest; a value only an unbound snapshot declares is "available but not bound" and is never offered; artifacts
/// that disagree are a visible conflict. A suggestion is never applied: the consumer's stored value is shown next to it and stays as it is.
/// Nothing here is runtime verified.
/// </summary>
public static class SourceConfigurationGuidance
{
    public static IReadOnlyList<SourceConfigurationSuggestion> Suggest(IReadOnlyList<SourceConfigurationRequirement> requirements, IReadOnlyList<IqrSourceSnapshot> bound,
        IReadOnlyList<IqrSourceSnapshot> all, IReadOnlyDictionary<string, string?>? configured = null)
    {
        var boundIds = bound.Select(snapshot => snapshot.Id).ToHashSet();
        var unbound = all.Where(snapshot => !boundIds.Contains(snapshot.Id)).ToArray();
        return requirements.Select(requirement =>
        {
            var configuredValue = configured?.GetValueOrDefault(requirement.Key) is { Length: > 0 } stored ? stored : null;
            var fromBound = bound.SelectMany(snapshot => Values(snapshot, requirement)).ToArray();
            if (fromBound.Length > 0)
            {
                // One candidate per value with every artifact behind it (scopes were already narrowed by the requirement's hint).
                var candidates = fromBound.GroupBy(value => value.Value, StringComparer.Ordinal)
                    .Select(group => new SourceConfigurationCandidate(group.Key, group.DistinctBy(item => (item.Artifact, item.Location)).ToArray())).ToArray();
                var distinct = candidates.Select(item => item.Value).Distinct(StringComparer.Ordinal).ToArray();
                var boundSources = bound.Where(snapshot => fromBound.Any(value => Values(snapshot, requirement).Contains(value))).Select(snapshot => snapshot.Archive.FileName).Distinct().ToArray();
                // A declared set (token providers, schemes …) is listed, not in conflict.
                // A value meant to be applied is one value: several are a conflict even for a key that may list several.
                if (distinct.Length > 1 && SourceConfigurationKeys.MultiValued.Contains(requirement.SemanticKey) && !requirement.AutoApply)
                    return new SourceConfigurationSuggestion(requirement, SourceSuggestionState.Suggested)
                    {
                        Value = string.Join(", ", distinct), Candidates = candidates, BoundSources = boundSources, ConfiguredValue = configuredValue,
                        Detail = $"Declared in the bound source ({distinct.Length} values). Not runtime verified.",
                    };
                if (distinct.Length > 1)
                    return new SourceConfigurationSuggestion(requirement, SourceSuggestionState.Conflict)
                    {
                        Candidates = candidates, BoundSources = boundSources, ConfiguredValue = configuredValue,
                        Detail = $"Configuration conflict: the bound source declares {distinct.Length} different values. Nothing is suggested until one is chosen.",
                    };
                return new SourceConfigurationSuggestion(requirement, SourceSuggestionState.Suggested)
                {
                    Value = distinct[0], Candidates = candidates, BoundSources = boundSources, ConfiguredValue = configuredValue,
                    Detail = $"Declared in the bound source ({string.Join(", ", candidates.SelectMany(item => item.Provenance).Select(Provenance).Distinct())}). Not runtime verified.",
                };
            }
            var elsewhere = unbound.Where(snapshot => Values(snapshot, requirement).Any()).Select(snapshot => snapshot.Archive.FileName).Distinct().ToArray();
            return elsewhere.Length > 0
                ? new SourceConfigurationSuggestion(requirement, SourceSuggestionState.AvailableNotBound)
                {
                    UnboundSources = elsewhere, ConfiguredValue = configuredValue,
                    Detail = $"Declared in {string.Join(", ", elsewhere)}, which is not bound in Integration Quality Review → Evidence. Bind it to use the value; it is not source verified.",
                }
                : new SourceConfigurationSuggestion(requirement, SourceSuggestionState.NotDeclared)
                {
                    ConfiguredValue = configuredValue,
                    Detail = bound.Count == 0 ? $"No source is bound. Expected evidence: {requirement.EvidenceType} of {requirement.LogicalSource}."
                        : $"The bound source does not declare it. Expected evidence: {requirement.EvidenceType} of {requirement.LogicalSource}.",
                };
        }).ToArray();
    }

    /// <summary>"OpenAPI api/openapi.json → servers[1]".</summary>
    public static string Provenance(SourceConfigurationValue value) => $"{Kind(value.EvidenceKind)} {value.Artifact} → {value.Location}";

    public static string Kind(SourceConfigurationEvidenceKind kind) => kind switch
    {
        SourceConfigurationEvidenceKind.OpenApi => "OpenAPI",
        SourceConfigurationEvidenceKind.ApplicationConfig => "Configuration",
        SourceConfigurationEvidenceKind.InfrastructureAsCode => "Infrastructure as Code",
        SourceConfigurationEvidenceKind.DeploymentManifest => "Deployment manifest",
        SourceConfigurationEvidenceKind.PipelineConfig => "Pipeline",
        SourceConfigurationEvidenceKind.SourceConstant => "Source constant",
        _ => "Source",
    };

    /// <summary>The values one snapshot declares for a requirement: its semantic key, plus application configuration entries under the keys
    /// the requirement names (same environment role or none; sensitive entries never give a value).</summary>
    public static IReadOnlyList<SourceConfigurationValue> Values(IqrSourceSnapshot snapshot, SourceConfigurationRequirement requirement)
    {
        var declared = Evidence.SourceConfigurationValueCollector.Values(snapshot.EvidenceDomains)
            .Where(value => value.SemanticKey == requirement.SemanticKey && value.Value is not null && InScope(value.Scope, requirement.ScopeHint)).ToList();
        if (requirement.ConfigurationKeys.Count == 0) return declared;
        var role = RoleOf(requirement.SemanticKey);
        var keys = requirement.ConfigurationKeys.Select(key => key.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in snapshot.EvidenceDomains?.Configuration.Entries ?? [])
        {
            if (!keys.Contains(entry.NormalizedKey) || entry.Sensitivity != ConfigurationSensitivity.None || entry.ValuePreviewSafe is not { Length: > 0 } preview) continue;
            var environment = Evidence.SourceConfigurationValueCollector.Role(entry.Environment);
            if (role is not null && environment != SourceEnvironmentRole.Any && environment != role) continue;
            declared.Add(new SourceConfigurationValue
            {
                SemanticKey = requirement.SemanticKey, Value = preview.TrimEnd('/'), Group = SourceConfigurationGroup.Environment, Environment = role ?? environment,
                EvidenceKind = SourceConfigurationEvidenceKind.ApplicationConfig, Artifact = entry.File, Location = $"{entry.Key} (line {entry.Line})",
                Basis = $"configuration key {entry.Key}", Confidence = entry.EvidenceState,
            });
        }
        return declared;
    }

    /// <summary>A value is in the requirement's scope when no hint is given, when it states no scope, or when its scope is the hint or ends
    /// with "/{hint}" (an app route's app segment).</summary>
    public static bool InScope(string? scope, string? hint) =>
        hint is null || scope is null || scope.Equals(hint, StringComparison.OrdinalIgnoreCase) || scope.EndsWith("/" + hint, StringComparison.OrdinalIgnoreCase);

    /// <summary>The environment role a semantic key names ("Environment.Server.Test" → Test), or null.</summary>
    public static SourceEnvironmentRole? RoleOf(string semanticKey) =>
        Enum.TryParse<SourceEnvironmentRole>(semanticKey[(semanticKey.LastIndexOf('.') + 1)..], out var role) && semanticKey.StartsWith(SourceConfigurationKeys.EnvironmentServer + ".", StringComparison.Ordinal) ? role : null;
}
