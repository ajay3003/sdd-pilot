using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using BirkNext.Api.Services.AltinnTesting;
using BirkNext.Api.Services.Integrations;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.IntegrationJourneys.Packs.SkoleNaervaer;

/// <summary>Typed, validated reference to one existing Altinn instance (party id + instance GUID). Held in memory for the run only.</summary>
public static class SkoleNaervaerInstanceReference
{
    public const string PartyKey = "instanceOwnerPartyId";
    public const string InstanceKey = "instanceGuid";
    private static readonly Regex PartyPattern = new(@"^[1-9][0-9]{0,9}$", RegexOptions.CultureInvariant);

    public static JourneyInputPreparation Prepare(JourneyScenarioDescriptor scenario, IReadOnlyDictionary<string, string> inputs)
    {
        var errors = new List<string>();
        var reporter = SkoleNaervaerReportInput.PrepareReporter(scenario, inputs, errors);
        var party = inputs.GetValueOrDefault(PartyKey)?.Trim() ?? "";
        var instance = inputs.GetValueOrDefault(InstanceKey)?.Trim() ?? "";
        if (!PartyPattern.IsMatch(party) || !int.TryParse(party, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            errors.Add("The instance owner party id must be a positive whole number (Altinn party id), nothing else.");
        if (!Guid.TryParseExact(instance, "D", out var guid))
            errors.Add("The instance GUID must be a GUID such as 6a1b2c3d-0000-4000-8000-000000000001.");
        if (errors.Count > 0) return new JourneyInputPreparation(new Dictionary<string, string>(), errors, []);
        return new JourneyInputPreparation(
            new Dictionary<string, string>(StringComparer.Ordinal) { [PartyKey] = party, [InstanceKey] = guid.ToString("D"), [SkoleNaervaerReportInput.ReporterKey] = reporter.Value },
            [], [$"Altinn instance inst-{guid.ToString("N")[..8]} (read-only Storage read; the party id is not stored).", reporter.Summary]);
    }

    public static (long PartyId, Guid InstanceGuid) Read(IReadOnlyDictionary<string, string> values) =>
        (long.Parse(values[PartyKey], CultureInfo.InvariantCulture), Guid.ParseExact(values[InstanceKey], "D"));
}

/// <summary>
/// Read-only Altinn instance evidence for Rapportmottak: one GET on Altinn Storage (TT02) with the Altinn token of the run's reporter (the
/// same authentication providers as submissions — unattended synthetic reporter or the interactive fallback), then each Altinn
/// step is reported from that single observation — Observed or Not verified, never Verified (an observed instance is not a submission this
/// run made or verified). Nothing is created, uploaded, validated or advanced, and the app's own GET instance (which marks the instance read
/// for person callers) is not used. MU, MM, Skolenærværsrapport and Person steps stay Not assessed: an Altinn Storage path is not an MU blob.
/// </summary>
public sealed class SkoleNaervaerAltinnInstanceObserver(IServiceScopeFactory scopes, AltinnTestAuthenticator authenticator, AltinnStorageInstanceReader storage, TimeProvider clock)
    : IJourneyStepObserver
{
    private const string Source = "Altinn TT02 Storage (read-only GET)";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private sealed record Observation(AltinnCallResult? Result, AltinnStorageInstanceEvidence? Evidence, string? Blocked, string? FormDataTypeId, DateTimeOffset At);
    private readonly ConcurrentDictionary<Guid, Observation> _observations = new();

    public bool CanObserve(JourneyStepContext context, out string reason)
    {
        var mine = context.Journey.PackId == SkoleNaervaerJourneyPack.Id && context.Journey.JourneyId == SkoleNaervaerJourneyPack.RapportmottakId
            && context.Scenario?.ScenarioId == SkoleNaervaerJourneyPack.InstanceEvidenceScenario && SkoleNaervaerAltinnExecutor.AltinnSteps.Contains(context.Step.StepId);
        reason = mine ? "Observed from a read-only Altinn Storage GET of the instance." : "Not the read-only instance-evidence scenario.";
        return mine;
    }

    public async Task<JourneyStepResult> ObserveAsync(JourneyStepContext context, CancellationToken ct)
    {
        foreach (var (run, old) in _observations) if (clock.GetUtcNow() - old.At > Lifetime) _observations.TryRemove(run, out _);
        try
        {
            if (!_observations.TryGetValue(context.RunId, out var observation))
                _observations[context.RunId] = observation = await ReadAsync(context, ct);
            return Map(context, observation);
        }
        finally
        {
            if (context.Step.StepId == SkoleNaervaerAltinnExecutor.ReceiptStep) _observations.TryRemove(context.RunId, out _);
        }
    }

    private async Task<Observation> ReadAsync(JourneyStepContext context, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var package = await scope.ServiceProvider.GetRequiredService<IIntegrationMessageFlowStore>().GetAsync(context.EnvironmentId, ct);
        var policy = scope.ServiceProvider.GetRequiredService<AltinnTestingPolicy>();
        var contract = scope.ServiceProvider.GetRequiredService<AltinnAppContractService>().Evaluate(context.EnvironmentId, package.AltinnConfiguration.AppIdentifier, package.AppContract);
        string? Block()
        {
            if (AltinnTestingPolicy.TargetBlock(context.Environment, "No trusted environment.") is { } target) return target;
            if (AltinnTestingPolicy.AltinnEnvironmentBlock(package.AltinnConfiguration) is { } environment) return environment;
            if (policy.ApprovedApp(package.AltinnConfiguration.AppIdentifier, out var appReason) is null) return appReason;
            if (!context.InputValues.ContainsKey(SkoleNaervaerInstanceReference.PartyKey) || !context.InputValues.ContainsKey(SkoleNaervaerInstanceReference.InstanceKey))
                return "The run has no validated instance reference.";
            if (context.InputValues.GetValueOrDefault(SkoleNaervaerReportInput.ReporterKey) is not { Length: > 0 }) return "The run has no reporter.";
            return null;
        }
        var now = clock.GetUtcNow();
        if (Block() is { } blocked) return new Observation(null, null, blocked, null, now);
        var (party, guid) = SkoleNaervaerInstanceReference.Read(context.InputValues);
        try
        {
            var auth = await authenticator.AuthenticateAsync(new AltinnAuthenticationRequest(context.EnvironmentId, context.Initiator,
                context.InputValues[SkoleNaervaerReportInput.ReporterKey], package.AltinnConfiguration), ct);
            if (!auth.Success) return new Observation(null, null, $"{AltinnAuthenticationResult.StageLabel(auth.Stage)} failed: {auth.Detail}", null, now);
            var (result, evidence) = await storage.ReadAsync(auth.Token!, party, guid, ct);
            return new Observation(result, evidence, null, contract.FormDataTypeId, now);
        }
        catch (AltinnSignInException ex) { return new Observation(null, null, ex.Message, null, now); }
        catch (HttpRequestException) { return new Observation(null, null, "Altinn Storage could not be reached.", null, now); }
        catch (System.Text.Json.JsonException) { return new Observation(new AltinnCallResult(false, System.Net.HttpStatusCode.OK, "Altinn Storage answered with an unreadable document."), null, null, null, now); }
    }

    private JourneyStepResult Map(JourneyStepContext context, Observation observation)
    {
        if (observation.Blocked is { } blocked)
            return context.Step.StepId == SkoleNaervaerAltinnExecutor.AuthStep
                ? Result(context, JourneyStepState.Unavailable, "", $"Not observed: {blocked} Nothing was read or changed.", [])
                : Result(context, JourneyStepState.NotAssessed, "", "Not observed: the read-only instance read did not run.", []);
        var http = observation.Result is { } call ? $"HTTP {(int)call.Status}" : "";
        if (observation.Evidence is not { } evidence)
            return context.Step.StepId == SkoleNaervaerAltinnExecutor.AuthStep
                ? Result(context, JourneyStepState.NotVerified, http, observation.Result?.Detail ?? "The instance could not be read.", [])
                : Result(context, JourneyStepState.NotAssessed, "", "Not observed: the instance could not be read from Altinn Storage.", []);
        if (!evidence.IdentityMatches)
            return Result(context, JourneyStepState.UnexpectedResult, http, "Altinn Storage returned a different instance than the one requested; nothing is attributed to it.", []);

        var task = evidence.CurrentTask;
        var formElements = evidence.Data.Where(item => observation.FormDataTypeId is null || item.DataType == observation.FormDataTypeId).ToArray();
        return context.Step.StepId switch
        {
            SkoleNaervaerAltinnExecutor.AuthStep => Result(context, JourneyStepState.Observed, $"{http} · read-only Storage GET",
                "Altinn Storage accepted the ID-porten TEST token for a read of this instance. Authenticated ≠ submitted.", []),
            SkoleNaervaerAltinnExecutor.InstanceStep => Result(context, JourneyStepState.Observed, $"{http} · instance {evidence.InstanceReference}",
                "The instance exists in Altinn. Observed, not created by this run.",
            [
                new("Instance", evidence.InstanceReference), new("App", evidence.AppId ?? "(not given)"),
                new("Created", Time(evidence.Created)), new("Last changed", Time(evidence.LastChanged)),
                new("Process started", Time(evidence.ProcessStarted)), new("Start event", evidence.StartEvent ?? "(not given)"),
            ]),
            SkoleNaervaerAltinnExecutor.DataStep => formElements.Length > 0
                ? Result(context, JourneyStepState.Observed, $"{http} · {evidence.Data.Count} data element(s)",
                    "Data elements observed (metadata only; no content was read). The storage path is Altinn's own storage, not an MU blob.",
                    [.. evidence.Data.SelectMany(DataFacts)])
                : Result(context, JourneyStepState.NotVerified, $"{http} · {evidence.Data.Count} data element(s)",
                    observation.FormDataTypeId is { } type ? $"No data element of the form data type {type} is present." : "The instance has no data element.",
                    [.. evidence.Data.SelectMany(DataFacts)]),
            SkoleNaervaerAltinnExecutor.ValidateStep => task?.CanCompleteTask is { } canComplete
                ? Result(context, JourneyStepState.Observed, $"{http} · can complete task: {(canComplete ? "yes" : "no")}",
                    "Altinn recorded a validation of the current task. Observed only — this run did not validate anything.", TaskFacts(task))
                : Result(context, JourneyStepState.NotAssessed, http, task is null ? "No current task: nothing to validate (see the process state)." : "Altinn has recorded no validation of the current task.", task is null ? [] : TaskFacts(task)),
            SkoleNaervaerAltinnExecutor.SubmitStep => evidence.ProcessEnded is { } endedAt
                ? Result(context, JourneyStepState.Observed, $"{http} · process ended {endedAt:yyyy-MM-dd HH:mm:ss} UTC",
                    "The instance's process has ended in Altinn. Observed only: process ended ≠ MU received.",
                    [new("Process ended", Time(endedAt)), new("End event", evidence.EndEvent ?? "(not given)")])
                : Result(context, JourneyStepState.NotVerified, $"{http} · process not ended",
                    $"The process has not ended; current task: {task?.ElementId ?? "(none)"}. Not submitted.", task is null ? [] : TaskFacts(task)),
            _ => evidence.ProcessEnded is { } completed
                ? Result(context, JourneyStepState.Observed, $"{http} · instance completed {completed:yyyy-MM-dd HH:mm:ss} UTC · end event {evidence.EndEvent ?? "(not given)"}",
                    "Altinn shows the instance as completed. This is not evidence that MU received, MM processed or Skolenærværsrapport stored the report.",
                    [new("Process ended", Time(completed)), new("End event", evidence.EndEvent ?? "(not given)")])
                : Result(context, JourneyStepState.NotVerified, $"{http} · instance not completed", "Altinn does not show the instance as completed.", []),
        };
    }

    private static IReadOnlyList<JourneyEvidenceFact> TaskFacts(AltinnTaskEvidence task) =>
    [
        new("Current task", task.ElementId ?? "(not given)"), new("Task name", task.Name ?? "(not given)"), new("Altinn task type", task.AltinnTaskType ?? "(not given)"),
        new("Can complete task", task.CanCompleteTask is { } can ? (can ? "yes" : "no") : "(not recorded)"), new("Task ended", Time(task.Ended)),
    ];

    private static IEnumerable<JourneyEvidenceFact> DataFacts(AltinnDataElementEvidence data) =>
    [
        new($"{data.DataReference} · data type", data.DataType ?? "(not given)"),
        new($"{data.DataReference} · content type", data.ContentType ?? "(not given)"),
        new($"{data.DataReference} · size", data.Size is { } size ? $"{size} bytes" : "(not given)"),
        new($"{data.DataReference} · locked", data.Locked is { } locked ? (locked ? "yes" : "no") : "(not given)"),
        new($"{data.DataReference} · Altinn storage path (not an MU blob)", data.RedactedStoragePath ?? "(not given)"),
        new($"{data.DataReference} · content hash", data.ContentHashPresent ? "present (not shown)" : "absent"),
        new($"{data.DataReference} · references", data.ReferenceCount.ToString(CultureInfo.InvariantCulture)),
    ];

    private static string Time(DateTimeOffset? value) => value is { } at ? $"{at.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC" : "(not given)";

    private JourneyStepResult Result(JourneyStepContext context, JourneyStepState state, string evidence, string reason, IReadOnlyList<JourneyEvidenceFact> facts) => new()
    {
        StepId = context.Step.StepId, Label = context.Step.Label, IntegrationId = context.Integration?.Id, State = state, Evidence = evidence, Reason = reason,
        Verification = state == JourneyStepState.Observed ? "Observed through a read-only Altinn Storage GET" : "", EvidenceSource = Source,
        Correlation = context.InputValues.TryGetValue(SkoleNaervaerInstanceReference.InstanceKey, out var guid) && Guid.TryParse(guid, out var parsed)
            ? $"inst-{parsed.ToString("N")[..8]}" : context.RunId.ToString("N"),
        CapturedAt = clock.GetUtcNow(), Facts = facts,
    };
}
