using System.Text.RegularExpressions;
using BirkNext.Api.Services.ContractEvolution;
using BirkNext.Integrations;
using BirkNext.SourceArchitecture;
using BirkNext.SourceDomains;

namespace BirkNext.Api.Services.Integrations;

/// <summary>Everything contract discovery reads for one integration. The snapshot is the integration's OWN bound snapshot, never the newest one.</summary>
public sealed record IntegrationContractDiscoveryInput(IntegrationDefinition Definition, IntegrationEvidenceBinding? Binding, IqrSourceSnapshot? Snapshot,
    IntegrationReviewEvidenceFacts? Facts, IReadOnlyList<IntegrationContractArtifact> Stored);

/// <summary>
/// Producer and consumer contract discovery from the integration's canonical source binding. Generic: it reads Source Analysis contract
/// evidence (code references, packaged files, ownership), the template's side metadata and the reference's declared data type — no product,
/// file or schema is named here. Evidence is ranked; a file-name similarity is never authoritative, several candidates at the best rank are
/// never decided (no "highest version wins"), a library schema only supports an entry schema, and a test-project copy is never resolved.
/// Read-only: discovering selects, binds and stores nothing.
/// </summary>
public static class IntegrationContractDiscoveryEngine
{
    /// <summary>Contract analyzer version from which code/project references are recorded in a snapshot.</summary>
    public const int CodeReferenceAnalyzerVersion = 2;
    /// <summary>More contract files than this packaged by one project file is a bundle: weak evidence for any one of them.</summary>
    private const int BulkPackaging = 5;

    private sealed record Considered(SourceContract Contract, IntegrationContractCandidate Candidate);

    public static IntegrationContractDiscovery Discover(IntegrationContractDiscoveryInput input)
    {
        var (definition, binding, snapshot, facts, stored) = input;
        var side = facts?.SourceSide ?? IntegrationSourceSide.Unknown;
        var referenceSide = facts?.ReferenceSide ?? IntegrationSourceSide.Unknown;
        var limitations = new List<string>();
        var basis = side switch
        {
            IntegrationSourceSide.Consumer => $"The bound source implements the consumer side{(facts?.SourceRole is { } role ? $" ({role})" : "")} (template metadata).",
            IntegrationSourceSide.Producer => $"The bound source implements the producer side{(facts?.SourceRole is { } role ? $" ({role})" : "")} (template metadata).",
            _ => "Which side the bound source implements is not declared: its candidates are listed for both sides and need confirmation.",
        };

        if (binding is null || snapshot is null)
        {
            var reason = binding is null
                ? "No canonical source binding. Bind this integration's source in IQR Evidence — a newer or similar snapshot is never used in its place."
                : "The bound Source Analysis snapshot is no longer stored. Rebind the integration's source in IQR Evidence.";
            return Finish(definition, binding, side, basis, Unavailable(definition, IntegrationContractRole.Producer, reason, facts, referenceSide, stored, binding),
                Unavailable(definition, IntegrationContractRole.Consumer, reason, facts, referenceSide, stored, binding), [reason]);
        }

        var evidence = snapshot.EvidenceDomains?.Contracts;
        if (evidence is null || evidence.AnalyzerVersion < CodeReferenceAnalyzerVersion)
            limitations.Add($"{snapshot.Archive.FileName} was analyzed before code and project references to contract files were recorded. Reanalyze the source in Source Analysis (a new snapshot) to rank candidates by handler/validator references; the binding is not changed automatically.");
        var all = (evidence?.Contracts ?? []).Where(c => c.Type is not SourceContractType.GeneratedClient and not SourceContractType.GraphQlOperations).ToList();

        var producer = Side(IntegrationContractRole.Producer, definition, all, side, referenceSide, facts, stored, binding, snapshot);
        var consumer = Side(IntegrationContractRole.Consumer, definition, all, side, referenceSide, facts, stored, binding, snapshot);
        return Finish(definition, binding, side, basis, producer, consumer, limitations) with { SnapshotId = snapshot.Id, Archive = snapshot.Archive.FileName };
    }

    private static IntegrationContractDiscovery Finish(IntegrationDefinition definition, IntegrationEvidenceBinding? binding, IntegrationSourceSide side, string basis,
        IntegrationContractSide producer, IntegrationContractSide consumer, List<string> limitations)
    {
        var both = IntegrationContractLabels.Usable(producer) && IntegrationContractLabels.Usable(consumer);
        return new IntegrationContractDiscovery
        {
            IntegrationId = definition.Id, SourceBound = binding is not null, SnapshotId = binding?.SnapshotId, Archive = binding?.ArchiveName,
            SourceSide = side, SourceSideBasis = basis, Producer = producer, Consumer = consumer,
            Compatibility = both ? IntegrationContractCompatibilityState.ReadyToAssess : IntegrationContractCompatibilityState.NotAssessable,
            CompatibilityReason = both ? "Both sides have a usable contract. Availability is not compatibility: the contracts still have to be compared."
                : $"Needs a usable contract on both sides (resolved from source evidence, or selected/uploaded): {string.Join(" and ", new[] { (producer, "producer"), (consumer, "consumer") }.Where(x => !IntegrationContractLabels.Usable(x.Item1)).Select(x => x.Item2))} missing.",
            Limitations = limitations,
        };
    }

    private static IntegrationContractSide Unavailable(IntegrationDefinition definition, IntegrationContractRole role, string reason, IntegrationReviewEvidenceFacts? facts,
        IntegrationSourceSide referenceSide, IReadOnlyList<IntegrationContractArtifact> stored, IntegrationEvidenceBinding? binding) => new()
    {
        Role = role, Party = Party(definition, role), State = IntegrationContractCandidateState.Unavailable, Explanation = reason,
        Selected = Selection(stored, definition.Id, role, binding), ReferenceHints = Hints(facts, role, referenceSide),
    };

    private static IntegrationContractSide Side(IntegrationContractRole role, IntegrationDefinition definition, IReadOnlyList<SourceContract> all, IntegrationSourceSide side,
        IntegrationSourceSide referenceSide, IntegrationReviewEvidenceFacts? facts, IReadOnlyList<IntegrationContractArtifact> stored, IntegrationEvidenceBinding binding, IqrSourceSnapshot snapshot)
    {
        var ownSide = role == IntegrationContractRole.Producer ? IntegrationSourceSide.Producer : IntegrationSourceSide.Consumer;
        // Usage in the bound source (a validator, a packaged file, an owning project) is evidence for the side the source implements;
        // the reference's declared data type is evidence for the side the reference describes. Unknown sides: both, never decided.
        var usage = side == ownSide || side == IntegrationSourceSide.Unknown;
        var declared = referenceSide == ownSide || referenceSide == IntegrationSourceSide.Unknown;
        var considered = new List<Considered>();
        // A project that packages many contract files at once (a schema/test-data bundle) says nothing about which one this integration uses.
        var packaged = all.SelectMany(c => c.CodeReferences.Where(r => r.Kind == "Embedded resource")).GroupBy(r => r.File, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var family = facts?.SchemaName is { Length: > 0 } declaredName ? Stem(declaredName) : "";
        var related = new HashSet<string>(StringComparer.Ordinal);
        foreach (var contract in all)
        {
            var items = new List<IntegrationContractEvidenceItem>();
            var test = contract.ProducerBasis?.StartsWith("Test project copy", StringComparison.Ordinal) == true;
            if (usage)
            {
                foreach (var reference in contract.CodeReferences)
                {
                    var where = $"{reference.File}:{reference.Line}{(reference.Component is { } c ? $" ({c})" : "")}";
                    items.Add(reference.Kind switch
                    {
                        "Validator" => new(IntegrationContractEvidenceRank.HandlerReference, $"Named by code that loads/validates a schema: {where}", reference.File, reference.Line),
                        "Embedded resource" when packaged.GetValueOrDefault(reference.File) > BulkPackaging => new(IntegrationContractEvidenceRank.SourceRelationship,
                            $"Packaged in bulk with {packaged[reference.File] - 1} other contract files: {where} — weak", reference.File, reference.Line),
                        "Embedded resource" => new(IntegrationContractEvidenceRank.ComponentAssociation, $"Packaged with its project: {where}", reference.File, reference.Line),
                        "Configuration reference" => new(IntegrationContractEvidenceRank.ComponentAssociation, $"Named in configuration: {where}", reference.File, reference.Line),
                        "Test code reference" => new(IntegrationContractEvidenceRank.SourceRelationship, $"Named by test code only: {where}", reference.File, reference.Line),
                        _ => new(IntegrationContractEvidenceRank.ComponentAssociation, $"Named by code: {where}", reference.File, reference.Line),
                    });
                }
                if (!test && contract.Producer is { } owner && contract.EvidenceState is ArchitectureEvidenceState.StronglySupported or ArchitectureEvidenceState.Confirmed)
                    items.Add(new(IntegrationContractEvidenceRank.SourceRelationship, $"Owned by {owner}: {contract.ProducerBasis}", contract.File, contract.Line));
                if (!test && contract.ConsumerHints.Count > 0 && contract.EvidenceState is ArchitectureEvidenceState.StronglySupported or ArchitectureEvidenceState.Confirmed)
                    items.Add(new(IntegrationContractEvidenceRank.SourceRelationship, $"Consumed by {string.Join(", ", contract.ConsumerHints)}: {contract.ProducerBasis}", contract.File, contract.Line));
            }
            if (declared && facts?.SchemaName is { Length: > 0 } schemaName)
            {
                var stem = System.IO.Path.GetFileNameWithoutExtension(contract.File);
                if (string.Equals(contract.Name, schemaName, StringComparison.OrdinalIgnoreCase) || string.Equals(stem, schemaName, StringComparison.OrdinalIgnoreCase))
                    items.Add(new(IntegrationContractEvidenceRank.ContractMetadata, $"Named exactly like the declared data type {schemaName} ({facts.ReferenceTitle ?? "reference metadata"})", contract.File, contract.Line));
                else if (Stem(contract.Name) is { Length: > 0 } a && a == Stem(schemaName))
                    items.Add(new(IntegrationContractEvidenceRank.FilenameSimilarity, $"Name resembles the declared data type {schemaName} — similarity only, never proof", contract.File, contract.Line));
            }
            if (items.Count == 0) continue;
            if (family.Length > 0 && Stem(contract.Name) == family) related.Add(contract.Id);
            considered.Add(new(contract, new IntegrationContractCandidate
            {
                ContractId = contract.Id, Name = contract.Name, File = contract.File, Type = Type(contract.Type), Version = contract.Version,
                TargetNamespace = contract.XmlSchema?.TargetNamespace, IsEntry = IsEntry(contract), TestCopy = test,
                Rank = items.Min(i => i.Rank), Evidence = [.. items.OrderBy(i => i.Rank)], ParseFailed = contract.ParseSupport == DomainSupport.Unsupported,
                ContentAvailable = contract.Type == SourceContractType.XmlSchema,
            }));
        }

        // Library schemas support entry schemas; an entry schema's imports/includes are listed with it.
        var libraries = considered.Where(c => !c.Candidate.IsEntry).Select(c => c.Candidate).ToList();
        var entries = considered.Where(c => c.Candidate.IsEntry).Select(c => c.Candidate with
        {
            SupportingSchemas = [.. (c.Contract.XmlSchema?.Dependencies ?? []).Select(d => d.SchemaLocation).OfType<string>().Select(l => System.IO.Path.GetFileName(l.Replace('\\', '/'))).Distinct(StringComparer.OrdinalIgnoreCase)],
        }).OrderBy(c => c.Rank).ThenBy(c => c.TestCopy).ThenBy(c => c.File, StringComparer.Ordinal).ToList();

        var party = Party(definition, role);
        var noun = role == IntegrationContractRole.Producer ? "producer" : "consumer";
        IntegrationContractSide Result(IntegrationContractCandidateState state, IntegrationContractCandidate? primary, string explanation) => new()
        {
            Role = role, Party = party, State = state, Primary = primary, Candidates = entries, Supporting = libraries,
            Selected = Selection(stored, definition.Id, role, binding), Explanation = explanation, ReferenceHints = Hints(facts, role, referenceSide),
        };

        if (entries.Count == 0)
            return Result(IntegrationContractCandidateState.Unavailable, null, libraries.Count > 0
                ? $"Only library/supporting schemas were found for the {noun} side ({string.Join(", ", libraries.Select(l => System.IO.Path.GetFileName(l.File)))}); a library is never the primary contract."
                : usage || declared
                    ? $"No {noun} contract evidence in the bound source {snapshot.Archive.FileName}: no schema is named by its code, packaged with a project{(declared ? " or named like the declared data type" : "")}. A schema loaded at runtime (e.g. from storage) leaves no source evidence."
                    : $"The bound source implements the other side, and no reference metadata describes the {noun}'s contract.");

        var best = entries[0].Rank;
        var top = entries.Where(c => c.Rank == best).ToList();
        var versions = entries.Select(c => c.Version ?? c.TargetNamespace).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var versionNote = versions > 1 ? $" {versions} versions are present; the highest is never chosen automatically." : "";
        if (top.Count > 1 && !(best <= IntegrationContractEvidenceRank.ComponentAssociation && top.Count(c => related.Contains(c.ContractId)) == 1))
            return Result(best == IntegrationContractEvidenceRank.HandlerReference ? IntegrationContractCandidateState.Ambiguous : IntegrationContractCandidateState.NeedsConfirmation, null,
                $"{top.Count} {noun} candidates rest on equally strong evidence ({IntegrationContractLabels.Rank(best).ToLowerInvariant()}); none is chosen.{versionNote} Review the candidates and select one.");
        // Equally strong code evidence for several schemas (e.g. an inbound schema and a receipt schema): the one in the declared data type's schema
        // family is offered first — as a candidate to review, never resolved, because a name relation only orders evidence, it never proves it.
        if (top.Count > 1 && best <= IntegrationContractEvidenceRank.ComponentAssociation && top.Count(c => related.Contains(c.ContractId)) == 1)
        {
            var preferred = top.Single(c => related.Contains(c.ContractId));
            return Result(IntegrationContractCandidateState.Candidate, preferred,
                $"{top.Count} schemas rest on equally strong evidence ({IntegrationContractLabels.Rank(best).ToLowerInvariant()}); {System.IO.Path.GetFileName(preferred.File)} is offered first because it belongs to the declared data type's schema family ({facts!.SchemaName}). That only orders the candidates — review it before using it.{versionNote}");
        }
        var only = top[0];
        if (only.ParseFailed) return Result(IntegrationContractCandidateState.ParseFailed, only, $"{System.IO.Path.GetFileName(only.File)} could not be parsed by Source Analysis.");
        if (only.TestCopy)
            return Result(IntegrationContractCandidateState.Candidate, only, $"{System.IO.Path.GetFileName(only.File)} is a test-project copy: evidence of what tests use, not of the deployed {noun}. Confirm before using it.{versionNote}");
        return best switch
        {
            IntegrationContractEvidenceRank.HandlerReference => Result(IntegrationContractCandidateState.Resolved, only,
                $"Resolved from source: the {noun}'s code loads/validates {System.IO.Path.GetFileName(only.File)}.{versionNote}"),
            IntegrationContractEvidenceRank.FilenameSimilarity => Result(IntegrationContractCandidateState.NeedsConfirmation, only,
                $"Only a name similarity points to {System.IO.Path.GetFileName(only.File)} — a weak candidate that a person must confirm.{versionNote}"),
            _ => Result(IntegrationContractCandidateState.Candidate, only,
                $"Candidate found ({IntegrationContractLabels.Rank(best).ToLowerInvariant()}); no handler or validator reference resolves it. Review it before using it.{versionNote}"),
        };
    }

    private static IntegrationContractSelection? Selection(IReadOnlyList<IntegrationContractArtifact> stored, string integrationId, IntegrationContractRole role, IntegrationEvidenceBinding? binding) =>
        stored.FirstOrDefault(a => a.IntegrationId == integrationId && a.Role == role) is not { } artifact ? null : new IntegrationContractSelection
        {
            Origin = artifact.Origin, FileName = artifact.FileName, Format = artifact.Format, ShortHash = artifact.ShortHash, ContractId = artifact.SourceContractId,
            SourceSnapshotId = artifact.SourceSnapshotId, SourceArchive = artifact.SourceArchive,
            // A source selection belongs to the snapshot it was chosen from: after a rebind it needs confirming again (never re-pointed silently).
            Stale = artifact.Origin == IntegrationContractOrigin.SourceSelection && artifact.SourceSnapshotId != binding?.SnapshotId,
        };

    private static List<string> Hints(IntegrationReviewEvidenceFacts? facts, IntegrationContractRole role, IntegrationSourceSide referenceSide)
    {
        var own = role == IntegrationContractRole.Producer ? IntegrationSourceSide.Producer : IntegrationSourceSide.Consumer;
        if (facts is null || referenceSide != own && referenceSide != IntegrationSourceSide.Unknown) return [];
        return [.. facts.ContractReference.Select(f => $"{f.Label}: {f.Value} ({facts.ReferenceTitle ?? "reference"} · metadata, not a contract)")];
    }

    private static string? Party(IntegrationDefinition definition, IntegrationContractRole role) =>
        role == IntegrationContractRole.Producer ? definition.Producer : definition.Consumer.DisplayName;

    /// <summary>An XSD declaring at least one global element is a message entry schema; one with only types/groups is a library.</summary>
    public static bool IsEntry(SourceContract contract) =>
        contract.Type != SourceContractType.XmlSchema || contract.XmlSchema is null || contract.XmlSchema.Elements.Any(e => !e.Path.Contains('/'));

    public static IntegrationContractType Type(SourceContractType type) => type switch
    {
        SourceContractType.XmlSchema => IntegrationContractType.Xsd,
        SourceContractType.JsonSchema => IntegrationContractType.JsonSchema,
        SourceContractType.OpenApi => IntegrationContractType.OpenApi,
        SourceContractType.AsyncApi => IntegrationContractType.AsyncApi,
        SourceContractType.Protobuf => IntegrationContractType.Protobuf,
        SourceContractType.GraphQlSchema => IntegrationContractType.GraphQlSchema,
        _ => IntegrationContractType.Other,
    };

    /// <summary>A schema name without separators, version and variant ("a_b_v2_0_0_x" and "a.b.v2.0.0.y" → "a b"): for similarity only.</summary>
    private static string Stem(string name)
    {
        var words = Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();
        var match = Regex.Match(words, @"\bv?\d+(\s\d+)*\b");
        return (match.Success ? words[..match.Index] : words).Trim();
    }

    // ── Compatibility ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Structural comparison of a producer and a consumer XSD with the shared XSD engine (effective schema, cross-file types). Identical
    /// normalized structure is Compatible; any difference is Differences found with the changes listed — never judged harmless here.
    /// </summary>
    public static IntegrationContractDiscovery WithXsdComparison(IntegrationContractDiscovery discovery, (IReadOnlyDictionary<string, string> Files, string Root)? producer,
        (IReadOnlyDictionary<string, string> Files, string Root)? consumer, DateTimeOffset now)
    {
        if (discovery.Compatibility != IntegrationContractCompatibilityState.ReadyToAssess) return discovery;
        if (producer is null || consumer is null)
            return discovery with { CompatibilityReason = discovery.CompatibilityReason + " Automatic comparison covers XSD pairs whose content is available; other pairs are compared by the Integration Quality Review run where it supports the format." };
        try
        {
            var p = ContractSnapshots.FromXmlSchemaPackage(new ContractDescriptor("producer", SourceContractType.XmlSchema, producer.Value.Root, null, "producer"), producer.Value.Files, producer.Value.Root, now);
            var c = ContractSnapshots.FromXmlSchemaPackage(new ContractDescriptor("consumer", SourceContractType.XmlSchema, consumer.Value.Root, null, "consumer"), consumer.Value.Files, consumer.Value.Root, now);
            var changes = ContractDiff.Compare(c, p);
            return changes.Count == 0
                ? discovery with { Compatibility = IntegrationContractCompatibilityState.Compatible, CompatibilityReason = "The producer's and the consumer's XSD have the same normalized structure (namespace, elements, types, cardinality, restrictions). Structural only — business rules and runtime payloads are not judged." }
                : discovery with
                {
                    Compatibility = IntegrationContractCompatibilityState.DifferencesFound,
                    CompatibilityReason = $"{changes.Count} structural difference{(changes.Count == 1 ? "" : "s")} between what the producer's schema allows and what the consumer's schema accepts. Each needs review; none is judged harmless automatically.",
                    CompatibilityDifferences = [.. changes.Take(12).Select(change => $"{change.Change}: {change.Path}{(change.OldValue is not null || change.NewValue is not null ? $" (consumer {change.OldValue ?? "—"} · producer {change.NewValue ?? "—"})" : "")}")],
                };
        }
        catch (Exception ex) when (ex is ArgumentException or System.Xml.XmlException or System.Xml.Schema.XmlSchemaException or InvalidOperationException)
        {
            return discovery with { CompatibilityReason = "Both contracts are available, but the XSD comparison could not read them: " + ex.Message };
        }
    }
}

/// <summary>
/// Loads what discovery reads — the integration, ITS binding, the bound snapshot, its retained XSD files, the pack facts and the stored
/// contracts — and, on an explicit request, stores one source candidate as a side's contract. No binding or snapshot is ever changed.
/// </summary>
public sealed class IntegrationContractDiscoveryService(IIntegrationCatalogService catalog, SourceEvidence.IIntegrationEvidenceBindingStore bindings,
    SourceEvidence.IqrSourceStore sources, IIntegrationContractStore contracts, IntegrationJourneys.IIntegrationJourneyService journeys, TimeProvider? clock = null)
{
    private static readonly System.Text.Json.JsonSerializerOptions SchemaJson = new(System.Text.Json.JsonSerializerDefaults.Web);

    public async Task<IntegrationContractDiscovery?> DiscoverAsync(string environmentId, string integrationId, CancellationToken ct)
    {
        var configured = await catalog.GetAsync(environmentId, null, null, ct);
        if (configured.Integrations.FirstOrDefault(i => i.Id == integrationId) is not { } definition) return null;
        var binding = (await bindings.ListAsync(environmentId, ct)).FirstOrDefault(b => b.IntegrationId == integrationId);
        var snapshot = binding is null ? null : await sources.FindSourceAnalysisAsync(binding.SnapshotId, ct);
        var facts = (await journeys.ReviewFactsAsync(environmentId, ct)).FirstOrDefault(f => f.IntegrationId == integrationId);
        var stored = (await contracts.ListAsync(environmentId, ct)).Where(a => a.IntegrationId == integrationId).ToList();
        var discovery = IntegrationContractDiscoveryEngine.Discover(new(definition, binding, snapshot, facts, stored));
        if (discovery.Compatibility != IntegrationContractCompatibilityState.ReadyToAssess) return discovery;

        var files = snapshot is null ? null : await SchemaFilesAsync(snapshot, ct);
        async Task<(IReadOnlyDictionary<string, string>, string)?> Content(IntegrationContractSide side)
        {
            if (side.Selected is { Stale: false } && await contracts.ContentAsync(environmentId, integrationId, side.Role, ct) is { } own && own.Artifact.Format == "XSD")
            {
                // A source selection keeps the bound package around it (imports); an upload is a single file.
                if (own.Artifact.Origin == IntegrationContractOrigin.SourceSelection && files is not null && SelectedPath(own.Artifact, snapshot!) is { } path && files.ContainsKey(path))
                    return (files, path);
                return (new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [own.Artifact.FileName] = own.Content }, own.Artifact.FileName);
            }
            if (side.Selected is null && side.State == IntegrationContractCandidateState.Resolved && side.Primary is { Type: IntegrationContractType.Xsd } primary && files?.ContainsKey(primary.File) == true)
                return (files, primary.File);
            return null;
        }
        return IntegrationContractDiscoveryEngine.WithXsdComparison(discovery, await Content(discovery.Producer), await Content(discovery.Consumer), (clock ?? TimeProvider.System).GetUtcNow());
    }

    /// <summary>Uses one discovered candidate as a side's contract. Only candidates of the integration's CURRENT bound snapshot with retained content.</summary>
    public async Task<(IntegrationContractArtifact? Artifact, string? Error)> SelectAsync(IntegrationContractSourceSelectionRequest request, CancellationToken ct)
    {
        var discovery = await DiscoverAsync(request.EnvironmentId, request.IntegrationId, ct);
        if (discovery is null) return (null, "Unknown integration.");
        if (!discovery.SourceBound || discovery.SnapshotId != request.SnapshotId)
            return (null, "Only a candidate from the integration's current source binding can be used. Rebind in IQR Evidence first if the source changed.");
        var side = discovery.Side(request.Role);
        var candidate = side.Candidates.Concat(side.Supporting).FirstOrDefault(c => c.ContractId == request.ContractId);
        if (candidate is null) return (null, "That contract is not a candidate for this side in the bound source.");
        if (!candidate.IsEntry) return (null, "A library/supporting schema cannot be the primary contract.");
        var snapshot = await sources.FindSourceAnalysisAsync(request.SnapshotId, ct);
        var files = snapshot is null ? null : await SchemaFilesAsync(snapshot, ct);
        if (files is null || !files.TryGetValue(candidate.File, out var content))
            return (null, "The bound snapshot did not retain this contract's content. Upload the contract instead, or reanalyze the source.");
        return await contracts.SaveSourceSelectionAsync(request.EnvironmentId, request.IntegrationId, request.Role, request.SnapshotId, snapshot!.Archive.FileName,
            candidate.ContractId, candidate.File, content, ct);
    }

    private static string? SelectedPath(IntegrationContractArtifact artifact, IqrSourceSnapshot snapshot) =>
        snapshot.EvidenceDomains?.Contracts?.Contracts.FirstOrDefault(c => c.Id == artifact.SourceContractId)?.File;

    /// <summary>The XSD files the snapshot retained (path → text), or null when it retained none or they do not match the snapshot.</summary>
    private async Task<IReadOnlyDictionary<string, string>?> SchemaFilesAsync(IqrSourceSnapshot snapshot, CancellationToken ct)
    {
        var artifact = await sources.FindSchemaArtifactAsync(snapshot.Id, ct);
        if (artifact is null || !string.Equals(artifact.SnapshotFingerprint, snapshot.Archive.Sha256, StringComparison.Ordinal)) return null;
        try
        {
            var files = System.Text.Json.JsonSerializer.Deserialize<List<Controllers.SourceSchemaFileDto>>(artifact.PackageJson, SchemaJson) ?? [];
            return files.GroupBy(f => f.Path.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Text, StringComparer.OrdinalIgnoreCase);
        }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
