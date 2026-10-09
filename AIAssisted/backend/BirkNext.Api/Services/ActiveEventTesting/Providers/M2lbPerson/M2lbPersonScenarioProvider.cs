using System.Globalization;
using BirkNext.Api.Services.ActiveCdcTests;
using BirkNext.Api.Services.ActiveEventTesting.Debezium;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.ActiveEventTesting.Providers.M2lbPerson;

/// <summary>
/// Project extension for the reviewed synthetic Person CDC cases. It owns the Person source requirements, the Person synthetic key scope
/// and fixture generation; execution, safety, persistence and reporting are the shared core's.
/// </summary>
public sealed class M2lbPersonScenarioProvider(
    IqrSourceStore sources,
    ActiveEventPolicy policy,
    IActiveEventSyntheticIdentityReservation identities,
    ActiveCdcRunStore legacyRuns,
    IConfiguration configuration,
    TimeProvider clock) : IActiveEventScenarioProvider
{
    public const string Id = "m2lb.person";
    public const string PersonPkScope = "m2lb.person.PersonPK";

    public string ExtensionId => Id;
    public string ExtensionVersion => "2";
    public string DisplayName => "M2LB Person CDC";
    public string Description => "Synthetic Debezium Person events for the Person Adapter's Event Hub (BiRK dbo.Person).";
    public IReadOnlyList<string> Resources => ["dbo.Person"];
    public IReadOnlyList<ActiveEventScenarioDescriptor> CatalogScenarios => M2lbPersonScenarios.All.Select(Descriptor).ToArray();

    public bool CanApply(ActiveEventProjectEvidenceContext context) =>
        context.Integration is { Enabled: true, Kind: IntegrationKind.EventHub } integration &&
        context.Platform is { NamespaceFqdn.Length: > 0 } &&
        SourceParts(integration) is { } parts && string.Equals(parts[2], M2lbPersonScenarios.Table, StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<ActiveEventScenarioDescriptor> GetScenarios(ActiveEventProjectEvidenceContext context) => CanApply(context) ? CatalogScenarios : [];

    public async Task<ActiveEventScenarioPreparation> PrepareAsync(string scenarioId, ActiveEventProjectEvidenceContext context, Guid? sourceSnapshotId, CancellationToken ct)
    {
        var scenario = M2lbPersonScenarios.Find(scenarioId);
        if (scenario is null || !CanApply(context))
            return new() { Compatible = false, Detail = "This scenario is not applicable to the selected integration." };

        var integration = context.Integration!;
        var (selected, latestId, problem) = await SourceContractEvaluator.SelectAsync(sources, context.TargetEnvironmentId, integration.Id, sourceSnapshotId, ct);
        var evaluation = SourceContractEvaluator.Evaluate(Requirement(scenario, integration), selected, latestId);
        if (problem is not null) evaluation = evaluation with { Status = SourceContractStatus.NoSourceSnapshot, Detail = problem };
        var checks = new List<ActiveEventReadinessCheck>
        {
            new("source-contract", "Source contract", evaluation.Status == SourceContractStatus.Compatible ? ActiveEventReadinessState.Ready : ActiveEventReadinessState.Blocked,
                $"{evaluation.Label} — {evaluation.Detail}", ActiveEventReadinessCategory.SourceContract),
        };

        var invalidReviewed = true;
        var invalidDetail = "";
        if (scenario.Id == M2lbPersonScenarios.InvalidThenValidId)
        {
            (invalidReviewed, invalidDetail) = ReviewInvalidFixture(selected, evaluation);
            checks.Add(new("invalid-fixture", "Reviewed invalid fixture", invalidReviewed ? ActiveEventReadinessState.Ready : ActiveEventReadinessState.Blocked,
                invalidDetail, ActiveEventReadinessCategory.SourceContract));
        }

        var range = KeyRange(out var rangeReason);
        var required = scenario.Id == M2lbPersonScenarios.SamePersonPkReplayId ? 2 : 1;
        var available = range is null ? 0 : await identities.AvailableAsync(context.TargetEnvironmentId, PersonPkScope, range, await LegacyFloorAsync(context.TargetEnvironmentId, range, ct), ct);
        checks.Add(new("synthetic-key", "Synthetic test data policy", available >= required ? ActiveEventReadinessState.Ready : ActiveEventReadinessState.Blocked,
            range is null ? rangeReason : $"{rangeReason} {available} unused key(s); this scenario needs {required}.", ActiveEventReadinessCategory.SyntheticData));
        checks.Add(new("downstream", "Downstream verification", ActiveEventReadinessState.Partial,
            "No read-only Person verification exists (no standalone Person read endpoint; the ingestion success log is Debug level). Downstream state stays Not verified.",
            ActiveEventReadinessCategory.DownstreamVerification));

        var fingerprint = scenario.Id == M2lbPersonScenarios.InvalidThenValidId
            ? Short($"{evaluation.Fingerprint}|{InvalidFixtureReview.FixtureId}|{InvalidFixtureReview.FixtureVersion}|{invalidReviewed}")
            : evaluation.Fingerprint;
        return new()
        {
            Compatible = evaluation.Status == SourceContractStatus.Compatible && available >= required && invalidReviewed,
            Detail = checks.Any(check => ActiveEventReadinessRules.Blocks(check.State))
                ? string.Join(" ", checks.Where(check => ActiveEventReadinessRules.Blocks(check.State)).Select(check => $"{check.Label}: {check.Detail}"))
                : evaluation.Detail,
            Contract = new ActiveEventSourceContractReference
            {
                SourceSnapshotId = evaluation.SourceSnapshotId, ContractFingerprint = fingerprint, SourceResource = integration.SourceResource,
                ContractStatus = evaluation.Label, ExtensionVersion = ExtensionVersion, ScenarioVersion = scenario.Version,
            },
            Checks = checks,
            SafeMetadata = new Dictionary<string, string> { ["sourceTable"] = M2lbPersonScenarios.Table, ["contractStatus"] = evaluation.Status.ToString() },
            SyntheticSummary = new Dictionary<string, string>
            {
                ["Resource"] = integration.SourceResource ?? "",
                ["Operation"] = "Create (Debezium op \"c\")",
                ["Synthetic identity"] = range is null ? "No reserved range" : $"PersonPK from the reserved range {range.Min}–{range.Max} ({required} key(s))",
                ["Fields"] = string.Join(", ", scenario.Id == M2lbPersonScenarios.InvalidThenValidId ? M2lbPersonScenarios.Fields.Where(f => f != "PersonPK") : M2lbPersonScenarios.Fields) +
                    (scenario.Id == M2lbPersonScenarios.InvalidThenValidId ? " (invalid event; PersonPK omitted), then all fields (valid control)" : ""),
                ["Marker"] = "Fornavn = BIRKNEXT-TEST-<run>; Etternavn = Synthetic; no national id",
            },
        };
    }

    public async Task<ActiveEventScenarioGeneration> GenerateAsync(string scenarioId, Guid runId, ActiveEventTrustedTarget target,
        ActiveEventSourceContractReference contract, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var scenario = M2lbPersonScenarios.Find(scenarioId) ?? throw new InvalidOperationException("Unknown M2LB Person scenario.");
        if (!string.Equals(contract.ExtensionVersion, ExtensionVersion, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(contract.ContractFingerprint))
            throw new InvalidOperationException("The Person fixture requires a prepared, version-bound source contract.");
        if (ActiveEventPolicy.EnvironmentBlock(target.EnvironmentType) is { } blockedEnvironment)
            throw new InvalidOperationException(blockedEnvironment);
        var parts = (contract.SourceResource ?? "").Split('.');
        if (parts.Length != 3 || !string.Equals(parts[2], M2lbPersonScenarios.Table, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The prepared source contract does not identify the required database.schema.Person resource.");
        var range = KeyRange(out var rangeReason) ?? throw new InvalidOperationException(rangeReason);
        var keyCount = scenario.Id == M2lbPersonScenarios.SamePersonPkReplayId ? 2 : 1;
        var keys = await identities.ReserveAsync(target.TargetEnvironmentId, PersonPkScope, range, await LegacyFloorAsync(target.TargetEnvironmentId, range, ct), keyCount, runId, ExtensionId, ct);

        var source = new DebeziumSource(parts[0], parts[1], parts[2]);
        var now = clock.GetUtcNow();
        var marker = PersonCdcFixtureBuilder.Marker(runId);
        var max = policy.Options.MaxPayloadBytes;
        var events = new List<GeneratedActiveEvent>();
        if (scenario.Id == M2lbPersonScenarios.InvalidThenValidId)
        {
            events.Add(Generated(scenario, runId, "I", 0, null, PersonCdcFixtureBuilder.Create(null, marker + "-INVALID", source, now, max), marker + "-INVALID", now, "Reviewed invalid input", null));
            events.Add(Generated(scenario, runId, "V", 1, keys[0], PersonCdcFixtureBuilder.Create(keys[0], marker + "-V", source, now, max), marker + "-V", now, "Valid control", null));
        }
        else if (scenario.Id == M2lbPersonScenarios.SamePersonPkReplayId)
        {
            var first = PersonCdcFixtureBuilder.Create(keys[0], marker, source, now, max);
            events.Add(Generated(scenario, runId, "A", 0, keys[0], first, marker, now, "First create", null));
            events.Add(Generated(scenario, runId, "A2", 1, keys[0], first, marker, now, "Exact replay", $"{runId:N}-A"));
            events.Add(Generated(scenario, runId, "B", 2, keys[1], PersonCdcFixtureBuilder.Create(keys[1], marker + "-B", source, now, max), marker + "-B", now, "Following control", null));
        }
        else
        {
            events.Add(Generated(scenario, runId, "", 0, keys[0], PersonCdcFixtureBuilder.Create(keys[0], marker, source, now, max), marker, now, "Synthetic create", null));
        }
        return new ActiveEventScenarioGeneration
        {
            Events = events,
            SafeMetadata = new Dictionary<string, string> { ["reservedKeyCount"] = keyCount.ToString(CultureInfo.InvariantCulture) },
            Limitations = ["Person persistence and idempotency are not verified by the provider."],
        };
    }

    private GeneratedActiveEvent Generated(PersonScenario scenario, Guid runId, string label, int index, long? personPk, DebeziumEnvelope envelope, string marker,
        DateTimeOffset now, string role, string? replayOf)
    {
        var eventId = label.Length == 0 ? runId.ToString("N") : $"{runId:N}-{label}";
        var key = personPk is { } pk ? DebeziumEventBuilder.Key(new DebeziumRow { { "PersonPK", DebeziumValue.Number(pk) } }) : null;
        return new GeneratedActiveEvent
        {
            EventId = eventId, ExtensionId = ExtensionId, ScenarioId = scenario.Id, SequenceIndex = index, Operation = ActiveEventOperation.Create,
            Body = envelope.Body, BodySha256 = envelope.Sha256, BodyBytes = envelope.Body.Length, ContentType = "application/json", EventKey = key,
            Correlation = new ActiveEventCorrelation
            {
                RunId = runId, EventId = eventId, EventFingerprint = envelope.Sha256, SyntheticMarker = marker,
                SafeSourceIdentity = personPk?.ToString(CultureInfo.InvariantCulture),
                ExpectedResourceIdentity = personPk is { } value ? PersonCdcFixtureBuilder.ExpectedPersonId(value).ToString("D") : null,
                ReplaysEventId = replayOf, ReplayKind = replayOf is null ? ActiveEventReplayKind.None : ActiveEventReplayKind.ExactReplay,
            },
            SafeDisplayMetadata = new Dictionary<string, string>
            {
                ["sequenceLabel"] = label,
                ["role"] = role,
                ["syntheticIdentity"] = personPk is { } shown ? $"PersonPK {shown.ToString(CultureInfo.InvariantCulture)}" : "PersonPK omitted (invalid fixture)",
                ["expectedResourceIdentity"] = personPk is { } derived ? $"PersonId {PersonCdcFixtureBuilder.ExpectedPersonId(derived):D}" : "",
                ["marker"] = marker,
                ["syntheticBirthDate"] = PersonCdcFixtureBuilder.SyntheticBirthDate(now).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["invalidCondition"] = personPk is null ? InvalidFixtureReview.Condition : "",
                ["fields"] = string.Join(",", personPk is null ? M2lbPersonScenarios.Fields.Where(f => f != "PersonPK") : M2lbPersonScenarios.Fields),
            },
            TransportProperties = label.Length == 0 ? new Dictionary<string, string>() : new Dictionary<string, string> { ["BirkNextMessage"] = label },
        };
    }

    private ActiveEventScenarioDescriptor Descriptor(PersonScenario scenario) => new()
    {
        ExtensionId = ExtensionId, ExtensionVersion = ExtensionVersion, ProviderDisplayName = DisplayName,
        ScenarioId = scenario.Id, ScenarioVersion = scenario.Version, DisplayName = scenario.Name, Description = scenario.Description, Category = scenario.Category,
        ResourceLabel = "dbo.Person", RequiredIntegrationType = IntegrationKind.EventHub.ToString(), RequiredTransportType = "EventHub",
        SupportedOperations = [ActiveEventOperation.Create], ExpectedEventCount = scenario.EventCount, SyntheticDataRequired = true, StateChanging = true,
        RequiresDownstreamVerification = scenario.Id == M2lbPersonScenarios.NormalPersonId, RequiresContinuity = scenario.RequiresContinuity,
        ReplayKind = scenario.ReplayKind, RequiredSourceEvidence = M2lbPersonScenarios.Fields, RequiredSourceResources = ["dbo.Person"],
        VerificationCapabilities = ["Event Hub send", "consumer continuity (checkpoint progression)", "aggregate consumer activity", "downstream Person verification unavailable"],
        ResultMeaning = scenario.ResultMeaning, ResultDoesNotMean = $"{scenario.Limitation} {scenario.ResultDoesNotMean}",
    };

    private static SourceContractRequirement Requirement(PersonScenario scenario, IntegrationDefinition integration) => new(
        scenario.Id, scenario.Version, PersonCdcFixtureBuilder.FixtureSchemaVersion, integration.SourceResource ?? "", M2lbPersonScenarios.Fields, [],
        ActiveEventOperation.Create, ["PersonRecord"]);

    private (bool Reviewed, string Detail) ReviewInvalidFixture(IqrSourceSnapshot? snapshot, SourceContractEvaluation evaluation)
    {
        if (snapshot?.IntegrationPath is not { } path || evaluation.Status is SourceContractStatus.NoSourceSnapshot)
            return (false, "Needs review — no source snapshot is bound, so the invalid behavior cannot be checked.");
        var identity = path.Fields.FirstOrDefault(f => f.Key == "CDC PersonRecord.PersonPK")?.Steps
            .Any(s => s.Field == "PersonId" && s.Transformation == FieldTransformation.Derived && s.Location?.File.EndsWith("PersonMapper.cs", StringComparison.Ordinal) == true) == true;
        if (!identity)
            return (false, "Needs review — the snapshot does not show PersonMapper deriving the Person identity from PersonPK, so the invalid fixture's premise is not supported by this source.");
        var extra = configuration.GetSection("ActiveEventTesting:Providers:m2lb.person:InvalidFixtureReviewedArchives").GetChildren()
            .Concat(configuration.GetSection("ActiveCdcTests:InvalidFixtureReviewedArchives").GetChildren()).Select(c => c.Value ?? "").Where(v => v.Length == 64);
        var archive = snapshot.Archive.Sha256;
        return InvalidFixtureReview.ReviewedArchives.Contains(archive) || extra.Contains(archive, StringComparer.OrdinalIgnoreCase)
            ? (true, $"Reviewed against archive {archive[..12]}…: {InvalidFixtureReview.Condition} {string.Join(" ", InvalidFixtureReview.ReviewedPath)}")
            : (false, $"Needs review — archive {archive[..Math.Min(12, archive.Length)]}… was not reviewed for this fixture. The source analyzer does not see the mapper's null gate, so the invalid behavior must be re-reviewed before sending.");
    }

    /// <summary>The generic Person key range, or the earlier ActiveCdcTests:SyntheticPersonPkMin/Max keys (same meaning) when only those are set.</summary>
    private SyntheticIdentityRange? KeyRange(out string reason)
    {
        var range = identities.Range(PersonPkScope, out reason);
        if (range is not null)
        {
            if (range.Max <= int.MaxValue) return range;
            reason = "The PersonPK range must fit the Person table's int key.";
            return null;
        }
        if (long.TryParse(configuration["ActiveCdcTests:SyntheticPersonPkMin"], out var min) && long.TryParse(configuration["ActiveCdcTests:SyntheticPersonPkMax"], out var max)
            && min > 0 && min <= max && max - min < SyntheticIdentityReservation.MaxRangeSize && max <= int.MaxValue)
        {
            reason = $"Reserved synthetic PersonPK range {min}–{max} (ActiveCdcTests:SyntheticPersonPkMin/Max).";
            return new SyntheticIdentityRange(min, max);
        }
        return null;
    }

    /// <summary>Keys the retired Active CDC runner already used stay used.</summary>
    private async Task<long?> LegacyFloorAsync(string environmentId, SyntheticIdentityRange range, CancellationToken ct) =>
        await legacyRuns.MaxPersonPkAsync(environmentId, (int)Math.Min(range.Min, int.MaxValue), (int)Math.Min(range.Max, int.MaxValue), ct);

    private static string[]? SourceParts(IntegrationDefinition integration) => (integration.SourceResource?.Trim() ?? "").Split('.') is { Length: 3 } parts ? parts : null;

    private static string Short(string text) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant()[..16];
}
