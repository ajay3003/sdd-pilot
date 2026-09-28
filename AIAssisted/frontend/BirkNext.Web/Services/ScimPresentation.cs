using BirkNext.Integrations;

namespace BirkNext.Web.Services;

/// <summary>
/// Pure projection of SCIM identity-provisioning evidence for the Integrations panel, IQR and export. Source, configuration and runtime stay
/// apart: a source-verified stage is never shown as observed, "Not tested" is never "Passed", and End-to-end verified only appears when the
/// backend established it from runtime evidence of every stage.
/// </summary>
public static class ScimPresentation
{
    public const string Focus = "scim";

    public static string Tone(ScimEvidenceState state) => state switch
    {
        ScimEvidenceState.Verified => "ready",
        ScimEvidenceState.SourceVerified or ScimEvidenceState.Configured or ScimEvidenceState.Matched or ScimEvidenceState.Observed => "info",
        ScimEvidenceState.IssueDetected => "fail",
        ScimEvidenceState.NeedsReview or ScimEvidenceState.Partial or ScimEvidenceState.NotFound => "attention",
        _ => "muted",
    };

    public static string Tone(ScimRequirementStatus status) => status switch
    {
        ScimRequirementStatus.Implemented => "ready",
        ScimRequirementStatus.NotFound => "fail",
        ScimRequirementStatus.PartiallyImplemented or ScimRequirementStatus.DocumentedOnly => "attention",
        _ => "muted",
    };

    public static string Tone(ScimOverallState state) => state switch
    {
        ScimOverallState.EndToEndVerified => "ready",
        ScimOverallState.IssueDetected => "fail",
        ScimOverallState.Partial => "attention",
        _ => "muted",
    };

    public static string Tone(ScimFindingSeverity severity) => severity switch
    {
        ScimFindingSeverity.High => "fail",
        ScimFindingSeverity.Medium => "attention",
        ScimFindingSeverity.Low => "info",
        _ => "muted",
    };

    public static string Tone(ScimTestCoverageState state) => state switch { ScimTestCoverageState.Tested => "info", ScimTestCoverageState.TestedWithFake => "attention", _ => "muted" };

    public static string Severity(ScimFindingSeverity severity) => severity.ToString();

    /// <summary>The five flow nodes shown as the provisioning path, each with the stages that carry its evidence.</summary>
    public static IReadOnlyList<(string Title, string? Detail, ScimStage[] Stages)> Flow(ScimProvisioningSettings settings) =>
    [
        (settings.Provider, null, [ScimStage.EntraProvisioning]),
        ("SCIM adapter", settings.BasePath, [ScimStage.ScimEndpoint, ScimStage.Authentication]),
        ("KjentBruker", null, [ScimStage.KjentBrukerPersistence]),
        ("Service Bus", settings.Topic, [ScimStage.ServiceBusPublish, ScimStage.ServiceBusRoute]),
        (settings.Downstream ?? "Downstream", null, [ScimStage.DownstreamProcessing, ScimStage.AuthorizationState]),
    ];

    /// <summary>The most telling state of a node: runtime evidence when there is any, otherwise its source/configuration state.</summary>
    public static (ScimEvidenceState Source, ScimEvidenceState Runtime) NodeState(ScimEvidenceCheck check, ScimStage[] stages)
    {
        var items = check.Stages.Where(s => stages.Contains(s.Stage)).ToList();
        if (items.Count == 0) return (ScimEvidenceState.NotAssessed, ScimEvidenceState.NotAssessed);
        return (Worst(items.Select(i => i.Source)), Worst(items.Select(i => i.Runtime)));
    }

    private static readonly ScimEvidenceState[] StateRank =
    [
        ScimEvidenceState.IssueDetected, ScimEvidenceState.NotFound, ScimEvidenceState.NeedsReview, ScimEvidenceState.Partial, ScimEvidenceState.Unavailable,
        ScimEvidenceState.NotConfigured, ScimEvidenceState.NotSupported, ScimEvidenceState.NotTested, ScimEvidenceState.NotAssessed,
        ScimEvidenceState.Configured, ScimEvidenceState.SourceVerified, ScimEvidenceState.Matched, ScimEvidenceState.Observed, ScimEvidenceState.Verified,
    ];

    /// <summary>The weakest state among several (a node is only as established as its weakest stage).</summary>
    public static ScimEvidenceState Worst(IEnumerable<ScimEvidenceState> states) => states.OrderBy(s => Array.IndexOf(StateRank, s)).DefaultIfEmpty(ScimEvidenceState.NotAssessed).First();

    /// <summary>Pre-run summary rows from configuration and stored source evidence (nothing contacted).</summary>
    public static IReadOnlyList<(string Label, string Value, ScimEvidenceState State, string TestId)> Summary(IntegrationPlatform platform, ScimSourceEvidence? source, FrontendEnvironmentTypeName environment)
    {
        var settings = platform.ScimProvisioning ?? new ScimProvisioningSettings();
        ScimSourceFact? F(string id) => source?.Facts.FirstOrDefault(f => f.Id == id);
        var detected = source?.Detected == true;
        var sourceState = source is null ? ScimEvidenceState.NotAssessed : detected ? ScimEvidenceState.SourceVerified : ScimEvidenceState.NotFound;
        return
        [
            ("SCIM", source is null ? "Configured — source not analyzed" : detected ? "Confirmed from source" : "Not found in the analyzed source", source is null ? ScimEvidenceState.Configured : sourceState, "scim-row-scim"),
            ("Endpoint", detected ? $"{source!.BasePath} ({source.Operations.Count} operations)" : settings.BasePath, detected ? ScimEvidenceState.SourceVerified : ScimEvidenceState.Configured, "scim-row-endpoint"),
            ("Authentication", settings.Authentication ?? "Not configured", F("scim-auth-required") is not null ? ScimEvidenceState.SourceVerified : settings.Authentication is null ? ScimEvidenceState.NotConfigured : ScimEvidenceState.Configured, "scim-row-auth"),
            ("Persistence", settings.Persistence ?? "Not configured", F("scim-persistence")?.State ?? (settings.Persistence is null ? ScimEvidenceState.NotConfigured : ScimEvidenceState.Configured), "scim-row-persistence"),
            ("Outbound", settings.Topic is null ? "Not configured" : $"Service Bus {settings.Topic}", settings.Topic is null ? ScimEvidenceState.NotConfigured : ScimEvidenceState.Configured, "scim-row-outbound"),
            ("Events", string.Join(", ", settings.Events.DefaultIfEmpty("Not configured")), source?.Events.Count > 0 ? ScimEvidenceState.SourceVerified : ScimEvidenceState.Configured, "scim-row-events"),
            ("Safe runtime checks", SafeChecks(settings, environment).Label, SafeChecks(settings, environment).State, "scim-row-runtime"),
            ("Synthetic mutation test", ScimLabels.Mutation(MutationState(settings)), ScimEvidenceState.NotConfigured, "scim-row-mutation"),
        ];
    }

    /// <summary>Mirrors the backend gate: never Production, a known non-production type and a configured base URL.</summary>
    public static (string Label, ScimEvidenceState State, string Detail) SafeChecks(ScimProvisioningSettings settings, FrontendEnvironmentTypeName environment) =>
        environment.Value == "Production" ? ("Not allowed (Production)", ScimEvidenceState.NotSupported, "Production is never contacted by the SCIM checks.")
        : environment.Value is not ("Local" or "Development" or "QA" or "Test" or "RC") ? ("Not allowed for this environment type", ScimEvidenceState.NotSupported, "Safe SCIM checks run only for a known non-production environment.")
        : string.IsNullOrWhiteSpace(settings.BaseUrl) ? ("Limited — base URL unknown", ScimEvidenceState.NotConfigured, "Without the public SCIM base URL, only source and configuration evidence is available.")
        : ("Available", ScimEvidenceState.Configured, "GET only: health, authentication challenge on a random id, SCIM metadata.");

    public static ScimMutationState MutationState(ScimProvisioningSettings settings)
    {
        var c = settings.SyntheticTest;
        if (string.IsNullOrWhiteSpace(c.TestUserPrefix) || string.IsNullOrWhiteSpace(c.Environment)) return ScimMutationState.NotConfigured;
        if (!c.Enabled || !c.ApprovedByTestLead || string.IsNullOrWhiteSpace(c.CleanupPlan)) return ScimMutationState.Disabled;
        return c.Validate() is null ? ScimMutationState.NotImplemented : ScimMutationState.NotAllowedForEnvironment;
    }

    public static readonly ScimArea[] AreaOrder = [ScimArea.Security, ScimArea.Reliability, ScimArea.ErrorHandling, ScimArea.Contract, ScimArea.Protocol, ScimArea.Observability, ScimArea.DataQuality, ScimArea.Privacy, ScimArea.Configuration];

    public static IReadOnlyList<(ScimArea Area, List<ScimCheck> Checks)> Areas(ScimEvidenceCheck check) =>
        AreaOrder.Select(a => (a, check.Checks.Where(c => c.Area == a).ToList())).Where(g => g.Item2.Count > 0).ToList();

    /// <summary>Headline lines for the result: what is established, and what is not, per area (✓ evidence, – gap).</summary>
    public static IReadOnlyList<(bool Established, string Text)> Highlights(ScimEvidenceCheck check, ScimArea area)
    {
        // The headline facts of each area first (authentication, deactivation, publish-before-success, retry configured vs observed …),
        // then the weakest remaining checks.
        var key = area switch
        {
            ScimArea.Security => new[] { "scim-auth-required", "scim-auth-missing", "scim-auth-invalid", "scim-deactivation-propagation" },
            ScimArea.Reliability => ["scim-order-publish-before-commit", "scim-retry-policy", "scim-retry-observed", "scim-e2e-activation"],
            ScimArea.Configuration => ["scim-detected", "scim-config-base-url", "scim-persistence", "scim-config-topic"],
            _ => [],
        };
        var candidates = check.Checks.Where(c => c.Area == area && !c.CheckId.StartsWith("scim-config-note", StringComparison.Ordinal)).ToList();
        return candidates.Where(c => key.Contains(c.CheckId)).OrderBy(c => Array.IndexOf(key, c.CheckId))
            .Concat(candidates.Where(c => !key.Contains(c.CheckId)).OrderBy(c => ScimLabels.IsEvidence(c.State) ? 1 : 0))
            .Take(4).Select(c => (ScimLabels.IsEvidence(c.State), $"{c.Title} — {ScimLabels.State(c.State)}")).ToList();
    }

    public static IReadOnlyList<(ScimRequirementStatus Status, int Count)> RequirementCounts(IEnumerable<ScimRequirement> requirements) =>
        requirements.GroupBy(r => r.Status).OrderBy(g => g.Key).Select(g => (g.Key, g.Count())).ToList();

    public static string Location(SourceLocation location)
    {
        var parts = location.File.Split('/');
        return $"{string.Join('/', parts.Skip(Math.Max(0, parts.Length - 2)))}:{location.Line}";
    }

    public static string Locations(IEnumerable<SourceLocation> locations) => string.Join(", ", locations.Take(3).Select(Location));

    public static string Headline(ScimEvidenceCheck check)
    {
        var high = check.Findings.Count(f => f.Severity == ScimFindingSeverity.High);
        var runtime = check.Stages.Count(s => ScimLabels.IsRuntime(s.Runtime));
        return $"{ScimLabels.Overall(check.OverallState)} · {runtime} of {check.Stages.Count} stages with runtime evidence · {check.Findings.Count} finding(s){(high > 0 ? $", {high} high" : "")}";
    }
}

/// <summary>The Target Environment type as the SCIM gate reads it (a string, so the gate matches the backend's exactly).</summary>
public readonly record struct FrontendEnvironmentTypeName(string Value)
{
    public static FrontendEnvironmentTypeName From(BirkNext.Web.Models.FrontendEnvironmentType type) => new(type.ToString());
}
