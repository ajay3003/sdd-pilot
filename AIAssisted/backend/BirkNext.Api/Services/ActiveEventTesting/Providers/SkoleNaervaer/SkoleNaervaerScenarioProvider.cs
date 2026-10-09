using System.Globalization;
using BirkNext.Api.Services.ActiveEventTesting.Debezium;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.ActiveEventTesting.Providers.SkoleNaervaer;

/// <summary>
/// Skolenærvær Testing: synthetic CDC events for the SkoleAdapter input (BiRK dbo.Utdanning and dbo.ManglendeSkoletilbud), executed by the
/// shared Active Event core. It applies only to an Event Hub integration whose configured CDC source resource is one of those tables — never
/// because of a project name. Every prerequisite the real pipeline needs is a readiness check: the SkoleAdapter source contract, CDC capture
/// of the table declared in analyzed source, a reviewed fixture, the synthetic data policy (reserved key/child ranges, reference codes) and
/// a downstream verifier. Until they exist the scenarios are visible but not runnable.
/// </summary>
public sealed class SkoleNaervaerScenarioProvider(
    IqrSourceStore sources,
    ActiveEventPolicy policy,
    IActiveEventSyntheticIdentityReservation identities,
    IConfiguration configuration,
    TimeProvider clock) : IActiveEventScenarioProvider
{
    public const string Id = "skolenaervaer.cdc";
    private const string ConfigurationRoot = "ActiveEventTesting:Providers:skolenaervaer.cdc";

    public string ExtensionId => Id;
    public string ExtensionVersion => "1";
    public string DisplayName => "Skolenærvær Testing";
    public string Description => "Synthetic CDC events for the SkoleAdapter input (Utdanning and Manglende skoletilbud). The real SkoleAdapter integration is specified, not yet deployed.";
    public IReadOnlyList<string> Resources => SkoleNaervaerFixtures.Tables.Select(t => $"dbo.{t.Table}").ToArray();

    private enum Kind { Create, ExactReplay, InvalidThenValid, Update, Delete, Tombstone, SnapshotRead, NaturalKeyDuplicate, UnknownChildLateLinkage }

    private sealed record Definition(Kind Kind, string Suffix, string Name, string Category, string Description, int EventCount, ActiveEventOperation[] Operations,
        ActiveEventReplayKind Replay, string? NotAssessedBecause);

    private static readonly Definition[] Definitions =
    [
        new(Kind.Create, "create", "Valid create", "Transport", "One synthetic create (op \"c\") with a reserved synthetic key and synthetic child reference.",
            1, [ActiveEventOperation.Create], ActiveEventReplayKind.None, null),
        new(Kind.ExactReplay, "exact-replay", "Exact replay", "Runtime resilience", "A synthetic create followed by a byte-identical replay (same key, same body). Replay sent ≠ idempotency verified.",
            2, [ActiveEventOperation.Create], ActiveEventReplayKind.ExactReplay, null),
        new(Kind.InvalidThenValid, "invalid-then-valid", "Invalid record → valid control", "Fault resilience",
            "A reviewed invalid record (child reference missing: the specification says such a row is rejected and recorded) followed by a valid synthetic control record.",
            2, [ActiveEventOperation.Create], ActiveEventReplayKind.ControlAfterInvalid, null),
        new(Kind.Update, "update", "Update (before/after)", "Lifecycle", "A create followed by an update of the same record identity (op \"u\" with before and after).",
            2, [ActiveEventOperation.Create, ActiveEventOperation.Update], ActiveEventReplayKind.None,
            "Not assessed: the fixture is supported, but the update outcome in Utdanning cannot be verified without a downstream read contract."),
        new(Kind.Delete, "delete", "Delete", "Lifecycle", "A create followed by a delete of the same record identity (op \"d\", before only).",
            2, [ActiveEventOperation.Create, ActiveEventOperation.Delete], ActiveEventReplayKind.None,
            "Not assessed: the delete outcome (end/remove in Utdanning) cannot be verified without a downstream read contract, and no cleanup/retention policy exists."),
        new(Kind.Tombstone, "tombstone", "Tombstone after delete", "Lifecycle", "A delete followed by a tombstone (record key, no value).",
            2, [ActiveEventOperation.Delete, ActiveEventOperation.Tombstone], ActiveEventReplayKind.None,
            "Not assessed: how a Kafka null-value tombstone appears to an Event Hubs AMQP consumer is not verified, so tombstones are never sent."),
        new(Kind.SnapshotRead, "snapshot-read", "Snapshot read (op \"r\")", "Full load", "One snapshot read event. A single op \"r\" event is not a full reload.",
            1, [ActiveEventOperation.ReadSnapshot], ActiveEventReplayKind.None,
            "Not assessed: full reload needs ordered batches with a partition key and reconciliation evidence; a single snapshot event proves nothing about a reload."),
        new(Kind.NaturalKeyDuplicate, "natural-key-duplicate", "Natural-key duplicate", "Duplicate handling", "Two records with different source keys and the same business identity.",
            2, [ActiveEventOperation.Create], ActiveEventReplayKind.NaturalKeyDuplicate,
            "Not assessed: the specification does not define natural-key duplicate semantics for these tables (identity is the source key)."),
        new(Kind.UnknownChildLateLinkage, "unknown-child-late-linkage", "Unknown child → late linkage", "Domain behavior",
            "A record for an unknown synthetic child (held pending linkage), then the child is registered and the record is linked later.",
            2, [ActiveEventOperation.Create], ActiveEventReplayKind.None,
            "Not assessed: needs a downstream verifier, a synthetic child-registration event source, stable correlation and a cleanup policy."),
    ];

    public IReadOnlyList<ActiveEventScenarioDescriptor> CatalogScenarios =>
        SkoleNaervaerFixtures.Tables.SelectMany(table => Definitions.Select(definition => Descriptor(table, definition))).ToArray();

    public bool CanApply(ActiveEventProjectEvidenceContext context) => Table(context) is not null;

    public IReadOnlyList<ActiveEventScenarioDescriptor> GetScenarios(ActiveEventProjectEvidenceContext context) =>
        Table(context) is { } table ? Definitions.Select(definition => Descriptor(table, definition)).ToArray() : [];

    public async Task<ActiveEventScenarioPreparation> PrepareAsync(string scenarioId, ActiveEventProjectEvidenceContext context, Guid? sourceSnapshotId, CancellationToken ct)
    {
        if (Table(context) is not { } table || Resolve(scenarioId) is not { } resolved || resolved.Table != table)
            return new() { Compatible = false, Detail = "This scenario is not applicable to the selected integration." };
        var definition = resolved.Definition;
        var integration = context.Integration!;
        var parts = integration.SourceResource!.Trim().Split('.');
        var checks = new List<ActiveEventReadinessCheck>();

        var (selected, latestId, problem) = await SourceContractEvaluator.SelectAsync(sources, context.TargetEnvironmentId, integration.Id, sourceSnapshotId, ct);
        var evaluation = SourceContractEvaluator.Evaluate(Requirement(table, definition, integration), selected, latestId);
        if (problem is not null) evaluation = evaluation with { Status = SourceContractStatus.NoSourceSnapshot, Detail = problem };
        checks.Add(new("source-contract", "Source contract (SkoleAdapter)", evaluation.Status == SourceContractStatus.Compatible ? ActiveEventReadinessState.Ready : ActiveEventReadinessState.Blocked,
            evaluation.Status == SourceContractStatus.NoSourceSnapshot
                ? $"Source evidence unavailable — SkoleAdapter source is not present in Source Analysis for this integration. {evaluation.Detail}"
                : $"{evaluation.Label} — {evaluation.Detail}", ActiveEventReadinessCategory.SourceContract));

        var capture = SourceContractEvaluator.CdcCapture(await sources.ListAsync(context.TargetEnvironmentId, ct), parts[1], parts[2]);
        checks.Add(new("cdc-capture", "CDC capture of the table", capture.Captured ? ActiveEventReadinessState.Ready : ActiveEventReadinessState.Blocked, capture.Detail,
            ActiveEventReadinessCategory.CdcCapture));

        var reviewed = selected is not null && ReviewedArchives().Contains(selected.Archive.Sha256, StringComparer.OrdinalIgnoreCase);
        checks.Add(new("fixture-review", "Reviewed fixture", reviewed ? ActiveEventReadinessState.Ready : ActiveEventReadinessState.Blocked,
            reviewed ? $"The fixture (fields, date encoding{(definition.Kind == Kind.InvalidThenValid ? ", invalid case" : "")}) was reviewed against archive {selected!.Archive.Sha256[..12]}…."
                : "Needs review — the fixture fields, the date encoding and the invalid case have not been reviewed against an analyzed SkoleAdapter source archive " +
                  $"({ConfigurationRoot}:ReviewedArchives).", ActiveEventReadinessCategory.SourceContract));

        var (policyReady, policyDetail, summary) = await SyntheticPolicyAsync(context.TargetEnvironmentId, table, definition, ct);
        checks.Add(new("synthetic-data", "Synthetic test data policy", policyReady ? ActiveEventReadinessState.Ready : ActiveEventReadinessState.NotConfigured, policyDetail,
            ActiveEventReadinessCategory.SyntheticData));

        checks.Add(new("downstream", "Downstream verification (Utdanning)", ActiveEventReadinessState.Partial,
            "No Utdanning read contract is configured and no machine-identity token source exists, so no downstream verifier is registered. " +
            "The result can be at most limited evidence: Event Hub acceptance is never a Skolenærvær pass.", ActiveEventReadinessCategory.DownstreamVerification));

        // A NotAssessed scenario is blocked by the core (descriptor support) and by the runner; no provider duplicate of that check.

        var blocking = checks.Where(check => ActiveEventReadinessRules.Blocks(check.State)).ToList();
        return new()
        {
            Compatible = blocking.Count == 0,
            Detail = blocking.Count > 0 ? string.Join(" ", blocking.Select(check => $"{check.Label}: {check.Detail}")) : evaluation.Detail,
            Contract = new ActiveEventSourceContractReference
            {
                SourceSnapshotId = evaluation.SourceSnapshotId, ContractFingerprint = evaluation.Status == SourceContractStatus.NoSourceSnapshot ? null : evaluation.Fingerprint,
                SourceResource = integration.SourceResource, ContractStatus = evaluation.Label, ExtensionVersion = ExtensionVersion, ScenarioVersion = "1",
            },
            Checks = checks,
            SafeMetadata = new Dictionary<string, string> { ["sourceTable"] = table.Table, ["contractStatus"] = evaluation.Status.ToString() },
            SyntheticSummary = summary,
        };
    }

    public async Task<ActiveEventScenarioGeneration> GenerateAsync(string scenarioId, Guid runId, ActiveEventTrustedTarget target,
        ActiveEventSourceContractReference contract, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var (table, definition) = Resolve(scenarioId) ?? throw new InvalidOperationException("Unknown Skolenærvær scenario.");
        if (definition.NotAssessedBecause is { } notAssessed) throw new InvalidOperationException(notAssessed);
        if (!string.Equals(contract.ExtensionVersion, ExtensionVersion, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(contract.ContractFingerprint))
            throw new InvalidOperationException("The Skolenærvær fixture requires a prepared, version-bound source contract.");
        if (ActiveEventPolicy.EnvironmentBlock(target.EnvironmentType) is { } blockedEnvironment) throw new InvalidOperationException(blockedEnvironment);
        var parts = (contract.SourceResource ?? "").Split('.');
        if (parts.Length != 3 || !string.Equals(parts[2], table.Table, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The prepared source contract does not identify the required database.schema.{table.Table} resource.");

        var keyRange = identities.Range(table.KeyScope, out var keyReason) ?? throw new InvalidOperationException(keyReason);
        var childRange = identities.Range(SkoleNaervaerFixtures.ChildReferenceScope, out var childReason) ?? throw new InvalidOperationException(childReason);
        var codes = ReferenceCodes(table, out var codeReason) ?? throw new InvalidOperationException(codeReason);
        var keyCount = definition.Kind == Kind.InvalidThenValid ? 2 : 1;
        var keys = await identities.ReserveAsync(target.TargetEnvironmentId, table.KeyScope, keyRange, null, keyCount, runId, ExtensionId, ct);
        var children = await identities.ReserveAsync(target.TargetEnvironmentId, SkoleNaervaerFixtures.ChildReferenceScope, childRange, null, 1, runId, ExtensionId, ct);

        var source = new DebeziumSource(parts[0], parts[1], parts[2]);
        var now = clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var valid = new SkoleNaervaerFixtures.RecordValues(keys[0], children[0], today.AddDays(-30), null, codes);
        var max = policy.Options.MaxPayloadBytes;
        var marker = $"BIRKNEXT-TEST-{runId.ToString("N")[..12].ToUpperInvariant()}";
        var events = definition.Kind switch
        {
            Kind.Create => [Generated(table, definition, runId, "", 0, valid, SkoleNaervaerFixtures.Insert(table, source, valid, now, max), "Synthetic create", null, marker)],
            Kind.ExactReplay => ExactReplay(),
            Kind.InvalidThenValid =>
            [
                Generated(table, definition, runId, "I", 0, valid with { ChildReference = null },
                    SkoleNaervaerFixtures.Insert(table, source, valid with { ChildReference = null }, now, max), "Reviewed invalid input (child reference missing)", null, marker),
                Generated(table, definition, runId, "V", 1, valid with { Key = keys[1] }, SkoleNaervaerFixtures.Insert(table, source, valid with { Key = keys[1] }, now, max), "Valid control", null, marker),
            ],
            _ => throw new InvalidOperationException("This scenario is not runnable."),
        };
        return new ActiveEventScenarioGeneration
        {
            Events = events,
            SafeMetadata = new Dictionary<string, string> { ["table"] = table.Table },
            Limitations =
            [
                "Utdanning persistence, linkage and idempotency are not verified: no downstream verifier is registered.",
                "The synthetic child reference does not resolve to a registered child unless one was created; Skoletjenesten holds such a record pending linkage.",
            ],
        };

        GeneratedActiveEvent[] ExactReplay()
        {
            var first = SkoleNaervaerFixtures.Insert(table, source, valid, now, max);
            return
            [
                Generated(table, definition, runId, "A", 0, valid, first, "First create", null, marker),
                Generated(table, definition, runId, "A2", 1, valid, first, "Exact replay (same key, same body)", $"{runId:N}-A", marker),
            ];
        }
    }

    private GeneratedActiveEvent Generated(SkoleNaervaerTable table, Definition definition, Guid runId, string label, int index, SkoleNaervaerFixtures.RecordValues values,
        DebeziumEnvelope envelope, string role, string? replayOf, string marker)
    {
        var eventId = label.Length == 0 ? runId.ToString("N") : $"{runId:N}-{label}";
        var key = DebeziumEventBuilder.Key(SkoleNaervaerFixtures.Key(table, values.Key));
        return new GeneratedActiveEvent
        {
            EventId = eventId, ExtensionId = ExtensionId, ScenarioId = ScenarioId(table, definition), SequenceIndex = index, Operation = envelope.Operation,
            Body = envelope.Body, BodySha256 = envelope.Sha256, BodyBytes = envelope.Body.Length, ContentType = "application/json", EventKey = key,
            // The specification partitions by BiRK primary key; the provider owns that domain key choice.
            PartitionKey = values.Key.ToString(CultureInfo.InvariantCulture),
            Correlation = new ActiveEventCorrelation
            {
                RunId = runId, EventId = eventId, EventFingerprint = envelope.Sha256, SyntheticMarker = marker,
                SafeSourceIdentity = $"{table.KeyField} {values.Key.ToString(CultureInfo.InvariantCulture)}",
                ReplaysEventId = replayOf, ReplayKind = replayOf is null ? ActiveEventReplayKind.None : ActiveEventReplayKind.ExactReplay,
                ExtensionValues = new Dictionary<string, string> { ["table"] = table.Table },
            },
            SafeDisplayMetadata = new Dictionary<string, string>
            {
                ["sequenceLabel"] = label,
                ["role"] = role,
                ["resource"] = $"dbo.{table.Table}",
                ["syntheticIdentity"] = $"{table.KeyField} {values.Key.ToString(CultureInfo.InvariantCulture)}",
                ["syntheticChildReference"] = values.ChildReference is { } child ? $"{table.ChildReferenceField} {child.ToString(CultureInfo.InvariantCulture)}" : "missing (invalid fixture)",
                ["expectedResourceIdentity"] = "Not derivable from available source: the adapter's record-identity namespace is not in analyzed source",
                ["fields"] = string.Join(",", values.ChildReference is null ? table.Fields.Where(f => f != table.ChildReferenceField) : table.Fields),
            },
            TransportProperties = label.Length == 0 ? new Dictionary<string, string>() : new Dictionary<string, string> { ["BirkNextMessage"] = label },
        };
    }

    private async Task<(bool Ready, string Detail, IReadOnlyDictionary<string, string> Summary)> SyntheticPolicyAsync(string environmentId, SkoleNaervaerTable table, Definition definition, CancellationToken ct)
    {
        var problems = new List<string>();
        var keyRange = identities.Range(table.KeyScope, out var keyReason);
        var childRange = identities.Range(SkoleNaervaerFixtures.ChildReferenceScope, out var childReason);
        if (keyRange is null) problems.Add(keyReason);
        if (childRange is null) problems.Add(childReason);
        if (ReferenceCodes(table, out var codeReason) is null) problems.Add(codeReason);
        var keyAvailable = keyRange is null ? 0 : await identities.AvailableAsync(environmentId, table.KeyScope, keyRange, null, ct);
        var childAvailable = childRange is null ? 0 : await identities.AvailableAsync(environmentId, SkoleNaervaerFixtures.ChildReferenceScope, childRange, null, ct);
        var keysNeeded = definition.Kind == Kind.InvalidThenValid ? 2 : 1;
        if (keyRange is not null && keyAvailable < keysNeeded) problems.Add($"The reserved {table.KeyField} range has too few unused values ({keyAvailable} left, {keysNeeded} needed).");
        if (childRange is not null && childAvailable < 1) problems.Add("The reserved synthetic child reference range has no unused value.");
        var summary = new Dictionary<string, string>
        {
            ["Resource"] = $"dbo.{table.Table}",
            ["Operation"] = string.Join(", ", definition.Operations.Select(o => o == ActiveEventOperation.ReadSnapshot ? "Snapshot read (op \"r\")" : $"{o} (op \"{(o == ActiveEventOperation.Tombstone ? "tombstone" : DebeziumEventBuilder.OperationCode(o))}\")")),
            ["Synthetic identity"] = keyRange is null ? "No reserved range configured" : $"{table.KeyField} from the reserved range {keyRange.Min}–{keyRange.Max}",
            ["Synthetic child reference"] = childRange is null ? "No reserved range configured" : $"{table.ChildReferenceField} from the reserved range {childRange.Min}–{childRange.Max} (not a real child)",
            ["Fields"] = string.Join(", ", table.Fields) + (definition.Kind == Kind.InvalidThenValid ? $" (invalid event omits {table.ChildReferenceField})" : ""),
            ["Never sent"] = "Names, remarks, contact data, national ids or any real child/school identifier",
        };
        return problems.Count == 0
            ? (true, $"Reserved synthetic ranges and reference codes are configured ({keyAvailable} unused key(s), {childAvailable} unused child reference(s)).", summary)
            : (false, $"Not configured: {string.Join(" ", problems)} No Skolenærvær synthetic identifier policy is agreed yet, so nothing can be generated safely.", summary);
    }

    private IReadOnlyDictionary<string, long>? ReferenceCodes(SkoleNaervaerTable table, out string reason)
    {
        var codes = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (_, code) in table.ReferenceFields)
        {
            if (!long.TryParse(configuration[$"{ConfigurationRoot}:ReferenceCodes:{code}"], out var value))
            {
                reason = $"No synthetic-safe reference code is configured for {code} ({ConfigurationRoot}:ReferenceCodes).";
                return null;
            }
            codes[code] = value;
        }
        reason = "";
        return codes;
    }

    private IReadOnlyList<string> ReviewedArchives() => configuration.GetSection($"{ConfigurationRoot}:ReviewedArchives").GetChildren()
        .Select(c => c.Value ?? "").Where(v => v.Length == 64).ToArray();

    private ActiveEventScenarioDescriptor Descriptor(SkoleNaervaerTable table, Definition definition) => new()
    {
        ExtensionId = ExtensionId, ExtensionVersion = ExtensionVersion, ProviderDisplayName = DisplayName,
        ScenarioId = ScenarioId(table, definition), ScenarioVersion = "1", DisplayName = $"{table.DisplayName}: {definition.Name}", Description = definition.Description,
        Category = definition.Category, ResourceLabel = $"dbo.{table.Table}", RequiredIntegrationType = IntegrationKind.EventHub.ToString(), RequiredTransportType = "EventHub",
        SupportedOperations = definition.Operations, ExpectedEventCount = definition.EventCount, SyntheticDataRequired = true, StateChanging = true,
        RequiresDownstreamVerification = true, RequiresContinuity = definition.Kind is Kind.ExactReplay or Kind.InvalidThenValid, ReplayKind = definition.Replay,
        Support = definition.NotAssessedBecause is null ? ActiveEventScenarioSupport.Supported : ActiveEventScenarioSupport.NotAssessed,
        SupportDetail = definition.NotAssessedBecause ?? "", RequiredSourceEvidence = table.Fields, RequiredSourceResources = [$"dbo.{table.Table}"],
        VerificationCapabilities = ["Event Hub send", "consumer continuity (checkpoint progression)", "aggregate consumer activity", "Utdanning verification unavailable (no read contract)"],
        ResultMeaning = "Every event was accepted, the consumer progressed past them, and a downstream verifier observed the expected Utdanning state.",
        ResultDoesNotMean = definition.Kind switch
        {
            Kind.ExactReplay => "Replay sent ≠ idempotency verified: only a downstream verifier can show that the replay was applied once.",
            Kind.InvalidThenValid => "Continuity after the invalid record does not prove it was rejected for the right reason or recorded, nor that the control was stored.",
            _ => "Event Hub acceptance and checkpoint progression do not mean Utdanning stored, linked or ended the record.",
        },
    };

    private static SourceContractRequirement Requirement(SkoleNaervaerTable table, Definition definition, IntegrationDefinition integration) => new(
        ScenarioId(table, definition), "1", SkoleNaervaerFixtures.FixtureSchemaVersion, integration.SourceResource ?? "", table.Fields, [],
        definition.Operations[0], []);

    private static string ScenarioId(SkoleNaervaerTable table, Definition definition) => $"skolenaervaer.{table.Slug}.{definition.Suffix}";

    private static (SkoleNaervaerTable Table, Definition Definition)? Resolve(string scenarioId)
    {
        foreach (var table in SkoleNaervaerFixtures.Tables)
            foreach (var definition in Definitions)
                if (string.Equals(ScenarioId(table, definition), scenarioId, StringComparison.Ordinal)) return (table, definition);
        return null;
    }

    /// <summary>The education table the integration's configured CDC source resource names (database.schema.table), or null.</summary>
    private static SkoleNaervaerTable? Table(ActiveEventProjectEvidenceContext context) =>
        context.Integration is { Enabled: true, Kind: IntegrationKind.EventHub } integration && context.Platform is { NamespaceFqdn.Length: > 0 } &&
        (integration.SourceResource?.Trim() ?? "").Split('.') is { Length: 3 } parts && parts.All(part => part.Length > 0)
            ? SkoleNaervaerFixtures.ForTable(parts[2])
            : null;
}
