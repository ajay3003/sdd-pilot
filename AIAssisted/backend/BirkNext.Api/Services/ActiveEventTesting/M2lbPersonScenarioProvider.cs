using BirkNext.Api.Services.ActiveCdcTests;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.ActiveEventTesting;

/// <summary>Project extension for the reviewed synthetic Person CDC cases. It owns Person source requirements and fixture generation.</summary>
public sealed class M2lbPersonScenarioProvider(
    IqrSourceStore sources,
    ActiveCdcPolicy policy,
    ActiveCdcRunStore runs,
    TimeProvider clock) : IActiveEventScenarioProvider
{
    public string ExtensionId => M2lbPersonActiveEventAdapter.ExtensionId;
    public string ExtensionVersion => "1";

    public bool CanApply(ActiveEventProjectEvidenceContext context) =>
        context.Integration is { Enabled: true, Kind: IntegrationKind.EventHub } integration &&
        context.Platform is { NamespaceFqdn.Length: > 0 } &&
        SourceResource(integration)?.EndsWith(".Person", StringComparison.OrdinalIgnoreCase) == true;

    public IReadOnlyList<ActiveEventScenarioDescriptor> GetScenarios(ActiveEventProjectEvidenceContext context)
    {
        if (!CanApply(context)) return [];
        return ActiveCdcScenarioCatalog.All.Select(scenario => new ActiveEventScenarioDescriptor
        {
            ExtensionId = ExtensionId,
            ExtensionVersion = ExtensionVersion,
            ScenarioId = scenario.Id,
            ScenarioVersion = scenario.Version,
            DisplayName = scenario.Name,
            Description = scenario.Description,
            Category = scenario.Category,
            RequiredIntegrationType = IntegrationKind.EventHub.ToString(),
            RequiredTransportType = "EventHub",
            SupportedOperations = [ActiveEventOperation.Create],
            ExpectedEventCount = scenario.MessageCount,
            SyntheticDataRequired = true,
            ReplayKind = scenario.ReplayKind,
            RequiredSourceEvidence = scenario.Fields,
            VerificationCapabilities = ["Event Hub send", "consumer continuity (checkpoint only)", "downstream persistence unavailable"],
        }).ToArray();
    }

    public async Task<ActiveEventScenarioPreparation> PrepareAsync(string scenarioId, ActiveEventProjectEvidenceContext context,
        Guid? sourceSnapshotId, CancellationToken ct)
    {
        var scenario = ActiveCdcScenarioCatalog.Find(scenarioId);
        if (scenario is null || !CanApply(context))
            return new() { Compatible = false, Detail = "This scenario is not applicable to the selected integration." };

        var integration = context.Integration!;
        var snapshots = (await sources.ListAsync(context.TargetEnvironmentId, ct))
            .Where(snapshot => snapshot.IntegrationId == integration.Id).OrderByDescending(snapshot => snapshot.AnalyzedAt).ToList();
        var selected = sourceSnapshotId is { } requested
            ? snapshots.FirstOrDefault(snapshot => snapshot.Id == requested)
            : snapshots.FirstOrDefault();
        if (sourceSnapshotId is { } chosen && selected is null && await sources.FindSourceAnalysisAsync(chosen, ct) is { } sourceAnalysis)
            selected = sourceAnalysis with { IntegrationId = integration.Id };

        var latestId = snapshots.FirstOrDefault()?.Id;
        var manifest = sourceSnapshotId is not null && selected is null
            ? ActiveCdcContractManifestService.Evaluate(scenario, null, null) with { Detail = "The selected source snapshot does not belong to this integration." }
            : ActiveCdcContractManifestService.Evaluate(scenario, selected, latestId, policy.Options.InvalidFixtureReviewedArchives);
        var checks = new List<ActiveEventReadinessCheck>
        {
            new("source-contract", "Source contract", manifest.Status == ActiveCdcContractStatus.Compatible ? ActiveEventReadinessState.Ready : ActiveEventReadinessState.Blocked,
                $"{ActiveCdcLabels.Contract(manifest.Status)} — {manifest.Detail}"),
        };
        var range = policy.PersonPkRange(out var rangeReason);
        var requiredKeys = scenario.Id == ActiveCdcScenarioCatalog.SamePersonPkReplayId ? 2 : 1;
        var availableKeys = 0L;
        if (range is { } reserved)
        {
            var nextKey = await runs.MaxPersonPkAsync(context.TargetEnvironmentId, reserved.Min, reserved.Max, ct) is { } max ? max + 1L : reserved.Min;
            availableKeys = Math.Max(0, (long)reserved.Max - nextKey + 1);
        }
        checks.Add(new("synthetic-key", "Reserved synthetic PersonPK range", availableKeys >= requiredKeys ? ActiveEventReadinessState.Ready : ActiveEventReadinessState.Blocked,
            range is null ? rangeReason : $"{rangeReason} {availableKeys} unused key(s); this scenario needs {requiredKeys}."));
        if (scenario.Id == ActiveCdcScenarioCatalog.InvalidThenValidId)
            checks.Add(new("invalid-fixture", "Reviewed invalid fixture", manifest.InvalidFixtureStatus == "Reviewed" ? ActiveEventReadinessState.Ready : ActiveEventReadinessState.Blocked,
                manifest.InvalidFixtureStatus == "Reviewed" ? "The invalid fixture is reviewed against the bound source snapshot." : manifest.InvalidFixtureDetail));

        return new()
        {
            Compatible = manifest.Status == ActiveCdcContractStatus.Compatible && availableKeys >= requiredKeys &&
                (scenario.Id != ActiveCdcScenarioCatalog.InvalidThenValidId || manifest.InvalidFixtureStatus == "Reviewed"),
            Detail = checks.Any(check => check.State == ActiveEventReadinessState.Blocked)
                ? string.Join(" ", checks.Where(check => check.State == ActiveEventReadinessState.Blocked).Select(check => $"{check.Label}: {check.Detail}"))
                : manifest.Detail,
            Contract = new ActiveEventSourceContractReference
            {
                SourceSnapshotId = manifest.SourceSnapshotId,
                ContractFingerprint = manifest.Fingerprint,
                SourceResource = integration.SourceResource,
                ExtensionVersion = ExtensionVersion,
                ScenarioVersion = scenario.Version,
            },
            Checks = checks,
            SafeMetadata = new Dictionary<string, string> { ["sourceTable"] = scenario.Table, ["contractStatus"] = manifest.Status.ToString() },
        };
    }

    public async Task<ActiveEventScenarioGeneration> GenerateAsync(string scenarioId, Guid runId, ActiveEventTrustedTarget target,
        ActiveEventSourceContractReference contract, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var scenario = ActiveCdcScenarioCatalog.Find(scenarioId) ?? throw new InvalidOperationException("Unknown M2LB Person scenario.");
        if (!string.Equals(contract.ExtensionVersion, ExtensionVersion, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(contract.ContractFingerprint))
            throw new InvalidOperationException("The Person fixture requires a prepared, version-bound source contract.");
        if (ActiveCdcPolicy.EnvironmentBlock(target.EnvironmentType) is { } blockedEnvironment)
            throw new InvalidOperationException(blockedEnvironment);
        var range = policy.PersonPkRange(out var rangeReason) ?? throw new InvalidOperationException(rangeReason);
        var keyCount = scenario.Id == ActiveCdcScenarioCatalog.SamePersonPkReplayId ? 2 : 1;
        var nextKey = await runs.MaxPersonPkAsync(target.TargetEnvironmentId, range.Min, range.Max, ct) is { } max ? max + 1 : range.Min;
        if (nextKey < range.Min || (long)nextKey + keyCount - 1 > range.Max)
            throw new InvalidOperationException("The reserved synthetic PersonPK range has too few unused keys.");

        var parts = (contract.SourceResource ?? "").Split('.');
        if (parts.Length != 3 || !string.Equals(parts[2], scenario.Table, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The prepared source contract does not identify the required database.schema.Person resource.");
        if (string.IsNullOrWhiteSpace(target.Endpoint) || string.IsNullOrWhiteSpace(target.Resource))
            throw new InvalidOperationException("The trusted Event Hub target is incomplete.");

        var destination = new ActiveCdcDestination
        {
            EnvironmentId = target.TargetEnvironmentId,
            IntegrationId = target.IntegrationId,
            NamespaceFqdn = target.Endpoint,
            EventHub = target.Resource,
            ConsumerGroup = target.Consumer,
            SourceDatabase = parts[0],
            SourceSchema = parts[1],
            SourceTable = parts[2],
        };
        var now = clock.GetUtcNow();
        var events = new List<SyntheticCdcEvent>();
        if (scenario.Id == ActiveCdcScenarioCatalog.InvalidThenValidId)
        {
            events.Add(PersonCdcFixtureBuilder.BuildInvalid(runId, destination, now, policy.Options.MaxPayloadBytes, scenarioId, "I").Event);
            events.Add(PersonCdcFixtureBuilder.Build(runId, nextKey, destination, now, policy.Options.MaxPayloadBytes, scenarioId, "V", "-V").Event);
        }
        else if (scenario.Id == ActiveCdcScenarioCatalog.SamePersonPkReplayId)
        {
            var first = PersonCdcFixtureBuilder.Build(runId, nextKey, destination, now, policy.Options.MaxPayloadBytes, scenarioId, "A", "").Event;
            events.Add(first);
            events.Add(PersonCdcFixtureBuilder.Replay(first, "A2"));
            events.Add(PersonCdcFixtureBuilder.Build(runId, nextKey + 1, destination, now, policy.Options.MaxPayloadBytes, scenarioId, "B", "-B").Event);
        }
        else
        {
            events.Add(PersonCdcFixtureBuilder.Build(runId, nextKey, destination, now, policy.Options.MaxPayloadBytes).Event);
        }

        var generated = events.Select(M2lbPersonActiveEventAdapter.ToGenerated).ToArray();
        return new ActiveEventScenarioGeneration
        {
            Events = generated,
            SafeMetadata = new Dictionary<string, string> { ["reservedKeyCount"] = keyCount.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            Limitations = ["Person persistence and idempotency are not verified by the provider."],
        };
    }

    private static string? SourceResource(IntegrationDefinition integration) => integration.SourceResource?.Trim();
}
