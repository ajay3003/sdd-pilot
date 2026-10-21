namespace BirkNext.Integrations;

/// <summary>What is known about one integration beyond its configuration: its canonical source binding and its contract discovery.</summary>
public sealed record IntegrationReadinessEvidence
{
    public bool SourceBound { get; init; }
    public string? BoundArchive { get; init; }
    /// <summary>Contract discovery for the integration, when it has been read; null = contracts are judged from the stored relationship only.</summary>
    public IntegrationContractDiscovery? Contracts { get; init; }
    public IReadOnlyList<IntegrationFieldEvidence> FieldEvidence { get; init; } = [];
}

/// <summary>
/// Review readiness of ONE integration with its causes: what is ready, what needs attention (limits the review), what cannot be assessed,
/// and what does not block (not applicable fields). Never configuration completeness and never a result.
/// </summary>
public sealed record IntegrationReviewReadinessAssessment(string Label, IntegrationReviewCapability Capability)
{
    public IReadOnlyList<string> Ready { get; init; } = [];
    public IReadOnlyList<string> NeedsAttention { get; init; } = [];
    public IReadOnlyList<string> NotAssessed { get; init; } = [];
    public IReadOnlyList<string> NotBlocking { get; init; } = [];
    /// <summary>One sentence for a compact row: the label's cause.</summary>
    public string Detail { get; init; } = "";
}

/// <summary>Which review profile an integration kind has. The single mapping the IQR backend and the Integrations pane both use.</summary>
public static class IntegrationReviewCapabilities
{
    public static IntegrationReviewCapability Of(IntegrationKind kind) => kind switch
    {
        IntegrationKind.EventHub => IntegrationReviewCapability.EventStream,
        IntegrationKind.ServiceBus => IntegrationReviewCapability.MessageBroker,
        IntegrationKind.HttpApi => IntegrationReviewCapability.HttpApi,
        _ => IntegrationReviewCapability.ConfigurationOnly,
    };
}

/// <summary>
/// Causal per-integration review readiness from the shared field rules, the integration's capability profile, its canonical source binding and
/// its contract discovery. Not-applicable fields are listed as not blocking, never as limits; source evidence found is never runtime verified;
/// contracts found are never compatible.
/// </summary>
public static class IntegrationReadinessRules
{
    public static readonly string[] Labels = ["Ready", "Partial", "Configuration only", "Not included"];

    public static IntegrationReviewReadinessAssessment Assess(IntegrationDefinition definition, IntegrationPlatform? platform, IntegrationReadinessEvidence? evidence = null)
    {
        evidence ??= new IntegrationReadinessEvidence();
        var capability = IntegrationReviewCapabilities.Of(definition.Kind);
        if (!definition.Enabled) return new("Not included", capability) { Detail = "Disabled integrations are not reviewed." };

        var configuration = IntegrationFieldRules.Assess(definition, platform, evidence.FieldEvidence);
        var ready = new List<string>();
        var attention = new List<string>();
        var notAssessed = new List<string>();
        var notBlocking = configuration.NotApplicable.Where(f => f.Resolution == IntegrationFieldResolution.NotApplicable)
            .Select(f => $"{f.Label} not applicable").ToList();

        if (configuration.Field(IntegrationField.ConfiguredChannel) is { Value: { } channel })
            ready.Add(definition.Kind == IntegrationKind.HttpApi
                ? Uri.TryCreate(channel, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? "Endpoint configured (HTTPS)" : "Endpoint configured"
                : "Channel configured");
        foreach (var missing in configuration.Missing) attention.Add($"{missing.Label} not configured");
        var mapping = configuration.Field(IntegrationField.ConsumerMapping)!;
        var consumer = configuration.Field(IntegrationField.Consumer)!;
        if (consumer.Value is null) attention.Add("Consumer not assigned");
        else if (mapping.Resolution == IntegrationFieldResolution.ReviewRequired) attention.Add("Consumer mapping differs from source evidence — review required");
        else if (mapping.Resolution != IntegrationFieldResolution.Configured) attention.Add("Consumer mapping needs confirmation");
        else ready.Add("Consumer mapping confirmed");

        if (capability == IntegrationReviewCapability.ConfigurationOnly)
        {
            attention.Add($"No domain review profile for {IntegrationConfigurationRules.KindLabel(definition.Kind)}: only configuration is reviewed");
            return Build("Configuration only", capability, ready, attention, notAssessed, notBlocking,
                $"Only configuration is reviewed for {IntegrationConfigurationRules.KindLabel(definition.Kind)} integrations; no domain review profile exists for this type.");
        }

        if (capability == IntegrationReviewCapability.EventStream) return EventStream(definition, platform, configuration, evidence, ready, attention, notAssessed, notBlocking);

        // HTTP/API and message brokers: source evidence, contracts, authentication and runtime evidence as the capability profile reads them.
        if (evidence.SourceBound) ready.Add($"Source evidence bound{(evidence.BoundArchive is { } archive ? $" ({archive})" : "")}");
        else attention.Add("Source evidence not bound (bind it in IQR Evidence)");
        Contracts(definition, evidence, ready, attention);
        foreach (var auth in new[] { IntegrationField.ProducerAuthentication, IntegrationField.ConsumerAuthentication }.Select(configuration.Field).OfType<IntegrationFieldStatus>())
        {
            if (auth.Resolution == IntegrationFieldResolution.NotAssessed) notAssessed.Add($"{auth.Label} (no configuration or evidence shows the endpoint's requirement)");
            else if (auth.Resolution is IntegrationFieldResolution.Configured or IntegrationFieldResolution.TemplateDefault or IntegrationFieldResolution.PlatformManaged) ready.Add($"{auth.Label}: {auth.Value}");
        }
        if (capability == IntegrationReviewCapability.HttpApi)
            notAssessed.Add("Endpoint reachability and runtime behaviour (the review sends no request)");
        else if (platform is null || string.IsNullOrWhiteSpace(platform.RuntimeEvidence?.SubscriptionId))
            attention.Add("Runtime evidence source not configured for the messaging platform");
        else ready.Add("Runtime evidence source configured");

        var label = attention.Count == 0 && notAssessed.Count == 0 ? "Ready" : "Partial";
        return Build(label, capability, ready, attention, notAssessed, notBlocking, label == "Ready"
            ? "All prerequisites this review profile uses are present."
            : $"The review runs with limitations: {string.Join("; ", attention.Concat(notAssessed).Select(Lower))}. Partial is not a failure.");
    }

    /// <summary>Event streams keep their existing checkpoint/consumer-group/runtime-source causes; a missing consumer group limits only checkpoint review.</summary>
    private static IntegrationReviewReadinessAssessment EventStream(IntegrationDefinition definition, IntegrationPlatform? platform, IntegrationConfigurationAssessment configuration,
        IntegrationReadinessEvidence evidence, List<string> ready, List<string> attention, List<string> notAssessed, List<string> notBlocking)
    {
        var limits = new List<string>();
        if (definition.Consumer.MappingState != ConsumerMappingState.Confirmed) limits.Add("consumer not confirmed");
        var (group, assumed) = IntegrationConfigurationRules.EffectiveConsumerGroup(definition, platform);
        if (group is null) { limits.Add("consumer group unknown (no checkpoint review)"); attention.Add("Consumer group not configured — checkpoint and lag checks are not assessed"); }
        else if (assumed) { limits.Add($"consumer group {group} is a configured assumption (needs confirmation; no checkpoint verdict)"); attention.Add($"Consumer group {group} is a configured assumption — needs confirmation"); }
        else ready.Add($"Consumer group {group} configured");
        if (evidence.Contracts is not null) Contracts(definition, evidence, ready, attention);
        else if (definition.ContractRelationship == ContractRelationshipState.NotConfigured) { limits.Add("no contract (no compatibility review)"); attention.Add("No contract selected — compatibility is not reviewed"); }
        if (platform is not null)
        {
            var runtime = platform.RuntimeEvidence;
            var missingRuntime = new List<string>();
            if (runtime?.EventHubMetadata != true) missingRuntime.Add("Event Hub metadata");
            if (runtime?.ResolvedCheckpointContainerUrl() is null) missingRuntime.Add("checkpoint evidence");
            // Telemetry is Application Insights by resource or a Log Analytics workspace; a workspace is never required.
            if (runtime?.TelemetryConfigured != true) missingRuntime.Add("runtime telemetry");
            if (missingRuntime.Count > 0) { limits.Add("not configured: " + string.Join(", ", missingRuntime)); attention.Add($"Runtime evidence sources not configured: {string.Join(", ", missingRuntime)}"); }
            else ready.Add("Runtime evidence sources configured");
        }
        if (limits.Count == 0 && attention.Count == 0) return Build("Ready", IntegrationReviewCapability.EventStream, ready, attention, notAssessed, notBlocking, "All configured prerequisites are present.");
        var lead = configuration.State == IntegrationConfigurationState.NeedsConfiguration
            ? "Some configuration is missing, so the checks that need it are not assessed."
            : "Configuration is usable, but some runtime, checkpoint, consumer or contract evidence is unavailable.";
        return Build("Partial", IntegrationReviewCapability.EventStream, ready, attention, notAssessed, notBlocking,
            $"{lead} Missing: {string.Join("; ", limits.Count > 0 ? limits : attention.Select(Lower))}. Partial is not a failure; the review runs with limitations.");
    }

    /// <summary>Contracts by side and compatibility separately: a selected or resolved contract is ready; both available is not compatible.</summary>
    private static void Contracts(IntegrationDefinition definition, IntegrationReadinessEvidence evidence, List<string> ready, List<string> attention)
    {
        if (evidence.Contracts is not { } discovery)
        {
            if (definition.ContractRelationship == ContractRelationshipState.NotConfigured) attention.Add("Contract selection: no producer or consumer contract selected");
            else ready.Add(IntegrationConfigurationRules.ContractLabel(definition.ContractRelationship));
            attention.Add("Contract compatibility not assessed");
            return;
        }
        foreach (var side in new[] { discovery.Producer, discovery.Consumer })
        {
            var name = side.Role == IntegrationContractRole.Producer ? "Producer contract" : "Consumer contract";
            if (side.Selected is { Stale: true }) attention.Add($"{name} was selected from an earlier source binding — confirm it again");
            else if (IntegrationContractLabels.Usable(side)) ready.Add($"{name} {(side.Selected is not null ? "selected" : "resolved from source")}");
            else attention.Add($"{name}: {IntegrationContractLabels.State(side.State).ToLowerInvariant()}");
        }
        if (discovery.Compatibility == IntegrationContractCompatibilityState.Compatible) ready.Add("Contract compatibility assessed: compatible");
        else attention.Add(discovery.Compatibility switch
        {
            IntegrationContractCompatibilityState.DifferencesFound => "Contract compatibility: differences found",
            IntegrationContractCompatibilityState.ReadyToAssess => "Contract compatibility ready to assess, not yet assessed",
            _ => "Contract compatibility not assessed",
        });
    }

    private static IntegrationReviewReadinessAssessment Build(string label, IntegrationReviewCapability capability, List<string> ready, List<string> attention,
        List<string> notAssessed, List<string> notBlocking, string detail) =>
        new(label, capability) { Ready = ready, NeedsAttention = attention, NotAssessed = notAssessed, NotBlocking = notBlocking, Detail = detail };

    private static string Lower(string text) => text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];
}
