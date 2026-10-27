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
    /// whose scope or title names a token, or (Infrastructure as Code source) declared infrastructure of any supported format — never its archive name.</summary>
    public static bool Recognizes(TestPackSourceRole role, IqrSourceSnapshot snapshot)
    {
        if (role.RequiresInfrastructureAsCode)
        {
            if (snapshot.EvidenceDomains?.Infrastructure is not { } infrastructure || (infrastructure.Formats.Count == 0 && infrastructure.Resources.Count == 0)) return false;
            return role.ComponentTokens.Count == 0 || infrastructure.Resources.Any(resource =>
                ArchitectureRuleEvaluator.MatchesAny(role.ComponentTokens, resource.LogicalName, resource.DeclaredName ?? "", resource.ModulePath ?? ""));
        }
        if (role.ComponentTokens.Count == 0) return false;
        if (role.ExcludedComponentTokens.Count > 0 && MatchesComponents(role.ExcludedComponentTokens, snapshot)) return false;
        if (role.RequiresApiDocument)
            return SourceAnalysis.Evidence.SourceConfigurationValueCollector.Values(snapshot.EvidenceDomains)
                .Where(value => value.EvidenceKind == SourceConfigurationEvidenceKind.OpenApi)
                .Any(value => ArchitectureRuleEvaluator.MatchesAny(role.ComponentTokens, value.Scope, value.LogicalSource)
                    && (role.ApiDocumentKeys.Count == 0 || role.ApiDocumentKeys.Contains(value.SemanticKey, StringComparer.Ordinal)));
        return MatchesComponents(role.ComponentTokens, snapshot);
    }

    private static bool MatchesComponents(IReadOnlyList<string> tokens, IqrSourceSnapshot snapshot) =>
        (snapshot.Architecture?.Components ?? []).Any(component => ArchitectureRuleEvaluator.MatchesAny(tokens, component.Name, component.LogicalName, component.SourceProject))
        || snapshot.Projects.Any(project => ArchitectureRuleEvaluator.MatchesAny(tokens, project.Name))
        || (snapshot.EvidenceDomains?.Configuration.Files ?? []).Any(file => ArchitectureRuleEvaluator.MatchesAny(tokens, file.Component));

    /// <summary>The names that made a snapshot this source (components, projects, configuration files, API scopes or IaC formats), bounded —
    /// shown as "why this source matches". Names only, never source text.</summary>
    public static IReadOnlyList<string> MatchedOn(TestPackSourceRole role, IqrSourceSnapshot snapshot)
    {
        if (role.RequiresInfrastructureAsCode) return [.. InfrastructureFormats(snapshot).Select(format => $"Infrastructure as Code: {format}")];
        if (role.RequiresApiDocument)
            return [.. SourceAnalysis.Evidence.SourceConfigurationValueCollector.Values(snapshot.EvidenceDomains)
                .Where(value => value.EvidenceKind == SourceConfigurationEvidenceKind.OpenApi && ArchitectureRuleEvaluator.MatchesAny(role.ComponentTokens, value.Scope, value.LogicalSource))
                .Select(value => $"API document: {value.Scope ?? value.LogicalSource}").Distinct().Take(6)];
        return [.. (snapshot.Architecture?.Components ?? []).Where(component => ArchitectureRuleEvaluator.MatchesAny(role.ComponentTokens, component.Name, component.LogicalName, component.SourceProject))
                .Select(component => $"Component: {component.Name}")
            .Concat(snapshot.Projects.Where(project => ArchitectureRuleEvaluator.MatchesAny(role.ComponentTokens, project.Name)).Select(project => $"Project: {project.Name}"))
            .Concat((snapshot.EvidenceDomains?.Configuration.Files ?? []).Where(file => ArchitectureRuleEvaluator.MatchesAny(role.ComponentTokens, file.Component)).Select(file => $"Configuration: {file.Component}"))
            .Distinct().Take(8)];
    }

    /// <summary>Snapshots whose archive name or repository key names the role (a hint only) but which are not recognized as it, with the
    /// reason. The name never makes a snapshot the source; this only explains a Missing state.</summary>
    public static IReadOnlyList<TestPackSourceNearMiss> NearMisses(TestPackSourceRole role, JourneyPackContext context)
    {
        string[] hints = [role.Key, role.Label.Split(' ', '(')[0]];
        return [.. context.SourceSnapshots
            .Where(snapshot => !Recognizes(role, snapshot)
                && ArchitectureRuleEvaluator.MatchesAny(hints, System.IO.Path.GetFileNameWithoutExtension(snapshot.Archive.FileName), snapshot.Repository?.Key ?? "", snapshot.Repository?.DisplayName ?? ""))
            .OrderByDescending(snapshot => snapshot.AnalyzedAt)
            .Select(snapshot => new TestPackSourceNearMiss(snapshot.Id, snapshot.Archive.FileName, NearMissReason(role, snapshot)))];
    }

    private static string NearMissReason(TestPackSourceRole role, IqrSourceSnapshot snapshot)
    {
        if (role.RequiresInfrastructureAsCode) return "It declares no Infrastructure as Code (Terraform, Bicep, ARM, Kubernetes or Helm).";
        if (role.ExcludedComponentTokens.Count > 0 && MatchesComponents(role.ExcludedComponentTokens, snapshot))
            return $"It contains {string.Join(", ", role.ExcludedComponentTokens.Where(token => MatchesComponents([token], snapshot)))}, which identifies another source.";
        if (role.RequiresApiDocument) return "It contains no API document (OpenAPI / application metadata) that declares this application.";
        return $"It contains none of the expected components or projects: {string.Join(", ", role.ComponentTokens.Distinct(StringComparer.OrdinalIgnoreCase))}.";
    }

    /// <summary>The state of every source role of a pack, with the next action at the owner that resolves it.</summary>
    public static IReadOnlyList<TestPackSourceStatus> Statuses(IIntegrationJourneyPack pack, JourneyPackContext context) =>
        pack.SourceRoles.Select(role => Status(pack, context, role) with { NearMisses = role.Provider == TestPackSourceProvider.TestPackReference ? [] : NearMisses(role, context) }).ToArray();

    /// <summary>The Infrastructure as Code formats a snapshot declares ("Terraform", "Bicep" …), in a stable order.</summary>
    public static IReadOnlyList<string> InfrastructureFormats(IqrSourceSnapshot snapshot) =>
        snapshot.EvidenceDomains?.Infrastructure is { } infrastructure
            ? [.. infrastructure.Formats.Concat(infrastructure.Resources.Select(resource => resource.Format)).Distinct().Order().Select(format => format.ToString())]
            : [];

    /// <summary>Infrastructure automation scripts in the same source package, by language, with the source checks they implement (static evidence;
    /// never "executed"). Not an Infrastructure as Code format: it never satisfies an IaC requirement.</summary>
    public static string? InfrastructureAutomation(IqrSourceSnapshot snapshot) =>
        snapshot.EvidenceDomains?.Infrastructure is { Scripts.Count: > 0 } infrastructure
            ? $"{string.Join(", ", infrastructure.Scripts.Select(s => s.Language).Distinct().Order())} scripts · {infrastructure.Checks.Count} source check(s) · read statically, not executed"
            : null;

    /// <summary>Every stored snapshot recognized as the role's source: the authoritative (bound) ones first, then newest first.</summary>
    public static IReadOnlyList<TestPackSourceCandidate> Candidates(IIntegrationJourneyPack pack, JourneyPackContext context, TestPackSourceRole role)
    {
        var authoritative = role.BindingRequired ? Authoritative(pack, context, role).Select(snapshot => snapshot.Id).ToHashSet() : [];
        return context.SourceSnapshots.Where(snapshot => Recognizes(role, snapshot))
            .OrderByDescending(snapshot => authoritative.Contains(snapshot.Id)).ThenByDescending(snapshot => snapshot.AnalyzedAt).ThenBy(snapshot => snapshot.Id)
            .Select(snapshot => new TestPackSourceCandidate(snapshot.Id, snapshot.Archive.FileName, snapshot.Archive.Sha256, snapshot.AnalyzedAt)
            {
                Bound = authoritative.Contains(snapshot.Id), ImportId = snapshot.ProjectImport?.ImportId,
                Technologies = role.RequiresInfrastructureAsCode ? InfrastructureFormats(snapshot) : [],
                MatchedOn = MatchedOn(role, snapshot), AnalysisStatus = SourceAnalysis.ReviewSourceEvidenceProvider.SourceStatus(snapshot.Status),
            }).ToArray();
    }

    public static TestPackSourceStatus Status(IIntegrationJourneyPack pack, JourneyPackContext context, TestPackSourceRole role)
    {
        var candidates = Candidates(pack, context, role);
        // A Test Pack reference has its own states (available / unavailable); an imported document of the same source is additional evidence only.
        if (role.Provider == TestPackSourceProvider.TestPackReference) return TestPackReferences.Status(pack, role, candidates);
        if (!role.BindingRequired) return Unbound(role, candidates);
        var integrations = Integrations(pack, context, role);
        var names = integrations.Select(item => item.DisplayName).ToArray();
        var bound = BoundToRole(pack, context, role);
        var authoritative = bound.Where(snapshot => Recognizes(role, snapshot)).ToArray();
        var available = Available(pack, context, role).Select(snapshot => snapshot.Archive.FileName).Distinct().ToArray();
        if (authoritative.Length > 0)
            return new(role, TestPackSourceState.Bound)
            {
                BoundArchives = [.. authoritative.Select(snapshot => snapshot.Archive.FileName).Distinct()], AvailableArchives = available, Integrations = names, Candidates = candidates,
                Detail = $"{role.Label} source is bound in Integration Quality Review{(names.Length > 0 ? $" ({string.Join(", ", names)})" : "")}.",
                NextAction = "Open IQR Evidence", NextActionTarget = "iqr-evidence",
            };
        // A role with its own integrations whose bound snapshot is another source: wrong source context, never "component missing".
        if (role.IntegrationRequirementKeys.Count > 0 && bound.Count > 0)
            return new(role, TestPackSourceState.ReviewRequired)
            {
                BoundArchives = [.. bound.Select(snapshot => snapshot.Archive.FileName).Distinct()], AvailableArchives = available, Integrations = names, Candidates = candidates,
                Detail = $"The snapshot bound to {string.Join(", ", names)} is not recognized as the {role.Label} source.",
                NextAction = "Check the binding in IQR Evidence", NextActionTarget = "iqr-evidence",
            };
        var noTarget = string.IsNullOrWhiteSpace(context.EnvironmentId);
        // Several snapshots of different content match and none is bound: which one is the source is the tester's choice, never the newest.
        if (candidates.Select(candidate => candidate.Fingerprint).Distinct(StringComparer.Ordinal).Count() > 1)
            return new(role, TestPackSourceState.ReviewRequired)
            {
                AvailableArchives = available, Integrations = names, Candidates = candidates,
                Detail = $"Several stored snapshots match the {role.Label} source ({string.Join(", ", available)}) and none is bound. The newest is not chosen automatically.",
                NextAction = noTarget ? "Select a target environment, then bind one snapshot in IQR Evidence" : "Review the matching snapshots and bind one in IQR Evidence",
                NextActionTarget = noTarget ? "target-environment" : "iqr-evidence",
            };
        if (available.Length > 0)
            return new(role, TestPackSourceState.AvailableNotBound)
            {
                AvailableArchives = available, Integrations = names, Candidates = candidates,
                Detail = noTarget
                    ? $"{role.Label} source is available ({string.Join(", ", available)}). Bindings belong to a target environment: select one to see or set the binding."
                    : names.Length > 0 || role.IntegrationRequirementKeys.Count == 0
                        ? $"{role.Label} source is available ({string.Join(", ", available)}) but not bound where it is needed."
                        : $"{role.Label} source is available ({string.Join(", ", available)}), but no configured integration uses it yet.",
                NextAction = noTarget ? "Select a target environment"
                    : names.Length > 0 || role.IntegrationRequirementKeys.Count == 0 ? "Bind it in IQR Evidence" : "Configure the integration in Target Environment → Integration Catalog, then bind it in IQR Evidence",
                NextActionTarget = noTarget ? "target-environment"
                    : names.Length > 0 || role.IntegrationRequirementKeys.Count == 0 ? "iqr-evidence" : "target-environment-integrations",
            };
        return NotFound(role, names);
    }

    /// <summary>A source read as workspace evidence directly (no binding): available when any snapshot provides it.</summary>
    private static TestPackSourceStatus Unbound(TestPackSourceRole role, IReadOnlyList<TestPackSourceCandidate> candidates)
    {
        if (candidates.Count == 0) return NotFound(role, []);
        var archives = candidates.Select(candidate => candidate.ArchiveFileName).Distinct().ToArray();
        var technologies = candidates.SelectMany(candidate => candidate.Technologies).Distinct().ToArray();
        return new(role, TestPackSourceState.Available)
        {
            AvailableArchives = archives, Candidates = candidates,
            Detail = $"{role.Label} is available in {string.Join(", ", archives)}{(technologies.Length > 0 ? $" ({string.Join(", ", technologies)})" : "")}. No binding is needed.",
            NextAction = "Open Source Analysis", NextActionTarget = "source-analysis",
        };
    }

    /// <summary>No snapshot provides the role: Missing only for a required source; an optional or conditional one is "not provided", never a failure.</summary>
    private static TestPackSourceStatus NotFound(TestPackSourceRole role, IReadOnlyList<string> integrations) => role.Requirement == TestPackSourceRequirement.Required
        ? new(role, TestPackSourceState.Missing)
        {
            Integrations = integrations,
            Detail = $"{role.Label} source is missing: no stored snapshot is recognized as it. Expected: {role.ExpectedArtifact}.",
            NextAction = "Import it in Source Analysis", NextActionTarget = "source-analysis",
        }
        : new(role, TestPackSourceState.NotProvided)
        {
            Integrations = integrations,
            Detail = role.Requirement == TestPackSourceRequirement.Conditional
                ? $"Not provided. Needed only when: {role.RequirementCondition ?? "the stated condition applies"}"
                : $"Optional — not provided. It adds evidence and never blocks the pack.{(role.RequirementCondition is { Length: > 0 } scope ? " " + scope : "")}",
            NextAction = $"Optional: import {role.ExpectedArtifact}", NextActionTarget = "source-analysis",
        };

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
