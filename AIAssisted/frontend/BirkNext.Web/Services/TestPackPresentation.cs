using BirkNext.Integrations;

namespace BirkNext.Web.Services;

/// <summary>
/// Frontend side of the test-pack layer: generic presentation of a pack descriptor and of its journeys' status dimensions. Which page hosts
/// a pack is registration metadata in <see cref="TestPackPages"/>. A pack without a page is still listed and reviewable through Integration Quality Review.
/// </summary>
public static class TestPackPresentation
{
    /// <summary>The page that hosts a pack, from the pack-page registration (<see cref="TestPackPages"/>); null when the pack has none.</summary>
    public static string? PageHref(string packId) => TestPackPages.Registered.TryGetValue(packId, out var href) ? href : null;

    /// <summary>Every view of a pack in navigation order, flattened.</summary>
    public static IReadOnlyList<TestPackView> Views(TestPackDescriptor pack) => pack.Navigation.SelectMany(group => group.Views).ToList();

    /// <summary>The view a page should show for a requested id: a declared view, else the pack's first view (its overview).</summary>
    public static TestPackView? Resolve(TestPackDescriptor pack, string? viewId) =>
        Views(pack).FirstOrDefault(view => string.Equals(view.ViewId, viewId, StringComparison.Ordinal)) ?? Views(pack).FirstOrDefault();

    // ── Status dimensions of a journey (generic: they read only the backend's journey view) ─────────────────────────────

    /// <summary>Configuration: whether the journey's required integrations are configured (Target Environment → Integrations).</summary>
    public static string Configuration(IntegrationJourneyView view) => view.Journey.Integrations.Count == 0 ? "Not applicable" : view.Maturity.Configured ? "Configured" : "Not configured";

    /// <summary>Source evidence: from the journey's own source-evidence prerequisites — Verified (all bound and found), "Available · needs
    /// binding" (found only in unbound snapshots), Partial, Missing (nothing found at all) or Not applicable. Unbound is never Missing.</summary>
    public static string SourceEvidence(IntegrationJourneyView view)
    {
        var source = view.Prerequisites.Where(item => item.Category == JourneyPrerequisiteCategory.SourceEvidence).ToList();
        if (source.Count == 0) return "Not applicable";
        var ready = source.Count(item => item.State == JourneyReadinessState.Ready);
        var unbound = source.Count(item => item.State == JourneyReadinessState.Partial);
        return ready == source.Count ? "Verified" : ready == 0 && unbound == 0 ? "Missing"
            : ready == 0 && unbound == source.Count ? ProjectImport.SourceSetupPresentation.AvailableNeedsBinding : "Partial";
    }

    // ── Active execution (execution-specific, never environment health) ─────────────────────────────────────────────────

    /// <summary>Where the trusted-environment execution policy is described (Target Environment); the policy itself is backend configuration.</summary>
    public const string ExecutionSettingsHref = "/admin/system-settings?section=target-environments";

    /// <summary>Whether this environment may run active tests — the backend's trusted-environment gate (trusted and execution allowed).
    /// Source imports, bindings, templates and reference metadata never change it.</summary>
    public static string ActiveExecution(IntegrationJourneyPackView? pack) => pack is null ? "Not known" : pack.EnvironmentTrusted ? "Allowed" : "Not allowed";

    /// <summary>One readiness dimension of a journey: its name, its value and a tone (the value always carries the meaning).</summary>
    public sealed record ReadinessDimension(string Key, string Label, string Value, string Tone);

    /// <summary>
    /// The separate readiness dimensions of a journey that are not its configuration: each logical source it needs (imported source or Test
    /// Pack reference, in the shared source wording), runtime (only a run observes it) and active execution (the backend gate). Never one
    /// combined "Ready".
    /// </summary>
    public static IReadOnlyList<ReadinessDimension> Dimensions(TestPackSourceGuidance? guidance, string journeyId, IntegrationJourneyPackView? pack)
    {
        var dimensions = SourceGuidancePresentation.ForJourney(guidance, journeyId).Select(source => ProjectImport.SourceSetupPresentation.IsReference(source)
                ? new ReadinessDimension($"source:{source.Role.Key}", $"{source.Role.Label} reference", source.State == TestPackSourceState.ReferenceAvailable ? "Available" : "Unavailable",
                    source.State == TestPackSourceState.ReferenceAvailable ? "ready" : "attention")
                : new ReadinessDimension($"source:{source.Role.Key}", $"{source.Role.Label} source", SourceGuidancePresentation.SourceStateLabel(source), SourceGuidancePresentation.SourceStateTone(source.State)))
            .ToList();
        dimensions.Add(new("runtime", "Runtime", "Not verified", "neutral"));
        dimensions.Add(new("execution", "Active execution", ActiveExecution(pack), pack?.EnvironmentTrusted == true ? "ready" : "neutral"));
        return dimensions;
    }

    /// <summary>Execution readiness: Ready when at least one scenario may be started (after confirmation), else Blocked. Ready is never passed.</summary>
    public static string ExecutionReadiness(IntegrationJourneyView view) => view.CanRun || view.RunnableScenarios.Count > 0 ? "Ready" : "Blocked";

    /// <summary>Mandatory prerequisites that are not ready, in the backend's order.</summary>
    public static IReadOnlyList<JourneyPrerequisite> Blockers(IntegrationJourneyView view) =>
        view.Prerequisites.Where(item => item.Mandatory && item.State != JourneyReadinessState.Ready).ToList();

    /// <summary>The reading group of a prerequisite, from its category metadata (never from its text).</summary>
    public static string Group(JourneyPrerequisiteCategory category) => category switch
    {
        JourneyPrerequisiteCategory.TrustedEnvironment => "Environment",
        JourneyPrerequisiteCategory.Integration => "Integration",
        JourneyPrerequisiteCategory.Authentication => "Authentication",
        JourneyPrerequisiteCategory.Authorization => "Authorization",
        JourneyPrerequisiteCategory.SyntheticData => "Test Data",
        JourneyPrerequisiteCategory.Contract => "Contracts",
        JourneyPrerequisiteCategory.SourceEvidence => "Source evidence",
        JourneyPrerequisiteCategory.Observation or JourneyPrerequisiteCategory.DownstreamVerification => "Observability",
        JourneyPrerequisiteCategory.Trigger or JourneyPrerequisiteCategory.Executor => "Execution",
        _ => "Other",
    };

    /// <summary>The fixed reading order of the groups.</summary>
    public static readonly IReadOnlyList<string> GroupOrder = ["Environment", "Integration", "Authentication", "Authorization", "Test Data", "Contracts", "Source evidence", "Observability", "Execution", "Other"];

    /// <summary>Prerequisites grouped by category metadata in <see cref="GroupOrder"/>; empty groups are left out and each item appears once.</summary>
    public static IReadOnlyList<(string Group, IReadOnlyList<JourneyPrerequisite> Items)> Grouped(IEnumerable<JourneyPrerequisite> items)
    {
        var list = items.DistinctBy(item => item.Key).ToList();
        return GroupOrder.Select(group => (group, (IReadOnlyList<JourneyPrerequisite>)list.Where(item => Group(item.Category) == group).ToList()))
            .Where(entry => entry.Item2.Count > 0).ToList();
    }

    /// <summary>The source a prerequisite was resolved from (metadata), or "BirkNext" when the backend names none.</summary>
    public static string Source(JourneyPrerequisite item) => string.IsNullOrWhiteSpace(item.Source) ? "BirkNext" : item.Source!;
}
