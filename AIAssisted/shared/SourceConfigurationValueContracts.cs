using System.Text.Json.Serialization;
using BirkNext.SourceArchitecture;

namespace BirkNext.SourceDomains;

// ── Source-declared configuration values ────────────────────────────────────────────────────────────────────────────────
//
// A configuration value one artifact of a Source Analysis snapshot declares, under a generic semantic key ("Environment.Server.Test",
// "Application.Route" …). Declared in source is not suggested, not configured, not runtime verified and not passed: the value is evidence a
// consuming feature may offer as a suggestion; Target Environment (or the consuming review) stores what a person accepts.

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourceConfigurationEvidenceKind { OpenApi, ApplicationConfig, InfrastructureAsCode, DeploymentManifest, SourceConstant, PipelineConfig, Other }

/// <summary>The environment a value is declared for. Any = not environment-specific; Unclassified = an environment value whose role the
/// artifact does not state (never guessed from list order).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourceEnvironmentRole { Any, Local, Test, Production, Unclassified }

/// <summary>Display groups for configuration evidence.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourceConfigurationGroup { Application, Environment, Authentication, ApiEndpoints, Payload, Contract, Messaging, Infrastructure, Other }

public sealed record SourceConfigurationValue
{
    public string SemanticKey { get; init; } = "";
    /// <summary>The display-safe value. Null for a sensitive value (only its presence and reference metadata are kept).</summary>
    public string? Value { get; init; }
    public SourceConfigurationGroup Group { get; init; } = SourceConfigurationGroup.Other;
    public SourceEnvironmentRole Environment { get; init; } = SourceEnvironmentRole.Any;
    public SourceConfigurationEvidenceKind EvidenceKind { get; init; }
    /// <summary>The artifact's path inside the snapshot (provenance, never a domain identity).</summary>
    public string Artifact { get; init; } = "";
    /// <summary>Where in the artifact: "servers[1]", "info.description (process tasks table)", "paths./a/b.put".</summary>
    public string Location { get; init; } = "";
    /// <summary>Why the value has this key and environment, e.g. "server description \"TT02 server\"".</summary>
    public string Basis { get; init; } = "";
    /// <summary>The logical source the artifact describes (an API title, a component), when the artifact names it.</summary>
    public string? LogicalSource { get; init; }
    /// <summary>The application/component the value belongs to (an app route, an API, a project) when the evidence states it. Values of
    /// different scopes never conflict: two apps in one configuration file are two apps, not one app with two routes.</summary>
    public string? Scope { get; init; }
    public ArchitectureEvidenceState Confidence { get; init; } = ArchitectureEvidenceState.Confirmed;
    public bool Sensitive { get; init; }
    public bool SecretReference { get; init; }
    /// <summary>Further facts of the same declaration (e.g. a data type's task and MIME types), safe labels only.</summary>
    public Dictionary<string, string> Attributes { get; init; } = [];
}

/// <summary>The generic semantic keys. Keys name a concept, never a product: a test pack or review declares which keys it consumes.</summary>
public static class SourceConfigurationKeys
{
    public const string ApplicationRoute = "Application.Route";
    /// <summary>The application's base address (scheme, host and route path), e.g. a hosted app's URL.</summary>
    public const string ApplicationBaseUrl = "Application.BaseUrl";
    /// <summary>The environment role the application's address belongs to ("Test", "Production"), when the provider can tell deterministically.</summary>
    public const string ApplicationEnvironment = "Application.Environment";
    /// <summary>An endpoint that receives the application's events (an event subscription's webhook).</summary>
    public const string EndpointEventWebhook = "Endpoint.EventWebhook";
    public const string ApplicationDisplayName = "Application.DisplayName";
    public const string ApplicationVersion = "Application.Version";
    public const string ApplicationDataType = "Application.DataType";
    public const string ApplicationFormDataType = "Application.FormDataType";
    public const string ApplicationTaskId = "Application.TaskId";
    public const string ApplicationTaskName = "Application.TaskName";
    public const string EnvironmentServer = "Environment.Server";
    public const string AuthenticationScheme = "Authentication.Scheme";
    public const string AuthenticationTokenProvider = "Authentication.TokenProvider";
    public const string EndpointInstanceCreate = "Endpoint.InstanceCreate";
    public const string EndpointInstanceRead = "Endpoint.InstanceRead";
    public const string EndpointDataWrite = "Endpoint.DataWrite";
    public const string EndpointProcessNext = "Endpoint.ProcessNext";
    public const string EndpointValidate = "Endpoint.Validate";
    public const string EndpointApplicationMetadata = "Endpoint.ApplicationMetadata";
    public const string EndpointOptions = "Endpoint.Options";
    public const string EndpointAuthenticationExchange = "Endpoint.AuthenticationExchange";
    public const string EndpointLocalTestToken = "Endpoint.LocalTestToken";
    public const string PayloadContentType = "Payload.ContentType";
    public const string ContractSchemaReference = "Contract.SchemaReference";
    public const string MessagingServiceBusNamespace = "Messaging.ServiceBus.Namespace";
    public const string MessagingServiceBusTopic = "Messaging.ServiceBus.Topic";
    public const string MessagingServiceBusSubscription = "Messaging.ServiceBus.Subscription";
    public const string MessagingServiceBusQueue = "Messaging.ServiceBus.Queue";
    public const string MessagingEventHubNamespace = "Messaging.EventHub.Namespace";
    public const string MessagingEventHubName = "Messaging.EventHub.Name";
    public const string MessagingEventHubConsumerGroup = "Messaging.EventHub.ConsumerGroup";

    /// <summary>Keys an artifact may legitimately declare several values for; two different values of these keys are not a conflict.</summary>
    public static readonly IReadOnlySet<string> MultiValued = new HashSet<string>(StringComparer.Ordinal)
    {
        ApplicationDataType, ApplicationTaskId, ApplicationTaskName, AuthenticationScheme, AuthenticationTokenProvider, EndpointInstanceCreate, EndpointInstanceRead,
        EndpointDataWrite, EndpointEventWebhook, EndpointProcessNext, EndpointValidate, EndpointApplicationMetadata, EndpointOptions, EndpointAuthenticationExchange, EndpointLocalTestToken,
        ContractSchemaReference, PayloadContentType, MessagingServiceBusTopic, MessagingServiceBusSubscription, MessagingServiceBusQueue, MessagingEventHubName,
        MessagingEventHubConsumerGroup, MessagingServiceBusNamespace, MessagingEventHubNamespace,
    };

    /// <summary>"Environment.Server" for an environment role, e.g. Environment.Server.Test.</summary>
    public static string Server(SourceEnvironmentRole role) => role is SourceEnvironmentRole.Any or SourceEnvironmentRole.Unclassified ? EnvironmentServer : $"{EnvironmentServer}.{role}";
}

/// <summary>All values one snapshot declares (null on snapshots analyzed before this evidence existed).</summary>
public sealed record SourceConfigurationValuesEvidence
{
    public int Version { get; init; } = 1;
    public List<SourceConfigurationValue> Values { get; init; } = [];
    /// <summary>What each producer reads, so a missing value is never read as "not in the source" when the producer does not support it.</summary>
    public List<string> Producers { get; init; } = [];
    public List<string> Limitations { get; init; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourceConfigurationResolutionState { Declared, Agreed, Conflict }

/// <summary>One value with every artifact that declares it.</summary>
public sealed record SourceConfigurationCandidate(string? Value, IReadOnlyList<SourceConfigurationValue> Provenance);

/// <summary>A semantic key in one environment across all artifacts: one value (Declared), the same value from several artifacts (Agreed,
/// provenance aggregated) or different values (Conflict — never silently resolved).</summary>
public sealed record SourceConfigurationResolution(string SemanticKey, SourceEnvironmentRole Environment, SourceConfigurationResolutionState State,
    IReadOnlyList<SourceConfigurationCandidate> Candidates)
{
    /// <summary>The application/component the values belong to, when stated.</summary>
    public string? Scope { get; init; }
    /// <summary>The single value when there is no conflict.</summary>
    public string? Value => State == SourceConfigurationResolutionState.Conflict ? null : Candidates.FirstOrDefault()?.Value;
}

// ── Configuration requirements and source guidance ─────────────────────────────────────────────────────────────────────

/// <summary>
/// A configuration value a consumer (a test pack, a review) needs, declared by semantic key and logical source — never by file name. The
/// evidence type names the kind of artifact that can provide it ("OpenAPI / Swagger document"), shown only because an analyzer reads it.
/// </summary>
public sealed record SourceConfigurationRequirement(string Key, string Label, string SemanticKey, string LogicalSource, string EvidenceType, string Purpose, string Owner)
{
    /// <summary>The prerequisite this value answers, when there is one (its blocker shows the guidance).</summary>
    public string? PrerequisiteKey { get; init; }
    /// <summary>Where an accepted value is stored, when a field exists ("App identifier — IQR → Extensions → Altinn"). Null: no field (shown only).</summary>
    public string? ConsumerField { get; init; }
    /// <summary>Application configuration keys (normalized, ':'-separated) that declare the same value, so a configuration file can agree or
    /// conflict with the API document. Empty: configuration files are not consulted for this value.</summary>
    public IReadOnlyList<string> ConfigurationKeys { get; init; } = [];
    /// <summary>The value a person must still verify at runtime (always true for declared source values).</summary>
    public bool RuntimeVerificationRequired { get; init; } = true;
    /// <summary>Which application/component's value is meant when the source declares several (matches a value's scope exactly or as its
    /// last route segment). Null: any scope; several different values are then a conflict.</summary>
    public string? ScopeHint { get; init; }
    /// <summary>The logical sources (pack source role keys) that can provide the value, in preference order. Empty: any bound source of the pack.</summary>
    public IReadOnlyList<string> SourceRoles { get; init; } = [];
    /// <summary>The value is safe, non-secret and meant to be source-derived: "Apply safe source-derived configuration" may store it into an
    /// EMPTY consumer field (never over a stored value, never on a conflict, never from an unbound snapshot).</summary>
    public bool AutoApply { get; init; }
    /// <summary>The pack integration requirement whose configured integration record owns this value (with <see cref="IntegrationField"/>), so
    /// consumers read the record instead of keeping their own copy. Null: no integration record owns it.</summary>
    public string? IntegrationRequirementKey { get; init; }
    /// <summary>The integration record field that holds the value: DisplayName, Kind, SystemName, Producer, Consumer, EndpointOrTopic,
    /// SourceResource or ConsumerGroup.</summary>
    public string? IntegrationField { get; init; }
    /// <summary>A safe, deterministic default the pack declares for every non-production environment (lowest precedence; never a secret).</summary>
    public string? DefaultValue { get; init; }
    /// <summary>Where a person resolves the value when nothing provides it ("Integration Quality Review → Extensions → Altinn").</summary>
    public string? OwnerAction { get; init; }
}

/// <summary>Where a resolved configuration value comes from, in precedence order: a value stored by its consumer, the configured integration
/// record that owns it, the canonical (IQR-bound) imported source, the test pack's built-in reference, the pack's safe default.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConfigurationValueOrigin { None, Configured, ConfiguredIntegration, CanonicalSource, TestPackReference, TestPackDefault }

/// <summary>A value another tier provides that differs from the resolved one (shown for review; never chosen silently).</summary>
public sealed record ConfigurationAlternative(ConfigurationValueOrigin Origin, string Value);

/// <summary>What one "apply safe source-derived configuration" stored, and what it left alone and why.</summary>
public sealed record SourceConfigurationApplied(string Key, string Label, string Value, string Provenance);
public sealed record SourceConfigurationNotApplied(string Key, string Label, string Reason);
public sealed record SourceConfigurationApplyResult(IReadOnlyList<SourceConfigurationApplied> Applied, IReadOnlyList<SourceConfigurationNotApplied> NotApplied);

/// <summary>Declared in source ≠ suggested ≠ configured ≠ runtime verified. Suggested = a single value in source the consumer's canonical
/// binding provides; AvailableNotBound = only an unbound snapshot declares it (never used); Conflict = bound artifacts disagree;
/// NotDeclared = the required source is bound but does not declare it; SourceMissing = no snapshot of the required source exists.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourceSuggestionState { NotDeclared, AvailableNotBound, Suggested, Conflict, SourceMissing, Reference }

public sealed record SourceConfigurationSuggestion(SourceConfigurationRequirement Requirement, SourceSuggestionState State)
{
    /// <summary>The suggested value (Suggested only).</summary>
    public string? Value { get; init; }
    /// <summary>Every value with its provenance (bound artifacts; for AvailableNotBound, where it could come from once bound — no value).</summary>
    public IReadOnlyList<SourceConfigurationCandidate> Candidates { get; init; } = [];
    /// <summary>The bound snapshot(s) consulted, as archive names (provenance; never an identity).</summary>
    public IReadOnlyList<string> BoundSources { get; init; } = [];
    /// <summary>Unbound snapshots that declare the value (discovery only).</summary>
    public IReadOnlyList<string> UnboundSources { get; init; } = [];
    /// <summary>What the consumer has stored, when it says (never overwritten by a suggestion).</summary>
    public string? ConfiguredValue { get; init; }
    /// <summary>The logical source the value was looked for in (label), when the requirement names one.</summary>
    public string? SourceRoleLabel { get; init; }
    /// <summary>What the test pack's built-in reference declares for the requirement (provenance "Test Pack reference"). It is the suggestion only
    /// when no bound imported evidence declares the value (state Reference); otherwise it is shown beside it, and a difference is a conflict note.</summary>
    public string? ReferenceValue { get; init; }
    /// <summary>Always false: a declared value is not runtime verified.</summary>
    public bool RuntimeVerified { get; init; }
    public string Detail { get; init; } = "";
    public bool ConfiguredDiffers => ConfiguredValue is not null && Value is not null && !string.Equals(ConfiguredValue, Value, StringComparison.OrdinalIgnoreCase);
    /// <summary>What the configured integration record that owns the value holds (null: no record, or the record does not set it).</summary>
    public string? IntegrationValue { get; init; }
    /// <summary>The configured integration that holds <see cref="IntegrationValue"/> (provenance).</summary>
    public string? IntegrationName { get; init; }
    /// <summary>The pack's safe default for the value (lowest precedence).</summary>
    public string? DefaultValue { get; init; }
    /// <summary>The one value every consumer uses for display and configuration readiness, by <see cref="SourceConfigurationPrecedence"/>.</summary>
    public string? ResolvedValue { get; init; }
    public ConfigurationValueOrigin ResolvedFrom { get; init; }
    /// <summary>Other tiers that disagree with the resolved value — visible, never resolved silently.</summary>
    public IReadOnlyList<ConfigurationAlternative> Disagreements { get; init; } = [];
    /// <summary>A value is known, but conflicting evidence or another tier disagrees: a person reviews it.</summary>
    public bool ReviewRequired => State == SourceSuggestionState.Conflict || Disagreements.Count > 0;
}

/// <summary>
/// The one precedence rule for safe configuration values, used by every consumer (Target Environment, Integration Quality Review, test packs):
/// explicitly stored value → configured integration record → canonical bound imported source → test pack reference → pack default → unresolved.
/// A lower tier never overrides a higher one; a lower tier that disagrees is kept as a visible disagreement. Nothing is stored here.
/// </summary>
public static class SourceConfigurationPrecedence
{
    public static SourceConfigurationSuggestion Resolve(SourceConfigurationSuggestion suggestion)
    {
        var multi = SourceConfigurationKeys.MultiValued.Contains(suggestion.Requirement.SemanticKey);
        var canonical = suggestion.State is SourceSuggestionState.Suggested ? suggestion.Value : null;
        var tiers = new (ConfigurationValueOrigin Origin, string? Value)[]
        {
            (ConfigurationValueOrigin.Configured, suggestion.ConfiguredValue),
            (ConfigurationValueOrigin.ConfiguredIntegration, suggestion.IntegrationValue),
            (ConfigurationValueOrigin.CanonicalSource, canonical),
            (ConfigurationValueOrigin.TestPackReference, suggestion.ReferenceValue),
            (ConfigurationValueOrigin.TestPackDefault, suggestion.DefaultValue),
        }.Where(tier => !string.IsNullOrWhiteSpace(tier.Value)).Select(tier => (tier.Origin, Value: tier.Value!.Trim())).ToArray();
        if (tiers.Length == 0) return suggestion with { ResolvedValue = null, ResolvedFrom = ConfigurationValueOrigin.None, Disagreements = [] };
        var (origin, value) = tiers[0];
        // A pack default only fills a gap: it never "disagrees" with a value someone or something declared.
        var disagreements = tiers.Skip(1).Where(tier => tier.Origin != ConfigurationValueOrigin.TestPackDefault && !Agrees(value, tier.Value, multi))
            .Select(tier => new ConfigurationAlternative(tier.Origin, tier.Value)).ToArray();
        return suggestion with { ResolvedValue = value, ResolvedFrom = origin, Disagreements = disagreements };
    }

    /// <summary>Same value (case- and trailing-slash-insensitive); for a key that may list several values, one being among the other's list.</summary>
    public static bool Agrees(string a, string b, bool multiValued)
    {
        static string Norm(string value) => value.Trim().TrimEnd('/');
        if (string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase)) return true;
        if (!multiValued) return false;
        static string[] List(string value) => [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Norm)];
        var (left, right) = (List(a), List(b));
        return left.All(item => right.Contains(item, StringComparer.OrdinalIgnoreCase)) || right.All(item => left.Contains(item, StringComparer.OrdinalIgnoreCase));
    }

    public static string Label(ConfigurationValueOrigin origin) => origin switch
    {
        ConfigurationValueOrigin.Configured => "Configured",
        ConfigurationValueOrigin.ConfiguredIntegration => "Configured integration",
        ConfigurationValueOrigin.CanonicalSource => "Canonical source evidence",
        ConfigurationValueOrigin.TestPackReference => "Test Pack reference",
        ConfigurationValueOrigin.TestPackDefault => "Test Pack default",
        _ => "Unresolved",
    };
}

public static class SourceConfigurationResolver
{
    /// <summary>Groups values by key and environment. Values of multi-valued keys are listed, never in conflict.</summary>
    public static IReadOnlyList<SourceConfigurationResolution> Resolve(IEnumerable<SourceConfigurationValue> values) =>
        values.GroupBy(value => (value.SemanticKey, value.Environment, Scope: value.Scope ?? ""))
            .OrderBy(group => group.Key.SemanticKey, StringComparer.Ordinal).ThenBy(group => group.Key.Environment).ThenBy(group => group.Key.Scope, StringComparer.Ordinal)
            .SelectMany(group =>
            {
                var candidates = group.GroupBy(value => value.Value, StringComparer.Ordinal)
                    .Select(byValue => new SourceConfigurationCandidate(byValue.Key, byValue.DistinctBy(item => (item.Artifact, item.Location)).ToArray())).ToArray();
                var scope = group.Key.Scope.Length == 0 ? null : group.Key.Scope;
                if (SourceConfigurationKeys.MultiValued.Contains(group.Key.SemanticKey))
                    return candidates.Select(candidate => new SourceConfigurationResolution(group.Key.SemanticKey, group.Key.Environment,
                        candidate.Provenance.Count > 1 ? SourceConfigurationResolutionState.Agreed : SourceConfigurationResolutionState.Declared, [candidate]) { Scope = scope });
                var state = candidates.Length > 1 ? SourceConfigurationResolutionState.Conflict
                    : candidates[0].Provenance.Count > 1 ? SourceConfigurationResolutionState.Agreed : SourceConfigurationResolutionState.Declared;
                return [new SourceConfigurationResolution(group.Key.SemanticKey, group.Key.Environment, state, candidates) { Scope = scope }];
            }).ToArray();
}
