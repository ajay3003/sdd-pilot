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
/// Presentation of the integration catalog. Configuration status (is the record complete?) and IQR readiness (what can the
/// review assess for it?) are computed separately and never substituted for one another.
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
    public static IReadOnlyList<IntegrationGroup> Groups(IntegrationCatalog catalog, string? search = null, IntegrationConfigurationState? state = null)
    {
        var rows = catalog.Integrations.Select(i => Row(catalog, i))
            .Where(r => state is null || r.Configuration == state)
            .Where(r => string.IsNullOrWhiteSpace(search)
                || r.Definition.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)
                || (r.Topic ?? "").Contains(search, StringComparison.OrdinalIgnoreCase)
                || r.Consumer.Contains(search, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return rows.GroupBy(r => r.Definition.SystemName ?? (r.Definition.PlatformId is null ? "Other integrations" : Platform(catalog, r.Definition)?.Name ?? "Other integrations"))
            .Select(g => new IntegrationGroup(g.Key, Platform(catalog, g.First().Definition), g.ToList()))
            .ToList();
    }

    public static IntegrationRow Row(IntegrationCatalog catalog, IntegrationDefinition definition)
    {
        var platform = Platform(catalog, definition);
        var (state, missing, unconfirmed) = IntegrationConfigurationRules.Evaluate(definition, platform);
        var shortName = definition.SourceResource?.Split(".dbo.").LastOrDefault() is { Length: > 0 } table && definition.SystemName is not null ? table : definition.DisplayName;
        var topic = definition.EndpointOrTopic;
        var topicShort = topic is null ? "Not configured"
            : platform?.TopicPrefix is { Length: > 0 } prefix && topic.StartsWith(prefix + ".", StringComparison.Ordinal) && topic.LastIndexOf(".dbo.", StringComparison.Ordinal) is var at and >= 0
                ? "…" + topic[(at + 1)..]
            : topic;
        var consumer = definition.Consumer.DisplayName is { Length: > 0 } name
            ? definition.Consumer.MappingState == ConsumerMappingState.Suggested ? $"{name} (suggested)" : name
            : "Needs confirmation";
        var (readiness, readinessDetail) = ReviewReadiness(definition);
        return new IntegrationRow(definition, shortName, topic, topicShort, ProducerShort(definition.Producer), consumer, definition.Consumer.MappingState, state,
            missing.Count > 0 ? $"Missing: {string.Join(", ", missing)}" : unconfirmed.Count > 0 ? $"To confirm: {string.Join(", ", unconfirmed)}" : "Configuration complete",
            readiness, readinessDetail);
    }

    /// <summary>
    /// What Integration Quality Review can assess for this integration — not whether its configuration is complete. An unknown
    /// consumer group or missing contract limits only the checks that need them.
    /// </summary>
    public static (string Label, string Detail) ReviewReadiness(IntegrationDefinition definition)
    {
        if (!definition.Enabled) return ("Not included", "Disabled integrations are not reviewed.");
        if (definition.Kind != IntegrationKind.EventHub) return ("Configuration only", $"Domain review for {IntegrationConfigurationRules.KindLabel(definition.Kind)} is not implemented yet.");
        var limits = new List<string>();
        if (definition.Consumer.MappingState != ConsumerMappingState.Confirmed) limits.Add("consumer not confirmed");
        if (string.IsNullOrWhiteSpace(definition.ConsumerGroup)) limits.Add("consumer group unknown (no checkpoint review)");
        if (definition.ContractRelationship == ContractRelationshipState.NotConfigured) limits.Add("no contract (no compatibility review)");
        return limits.Count == 0 ? ("Ready", "All configured prerequisites are present.") : ("Partial", "Can run with limitations: " + string.Join("; ", limits) + ".");
    }

    private static string ProducerShort(string? producer) => producer switch
    {
        null or "" => "Not configured",
        var p when p.StartsWith("Debezium", StringComparison.OrdinalIgnoreCase) => "Debezium",
        var p => p,
    };

    public static string StateTone(IntegrationConfigurationState state) => state switch
    {
        IntegrationConfigurationState.Ready => "ready",
        IntegrationConfigurationState.Disabled => "muted",
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
            if (!string.IsNullOrWhiteSpace(url) && !Uri.TryCreate(url, UriKind.Absolute, out _)) errors.Add($"{label} must be an absolute URL.");
        return errors;
    }
}
