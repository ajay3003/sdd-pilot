using System.Text.Json.Serialization;

namespace BirkNext.Integrations;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// SCIM identity provisioning (Microsoft Entra ID → SCIM adapter → KjentBruker → Service Bus → Autorisasjon). Identity provisioning is its own
// integration flow — never Event Hub CDC. Every stage carries SOURCE evidence and RUNTIME evidence separately: an endpoint that exists is not
// provisioning that works, a published deactivation is not revoked access, and a specification statement is not implemented behaviour.
// The safe runtime review only ever issues GET requests (health, metadata, authentication challenge); it never creates, changes or deletes a
// user, never enumerates users and never publishes a message. No provisioning secret, token or raw user payload is stored.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>How a specification requirement stands against the analyzed source.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScimRequirementStatus { Implemented, PartiallyImplemented, DocumentedOnly, NotFound, CannotAssess }

/// <summary>
/// One evidence state vocabulary. SourceVerified = the analyzed source implements it; Configured = configuration states it; Matched = two configured
/// sources agree; Observed = seen at runtime; Verified = an explicit expected-vs-observed runtime comparison held. NotTested is never Passed and
/// NotConfigured is never Failed.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScimEvidenceState { SourceVerified, Configured, Matched, Observed, Verified, Partial, NeedsReview, IssueDetected, NotTested, NotAssessed, NotConfigured, NotFound, NotSupported, Unavailable }

/// <summary>Stages of the provisioning flow, in order. Each has its own evidence; they are never collapsed into one Pass.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScimStage { EntraProvisioning, ScimEndpoint, Authentication, KjentBrukerPersistence, ServiceBusPublish, ServiceBusRoute, DownstreamProcessing, AuthorizationState }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScimArea { Configuration, Protocol, Security, Reliability, ErrorHandling, Contract, Observability, DataQuality, Privacy }

/// <summary>Overall state of a safe SCIM check. EndToEndVerified needs runtime evidence of every stage — source or configuration never produces it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScimOverallState { EndToEndVerified, Partial, IssueDetected, NotTestable }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScimFindingSeverity { High, Medium, Low, Info }

/// <summary>How far the analyzed repository's own tests cover a behaviour. TestedWithFake = covered only with a fake (auth handler, publisher).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScimTestCoverageState { Tested, TestedWithFake, NotTested }

/// <summary>Whether the explicit synthetic mutation test could run. It is a capability model: BirkNext never runs it by default.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScimMutationState { NotConfigured, Disabled, NotAllowedForEnvironment, NotImplemented }

/// <summary>An optional, explicit synthetic lifecycle test context (DEV/QA only, synthetic identity, approval, cleanup). Disabled by default.</summary>
public sealed record ScimSyntheticTestContext
{
    public bool Enabled { get; init; }
    /// <summary>DEV or QA. Production is never accepted.</summary>
    public string? Environment { get; init; }
    /// <summary>Opaque synthetic identity prefix, e.g. BIRKNEXT-SCIM-TEST-. Never a real employee.</summary>
    public string? TestUserPrefix { get; init; }
    public bool ApprovedByTestLead { get; init; }
    public string? CleanupPlan { get; init; }

    public const string RequiredPrefix = "BIRKNEXT-SCIM-TEST-";

    public string? Validate()
    {
        if (Environment is { } env && !env.Trim().Equals("DEV", StringComparison.OrdinalIgnoreCase) && !env.Trim().Equals("QA", StringComparison.OrdinalIgnoreCase))
            return "Synthetic SCIM testing is limited to DEV or QA.";
        if (TestUserPrefix is { Length: > 0 } prefix && !prefix.StartsWith(RequiredPrefix, StringComparison.Ordinal))
            return $"The synthetic test identity must start with {RequiredPrefix}.";
        return null;
    }
}

/// <summary>The configured SCIM provisioning integration of an environment (on its Identity provisioning platform). Never a secret.</summary>
public sealed record ScimProvisioningSettings
{
    public string Provider { get; init; } = "Microsoft Entra ID";
    public string Protocol { get; init; } = "SCIM 2.0";
    /// <summary>Public base URL Entra calls (tenant URL), when known. Null = Unknown — the safe runtime checks cannot run.</summary>
    public string? BaseUrl { get; init; }
    public string BasePath { get; init; } = "/scim/v2";
    public string UsersResource { get; init; } = "/Users";
    /// <summary>The authentication MECHANISM as configured or audited, e.g. "Entra ID JWT (Microsoft.Azure.SyncFabric)". Never the token.</summary>
    public string? Authentication { get; init; }
    public string? Persistence { get; init; } = "KjentBruker";
    /// <summary>The Service Bus platform the adapter publishes to (for topology and runtime correlation).</summary>
    public string? OutboundPlatformId { get; init; }
    public string? Topic { get; init; } = "entra.brukere";
    public List<string> Events { get; init; } = ["BrukerAktivert", "BrukerDeaktivert"];
    public string? Downstream { get; init; } = "Autorisasjon";
    public string? HealthLivePath { get; init; } = "/health/live";
    public string? HealthReadyPath { get; init; } = "/health/ready";
    public string? ContainerApp { get; init; }
    public string? ResourceGroup { get; init; }
    public string? ManagedIdentity { get; init; }
    /// <summary>Configured facts from outside the analyzed source (e.g. an audit of deployment configuration), each with its source.</summary>
    public List<string> ConfigurationNotes { get; init; } = [];
    public ScimSyntheticTestContext SyntheticTest { get; init; } = new();

    /// <summary>The first reason these settings cannot be stored, or null. A base URL never carries credentials or a query string.</summary>
    public string? Validate()
    {
        if (BaseUrl is { Length: > 0 } url)
        {
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
                return "The SCIM base URL is an absolute https URL.";
            if (uri.Scheme == "http" && !uri.IsLoopback) return "The SCIM base URL must use https (plain http is accepted for localhost only).";
            if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query)) return "The SCIM base URL must not carry credentials or a query string.";
        }
        if (!BasePath.StartsWith('/')) return "The SCIM base path starts with '/'.";
        return SyntheticTest.Validate();
    }
}

/// <summary>One source fact with file:line provenance.</summary>
public sealed record ScimSourceFact
{
    public string Id { get; init; } = "";
    public ScimArea Area { get; init; }
    public string Title { get; init; } = "";
    public ScimEvidenceState State { get; init; }
    public string Detail { get; init; } = "";
    public List<SourceLocation> Locations { get; init; } = [];
}

/// <summary>A SCIM operation the source maps, and what its handler does.</summary>
public sealed record ScimOperation
{
    public string Method { get; init; } = "";
    public string Path { get; init; } = "";
    public string? Handler { get; init; }
    public List<string> Responses { get; init; } = [];
    /// <summary>What the handler does, as source states it (e.g. "Publishes BrukerDeaktivert before SaveChanges inside a transaction").</summary>
    public List<string> Behaviour { get; init; } = [];
    public string? UnknownUser { get; init; }
    public SourceLocation? Location { get; init; }
}

/// <summary>An event contract as the source declares and sends it.</summary>
public sealed record ScimEventContract
{
    public string EventType { get; init; } = "";
    public string? Topic { get; init; }
    /// <summary>Body fields as serialized (the serializer's naming policy applied).</summary>
    public List<string> BodyFields { get; init; } = [];
    public List<string> MessageProperties { get; init; } = [];
    public List<string> Operations { get; init; } = [];
    /// <summary>How the body's event id relates to the transport MessageId, as source shows it.</summary>
    public string? IdentifierRelation { get; init; }
    public bool? SessionIdSet { get; init; }
    public List<string> IdentityFields { get; init; } = [];
    public SourceLocation? Location { get; init; }
}

/// <summary>A specification requirement and how the analyzed source stands against it.</summary>
public sealed record ScimRequirement
{
    public string Id { get; init; } = "";
    public string Text { get; init; } = "";
    public ScimRequirementStatus Status { get; init; }
    public string Evidence { get; init; } = "";
    public SourceLocation? SpecLocation { get; init; }
    public List<SourceLocation> Locations { get; init; } = [];
}

/// <summary>How the analyzed repository's own tests cover one behaviour (test names and the fakes they use).</summary>
public sealed record ScimTestCoverage
{
    public string Scenario { get; init; } = "";
    public ScimTestCoverageState State { get; init; }
    public List<string> Tests { get; init; } = [];
    public string Note { get; init; } = "";
}

/// <summary>A consumer of the provisioning topic, or a downstream use of the synchronized state, found in analyzed source.</summary>
public sealed record ScimDownstreamReference
{
    public string Kind { get; init; } = "";
    public string Detail { get; init; } = "";
    public SourceLocation? Location { get; init; }
}

/// <summary>What one analysis of uploaded source established about SCIM provisioning. Facts and provenance only; source text is never stored.</summary>
public sealed record ScimSourceEvidence
{
    public string EnvironmentId { get; init; } = "";
    public DateTimeOffset AnalyzedAt { get; init; }
    /// <summary>The Source Analysis snapshot this evidence came from (null when it was uploaded directly by an earlier version).</summary>
    public BirkNext.SourceEvidence.ReviewSourceScope? SourceScope { get; init; }
    public int AnalyzerVersion { get; init; }
    public List<SourceArchive> Archives { get; init; } = [];
    /// <summary>True only when a SCIM route surface is mapped. Service Bus usage alone never detects SCIM.</summary>
    public bool Detected { get; init; }
    public string? Project { get; init; }
    public string? BasePath { get; init; }
    public List<ScimOperation> Operations { get; init; } = [];
    public List<ScimSourceFact> Facts { get; init; } = [];
    public List<ScimEventContract> Events { get; init; } = [];
    public List<ScimRequirement> Requirements { get; init; } = [];
    public string? SpecificationSource { get; init; }
    public List<ScimTestCoverage> TestCoverage { get; init; } = [];
    public List<ScimDownstreamReference> Consumers { get; init; } = [];
    public List<ScimDownstreamReference> StateUsage { get; init; } = [];
    public List<string> Limitations { get; init; } = [];
    [JsonIgnore] public IEnumerable<ScimSourceFact> NeedsReview => Facts.Where(f => f.State is ScimEvidenceState.NeedsReview or ScimEvidenceState.IssueDetected);
}

/// <summary>One safe runtime observation (GET only). Stores status and derived facts — never a response body, token or user data.</summary>
public sealed record ScimProbeObservation
{
    public string CheckId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Method { get; init; } = "GET";
    public string Path { get; init; } = "";
    public string Expected { get; init; } = "";
    public int? StatusCode { get; init; }
    public ScimEvidenceState State { get; init; }
    public string Detail { get; init; } = "";
    public double? DurationMs { get; init; }
}

/// <summary>The safe runtime part of a SCIM check. State is typed (NotConfigured, NotSupported, Unavailable …), never zeros.</summary>
public sealed record ScimRuntimeEvidence
{
    public IntegrationEvidenceState State { get; init; }
    public string Reason { get; init; } = "";
    /// <summary>Scheme and host only.</summary>
    public string? Target { get; init; }
    public DateTimeOffset CapturedAt { get; init; }
    public List<ScimProbeObservation> Observations { get; init; } = [];
}

/// <summary>One stage of the flow with source/configuration evidence and runtime evidence kept apart.</summary>
public sealed record ScimStageEvidence
{
    public ScimStage Stage { get; init; }
    public string Title { get; init; } = "";
    public ScimEvidenceState Source { get; init; }
    public string SourceDetail { get; init; } = "";
    public ScimEvidenceState Runtime { get; init; }
    public string RuntimeDetail { get; init; } = "";
}

/// <summary>One statement in the result, by area.</summary>
public sealed record ScimCheck
{
    public string CheckId { get; init; } = "";
    public ScimArea Area { get; init; }
    public string Title { get; init; } = "";
    public ScimEvidenceState State { get; init; }
    public string Detail { get; init; } = "";
    public IntegrationEvidenceSource Provenance { get; init; }
    public List<SourceLocation> Locations { get; init; } = [];
}

public sealed record ScimFinding
{
    public string RuleId { get; init; } = "";
    public ScimFindingSeverity Severity { get; init; }
    public ScimArea Area { get; init; }
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public List<string> Evidence { get; init; } = [];
    public string Recommendation { get; init; } = "";
}

public sealed record ScimMutationCapability
{
    public ScimMutationState State { get; init; }
    public List<string> Reasons { get; init; } = [];
}

/// <summary>Result of "Run safe SCIM checks" (and of a review's SCIM part). Stored as an immutable snapshot.</summary>
public sealed record ScimEvidenceCheck
{
    public Guid RunId { get; init; }
    public string EnvironmentId { get; init; } = "";
    public string PlatformId { get; init; } = "";
    public string PlatformName { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
    public ScimOverallState OverallState { get; init; }
    /// <summary>The configured settings exactly as this run used them.</summary>
    public ScimProvisioningSettings Settings { get; init; } = new();
    public DateTimeOffset? SourceAnalyzedAt { get; init; }
    public List<SourceArchive> SourceArchives { get; init; } = [];
    public bool SourceDetected { get; init; }
    public List<ScimStageEvidence> Stages { get; init; } = [];
    public List<ScimCheck> Checks { get; init; } = [];
    public List<ScimOperation> Operations { get; init; } = [];
    public List<ScimEventContract> Events { get; init; } = [];
    public List<ScimRequirement> Requirements { get; init; } = [];
    public List<ScimTestCoverage> TestCoverage { get; init; } = [];
    public ScimRuntimeEvidence Runtime { get; init; } = new();
    public ScimMutationCapability SyntheticMutation { get; init; } = new();
    public List<ScimFinding> Findings { get; init; } = [];
    public List<string> Missing { get; init; } = [];
    public List<string> Limitations { get; init; } = [];
}

public sealed record ScimCheckSummary(Guid RunId, DateTimeOffset CompletedAt, string PlatformId, ScimOverallState OverallState, int Findings);

/// <summary>What the Integrations pane shows for one environment: the latest source analysis and the stored check history.</summary>
public sealed record ScimEvidenceOverview
{
    public ScimSourceEvidence? Source { get; init; }
    public ScimEvidenceCheck? Latest { get; init; }
    public List<ScimCheckSummary> History { get; init; } = [];
}

/// <summary>Pre-run summary of an Identity provisioning platform (configuration and stored source evidence only; nothing is contacted).</summary>
public sealed record ScimReadiness
{
    public string PlatformId { get; init; } = "";
    public string PlatformName { get; init; } = "";
    public bool SourceAnalyzed { get; init; }
    public bool Detected { get; init; }
    public int Operations { get; init; }
    public int RequirementsImplemented { get; init; }
    public int RequirementsTotal { get; init; }
    public int NeedsReview { get; init; }
    public IntegrationEvidenceState RuntimeState { get; init; }
    public string RuntimeReason { get; init; } = "";
    public ScimMutationState SyntheticMutation { get; init; }
}

public static class ScimLabels
{
    public static string State(ScimEvidenceState state) => state switch
    {
        ScimEvidenceState.SourceVerified => "Source verified",
        ScimEvidenceState.NeedsReview => "Needs review",
        ScimEvidenceState.IssueDetected => "Issue detected",
        ScimEvidenceState.NotTested => "Not tested",
        ScimEvidenceState.NotAssessed => "Not assessed",
        ScimEvidenceState.NotConfigured => "Not configured",
        ScimEvidenceState.NotFound => "Not found",
        ScimEvidenceState.NotSupported => "Not supported",
        _ => state.ToString(),
    };

    public static string Requirement(ScimRequirementStatus status) => status switch
    {
        ScimRequirementStatus.PartiallyImplemented => "Partially implemented",
        ScimRequirementStatus.DocumentedOnly => "Documented only",
        ScimRequirementStatus.NotFound => "Not found",
        ScimRequirementStatus.CannotAssess => "Cannot assess",
        _ => "Implemented",
    };

    public static string Overall(ScimOverallState state) => state switch
    {
        ScimOverallState.EndToEndVerified => "End-to-end verified",
        ScimOverallState.IssueDetected => "Issue detected",
        ScimOverallState.NotTestable => "Not testable",
        _ => "Partial",
    };

    public static string Stage(ScimStage stage) => stage switch
    {
        ScimStage.EntraProvisioning => "Microsoft Entra ID provisioning",
        ScimStage.ScimEndpoint => "SCIM endpoint",
        ScimStage.Authentication => "Authentication",
        ScimStage.KjentBrukerPersistence => "KjentBruker persistence",
        ScimStage.ServiceBusPublish => "Service Bus publish",
        ScimStage.ServiceBusRoute => "Service Bus route",
        ScimStage.DownstreamProcessing => "Downstream processing",
        _ => "Final authorization state",
    };

    public static string Area(ScimArea area) => area switch
    {
        ScimArea.ErrorHandling => "Error handling",
        ScimArea.DataQuality => "Data quality",
        _ => area.ToString(),
    };

    public static string Coverage(ScimTestCoverageState state) => state switch
    {
        ScimTestCoverageState.TestedWithFake => "Tested with a fake",
        ScimTestCoverageState.NotTested => "Not tested",
        _ => "Tested",
    };

    public static string Mutation(ScimMutationState state) => state switch
    {
        ScimMutationState.Disabled => "Disabled",
        ScimMutationState.NotAllowedForEnvironment => "Not allowed for this environment",
        ScimMutationState.NotImplemented => "Not available in this version",
        _ => "Not configured",
    };

    /// <summary>States that describe evidence of something (not a gap). Used to count what a check established.</summary>
    public static bool IsEvidence(ScimEvidenceState state) =>
        state is ScimEvidenceState.SourceVerified or ScimEvidenceState.Configured or ScimEvidenceState.Matched or ScimEvidenceState.Observed or ScimEvidenceState.Verified;

    public static bool IsRuntime(ScimEvidenceState state) => state is ScimEvidenceState.Observed or ScimEvidenceState.Verified;
}
