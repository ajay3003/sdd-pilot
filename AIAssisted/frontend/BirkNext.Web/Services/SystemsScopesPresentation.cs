using BirkNext.Interpretation;
using BirkNext.Scopes;

namespace BirkNext.Web.Services;

/// <summary>What the Systems tab can honestly say. "No systems" is only ever shown for a finished interpretation that found none.</summary>
public enum SystemsPageState
{
    Loading,
    NoProject,
    NoEnvironment,
    NoSource,
    NotInterpreted,
    /// <summary>An analysis is running and there is no earlier result to show yet.</summary>
    Analysing,
    ZeroSystems,
    Ready,
    Error,
}

/// <summary>Pure presentation rules of Systems & Scopes (states, labels, filters). No calls, no ids shown as names.</summary>
public static class SystemsScopesPresentation
{
    public static SystemsPageState StateOf(bool loading, bool hasProject, string? environmentScope, string? error, SystemsOverviewView? overview, int? sourceCount) =>
        loading ? SystemsPageState.Loading
        : !hasProject ? SystemsPageState.NoProject
        : string.IsNullOrWhiteSpace(environmentScope) ? SystemsPageState.NoEnvironment
        : overview is null ? (error is null ? SystemsPageState.Loading : SystemsPageState.Error)
        : overview.InterpretationId is null ? (overview.AnalysisRunningSince is not null ? SystemsPageState.Analysing
            : sourceCount == 0 ? SystemsPageState.NoSource : SystemsPageState.NotInterpreted)
        : overview.Systems.Count == 0 ? SystemsPageState.ZeroSystems
        : SystemsPageState.Ready;

    public static string ConnectionStateLabel(SystemConnectionState state) => state switch
    {
        SystemConnectionState.Candidate => "Candidate — needs evidence or review",
        SystemConnectionState.NeedsReview => "Needs review",
        SystemConnectionState.FromSourceEvidence => "From source evidence",
        SystemConnectionState.ConfirmedByPerson => "Confirmed by a person",
        SystemConnectionState.ObservedAtRuntime => "Observed at runtime",
        SystemConnectionState.Rejected => "Rejected by a person",
        _ => state.ToString(),
    };

    public static string ConnectionTone(SystemConnectionState state) => state switch
    {
        SystemConnectionState.ConfirmedByPerson or SystemConnectionState.ObservedAtRuntime => "ok",
        SystemConnectionState.FromSourceEvidence => "info",
        SystemConnectionState.Candidate or SystemConnectionState.NeedsReview => "attention",
        _ => "muted",
    };

    public static string TypeLabel(CanonicalEntityType type) => type == CanonicalEntityType.Service ? "Service" : "System";

    public static string KindLabel(ScopeMemberKind kind, bool plural = false) => (kind, plural) switch
    {
        (ScopeMemberKind.System, false) => "System or service", (ScopeMemberKind.System, true) => "Systems and services",
        (ScopeMemberKind.UserStory, false) => "User story", (ScopeMemberKind.UserStory, true) => "User stories",
        (ScopeMemberKind.TestDataAsset, false) => "Test Data asset", (ScopeMemberKind.TestDataAsset, true) => "Test Data",
        (ScopeMemberKind.AcceptanceCriterion, false) => "Acceptance criterion", (ScopeMemberKind.AcceptanceCriterion, true) => "Acceptance criteria",
        (ScopeMemberKind.TestCase, false) => "Test case", (ScopeMemberKind.TestSuite, false) => "Test suite",
        (ScopeMemberKind.CriticalE2EFlow, false) => "Critical E2E flow", (ScopeMemberKind.CriticalE2EFlow, true) => "Critical E2E flows",
        (var k, false) => k.ToString(), (var k, true) => k + "s",
    };

    /// <summary>The member kinds a person can pick from a catalog, in the order a tester thinks about them.</summary>
    public static readonly IReadOnlyList<ScopeMemberKind> CatalogKinds =
    [
        ScopeMemberKind.System, ScopeMemberKind.Scenario, ScopeMemberKind.Requirement, ScopeMemberKind.UserStory, ScopeMemberKind.Feature,
        ScopeMemberKind.Journey, ScopeMemberKind.Contract, ScopeMemberKind.TestDataAsset, ScopeMemberKind.CriticalE2EFlow,
    ];

    public static string PurposeLabel(ScopePurpose p) => p switch
    {
        ScopePurpose.FunctionalQuality => "Functional quality", ScopePurpose.Test => "Test scope", ScopePurpose.IntegrationE2E => "Integration / E2E",
        ScopePurpose.ReleaseDelivery => "Delivery / release", _ => "Other",
    };

    public static string StateLabel(ScopeSummary s) => s.State switch
    {
        ScopeState.Approved when s.CurrentRevision != s.ApprovedRevision => $"Approved (rev. {s.ApprovedRevision}) · draft rev. {s.CurrentRevision}",
        ScopeState.Approved => $"Approved (rev. {s.ApprovedRevision})",
        ScopeState.InReview => $"In review (rev. {s.CurrentRevision})",
        ScopeState.Archived => "Archived",
        _ => $"Draft (rev. {s.CurrentRevision})",
    };

    public static string CheckLabel(ScopeMemberCheck check) => check switch
    {
        ScopeMemberCheck.Current => "Current",
        ScopeMemberCheck.Missing => "Missing from catalog — review",
        ScopeMemberCheck.OtherContext => "From another environment",
        _ => "Not checkable here",
    };

    /// <summary>Systems filtered by search (name and every alias), kind, review need and scope membership; stable order by name.</summary>
    public static List<SystemOverviewItem> Filter(IEnumerable<SystemOverviewItem> systems, string? search, string kind, string review, string membership,
        Func<string, int> scopeCount)
    {
        var q = (search ?? "").Trim();
        return [.. systems.Where(s => q.Length == 0 || s.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || s.Aliases.Any(a => a.Alias.Contains(q, StringComparison.OrdinalIgnoreCase)))
            .Where(s => kind switch { "system" => s.Type == CanonicalEntityType.System, "service" => s.Type == CanonicalEntityType.Service, _ => true })
            .Where(s => review switch { "needs-review" => s.NeedsReview > 0, "no-review" => s.NeedsReview == 0, _ => true })
            .Where(s => membership switch { "in-scope" => scopeCount(s.Key) > 0, "no-scope" => scopeCount(s.Key) == 0, _ => true })
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Key, StringComparer.Ordinal)];
    }

    /// <summary>Connections filtered by a focused system and direction, evidence state and origin. Rejected ones are hidden unless asked for.</summary>
    public static List<SystemConnectionView> FilterConnections(IEnumerable<SystemConnectionView> connections, string? focusKey, string direction, string state, string origin,
        string? search)
    {
        var q = (search ?? "").Trim();
        return [.. connections
            .Where(c => focusKey is null || direction switch
            {
                "out" => c.SourceKey == focusKey,
                "in" => c.TargetKey == focusKey,
                _ => c.SourceKey == focusKey || c.TargetKey == focusKey,
            })
            .Where(c => state switch
            {
                "all" => true,
                "rejected" => c.State == SystemConnectionState.Rejected,
                "review" => c.State is SystemConnectionState.Candidate or SystemConnectionState.NeedsReview,
                "established" => c.State is SystemConnectionState.FromSourceEvidence or SystemConnectionState.ConfirmedByPerson or SystemConnectionState.ObservedAtRuntime,
                _ => c.State != SystemConnectionState.Rejected,
            })
            .Where(c => origin switch { "canonical" => c.Origin == "Canonical interpretation", "topology" => c.Origin == "Integration Topology", _ => true })
            .Where(c => q.Length == 0 || c.SourceName.Contains(q, StringComparison.OrdinalIgnoreCase) || c.TargetName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || c.Label.Contains(q, StringComparison.OrdinalIgnoreCase))];
    }
}
