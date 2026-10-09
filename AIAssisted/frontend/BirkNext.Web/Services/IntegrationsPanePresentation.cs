using BirkNext.Integrations;

namespace BirkNext.Web.Services;

/// <summary>Summary strip of Target Environment → Integrations. Technical platform topics are never counted as business integrations.</summary>
public sealed record IntegrationsSummary(int Configured, int Enabled, int Ready, int NeedsConfirmation, int NeedsConfiguration, int Disabled, IReadOnlyList<string> Platforms);

/// <summary>One row of the grouped integrations table.</summary>
public sealed record IntegrationRow(
    IntegrationDefinition Definition,
    string ShortName,
    string? Topic,
    string TopicShort,
    string Producer,
    string Consumer,
    ConsumerMappingState MappingState,
    IntegrationConfigurationState Configuration,
    string ConfigurationDetail,
    string ReviewReadiness,
    string ReviewReadinessDetail);

public sealed record IntegrationGroup(string SystemName, IntegrationPlatform? Platform, IReadOnlyList<IntegrationRow> Rows);

/// <summary>
/// Presentation of the integration catalog. Configuration status (is the record complete?) and Review readiness (what can
/// Integration Quality Review assess for it?) are computed separately and never substituted for one another.
/// </summary>
public static class IntegrationsPanePresentation
{
    public static IntegrationsSummary Summary(IntegrationCatalog catalog)
    {
        var states = catalog.Integrations.Select(i => IntegrationConfigurationRules.Evaluate(i, Platform(catalog, i)).State).ToList();
        return new(catalog.Integrations.Count, catalog.Integrations.Count(i => i.Enabled),
            states.Count(s => s == IntegrationConfigurationState.Ready), states.Count(s => s == IntegrationConfigurationState.NeedsConfirmation),
            states.Count(s => s == IntegrationConfigurationState.NeedsConfiguration), states.Count(s => s == IntegrationConfigurationState.Disabled),
            catalog.Platforms.Select(p => p.Name).ToList());
    }

    public static IntegrationPlatform? Platform(IntegrationCatalog catalog, IntegrationDefinition definition) =>
        catalog.Platforms.FirstOrDefault(p => p.Id == definition.PlatformId);

    /// <summary>Rows grouped by integration system (16 CDC topics are one system), filtered by search text and configuration state.</summary>
    public static IReadOnlyList<IntegrationGroup> Groups(IntegrationCatalog catalog, string? search = null, IntegrationConfigurationState? state = null, string? readiness = null)
    {
        var rows = catalog.Integrations.Select(i => Row(catalog, i))
            .Where(r => state is null || r.Configuration == state)
            .Where(r => string.IsNullOrWhiteSpace(readiness) || r.ReviewReadiness == readiness)
            .Where(r => string.IsNullOrWhiteSpace(search)
                || r.Definition.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)
                || (r.Topic ?? "").Contains(search, StringComparison.OrdinalIgnoreCase)
                || r.Consumer.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.Configuration switch { IntegrationConfigurationState.NeedsConfiguration => 0, IntegrationConfigurationState.NeedsConfirmation => 1, IntegrationConfigurationState.Ready => 2, _ => 3 })
            .ThenBy(r => r.Definition.DisplayName, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Definition.Id, StringComparer.Ordinal)
            .ToList();
        return rows.GroupBy(r => r.Definition.SystemName ?? (r.Definition.PlatformId is null ? "Other integrations" : Platform(catalog, r.Definition)?.Name ?? "Other integrations"))
            .Select(g => new IntegrationGroup(g.Key, Platform(catalog, g.First().Definition), g.ToList()))
            .ToList();
    }

    public static IntegrationRow Row(IntegrationCatalog catalog, IntegrationDefinition definition)
    {
        var platform = Platform(catalog, definition);
        var (state, missing, unconfirmed) = IntegrationConfigurationRules.Evaluate(definition, platform);
        // The configured resource's last segment ("…dbo.Person" → "Person"); no schema or source system is assumed.
        var shortName = definition.SourceResource?.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() is { Length: > 0 } entity ? entity : definition.DisplayName;
        var topic = definition.EndpointOrTopic;
        var topicShort = topic is null ? "Not configured"
            : platform?.TopicPrefix is { Length: > 0 } prefix && topic.StartsWith(prefix + ".", StringComparison.Ordinal) && topic.LastIndexOf(".dbo.", StringComparison.Ordinal) is var at and >= 0
                ? "…" + topic[(at + 1)..]
            : topic;
        var consumer = definition.Consumer.DisplayName is { Length: > 0 } name ? name : "Not assigned";
        var (readiness, readinessDetail) = ReviewReadiness(definition, platform, state);
        return new IntegrationRow(definition, shortName, topic, topicShort, ProducerShort(definition.Producer), consumer, definition.Consumer.MappingState, state,
            missing.Count > 0 ? $"Missing: {string.Join(", ", missing)}" : unconfirmed.Count > 0 ? $"To confirm: {string.Join(", ", unconfirmed)}" : "Configuration complete",
            readiness, readinessDetail);
    }

    /// <summary>
    /// What Integration Quality Review can assess for this integration — not whether its configuration is complete. An unknown
    /// consumer group or missing contract limits only the checks that need them.
    /// </summary>
    public static (string Label, string Detail) ReviewReadiness(IntegrationDefinition definition, IntegrationPlatform? platform = null,
        IntegrationConfigurationState configuration = IntegrationConfigurationState.Ready)
    {
        if (!definition.Enabled) return ("Not included", "Disabled integrations are not reviewed.");
        if (definition.Kind != IntegrationKind.EventHub) return ("Configuration only", $"Domain review for {IntegrationConfigurationRules.KindLabel(definition.Kind)} is not implemented yet.");
        var limits = new List<string>();
        if (definition.Consumer.MappingState != ConsumerMappingState.Confirmed) limits.Add("consumer not confirmed");
        var (group, assumed) = IntegrationConfigurationRules.EffectiveConsumerGroup(definition, platform);
        if (group is null) limits.Add("consumer group unknown (no checkpoint review)");
        else if (assumed) limits.Add($"consumer group {group} is a configured assumption (needs confirmation; no checkpoint verdict)");
        if (definition.ContractRelationship == ContractRelationshipState.NotConfigured) limits.Add("no contract (no compatibility review)");
        if (platform is not null)
        {
            var runtime = platform.RuntimeEvidence;
            var missingRuntime = new List<string>();
            if (runtime?.EventHubMetadata != true) missingRuntime.Add("Event Hub metadata");
            if (runtime?.ResolvedCheckpointContainerUrl() is null) missingRuntime.Add("checkpoint evidence");
            // Telemetry is Application Insights by resource or a Log Analytics workspace; a workspace is never required.
            if (runtime?.TelemetryConfigured != true) missingRuntime.Add("runtime telemetry");
            if (missingRuntime.Count > 0) limits.Add("not configured: " + string.Join(", ", missingRuntime));
        }
        if (limits.Count == 0) return ("Ready", "All configured prerequisites are present.");
        var lead = configuration == IntegrationConfigurationState.NeedsConfiguration
            ? "Some configuration is missing, so the checks that need it are not assessed."
            : "Configuration is usable, but some runtime, checkpoint, consumer or contract evidence is unavailable.";
        return ("Partial", $"{lead} Missing: {string.Join("; ", limits)}. Partial is not a failure; the review runs with limitations.");
    }

    /// <summary>Review readiness labels in filter order. Values are the ones <see cref="ReviewReadiness"/> produces — no extra states.</summary>
    public static readonly string[] ReadinessLabels = ["Ready", "Partial", "Configuration only", "Not included"];

    /// <summary>The configured producer text as entered — no technology is special-cased.</summary>
    private static string ProducerShort(string? producer) => string.IsNullOrWhiteSpace(producer) ? "Not configured" : producer;

    public static string StateTone(IntegrationConfigurationState state) => state switch
    {
        IntegrationConfigurationState.Ready => "ready",
        IntegrationConfigurationState.Disabled => "muted",
        IntegrationConfigurationState.NeedsConfiguration => "warning",
        _ => "attention",
    };

    /// <summary>Required on save: display name, type and topic/endpoint. Everything else is optional metadata, never "Failed".</summary>
    public static IReadOnlyList<string> Validate(IntegrationDefinition definition)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(definition.DisplayName)) errors.Add("Display name is required.");
        if (string.IsNullOrWhiteSpace(definition.EndpointOrTopic)) errors.Add(definition.Kind == IntegrationKind.HttpApi ? "Endpoint is required." : "Event Hub / topic is required.");
        if (definition.ConsumerGroup is { } group && group.Trim().Length == 0) errors.Add("Consumer group cannot be blank; leave it unset if unknown.");
        foreach (var (label, url) in new[] { ("Health URL", definition.HealthUrl), ("Worker URL", definition.WorkerUrl), ("Monitoring URL", definition.MonitoringUrl), ("Runbook URL", definition.RunbookUrl) })
            if (!string.IsNullOrWhiteSpace(url) && (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
                || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
                || string.IsNullOrWhiteSpace(parsed.Host)))
                errors.Add($"{label} must be an absolute URL.");
        return errors;
    }
}
