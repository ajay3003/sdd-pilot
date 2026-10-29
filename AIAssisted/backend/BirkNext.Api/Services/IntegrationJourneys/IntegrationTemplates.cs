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
            SchemaName = schemaName, SchemaCandidates = schemaName is null || evidence is null ? [] : Schemas(context, evidence, schemaName, Matching(pack, requirement, context), matches: true),
            OtherSchemaCandidates = schemaName is null || evidence is null ? [] : Schemas(context, evidence, schemaName, Matching(pack, requirement, context), matches: false),
            FixtureBindings = template.FixtureBindings,
            FixtureCapabilities = template.FixtureCapabilities,
            Flow = requirement is null ? null : $"{requirement.Producer} → {requirement.Consumer}",
        };
    }

    /// <summary>XML schemas named exactly <paramref name="name"/> (declared name or file stem, case-insensitive) in each snapshot recognized as the
    /// evidence source AND in every snapshot an IQR binding points at (a re-import gives the recognized source a new snapshot id; the bound one is
    /// still where the integration's evidence lives). Never a fuzzy match, never chosen automatically.</summary>
    private static IReadOnlyList<IntegrationDefinition> Matching(IIntegrationJourneyPack pack, JourneyIntegrationRequirement? requirement, JourneyPackContext context) =>
        requirement is null ? [] : [.. context.Catalog.Integrations.Where(integration => pack.Matches(requirement, integration))];

    /// <remarks>Each candidate carries the evidence that could select it (IQR binding of a fulfilling integration, a verified contract
    /// relationship naming the schema, recognition as the source role) and the analysis time for display — never a date-based choice.</remarks>
    private static IReadOnlyList<IntegrationTemplateSchemaCandidate> Schemas(JourneyPackContext context, TestPackSourceStatus evidence, string name,
        IReadOnlyList<IntegrationDefinition> integrations, bool matches)
    {
        var ids = integrations.Select(integration => integration.Id).ToHashSet(StringComparer.Ordinal);
        var bound = context.Bindings.Where(binding => ids.Contains(binding.IntegrationId)).ToList();
        var recognized = evidence.Candidates.Select(candidate => candidate.SnapshotId).ToHashSet();
        var scanned = evidence.Candidates.Select(candidate => (candidate.SnapshotId, candidate.ArchiveFileName))
            .Concat(context.Bindings.Select(binding => (SnapshotId: binding.SnapshotId, ArchiveFileName: binding.ArchiveName)))
            .DistinctBy(item => item.SnapshotId);
        return [.. scanned.SelectMany(item =>
        {
            var snapshot = context.SourceSnapshots.FirstOrDefault(s => s.Id == item.SnapshotId);
            var binding = bound.FirstOrDefault(b => b.SnapshotId == item.SnapshotId);
            var integration = binding is null ? null : integrations.FirstOrDefault(i => i.Id == binding.IntegrationId);
            return (snapshot?.EvidenceDomains?.Contracts.Contracts ?? [])
                .Where(contract => contract.Type == BirkNext.SourceDomains.SourceContractType.XmlSchema
                    && BirkNext.ContractSchemaEvidence.ContractArtifactPaths.NamedExactly(contract, name) == matches)
                .Select(contract => new IntegrationTemplateSchemaCandidate(item.SnapshotId, item.ArchiveFileName, contract.Name, contract.File)
                {
                    Version = contract.Version, AnalyzedAt = snapshot?.AnalyzedAt, Bound = binding is not null, BoundIntegration = integration?.DisplayName,
                    Recognized = recognized.Contains(item.SnapshotId), MatchesContract = matches,
                    ContractConfirmed = matches && integrations.Any(i => i.ContractRelationship == ContractRelationshipState.RelationshipVerified
                        && (i.ConsumerContractReference is null || string.Equals(System.IO.Path.GetFileNameWithoutExtension(i.ConsumerContractReference.Replace('\\', '/').Split('/').Last()), name, StringComparison.OrdinalIgnoreCase))),
                });
        })];
    }
}
