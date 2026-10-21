using BirkNext.Integrations;

namespace BirkNext.Api.Services.IntegrationJourneys;

/// <summary>
/// Resolves a pack's integration templates for one environment: the safe values the template knows (with provenance), the fields it leaves
/// for the tester, the evidence source state (the pack's own source requirement metadata — defined once) and existing records that already
/// fulfil the template's requirement. Read-only: nothing is created, bound, trusted or verified here.
/// </summary>
public static class IntegrationTemplates
{
    /// <summary>Production never receives a template endpoint: production endpoints are configured deliberately, never prefilled.</summary>
    public static bool IsProduction(string? environmentType) =>
        environmentType?.Trim().ToLowerInvariant() is "production" or "prod" or "prd";

    public static IntegrationTemplateView Resolve(IIntegrationJourneyPack pack, JourneyPackContext context, IntegrationTemplateDefinition template, string? environmentType)
    {
        var values = new Dictionary<string, IntegrationTemplateValue>(StringComparer.Ordinal);
        var needs = new List<string>();
        void Add(string field, string? value) { if (!string.IsNullOrWhiteSpace(value)) values[field] = new(value, "Template"); }
        Add(nameof(IntegrationDefinition.DisplayName), template.DisplayName);
        Add(nameof(IntegrationDefinition.SystemName), template.SystemName);
        Add(nameof(IntegrationDefinition.Producer), template.Producer);
        Add("Consumer", template.Consumer);
        Add(nameof(IntegrationDefinition.SourceSystem), template.SourceSystem);
        Add(nameof(IntegrationDefinition.DestinationSystem), template.DestinationSystem);
        Add(nameof(IntegrationDefinition.SourceResource), template.SourceResource);

        var kind = environmentType?.Trim() ?? "";
        var specific = template.EndpointByEnvironment.FirstOrDefault(pair => string.Equals(pair.Key, kind, StringComparison.OrdinalIgnoreCase));
        if (specific.Value is { Length: > 0 } environmentValue)
            values[nameof(IntegrationDefinition.EndpointOrTopic)] = new(environmentValue, $"Template · {specific.Key}");
        else if (template.EndpointOrTopic is { Length: > 0 } endpoint && !IsProduction(kind))
            values[nameof(IntegrationDefinition.EndpointOrTopic)] = new(endpoint, "Template");
        else
            needs.Add(nameof(IntegrationDefinition.EndpointOrTopic));
        if (template.Kind == IntegrationKind.EventHub && template.SourceResource is null) needs.Add(nameof(IntegrationDefinition.SourceResource));

        var requirement = template.RequirementKey is null ? null
            : pack.Journeys.SelectMany(journey => journey.Integrations).FirstOrDefault(item => string.Equals(item.Key, template.RequirementKey, StringComparison.Ordinal));
        var journeys = requirement is null ? [] : pack.Journeys.Where(journey => journey.Integrations.Any(item => item.Key == requirement.Key)).Select(journey => journey.DisplayName).Distinct().ToArray();
        var configured = requirement is null ? [] : context.Catalog.Integrations.Where(integration => pack.Matches(requirement, integration)).Select(integration => integration.DisplayName).ToArray();
        var role = TestPackSources.Role(pack, template.EvidenceSourceRoleKey);
        var evidence = role is null ? null : TestPackSources.Status(pack, context, role) with { NearMisses = TestPackSources.NearMisses(role, context) };
        // The reference is read through the same loader every consumer uses (TestPackReferences), so its facts are the reference tier of the
        // shared configuration resolution — shown here as known metadata, never saved on the integration and never run configuration.
        var referenceRole = TestPackSources.Role(pack, template.Reference?.RoleKey);
        var reference = referenceRole is null ? null : TestPackSources.Status(pack, context, referenceRole);
        var facts = reference is { State: TestPackSourceState.ReferenceAvailable, Reference: { } loaded } ? loaded.Facts : [];
        IReadOnlyList<TestPackReferenceFact> Pick(IReadOnlyList<string>? labels) =>
            [.. (labels ?? []).Select(label => facts.FirstOrDefault(fact => string.Equals(fact.Label, label, StringComparison.Ordinal))).OfType<TestPackReferenceFact>()];
        var schemaName = template.Reference?.SchemaFactKey is { } schemaKey ? facts.FirstOrDefault(fact => fact.SemanticKey == schemaKey)?.Value : null;
        return new IntegrationTemplateView(template.Key, template.DisplayName, template.Description, template.Kind, pack.PackId, pack.DisplayName)
        {
            Values = values, NeedsConfiguration = needs, Journeys = journeys, AlreadyConfigured = configured, EvidenceSource = evidence,
            ReferenceSource = reference, AuthenticationReference = Pick(template.Reference?.AuthenticationFacts), ContractReference = Pick(template.Reference?.ContractFacts),
            SchemaName = schemaName, SchemaCandidates = schemaName is null || evidence is null ? [] : Schemas(context, evidence, schemaName),
        };
    }

    /// <summary>XML schemas named exactly <paramref name="name"/> (case-insensitive) in each snapshot recognized as the evidence source — what the
    /// source contains, in the candidates' order. Never a fuzzy match, never chosen automatically.</summary>
    private static IReadOnlyList<IntegrationTemplateSchemaCandidate> Schemas(JourneyPackContext context, TestPackSourceStatus evidence, string name) =>
        [.. evidence.Candidates.SelectMany(candidate => (context.SourceSnapshots.FirstOrDefault(snapshot => snapshot.Id == candidate.SnapshotId)?.EvidenceDomains?.Contracts.Contracts ?? [])
            .Where(contract => contract.Type == BirkNext.SourceDomains.SourceContractType.XmlSchema && string.Equals(contract.Name, name, StringComparison.OrdinalIgnoreCase))
            .Select(contract => new IntegrationTemplateSchemaCandidate(candidate.SnapshotId, candidate.ArchiveFileName, contract.Name, contract.File) { Version = contract.Version }))];
}
