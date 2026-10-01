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

    // ── Three separate dimensions per stage: configuration (settings), source evidence (analyzed code), runtime evidence (a run) ──

    public static readonly ScimStatus NotAnalyzed = new("Not analyzed", "muted");
    public static readonly ScimStatus NotAssessed = new("Not assessed", "muted");
    private static readonly ScimStatus Configured = new("Configured", "info");
    private static readonly ScimStatus NotConfiguredStatus = new("Not configured", "attention");

    /// <summary>The five flow nodes, each with configuration, source evidence and runtime evidence kept apart (never one combined badge).</summary>
    public static IReadOnlyList<ScimFlowNode> FlowNodes(IntegrationPlatform platform, ScimSourceEvidence? source, ScimEvidenceCheck? check)
    {
        var settings = platform.ScimProvisioning ?? new ScimProvisioningSettings();
        ScimStatus Setting(object? value) => platform.ScimProvisioning is null || value is null ? NotConfiguredStatus : Configured;
        // BirkNext reads neither the Entra provisioning job nor the downstream consumer's configuration: Not assessed, never "Configured".
        ScimStatus[] configuration = [NotAssessed, Setting(platform.ScimProvisioning), Setting(settings.Persistence), Setting(settings.Topic), NotAssessed];
        return Flow(settings).Select((node, i) => new ScimFlowNode(node.Stages[0], node.Title, node.Detail, configuration[i], SourceStatus(node.Stages, source, check), RuntimeStatus(node.Stages, check))).ToList();
    }

    private static ScimStatus SourceStatus(ScimStage[] stages, ScimSourceEvidence? source, ScimEvidenceCheck? check)
    {
        if (source is null) return NotAnalyzed;
        if (check is not null && check.Stages.Any(s => stages.Contains(s.Stage))) return SourceLabel(NodeState(check, stages).Source);
        // Before a review: only what the stored analysis itself states.
        return stages[0] switch
        {
            ScimStage.ScimEndpoint => SourceLabel(source.Detected ? ScimEvidenceState.SourceVerified : ScimEvidenceState.NotFound),
            ScimStage.KjentBrukerPersistence => SourceLabel(source.Facts.FirstOrDefault(f => f.Id == "scim-persistence")?.State ?? ScimEvidenceState.Configured),
            ScimStage.ServiceBusPublish => SourceLabel(source.Events.Count > 0 ? ScimEvidenceState.SourceVerified : ScimEvidenceState.Configured),
            _ => NotAssessed,
        };
    }

    /// <summary>A source-dimension state as a label. "Configured" here only means no source fact decided it — shown as Not established.</summary>
    private static ScimStatus SourceLabel(ScimEvidenceState state) => state switch
    {
        ScimEvidenceState.Configured or ScimEvidenceState.NotConfigured or ScimEvidenceState.NotTested => new("Not established", "muted"),
        ScimEvidenceState.NotAssessed => NotAssessed,
        _ => new(ScimLabels.State(state), Tone(state)),
    };

    private static ScimStatus RuntimeStatus(ScimStage[] stages, ScimEvidenceCheck? check)
    {
        if (check is null) return NotAssessed;
        var runtime = NodeState(check, stages).Runtime;
        // No base URL / not reachable by design is a missing capability, not a misconfiguration of SCIM: Not assessed.
        return runtime is ScimEvidenceState.NotConfigured or ScimEvidenceState.NotAssessed ? NotAssessed : new(ScimLabels.State(runtime), Tone(runtime));
    }

    /// <summary>What is configured (settings only). No runtime status ever appears here.</summary>
    public static IReadOnlyList<ScimConfigRow> Configuration(IntegrationPlatform platform)
    {
        var s = platform.ScimProvisioning;
        ScimConfigRow Row(string key, string label, string? value, string? detail = null) =>
            new(key, label, s is null || value is null ? NotConfiguredStatus : Configured, value ?? "Not configured", detail);
        return
        [
            Row("endpoint", "Endpoint", s?.BasePath),
            Row("auth", "Authentication", s?.Authentication is { } auth ? ShortMechanism(auth) : null, s?.Authentication),
            Row("persistence", "Persistence", s?.Persistence),
            Row("outbound", "Outbound", s?.Topic is { } topic ? $"Service Bus {topic}" : null),
            Row("events", "Events", s is { Events.Count: > 0 } ? string.Join(", ", s.Events) : null),
        ];
    }

    /// <summary>The mechanism without its technical qualifier: "Entra ID JWT from the provisioning service (…): …" → "Entra ID JWT from the provisioning service".</summary>
    public static string ShortMechanism(string authentication)
    {
        var cut = authentication.IndexOfAny(['(', ':']);
        return cut > 0 ? authentication[..cut].Trim() : authentication.Trim();
    }

    /// <summary>The stored source analysis as one state of its own: Not analyzed / Analyzed (SCIM detected or not found).</summary>
    public static ScimSourceView SourceAnalysis(ScimSourceEvidence? source) => source switch
    {
        null => new(NotAnalyzed, "No SCIM adapter source has been analyzed. Not analyzed is not evidence that an implementation is missing."),
        { Detected: true } => new(new("Analyzed", "info"),
            $"SCIM detected in {source.Project ?? "the analyzed source"}: {source.Operations.Count} operation(s), {source.Events.Count} event(s), {source.Requirements.Count} requirement(s) classified."),
        _ => new(new("Analyzed", "info"), "SCIM was not detected in the analyzed source."),
    };

    /// <summary>Whether the GET-only safe checks can reach the endpoint. Limited (base URL unknown) is a missing capability, never a failure.</summary>
    public static ScimSafeChecksView SafeChecksView(ScimProvisioningSettings settings, FrontendEnvironmentTypeName environment)
    {
        var (_, state, detail) = SafeChecks(settings, environment);
        if (state == ScimEvidenceState.NotSupported) return new(new("Not available", "muted"), detail, "Only source and configuration evidence can be assessed for this environment.", false);
        if (state == ScimEvidenceState.NotConfigured)
            return new(new("Limited", "attention"), "Public SCIM base URL is unknown.",
                "Only source and configuration evidence can currently be assessed; runtime endpoint behavior cannot yet be verified.", false);
        return new(new("Available", "info"), null, detail, true);
    }

    public static ScimMutationView Mutation(ScimProvisioningSettings settings)
    {
        var state = MutationState(settings);
        return new(new(ScimLabels.Mutation(state), state == ScimMutationState.NotConfigured ? "muted" : "info"),
            "Controlled runtime mutation verification: create, activate and deactivate a synthetic user (DEV or QA only, approved, with a cleanup plan). Optional — not required for the SCIM review, and never run automatically; executing the lifecycle is not part of this version.");
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

/// <summary>One labelled state; the tone is cosmetic, the label always carries the meaning.</summary>
public sealed record ScimStatus(string Label, string Tone);

/// <summary>A flow node with its three dimensions kept apart.</summary>
public sealed record ScimFlowNode(ScimStage Stage, string Title, string? Detail, ScimStatus Configuration, ScimStatus Source, ScimStatus Runtime);

public sealed record ScimConfigRow(string Key, string Label, ScimStatus Status, string Value, string? Detail);

public sealed record ScimSourceView(ScimStatus Status, string Summary);

/// <summary>Safe runtime checks testability: state, why, and what it means for the review.</summary>
public sealed record ScimSafeChecksView(ScimStatus Status, string? Reason, string Impact, bool CanContactEndpoint);

public sealed record ScimMutationView(ScimStatus Status, string Purpose);

/// <summary>The Target Environment type as the SCIM gate reads it (a string, so the gate matches the backend's exactly).</summary>
public readonly record struct FrontendEnvironmentTypeName(string Value)
{
    public static FrontendEnvironmentTypeName From(BirkNext.Web.Models.FrontendEnvironmentType type) => new(type.ToString());
}
