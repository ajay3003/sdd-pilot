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

    /// <summary>Configuration: whether the journey's required integrations are configured (Target Environment → Integration Catalog).</summary>
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

    // ── Blocker groups (tester actions, not raw checks) ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// One tester action that resolves several low-level prerequisite checks: what it is, its state, why, who owns it and the one action that
    /// fixes it (a link, or a detail group on the page). The underlying checks stay available as <see cref="Items"/>.
    /// </summary>
    public sealed record BlockerGroup(string Key, string Title, string State, string Explanation, string Owner, string ActionLabel, string? ActionHref, string? ActionGroup,
        IReadOnlyList<JourneyPrerequisite> Items)
    {
        /// <summary>Values already known for the checks of a run-configuration group ("Environment: Test (Test Pack default)").</summary>
        public IReadOnlyList<string> KnownValues { get; init; } = [];
    }

    /// <summary>The group whose action is the journey's primary next step: what the tester can resolve first (configure the integration, bind
    /// or choose evidence, save known values …); the environment-level execution gate comes last because it is not resolved per journey.</summary>
    public static BlockerGroup? PrimaryGroup(IReadOnlyList<BlockerGroup> groups) =>
        groups.FirstOrDefault(group => group.Key != "active-execution") ?? groups.FirstOrDefault();

    /// <summary>The anchor of a page's safe-configuration action (a run-configuration group's action target).</summary>
    public const string SafeConfigurationAnchor = "#safe-configuration";

    private static readonly string[] BlockerGroupOrder = ["active-execution", "integration", "integration-mapping", "source", "run-configuration", "authentication", "authorization", "test-data", "contract", "verification", "execution"];

    /// <summary>
    /// Unresolved prerequisites as tester-facing action groups (at most ten kinds, typically fewer): a check whose value is already known
    /// from guidance but not saved for the active run is "Run configuration" (saved only by an explicit apply); every other check groups by its
    /// category metadata. Generic: <paramref name="titles"/> lets a pack name a group in its own terms (e.g. its sign-in setup), and
    /// <paramref name="authenticationSetupHref"/> names where the pack's environment authentication setup lives (its Target Environment section).
    /// </summary>
    public static IReadOnlyList<BlockerGroup> BlockerGroups(IEnumerable<JourneyPrerequisite> blockers, TestPackSourceGuidance? guidance, string? profileId,
        IReadOnlyDictionary<string, string>? titles = null, IReadOnlyList<TestPackSourceStatus>? journeySources = null, string? authenticationSetupHref = null)
    {
        var list = blockers.DistinctBy(item => item.Key).ToList();
        // Known but not saved: resolved from the canonical source, a Test Pack reference or a default. A value an integration owns (its
        // endpoint) is read from that record by the run, so it is never listed as something to save.
        IReadOnlyList<BirkNext.SourceDomains.SourceConfigurationSuggestion> Known(JourneyPrerequisite item) =>
            [.. SourceGuidancePresentation.For(guidance, item.Key).Where(s => s.ResolvedValue is not null && s.ConfiguredValue is null
                && s.ResolvedFrom != BirkNext.SourceDomains.ConfigurationValueOrigin.ConfiguredIntegration)];
        // The journey's imported logical sources decide how a source group reads: a match that only needs choosing or binding is never "Missing".
        var imported = (journeySources ?? []).Where(source => source.Role.Provider == TestPackSourceProvider.ImportedSource).ToList();
        var ambiguous = imported.FirstOrDefault(source => source.State == TestPackSourceState.ReviewRequired);
        var unbound = imported.FirstOrDefault(source => source.State == TestPackSourceState.AvailableNotBound);
        string KeyOf(JourneyPrerequisite item) => Known(item).Count > 0 ? "run-configuration" : item.Category switch
        {
            JourneyPrerequisiteCategory.TrustedEnvironment => "active-execution",
            // Found but not fully mapped (canonical identity matched, a declared dimension missing): its own group, never "missing".
            JourneyPrerequisiteCategory.Integration when item.Key.EndsWith(":mapping", StringComparison.Ordinal) => "integration-mapping",
            // A configured integration that is not enabled for active runs is the same execution-permission fix as the environment gate.
            JourneyPrerequisiteCategory.Integration when item.State != JourneyReadinessState.NotConfigured => "active-execution",
            JourneyPrerequisiteCategory.Integration => "integration",
            JourneyPrerequisiteCategory.SourceEvidence => "source",
            JourneyPrerequisiteCategory.Authentication => "authentication",
            JourneyPrerequisiteCategory.Authorization => "authorization",
            JourneyPrerequisiteCategory.SyntheticData => "test-data",
            JourneyPrerequisiteCategory.Contract or JourneyPrerequisiteCategory.Observation => "contract",
            JourneyPrerequisiteCategory.DownstreamVerification => "verification",
            _ => "execution",
        };
        string Title(string key, string fallback) => titles is not null && titles.TryGetValue(key, out var title) ? title : fallback;
        static string Checks(int n) => $"{n} check{(n == 1 ? "" : "s")} incomplete";
        return [.. list.GroupBy(KeyOf).OrderBy(g => Array.IndexOf(BlockerGroupOrder, g.Key)).Select(g =>
        {
            var items = g.ToList();
            return g.Key switch
            {
                "active-execution" => new BlockerGroup(g.Key, Title(g.Key, "Active execution"), "Not allowed",
                    items.Any(i => i.Category == JourneyPrerequisiteCategory.Integration) ? "Active execution is not enabled for this environment or its integration." : "This environment has not been enabled for active execution.",
                    "Target Environment", "Manage active execution", ExecutionSettingsHref, null, items),
                "integration" => new BlockerGroup(g.Key, Title(g.Key, "Integration"),
                    items.Any(i => i.State == JourneyReadinessState.NotConfigured) ? "Missing integration" : "Not enabled for active runs",
                    items.Any(i => i.State == JourneyReadinessState.NotConfigured) ? "The journey's integration is not configured yet." : "The integration is configured but not enabled for active runs in this environment.",
                    "Target Environment → Integration Catalog", "Configure integration", IntegrationReviewPresentation.IntegrationsHref(profileId), null, items),
                "integration-mapping" => new BlockerGroup(g.Key, Title(g.Key, "Integration mapping"), "Integration found · mapping incomplete",
                    items[0].Detail, "Target Environment → Integration Catalog", "Review integration mapping",
                    MappingIntegrationId(items[0]) is { } mappedId ? IntegrationReviewPresentation.IntegrationHref(profileId, mappedId) : IntegrationReviewPresentation.IntegrationsHref(profileId), null, items),
                "source" => ambiguous is not null
                    ? new BlockerGroup(g.Key, Title(g.Key, $"{ambiguous.Role.Label} source evidence"), "Review required",
                        $"Several stored {ambiguous.Role.Label} sources match; choose the canonical one — the newest is never chosen for you.", "Integration Quality Review → Setup & Evidence",
                        $"Choose {ambiguous.Role.Label} evidence", SourceGuidancePresentation.ActionHref("iqr-evidence"), null, items)
                    : items.Any(i => i.State == JourneyReadinessState.Partial) || unbound is not null
                    ? new BlockerGroup(g.Key, Title(g.Key, unbound is null ? "Source evidence" : $"{unbound.Role.Label} source evidence"), ProjectImport.SourceSetupPresentation.AvailableNeedsBinding,
                        "Matching source evidence exists but is not bound in Integration Quality Review.", "Integration Quality Review → Setup & Evidence",
                        unbound is null ? "Bind source evidence" : $"Bind {unbound.Role.Label} evidence", SourceGuidancePresentation.ActionHref("iqr-evidence"), null, items)
                    : imported.Any(source => source.State == TestPackSourceState.Bound)
                    // The source is bound; what is missing is the correlation of a logical role to its technical implementation.
                    ? new BlockerGroup(g.Key, Title(g.Key, "Source correlation"), "Source bound · component not correlated",
                        items[0].Detail, "Integration Quality Review → Setup & Evidence", "Review source evidence", SourceGuidancePresentation.ActionHref("iqr-evidence"), null, items)
                    : new BlockerGroup(g.Key, Title(g.Key, "Source evidence"), "Missing", "No matching source has been imported.", "Import Project", "Import source", "/project-import", null, items),
                "run-configuration" => new BlockerGroup(g.Key, Title(g.Key, "Run configuration"), "Known · not saved for active run",
                    "These values are already known. An active run uses only values you explicitly save for it.", "Test pack run configuration",
                    "Review & apply safe configuration", SafeConfigurationAnchor, null, items)
                {
                    KnownValues = [.. items.SelectMany(Known).Select(s => $"{s.Requirement.Label}: {s.ResolvedValue} ({SourceGuidancePresentation.Origin(s)})").Distinct()],
                },
                // The unattended client and its session secret are configured once on the Target Environment (never a terminal).
                "authentication" => new BlockerGroup(g.Key, Title(g.Key, "Authentication setup"), Checks(items.Count), "The unattended test sign-in setup is incomplete. Only automated execution needs it.",
                    "Target Environment", "View environment setup", authenticationSetupHref ?? "/admin/system-settings?section=target-environments", null, items),
                "authorization" => new BlockerGroup(g.Key, Title(g.Key, "Authorization policy"), "Not confirmed", "The test application's authorization policy is not confirmed.",
                    "Backend configuration (administrator)", "View details", null, "local", items),
                "test-data" => new BlockerGroup(g.Key, Title(g.Key, "Test data"), "Incomplete", "Approved synthetic test data is incomplete.", "Test Data", "Open Test Data", "/test-data", null, items),
                "contract" => new BlockerGroup(g.Key, Title(g.Key, "Contract & runtime evidence"), "Not verified",
                    "Reference metadata and source evidence are separate from runtime verification, which is not done yet.", "Test pack / Source Analysis", "View details", null, "contract", items),
                "verification" => new BlockerGroup(g.Key, Title(g.Key, "Downstream verification"), "Not verified",
                    "Downstream processing is shown only by runtime evidence; transport acceptance does not prove it.", "Runtime evidence", "View details", null, "advanced", items),
                _ => new BlockerGroup(g.Key, Title(g.Key, "Execution"), "Incomplete", "The run setup is incomplete.", "Test pack", "View details", null, "advanced", items),
            };
        })];
    }

    /// <summary>The configured integration a mapping prerequisite is about ("integration:{requirement}:mapping" rows name it in their detail).</summary>
    private static string? MappingIntegrationId(JourneyPrerequisite item) => item.Diagnostics.FirstOrDefault(line => line.StartsWith("integration-id:", StringComparison.Ordinal))?["integration-id:".Length..];

    /// <summary>The source a prerequisite was resolved from (metadata), or "BirkNext" when the backend names none.</summary>
    public static string Source(JourneyPrerequisite item) => string.IsNullOrWhiteSpace(item.Source) ? "BirkNext" : item.Source!;
}
