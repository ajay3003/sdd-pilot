using BirkNext.Integrations;
using BirkNext.SourceDomains;

namespace BirkNext.Api.Services.IntegrationJourneys;

/// <summary>
/// Generic resolution of a test pack's logical sources. A source role's authoritative evidence is the canonical IQR binding of the configured
/// integrations that fulfil the role's integration requirements (or, for a role without integration requirements, any binding of the pack's
/// integrations) — and only when that bound snapshot is recognized as the role's source. A stored snapshot that is recognized but not bound is
/// "available but not bound" and never authoritative; nothing is bound automatically and the newest snapshot never stands in. A snapshot is
/// recognized deterministically by the role's component tokens (architecture components, projects) or, for an API-described source, by an
/// API document's scope or title — never by its archive name.
/// </summary>
public static class TestPackSources
{
    /// <summary>The configured integrations whose binding supplies this role.</summary>
    public static IReadOnlyList<IntegrationDefinition> Integrations(IIntegrationJourneyPack pack, JourneyPackContext context, TestPackSourceRole role) =>
        pack.Journeys.SelectMany(journey => journey.Integrations)
            .Where(requirement => role.IntegrationRequirementKeys.Count == 0 || role.IntegrationRequirementKeys.Contains(requirement.Key, StringComparer.Ordinal))
            .SelectMany(requirement => context.Catalog.Integrations.Where(integration => pack.Matches(requirement, integration)))
            .DistinctBy(integration => integration.Id).ToArray();

    /// <summary>The bound snapshots of the role's integrations (recognized or not).</summary>
    public static IReadOnlyList<IqrSourceSnapshot> BoundToRole(IIntegrationJourneyPack pack, JourneyPackContext context, TestPackSourceRole role) =>
        Integrations(pack, context, role).Select(integration => context.BoundSnapshot(integration.Id)).OfType<IqrSourceSnapshot>().DistinctBy(snapshot => snapshot.Id).ToArray();

    /// <summary>The authoritative snapshots of a role: bound to its integrations and recognized as its source.</summary>
    public static IReadOnlyList<IqrSourceSnapshot> Authoritative(IIntegrationJourneyPack pack, JourneyPackContext context, TestPackSourceRole role) =>
        BoundToRole(pack, context, role).Where(snapshot => Recognizes(role, snapshot)).ToArray();

    /// <summary>Stored snapshots recognized as the role's source that are not authoritative for it (discovery only).</summary>
    public static IReadOnlyList<IqrSourceSnapshot> Available(IIntegrationJourneyPack pack, JourneyPackContext context, TestPackSourceRole role)
    {
        var authoritative = Authoritative(pack, context, role).Select(snapshot => snapshot.Id).ToHashSet();
        return context.SourceSnapshots.Where(snapshot => !authoritative.Contains(snapshot.Id) && Recognizes(role, snapshot)).ToArray();
    }

    public static TestPackSourceRole? Role(IIntegrationJourneyPack pack, string? key) =>
        key is null ? null : pack.SourceRoles.FirstOrDefault(role => string.Equals(role.Key, key, StringComparison.Ordinal));

    /// <summary>Whether a snapshot is this logical source: a component/project named by a role token, or (API-described source) an API document
    /// whose scope or title names a token.</summary>
    public static bool Recognizes(TestPackSourceRole role, IqrSourceSnapshot snapshot)
    {
        if (role.ComponentTokens.Count == 0) return false;
        if (role.RequiresApiDocument)
            return SourceAnalysis.Evidence.SourceConfigurationValueCollector.Values(snapshot.EvidenceDomains)
                .Where(value => value.EvidenceKind == SourceConfigurationEvidenceKind.OpenApi)
                .Any(value => ArchitectureRuleEvaluator.MatchesAny(role.ComponentTokens, value.Scope, value.LogicalSource));
        return (snapshot.Architecture?.Components ?? []).Any(component => ArchitectureRuleEvaluator.MatchesAny(role.ComponentTokens, component.Name, component.LogicalName, component.SourceProject))
            || snapshot.Projects.Any(project => ArchitectureRuleEvaluator.MatchesAny(role.ComponentTokens, project.Name))
            || (snapshot.EvidenceDomains?.Configuration.Files ?? []).Any(file => ArchitectureRuleEvaluator.MatchesAny(role.ComponentTokens, file.Component));
    }

    /// <summary>The state of every source role of a pack, with the next action at the owner that resolves it.</summary>
    public static IReadOnlyList<TestPackSourceStatus> Statuses(IIntegrationJourneyPack pack, JourneyPackContext context) =>
        pack.SourceRoles.Select(role => Status(pack, context, role)).ToArray();

    public static TestPackSourceStatus Status(IIntegrationJourneyPack pack, JourneyPackContext context, TestPackSourceRole role)
    {
        var integrations = Integrations(pack, context, role);
        var names = integrations.Select(item => item.DisplayName).ToArray();
        var bound = BoundToRole(pack, context, role);
        var authoritative = bound.Where(snapshot => Recognizes(role, snapshot)).ToArray();
        var available = Available(pack, context, role).Select(snapshot => snapshot.Archive.FileName).Distinct().ToArray();
        if (authoritative.Length > 0)
            return new(role, TestPackSourceState.Bound)
            {
                BoundArchives = [.. authoritative.Select(snapshot => snapshot.Archive.FileName).Distinct()], AvailableArchives = available, Integrations = names,
                Detail = $"{role.Label} source is bound in Integration Quality Review{(names.Length > 0 ? $" ({string.Join(", ", names)})" : "")}.",
                NextAction = "Open IQR Evidence", NextActionTarget = "iqr-evidence",
            };
        // A role with its own integrations whose bound snapshot is another source: wrong source context, never "component missing".
        if (role.IntegrationRequirementKeys.Count > 0 && bound.Count > 0)
            return new(role, TestPackSourceState.ReviewRequired)
            {
                BoundArchives = [.. bound.Select(snapshot => snapshot.Archive.FileName).Distinct()], AvailableArchives = available, Integrations = names,
                Detail = $"The snapshot bound to {string.Join(", ", names)} is not recognized as the {role.Label} source.",
                NextAction = "Check the binding in IQR Evidence", NextActionTarget = "iqr-evidence",
            };
        if (available.Length > 0)
            return new(role, TestPackSourceState.AvailableNotBound)
            {
                AvailableArchives = available, Integrations = names,
                Detail = names.Length > 0 || role.IntegrationRequirementKeys.Count == 0
                    ? $"{role.Label} source is available ({string.Join(", ", available)}) but not bound where it is needed."
                    : $"{role.Label} source is available ({string.Join(", ", available)}), but no configured integration uses it yet.",
                NextAction = names.Length > 0 || role.IntegrationRequirementKeys.Count == 0 ? "Bind it in IQR Evidence" : "Configure the integration in Target Environment → Integrations, then bind it in IQR Evidence",
                NextActionTarget = names.Length > 0 || role.IntegrationRequirementKeys.Count == 0 ? "iqr-evidence" : "target-environment-integrations",
            };
        return new(role, TestPackSourceState.Missing)
        {
            Integrations = names,
            Detail = $"{role.Label} source is missing: no stored snapshot is recognized as it. Expected: {role.ExpectedArtifact}.",
            NextAction = "Import it in Source Analysis", NextActionTarget = "source-analysis",
        };
    }

    /// <summary>A short "needs X" sentence for a rule or prerequisite that could not use the role's source.</summary>
    public static string NeedsSentence(TestPackSourceStatus status, IReadOnlyList<TestPackSourceStatus> all)
    {
        var others = all.Where(item => item.State == TestPackSourceState.Bound && item.Role.Key != status.Role.Key).Select(item => item.Role.Label).ToArray();
        var current = others.Length > 0 ? $" The currently bound source is {string.Join(", ", others)}." : " No source is bound for it.";
        return status.State switch
        {
            TestPackSourceState.AvailableNotBound => $"Needs {status.Role.Label} source evidence: {string.Join(", ", status.AvailableArchives)} is available but not bound.{current}",
            TestPackSourceState.ReviewRequired => $"Needs {status.Role.Label} source evidence: the bound snapshot is not recognized as it (review the binding).",
            TestPackSourceState.Missing => $"Needs {status.Role.Label} source evidence, which is not imported.{current}",
            _ => "",
        };
    }
}
