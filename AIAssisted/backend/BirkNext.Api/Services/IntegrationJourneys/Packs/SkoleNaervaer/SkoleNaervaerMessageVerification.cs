using System.Collections.Concurrent;
using System.Globalization;
using Azure;
using Azure.Identity;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.Integrations;
using BirkNext.Api.Services.Integrations.ApplicationMessaging;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.IntegrationJourneys.Packs.SkoleNaervaer;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// Message Verification for Rapportmottak: the read-only "message-verification" scenario of the existing journey runner. One observer fills
// every Rapportmottak step from evidence that already exists — BirkNext's own run history (no network), MM's documented outcome events and
// Skoletjenesten's intake log line (both through the existing Azure credential and the IQR platform's telemetry scope, one bounded,
// parameterized query each). The pack then records correlations and assertions on the run. Nothing is submitted, sent, written or retried.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Typed inputs of a message verification. Values live in memory for the run; history keeps the redacted summary only.</summary>
public static class SkoleNaervaerMessageInput
{
    public const string SourceRunKey = "sourceRun";
    public const string InstanceKey = "altinnInstance";
    public const string MessageKey = "messageId";
    public const string ExpectedKey = "expectedOutcome";
    public const string WindowKey = "observationWindow";

    /// <summary>The expected outcomes a verification can be checked against (the scenario catalog's ids where one exists).</summary>
    public static readonly IReadOnlyList<(string Id, string Name)> Expectations =
    [
        ("valid-report", "Valid report"), ("person-not-found", "Unknown child"), ("duplicate-report", "Duplicate report"), ("correction", "Correction"),
        ("invalid-xsd", "Invalid XML (MM rejects at XSD)"), ("temporary-failure", "Temporary failure and retry"), ("mm-rejection", "MM rejection"),
        ("skole-rejection", "Skoletjenesten rejection"), ("period-locked", "Report for a locked period"), ("coverage", "Coverage for the period"),
        ("bufdata-delivery", "Bufdata delivery"),
    ];

    public static readonly IReadOnlyList<int> Windows = [24, 72, 168];

    public static IReadOnlyList<JourneyScenarioInput> Inputs =>
    [
        new(ExpectedKey, "Expected outcome", JourneyScenarioInputKind.Choice, true, "What the message was meant to show. Assertions compare the evidence with this.",
            [.. Expectations.Select(item => new JourneyScenarioInputOption(item.Id, item.Name))]),
        new(SourceRunKey, "BirkNext run (optional)", JourneyScenarioInputKind.Reference, false,
            "The id of an earlier Rapportmottak run in this environment. Its recorded Altinn evidence is read from history; nothing is called."),
        new(InstanceKey, "Altinn instance GUID (optional)", JourneyScenarioInputKind.Reference, false,
            "Finds earlier runs that recorded this instance. Only the short reference (inst-xxxxxxxx) is kept."),
        new(MessageKey, "MU message id (optional)", JourneyScenarioInputKind.Reference, false,
            "Meldingshode/Id (a UUID). MM's outcome events and Skoletjenesten's intake log are read for exactly this id."),
        new(WindowKey, "Observation window", JourneyScenarioInputKind.Choice, true, "How far back telemetry is read.",
            [.. Windows.Select(hours => new JourneyScenarioInputOption(hours.ToString(CultureInfo.InvariantCulture), $"Last {hours} hours"))]),
    ];

    public sealed record Values(Guid? SourceRun, Guid? Instance, Guid? MessageId, string Expected, int WindowHours)
    {
        public string? InstanceReference => Instance is { } guid ? Reference(guid) : null;
        public string ExpectedName => Expectations.FirstOrDefault(item => item.Id == Expected).Name ?? Expected;
    }

    public static string Reference(Guid instance) => $"inst-{instance.ToString("N")[..8]}";

    public static JourneyInputPreparation Prepare(IReadOnlyDictionary<string, string> inputs)
    {
        var errors = new List<string>();
        Guid? Optional(string key, string label)
        {
            var text = inputs.GetValueOrDefault(key)?.Trim() ?? "";
            if (text.Length == 0) return null;
            if (Guid.TryParse(text, out var guid) && guid != Guid.Empty) return guid;
            errors.Add($"{label} must be a GUID; nothing else is accepted.");
            return null;
        }
        var run = Optional(SourceRunKey, "The BirkNext run");
        var instance = Optional(InstanceKey, "The Altinn instance");
        var message = Optional(MessageKey, "The MU message id");
        var expected = inputs.GetValueOrDefault(ExpectedKey)?.Trim() ?? "";
        if (Expectations.All(item => item.Id != expected)) errors.Add("Choose one of the listed expected outcomes.");
        var window = int.TryParse(inputs.GetValueOrDefault(WindowKey), NumberStyles.None, CultureInfo.InvariantCulture, out var hours) && Windows.Contains(hours) ? hours : 0;
        if (window == 0) errors.Add("Choose one of the listed observation windows.");
        if (run is null && instance is null && message is null && errors.Count == 0)
            errors.Add("Give at least one of: a BirkNext run, an Altinn instance GUID or an MU message id.");
        if (errors.Count > 0) return new JourneyInputPreparation(new Dictionary<string, string>(), errors, []);

        var values = new Dictionary<string, string>(StringComparer.Ordinal) { [ExpectedKey] = expected, [WindowKey] = window.ToString(CultureInfo.InvariantCulture) };
        var summary = new List<string> { $"Expected outcome: {Expectations.First(item => item.Id == expected).Name}", $"Observation window: {window} h" };
        if (run is { } r) { values[SourceRunKey] = r.ToString("D"); summary.Add($"Source run {r.ToString("N")[..8]}"); }
        if (instance is { } i) { values[InstanceKey] = i.ToString("D"); summary.Add($"Altinn instance {Reference(i)} (the full id is not stored)"); }
        if (message is { } m) { values[MessageKey] = m.ToString("D"); summary.Add($"MU message id {m:D}"); }
        return new JourneyInputPreparation(values, [], summary);
    }

    public static Values Read(IReadOnlyDictionary<string, string>? values)
    {
        Guid? Get(string key) => values?.GetValueOrDefault(key) is { } text && Guid.TryParse(text, out var guid) ? guid : null;
        return new Values(Get(SourceRunKey), Get(InstanceKey), Get(MessageKey), values?.GetValueOrDefault(ExpectedKey) ?? "",
            int.TryParse(values?.GetValueOrDefault(WindowKey), NumberStyles.None, CultureInfo.InvariantCulture, out var hours) ? hours : 24);
    }
}

/// <summary>One MM outcome event (contracts/utfallshendelser.md): allow-listed fields only. <c>Aktoer</c> is never read.</summary>
public sealed record MmOutcomeEvent(DateTimeOffset At, string Utfall, string? Aarsak, int? Forsoek, string? Kvittering, string? KoeMeldingId, string? Meldingstype, string? Versjon);

/// <summary>One Skoletjenesten intake log line for the message: the HTTP answer it gave, or the Tillegg "acknowledged without effect" line.</summary>
public sealed record SkoleIntakeEvent(DateTimeOffset At, string? Utfall, string? Aarsakskode, string? Periode, bool AcknowledgedWithoutEffect);

public sealed record MessageTelemetryRead<T>(MessageEvidenceProviderState State, string Detail, IReadOnlyList<T> Rows, DateTimeOffset CapturedAt);

/// <summary>Read-only, predefined telemetry reads for one message id. There is no free-form query.</summary>
public interface ISkoleNaervaerMessageTelemetry
{
    Task<MessageTelemetryRead<MmOutcomeEvent>> MmOutcomesAsync(IntegrationPlatform platform, Guid messageId, int windowHours, CancellationToken ct);
    Task<MessageTelemetryRead<SkoleIntakeEvent>> SkoleIntakeAsync(IntegrationPlatform platform, Guid messageId, int windowHours, CancellationToken ct);
}

/// <summary>
/// Log Analytics / Application Insights reads through BirkNext's existing backend Azure credential and the IQR platform's telemetry scope.
/// Each read is one parameterized query (the id is a parsed GUID, written as an escaped literal), bounded by window, row count (take 50) and a
/// 30 s server timeout, with no retry. A refusal is Access denied (configured is not authorized); errors carry no exception text.
/// </summary>
public sealed class LogAnalyticsSkoleNaervaerMessageTelemetry(IIntegrationAzureCredential azure, TimeProvider clock, ILogger<LogAnalyticsSkoleNaervaerMessageTelemetry> logger)
    : ISkoleNaervaerMessageTelemetry
{
    public const int MaxRows = 50;
    public const string MmCategory = "M2LB.Meldingsutvekslermottak.Utfall";
    public const string SkoleCategory = "M2LB.Skole.Api.Rest.Rapportinntak.Rapportinntaksbehandler";

    private static string Id(Guid id) => LogAnalyticsApplicationMessagingSource.Literal(id.ToString("D"));

    /// <summary>MM's documented outcome event (AppTraces, fields in Properties). Classic Application Insights schema when no workspace is set.</summary>
    internal static string MmQuery(Guid messageId, bool classic) => classic
        ? $"traces | where tostring(customDimensions.Hendelse) == \"MeldingUtfall\" and tolower(tostring(customDimensions.MuMeldingId)) == {Id(messageId)} " +
          "| project TimeGenerated = timestamp, Utfall = tostring(customDimensions.Utfall), Aarsak = tostring(customDimensions.Aarsak), Forsoek = toint(customDimensions.Forsoek), " +
          "Kvittering = tostring(customDimensions.Kvittering), KoeMeldingId = tostring(customDimensions.KoeMeldingId), Meldingstype = tostring(customDimensions.Meldingstype), " +
          $"Versjon = tostring(customDimensions.Versjon) | order by TimeGenerated asc | take {MaxRows}"
        : $"AppTraces | where tostring(Properties.Hendelse) == \"MeldingUtfall\" and tolower(tostring(Properties.MuMeldingId)) == {Id(messageId)} " +
          "| project TimeGenerated, Utfall = tostring(Properties.Utfall), Aarsak = tostring(Properties.Aarsak), Forsoek = toint(Properties.Forsoek), " +
          "Kvittering = tostring(Properties.Kvittering), KoeMeldingId = tostring(Properties.KoeMeldingId), Meldingstype = tostring(Properties.Meldingstype), " +
          $"Versjon = tostring(Properties.Versjon) | order by TimeGenerated asc | take {MaxRows}";

    /// <summary>Skoletjenesten's intake log line ("Rapport {MeldingId} for {Periode} besvart {Utfall} {Aarsakskode}") and its Tillegg line.
    /// The field names follow the log template; Skoletjenesten does not document its deployed telemetry schema.</summary>
    internal static string SkoleQuery(Guid messageId, bool classic) => classic
        ? $"traces | where tostring(customDimensions.CategoryName) == \"{SkoleCategory}\" and tolower(tostring(customDimensions.MeldingId)) == {Id(messageId)} " +
          "| project TimeGenerated = timestamp, Utfall = tostring(customDimensions.Utfall), Aarsakskode = tostring(customDimensions.Aarsakskode), " +
          $"Periode = tostring(customDimensions.Periode), Kjede = tostring(customDimensions.Kjede) | order by TimeGenerated asc | take {MaxRows}"
        : $"AppTraces | where tostring(Properties.CategoryName) == \"{SkoleCategory}\" and tolower(tostring(Properties.MeldingId)) == {Id(messageId)} " +
          "| project TimeGenerated, Utfall = tostring(Properties.Utfall), Aarsakskode = tostring(Properties.Aarsakskode), " +
          $"Periode = tostring(Properties.Periode), Kjede = tostring(Properties.Kjede) | order by TimeGenerated asc | take {MaxRows}";

    public Task<MessageTelemetryRead<MmOutcomeEvent>> MmOutcomesAsync(IntegrationPlatform platform, Guid messageId, int windowHours, CancellationToken ct) =>
        ReadAsync(platform, classic => MmQuery(messageId, classic), windowHours, row => new MmOutcomeEvent(
            row.GetDateTimeOffset("TimeGenerated") ?? default, row.GetString("Utfall") ?? "", Blank(row.GetString("Aarsak")), row.GetInt32("Forsoek"),
            Blank(row.GetString("Kvittering")), Blank(row.GetString("KoeMeldingId")), Blank(row.GetString("Meldingstype")), Blank(row.GetString("Versjon"))), "MM outcome events", ct);

    public Task<MessageTelemetryRead<SkoleIntakeEvent>> SkoleIntakeAsync(IntegrationPlatform platform, Guid messageId, int windowHours, CancellationToken ct) =>
        ReadAsync(platform, classic => SkoleQuery(messageId, classic), windowHours, row => new SkoleIntakeEvent(
            row.GetDateTimeOffset("TimeGenerated") ?? default, Blank(row.GetString("Utfall")), Blank(row.GetString("Aarsakskode")), Blank(row.GetString("Periode")),
            Blank(row.GetString("Kjede")) is not null), "Skoletjenesten intake log", ct);

    private async Task<MessageTelemetryRead<T>> ReadAsync<T>(IntegrationPlatform platform, Func<bool, string> query, int windowHours, Func<LogsTableRow, T> map, string what,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        MessageTelemetryRead<T> State(MessageEvidenceProviderState state, string detail) => new(state, detail, [], now);
        if (azure.Credential is null) return State(MessageEvidenceProviderState.NotConfigured, azure.DisabledReason);
        var scope = TelemetryScope.Of(platform);
        if (!scope.Configured) return State(MessageEvidenceProviderState.NotConfigured, TelemetryScope.NotConfiguredReason);
        try
        {
            var rows = await scope.QueryRowsAsync(new LogsQueryClient(azure.Credential), query(scope.Workspace is null),
                new QueryTimeRange(TimeSpan.FromHours(Math.Clamp(windowHours, 1, 168))), ct);
            var mapped = rows.Take(MaxRows).Select(map).ToArray();
            logger.LogInformation("Message verification read {What}: {Count} record(s).", what, mapped.Length);
            return mapped.Length == 0
                ? State(MessageEvidenceProviderState.QueryCompletedNoEvidence, $"The read of {what} completed: no record for this message id in the last {windowHours} hours. Not observed is not failed.")
                : new(MessageEvidenceProviderState.Available, $"{mapped.Length} record(s) of {what} in the last {windowHours} hours.", mapped, now);
        }
        catch (RequestFailedException ex) when (ex.Status is 401 or 403)
        {
            return State(MessageEvidenceProviderState.AccessDenied, $"The backend identity may not read this telemetry (HTTP {ex.Status}). A configured source is not an authorized one.");
        }
        catch (RequestFailedException ex)
        {
            logger.LogWarning("Message verification read of {What} failed with HTTP {Status}.", what, ex.Status);
            return State(MessageEvidenceProviderState.Unavailable, $"The telemetry source answered HTTP {ex.Status}; the read was not retried.");
        }
        catch (Exception ex) when (ex is AuthenticationFailedException or CredentialUnavailableException)
        {
            return State(MessageEvidenceProviderState.AccessDenied, "The backend could not authenticate to the telemetry source.");
        }
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Everything one verification collected, once per run (all steps observe the same snapshot).</summary>
public sealed record MessageEvidenceSnapshot(
    SkoleNaervaerMessageInput.Values Input,
    IntegrationJourneyRun? SourceRun,
    int SourceRunMatches,
    MessageEvidenceProviderStatus Altinn,
    MessageEvidenceProviderStatus Mm,
    IReadOnlyList<MmOutcomeEvent> MmEvents,
    MessageEvidenceProviderStatus Skole,
    IReadOnlyList<SkoleIntakeEvent> SkoleEvents,
    DateTimeOffset CollectedAt);

/// <summary>
/// Collects a verification's evidence once per run and hands it to the observer (per step) and to the pack (for the run record). The source
/// run is read from BirkNext's history only within the same environment and Rapportmottak journey. MM and Skoletjenesten telemetry is read
/// only through an enabled IQR integration of that component that is enrolled in the trusted environment and whose platform names a
/// telemetry source — configured there by an administrator, never by the request.
/// </summary>
public sealed class SkoleNaervaerMessageEvidence(IntegrationJourneyRunStore history, IServiceScopeFactory scopes, ISkoleNaervaerMessageTelemetry telemetry, TimeProvider clock)
{
    public const string AltinnProvider = "birknext-history";
    public const string MmProvider = "mm-telemetry";
    public const string SkoleProvider = "skole-telemetry";
    public const string CoverageProvider = "skole-graphql";
    public const string BufdataProvider = "bufdata";
    internal static readonly string[] MmTokens = ["MM", "Meldingsutvekslermottak", "Meldingsutvekslermottaket"];
    internal static readonly string[] SkoleTokens = ["Skoletjenesten", "Skolenærværsrapport", "Skolenaervaersrapport"];
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private readonly ConcurrentDictionary<Guid, (DateTimeOffset At, Lazy<Task<MessageEvidenceSnapshot>> Snapshot)> _runs = new();

    public Task<MessageEvidenceSnapshot> GetAsync(JourneyStepContext context, CancellationToken ct)
    {
        Prune();
        return _runs.GetOrAdd(context.RunId, _ => (clock.GetUtcNow(), new Lazy<Task<MessageEvidenceSnapshot>>(() => CollectAsync(context, ct)))).Snapshot.Value;
    }

    /// <summary>The run's snapshot, removed from memory (null when nothing was collected or collection failed).</summary>
    public MessageEvidenceSnapshot? Take(Guid runId) =>
        _runs.TryRemove(runId, out var entry) && entry.Snapshot.IsValueCreated && entry.Snapshot.Value.IsCompletedSuccessfully ? entry.Snapshot.Value.Result : null;

    private void Prune()
    {
        var cutoff = clock.GetUtcNow() - Lifetime;
        foreach (var (key, value) in _runs) if (value.At < cutoff) _runs.TryRemove(key, out _);
    }

    private async Task<MessageEvidenceSnapshot> CollectAsync(JourneyStepContext context, CancellationToken ct)
    {
        var input = SkoleNaervaerMessageInput.Read(context.InputValues);
        var now = clock.GetUtcNow();
        MessageEvidenceProviderStatus Status(string id, string name, string system, MessageEvidenceProviderState state, string detail, string basis, string team) =>
            new(id, name, system, state, detail, basis, now, team);

        // Altinn: BirkNext's own recorded runs of this environment and journey — no network call.
        const string altinnName = "BirkNext run history (Altinn steps)";
        const string altinnBasis = "Recorded Altinn TT02 App API answers of an earlier BirkNext run (redacted history).";
        IntegrationJourneyRun? source = null;
        var matches = 0;
        MessageEvidenceProviderStatus altinn;
        bool Mine(IntegrationJourneyRun run) => run.EnvironmentId == context.EnvironmentId && run.PackId == SkoleNaervaerJourneyPack.Id
            && run.JourneyId == SkoleNaervaerJourneyPack.RapportmottakId && run.ScenarioId != SkoleNaervaerJourneyPack.MessageVerificationScenario;
        if (input.SourceRun is { } runId)
        {
            source = await history.GetAsync(runId, ct) is { } run && Mine(run) ? run : null;
            matches = source is null ? 0 : 1;
            altinn = Status(AltinnProvider, altinnName, "Altinn", source is null ? MessageEvidenceProviderState.QueryCompletedNoEvidence : MessageEvidenceProviderState.Available,
                source is null ? "No Rapportmottak run with this id exists in this environment." : $"Run {runId.ToString("N")[..8]} found ({source.ScenarioName}).", altinnBasis, "BirkNext operator");
        }
        else if (input.InstanceReference is { } reference)
        {
            var runs = (await history.ListAsync(new IntegrationJourneyHistoryQuery(context.EnvironmentId, SkoleNaervaerJourneyPack.Id, SkoleNaervaerJourneyPack.RapportmottakId), 200, ct))
                .Where(item => Mine(item) && item.Steps.Any(step => step.Correlation == reference)).ToArray();
            source = runs.FirstOrDefault();
            matches = runs.Length;
            altinn = Status(AltinnProvider, altinnName, "Altinn", source is null ? MessageEvidenceProviderState.QueryCompletedNoEvidence : MessageEvidenceProviderState.Available,
                source is null ? $"No recorded run in this environment references {reference}." : $"{runs.Length} recorded run(s) reference {reference}; the most recent is used.", altinnBasis, "BirkNext operator");
        }
        else altinn = Status(AltinnProvider, altinnName, "Altinn", MessageEvidenceProviderState.NotEvaluated, "No BirkNext run or Altinn instance was given.", altinnBasis, "BirkNext operator");

        // MM and Skoletjenesten: only for an MU message id, only through an approved, enrolled IQR integration's telemetry scope.
        const string mmBasis = "MM outcome event MeldingUtfall (contracts/utfallshendelser.md: documented schema, not runtime verified).";
        const string skoleBasis = "Skoletjenesten intake log line (field names from its log template; its deployed telemetry schema is not documented).";
        MessageEvidenceProviderStatus mm, skole;
        IReadOnlyList<MmOutcomeEvent> mmEvents = [];
        IReadOnlyList<SkoleIntakeEvent> skoleEvents = [];
        if (input.MessageId is not { } messageId)
        {
            const string noId = "No MU message id was given. A BirkNext run does not record Meldingshode/Id (the Altinn app sets it), so MM and Skoletjenesten evidence cannot be correlated to it.";
            mm = Status(MmProvider, "MM outcome events", "MM", MessageEvidenceProviderState.NotEvaluated, noId, mmBasis, "MM team");
            skole = Status(SkoleProvider, "Skoletjenesten intake log", "Skoletjenesten", MessageEvidenceProviderState.NotEvaluated, noId, skoleBasis, "Skole team");
        }
        else
        {
            using var scope = scopes.CreateScope();
            var catalog = await scope.ServiceProvider.GetRequiredService<IIntegrationCatalogService>()
                .GetAsync(context.EnvironmentId, context.Environment?.EnvironmentType, context.Environment?.TargetUrl, ct);
            var (mmPlatform, mmState, mmDetail) = Scope(catalog, context.Environment, MmTokens, "MM");
            if (mmPlatform is null) mm = Status(MmProvider, "MM outcome events", "MM", mmState, mmDetail, mmBasis, "MM team");
            else
            {
                var read = await telemetry.MmOutcomesAsync(mmPlatform, messageId, input.WindowHours, ct);
                mmEvents = read.Rows;
                mm = new(MmProvider, "MM outcome events", "MM", read.State, read.Detail, mmBasis, read.CapturedAt, "MM team");
            }
            var (skolePlatform, skoleState, skoleDetail) = Scope(catalog, context.Environment, SkoleTokens, "Skoletjenesten");
            if (skolePlatform is null) skole = Status(SkoleProvider, "Skoletjenesten intake log", "Skoletjenesten", skoleState, skoleDetail, skoleBasis, "Skole team");
            else
            {
                var read = await telemetry.SkoleIntakeAsync(skolePlatform, messageId, input.WindowHours, ct);
                skoleEvents = read.Rows;
                skole = new(SkoleProvider, "Skoletjenesten intake log", "Skoletjenesten", read.State, read.Detail, skoleBasis, read.CapturedAt, "Skole team");
            }
        }
        return new MessageEvidenceSnapshot(input, source, matches, altinn, mm, mmEvents, skole, skoleEvents, now);
    }

    /// <summary>The telemetry platform of a component's enabled, enrolled IQR integration — or why there is none.</summary>
    internal static (IntegrationPlatform? Platform, MessageEvidenceProviderState State, string Detail) Scope(IntegrationCatalog catalog, TrustedExecutionEnvironment? environment,
        string[] tokens, string component)
    {
        if (environment is null) return (null, MessageEvidenceProviderState.Blocked, "Runtime evidence access is not enabled for this environment (technical: TargetEnvironments:Trusted).");
        var candidates = catalog.Integrations.Where(item => item.Enabled && ArchitectureRuleEvaluator.MatchesAny(tokens, item.Producer, item.SourceSystem,
            item.Consumer.DisplayName, item.Consumer.LogicalName, item.DestinationSystem, item.DisplayName)).ToArray();
        if (candidates.Length == 0)
            return (null, MessageEvidenceProviderState.NotConfigured, $"No enabled Integration Quality Review integration names {component} as producer or consumer in this environment.");
        var enrolled = candidates.Where(item => environment.IntegrationIds.Contains(item.Id, StringComparer.Ordinal)).ToArray();
        if (enrolled.Length == 0)
            return (null, MessageEvidenceProviderState.Blocked,
                $"Runtime evidence access is not enabled for the {component} integration in this environment, so its evidence is not approved for reading (technical: TargetEnvironments:Trusted IntegrationIds).");
        var platform = enrolled.Select(item => catalog.Platforms.FirstOrDefault(platform => platform.Id == item.PlatformId))
            .FirstOrDefault(platform => platform?.RuntimeEvidence?.TelemetryConfigured == true);
        return platform is null
            ? (null, MessageEvidenceProviderState.NotConfigured, $"The platform of the {component} integration names no telemetry source (Log Analytics workspace or Application Insights resource).")
            : (platform, MessageEvidenceProviderState.Available, "");
    }
}

/// <summary>Fills every Rapportmottak step of a message verification from the run's evidence snapshot. Read-only; never executes a step.</summary>
public sealed class SkoleNaervaerMessageVerificationObserver(SkoleNaervaerMessageEvidence evidence) : IJourneyStepObserver
{
    public bool CanObserve(JourneyStepContext context, out string reason)
    {
        var mine = context.Journey.PackId == SkoleNaervaerJourneyPack.Id && context.Journey.JourneyId == SkoleNaervaerJourneyPack.RapportmottakId
            && context.Scenario?.ScenarioId == SkoleNaervaerJourneyPack.MessageVerificationScenario;
        reason = mine ? "Observed from existing evidence (run history, MM and Skoletjenesten telemetry)." : "Not a message verification.";
        return mine;
    }

    public async Task<JourneyStepResult> ObserveAsync(JourneyStepContext context, CancellationToken ct) =>
        SkoleNaervaerMessageVerification.Step(context.Step, await evidence.GetAsync(context, ct));
}

/// <summary>
/// Step evidence, correlations and assertions (rule set 1) of a message verification. A verdict is Passed only when the evidence proves the
/// expected result; HTTP 200 (Mottatt) proves receipt, never Skoletjenesten's internal outcome, which is stored only in its database.
/// </summary>
public static class SkoleNaervaerMessageVerification
{
    public const string RuleVersion = "1";
    /// <summary>MM's own rejection reasons (Behandling/Aarsak); anything else on Avvist is the owning service's reason code.</summary>
    internal static readonly string[] MmOwnReasons = ["SkjemaBrudd", "UstoettetVersjon", "InnholdMangler", "KrypteringsdataMangler", "DekrypteringFeilet", "UregistrertNavnerom", "MappingFeil"];
    /// <summary>Skoletjenesten's six published intake reason codes (contracts/rapportinntak.md); UKJENT_BARN is made at lock only.</summary>
    internal static readonly string[] SkoleReasons = ["MANGLER_IDENTIFIKATOR", "UGYLDIG_TIMEVERDI", "STARTDATO_ETTER_SLUTTDATO", "UTENFOR_GYLDIGHETSVINDU", "IKKE_RAPPORTERINGSPERIODE", "UVENTET_MELDINGSFORBINDELSE"];
    private const string InternalOutcome = "Skoletjenesten's internal outcome (Godkjent, Holdt, KjedeAvsluttet, IngenEffekt, Tillegg, Avvist) is stored only in its database; no read surface exists, and 200 Mottatt is the same for all of them.";

    private static MmOutcomeEvent? Last(MessageEvidenceSnapshot s) => s.MmEvents.LastOrDefault();
    private static bool Forwarded(MmOutcomeEvent e) => e.Utfall == "Mottatt" || (e.Utfall == "Avvist" && e.Aarsak is { } reason && !MmOwnReasons.Contains(reason));

    // ── Steps ───────────────────────────────────────────────────────────────────────────────────────────────────────

    public static JourneyStepResult Step(JourneyStepDefinition step, MessageEvidenceSnapshot s)
    {
        JourneyStepResult R(JourneyStepState state, string evidence, string reason, string source, DateTimeOffset? at = null, string? correlation = null, params JourneyEvidenceFact[] facts) =>
            new() { StepId = step.StepId, Label = step.Label, State = state, Evidence = evidence, Reason = reason, EvidenceSource = source, CapturedAt = at ?? s.CollectedAt, Correlation = correlation, Facts = facts };

        if (SkoleNaervaerAltinnExecutor.AltinnSteps.Contains(step.StepId))
        {
            if (s.SourceRun?.Steps.FirstOrDefault(item => item.StepId == step.StepId) is not { } recorded)
                return R(JourneyStepState.NotAssessed, "", s.Altinn.Detail, s.Altinn.DisplayName);
            var state = recorded.State switch
            {
                JourneyStepState.Verified or JourneyStepState.Observed => JourneyStepState.Observed,
                JourneyStepState.NotVerified or JourneyStepState.UnexpectedResult => JourneyStepState.NotVerified,
                _ => JourneyStepState.NotAssessed,
            };
            return R(state, $"Recorded in run {s.SourceRun.RunId.ToString("N")[..8]}: {recorded.State} — {recorded.Evidence}".Trim(),
                "Read from BirkNext's run history; the step was not repeated. Recorded is not re-verified now.", s.Altinn.DisplayName, recorded.CapturedAt, recorded.Correlation);
        }

        var last = Last(s);
        var mmFacts = last is null ? [] : MmFacts(last, s.MmEvents.Count);
        switch (step.StepId)
        {
            case "mu-receive":
                return R(JourneyStepState.NotAssessed, "", "MU has no evidence source in BirkNext; MM's pick-up of the queue message is the earliest downstream evidence.", "—");
            case "mu-queue":
                return last is null ? Missing(step, s.Mm, s.CollectedAt)
                    : R(JourneyStepState.Observed, $"MM processed queue message {last.KoeMeldingId ?? "(id not recorded)"} for this MU message id.", "", s.Mm.DisplayName, s.MmEvents[0].At, last.KoeMeldingId, mmFacts);
            case "mm-retrieve" or "mm-decrypt" or "mm-xsd" or "mm-map":
            {
                if (last is null) return Missing(step, s.Mm, s.CollectedAt);
                var failedAt = last.Aarsak switch
                {
                    "InnholdMangler" => "mm-retrieve",
                    "KrypteringsdataMangler" or "DekrypteringFeilet" => "mm-decrypt",
                    "SkjemaBrudd" or "UstoettetVersjon" or "UregistrertNavnerom" => "mm-xsd",
                    "MappingFeil" => "mm-map",
                    _ => null,
                };
                string[] order = ["mm-retrieve", "mm-decrypt", "mm-xsd", "mm-map"];
                if (failedAt is not null && last.Utfall == "Avvist")
                {
                    var here = Array.IndexOf(order, step.StepId) - Array.IndexOf(order, failedAt);
                    return here < 0 ? R(JourneyStepState.Observed, "Passed before MM's rejection (MM checks in this order).", "", s.Mm.DisplayName, last.At, last.KoeMeldingId, mmFacts)
                        : here == 0 ? R(JourneyStepState.Observed, $"MM rejected the message here: {last.Aarsak}.", "", s.Mm.DisplayName, last.At, last.KoeMeldingId, mmFacts)
                        : R(JourneyStepState.NotAssessed, "", "Not reached: MM rejected the message at an earlier check.", s.Mm.DisplayName);
                }
                return Forwarded(last)
                    ? R(JourneyStepState.Observed, "Implied: MM forwards to Skoletjenesten only after retrieving, decrypting, validating and mapping the message.", "", s.Mm.DisplayName, last.At, last.KoeMeldingId, mmFacts)
                    : R(JourneyStepState.NotAssessed, "", $"MM's latest outcome is {last.Utfall}; it does not show this check.", s.Mm.DisplayName, last.At, last.KoeMeldingId, mmFacts);
            }
            case "report-receive":
            {
                if (s.SkoleEvents.LastOrDefault(item => item.Utfall is not null) is { } answer)
                    return R(JourneyStepState.Observed, $"Skoletjenesten logged its answer: {answer.Utfall}{(answer.Aarsakskode is { } code and not "-" ? $" ({code})" : "")}.",
                        "HTTP answer only; it does not show the internal outcome.", s.Skole.DisplayName, answer.At, null, SkoleFacts(answer));
                if (last is not null && Forwarded(last))
                    return R(JourneyStepState.Observed, $"MM recorded Skoletjenesten's answer: {(last.Utfall == "Mottatt" ? "200 Mottatt" : $"rejected ({last.Aarsak})")}.",
                        "Observed by MM, not by Skoletjenesten's own telemetry.", s.Mm.DisplayName, last.At, last.KoeMeldingId, mmFacts);
                return Missing(step, s.Skole.State == MessageEvidenceProviderState.Available ? s.Mm : s.Skole, s.CollectedAt);
            }
            case "report-state" when s.SkoleEvents.Any(item => item.AcknowledgedWithoutEffect):
                return R(JourneyStepState.Observed, "Skoletjenesten logged the message as an addition (Tillegg) acknowledged without effect.", "", s.Skole.DisplayName,
                    s.SkoleEvents.First(item => item.AcknowledgedWithoutEffect).At);
            default:
                return R(JourneyStepState.NotAssessed, "", step.StepId.StartsWith("report-", StringComparison.Ordinal) ? InternalOutcome : "No evidence source for this step.", "—");
        }
    }

    private static JourneyStepResult Missing(JourneyStepDefinition step, MessageEvidenceProviderStatus provider, DateTimeOffset at) => new()
    {
        StepId = step.StepId, Label = step.Label, CapturedAt = at, EvidenceSource = provider.DisplayName, Reason = provider.Detail,
        State = provider.State is MessageEvidenceProviderState.AccessDenied or MessageEvidenceProviderState.Unavailable ? JourneyStepState.Unavailable : JourneyStepState.NotAssessed,
    };

    private static JourneyEvidenceFact[] MmFacts(MmOutcomeEvent e, int count) =>
    [
        new("MM outcome", e.Utfall), new("Reason", e.Aarsak ?? "—"), new("Attempt", e.Forsoek?.ToString(CultureInfo.InvariantCulture) ?? "—"),
        new("Receipt", e.Kvittering ?? "—"), new("Outcome events", count.ToString(CultureInfo.InvariantCulture)),
    ];

    private static JourneyEvidenceFact[] SkoleFacts(SkoleIntakeEvent e) => [new("Answer", e.Utfall ?? "—"), new("Reason code", e.Aarsakskode ?? "—"), new("Period", e.Periode ?? "—")];

    // ── Record ──────────────────────────────────────────────────────────────────────────────────────────────────────

    public static MessageVerificationRecord Record(MessageEvidenceSnapshot? s, string expected, DateTimeOffset now)
    {
        if (s is null)
            return new MessageVerificationRecord
            {
                RuleVersion = RuleVersion, ExpectedScenario = expected, EvaluatedAt = now, Verdict = MessageVerificationVerdict.NotRun,
                VerdictReason = "No evidence was collected: the run ended before any step was observed.",
            };
        var input = s.Input;
        var providers = new List<MessageEvidenceProviderStatus>
        {
            s.Altinn, s.Mm, s.Skole,
            new(SkoleNaervaerMessageEvidence.CoverageProvider, "Skoletjenesten GraphQL (coverage and follow-up)", "Skoletjenesten", MessageEvidenceProviderState.Blocked,
                "BirkNext has no approved GraphQL client and role (Skole:SeDekningsstatistikk for coverage; Skole:SeOppfolgingsliste for follow-up lists). Coverage is aggregate and never a single message's outcome.",
                "Skoletjenesten GraphQL schema (source-reviewed).", s.CollectedAt, "Skole team"),
            new(SkoleNaervaerMessageEvidence.BufdataProvider, "Bufdata delivery", "Bufdata", MessageEvidenceProviderState.NotApplicable,
                "No Bufdata evidence provider exists: Skoletjenesten prepares delivery rows (Klargjort) but writes no dataset file and has no receipt surface yet (ADR-028).",
                "Skoletjenesten source review.", s.CollectedAt, "Skole team; Bufdata team"),
        };
        var assertions = Assertions(s);
        var verdict = Verdict(assertions, out var reason);
        return new MessageVerificationRecord
        {
            RuleVersion = RuleVersion, ExpectedScenario = input.Expected, ExpectedScenarioName = input.ExpectedName, WindowHours = input.WindowHours, EvaluatedAt = now,
            TargetKey = input.MessageId is { } id ? $"message:{id:D}" : input.SourceRun is { } run ? $"run:{run:N}" : $"instance:{input.InstanceReference}",
            TargetLabel = input.MessageId is { } message ? $"MU message {message:D}" : input.SourceRun is { } source ? $"Run {source.ToString("N")[..8]}" : $"Altinn instance {input.InstanceReference}",
            SourceRunId = s.SourceRun?.RunId ?? input.SourceRun, AltinnInstanceReference = input.InstanceReference ?? Instance(s.SourceRun), MessageId = input.MessageId?.ToString("D"),
            Providers = providers, Observations = Observations(s), Correlations = Correlations(s), Assertions = assertions, MissingEvidence = MissingEvidence(s),
            Verdict = verdict, VerdictReason = reason,
        };
    }

    private static string? Instance(IntegrationJourneyRun? run) =>
        run?.Steps.Select(step => step.Correlation).FirstOrDefault(value => value?.StartsWith("inst-", StringComparison.Ordinal) == true);

    private static IReadOnlyList<MessageEvidenceObservation> Observations(MessageEvidenceSnapshot s)
    {
        var result = new List<MessageEvidenceObservation>();
        if (s.SourceRun is { } run)
        {
            var steps = run.Steps.ToDictionary(step => step.StepId);
            JourneyStepResult? At(string id) => steps.GetValueOrDefault(id);
            var (stage, summary) = At(SkoleNaervaerAltinnExecutor.ReceiptStep)?.State == JourneyStepState.Verified ? (MessageEvidenceStage.Confirmed, "Altinn confirmed the instance completed (receipt read back).")
                : At(SkoleNaervaerAltinnExecutor.SubmitStep)?.State == JourneyStepState.Verified ? (MessageEvidenceStage.Submitted, "Submitted in Altinn (process ended); not confirmed by a receipt.")
                : At(SkoleNaervaerAltinnExecutor.DataStep)?.State == JourneyStepState.Verified ? (MessageEvidenceStage.Generated, "Form data stored in Altinn; the instance was not submitted.")
                : (MessageEvidenceStage.NotObserved, $"The recorded run did not store form data ({run.OverallState}).");
            result.Add(new("Altinn", stage, summary, SkoleNaervaerMessageEvidence.AltinnProvider, run.CompletedAt ?? run.StartedAt, "BirkNext run id (exact)",
                [new("Run", run.RunId.ToString("N")[..8]), new("Scenario", run.ScenarioName), new("Run result", run.OverallState.ToString()), new("Instance", Instance(run) ?? "—")],
                "Altinn confirmation is not MU receipt, MM processing or Skoletjenesten acceptance."));
        }
        else result.Add(new("Altinn", Stage(s.Altinn.State), s.Altinn.Detail, SkoleNaervaerMessageEvidence.AltinnProvider, null, "—", [], "Nothing is reconstructed without a recorded run."));

        foreach (var e in s.MmEvents)
            result.Add(new("MM", MessageEvidenceStage.MmProcessingObserved, $"MM outcome {e.Utfall}{(e.Aarsak is { } reason ? $" ({reason})" : "")}, attempt {e.Forsoek?.ToString(CultureInfo.InvariantCulture) ?? "?"}.",
                SkoleNaervaerMessageEvidence.MmProvider, e.At, "MuMeldingId (exact)", MmFacts(e, s.MmEvents.Count),
                e.Utfall == "Mottatt" ? "Mottatt means Skoletjenesten answered 200 Mottatt; it does not show the internal outcome." : ""));
        if (s.MmEvents.Count == 0) result.Add(new("MM", Stage(s.Mm.State), s.Mm.Detail, SkoleNaervaerMessageEvidence.MmProvider, null, "MuMeldingId", [], "Not observed is not failed."));
        foreach (var e in s.SkoleEvents)
            result.Add(new("Skoletjenesten", e.AcknowledgedWithoutEffect ? MessageEvidenceStage.SkoleProcessingObserved : MessageEvidenceStage.SkoleHttpResponseObserved,
                e.AcknowledgedWithoutEffect ? "Tillegg acknowledged without effect." : $"Answered {e.Utfall}{(e.Aarsakskode is { } code and not "-" ? $" ({code})" : "")}.",
                SkoleNaervaerMessageEvidence.SkoleProvider, e.At, "MeldingId (exact)", SkoleFacts(e), "An HTTP answer is not the internal outcome."));
        if (s.SkoleEvents.Count == 0)
            result.Add(new("Skoletjenesten", Stage(s.Skole.State), s.Skole.Detail, SkoleNaervaerMessageEvidence.SkoleProvider, null, "MeldingId", [], "Not observed is not failed."));
        result.Add(new("Period and coverage", MessageEvidenceStage.NotEvaluated, "Coverage and period status need an approved GraphQL client.", SkoleNaervaerMessageEvidence.CoverageProvider, null, "—", [],
            "Coverage is aggregate; it never shows one message's outcome."));
        result.Add(new("Bufdata", MessageEvidenceStage.NotEvaluated, "No Bufdata evidence provider exists.", SkoleNaervaerMessageEvidence.BufdataProvider, null, "—", [],
            "Prepared delivery rows are not a Bufdata receipt."));
        return result;
    }

    private static MessageEvidenceStage Stage(MessageEvidenceProviderState state) => state switch
    {
        MessageEvidenceProviderState.AccessDenied => MessageEvidenceStage.AccessDenied,
        MessageEvidenceProviderState.Unavailable => MessageEvidenceStage.Unavailable,
        MessageEvidenceProviderState.QueryCompletedNoEvidence => MessageEvidenceStage.NotObserved,
        _ => MessageEvidenceStage.NotEvaluated,
    };

    private static IReadOnlyList<MessageCorrelation> Correlations(MessageEvidenceSnapshot s)
    {
        var input = s.Input;
        var result = new List<MessageCorrelation>();
        if (s.SourceRun is { } run && Instance(run) is { } reference)
            result.Add(new("BirkNext run id", run.RunId.ToString("N")[..8], "Altinn instance reference", reference, SkoleNaervaerMessageEvidence.AltinnProvider, run.CompletedAt,
                "Recorded by the run itself", MessageCorrelationCertainty.ExactIdentifierMatch, "Only the short reference is kept."));
        if (input.InstanceReference is { } given)
            result.Add(new("Altinn instance reference", given, "BirkNext run id", s.SourceRun?.RunId.ToString("N")[..8], SkoleNaervaerMessageEvidence.AltinnProvider, s.CollectedAt,
                "A recorded run whose steps carry this reference", s.SourceRun is null ? MessageCorrelationCertainty.Uncorrelated : MessageCorrelationCertainty.ExactIdentifierMatch,
                s.SourceRunMatches > 1 ? $"{s.SourceRunMatches} runs reference it; the most recent is used." : ""));
        if (input.MessageId is { } id)
        {
            var text = id.ToString("D");
            result.Add(new("Altinn instance", s.SourceRun is null ? "—" : Instance(s.SourceRun) ?? "—", "MU message id", text, "—", null,
                s.SourceRun is null ? "No run or instance was given" : "Supplied by the tester together with the run or instance",
                s.SourceRun is null ? MessageCorrelationCertainty.Uncorrelated : MessageCorrelationCertainty.TesterAsserted,
                "Altinn → MU has no identifier BirkNext can see: Meldingshode/Id is set by the Altinn app and not recorded by BirkNext. Not verified."));
            if (s.MmEvents.FirstOrDefault(item => item.KoeMeldingId is not null) is { } queued)
                result.Add(new("MU message id", text, "MM queue message id (KoeMeldingId)", queued.KoeMeldingId, SkoleNaervaerMessageEvidence.MmProvider, queued.At,
                    "Both ids on the same MM outcome event", MessageCorrelationCertainty.ExplicitMappingObserved, ""));
            result.Add(new("MU message id", text, "Skoletjenesten MeldingId (X-Correlation-Id)", s.SkoleEvents.Count > 0 ? text : null,
                s.SkoleEvents.Count > 0 ? SkoleNaervaerMessageEvidence.SkoleProvider : SkoleNaervaerMessageEvidence.MmProvider, s.SkoleEvents.FirstOrDefault()?.At,
                "Same value: MM sends Meldingshode/Id as meldingId and X-Correlation-Id (MM source)",
                s.SkoleEvents.Count > 0 ? MessageCorrelationCertainty.ExactIdentifierMatch : MessageCorrelationCertainty.Uncorrelated,
                s.SkoleEvents.Count > 0 ? "" : "No Skoletjenesten record for this id was observed."));
        }
        return result;
    }

    private static IReadOnlyList<MessageAssertion> Assertions(MessageEvidenceSnapshot s)
    {
        var events = s.MmEvents;
        var last = events.LastOrDefault();
        var skoleAnswer = s.SkoleEvents.LastOrDefault(item => item.Utfall is not null);
        MessageVerificationVerdict Absent(MessageEvidenceProviderStatus provider) =>
            provider.State is MessageEvidenceProviderState.AccessDenied or MessageEvidenceProviderState.Blocked or MessageEvidenceProviderState.NotConfigured
                ? MessageVerificationVerdict.Blocked : MessageVerificationVerdict.NotEvaluated;
        string Observed(MessageEvidenceProviderStatus provider) => $"No evidence ({provider.State}): {provider.Detail}";
        const string mmBasis = "MM outcome event";
        var result = new List<MessageAssertion>();

        if (s.SourceRun is { } run)
        {
            var receipt = run.Steps.FirstOrDefault(step => step.StepId == SkoleNaervaerAltinnExecutor.ReceiptStep);
            result.Add(new("altinn-confirmed", "Altinn confirmed the submission", "Receipt read back: instance completed", receipt is null ? "Not recorded" : $"{receipt.State}: {receipt.Evidence}",
                receipt?.State == JourneyStepState.Verified ? MessageVerificationVerdict.Passed
                    : receipt?.State is JourneyStepState.NotVerified or JourneyStepState.UnexpectedResult ? MessageVerificationVerdict.Failed : MessageVerificationVerdict.NotEvaluated,
                "Recorded BirkNext run (Altinn TT02 App API answers)"));
        }

        MessageAssertion Delivered() => last is null
            ? new("mm-delivered", "MM delivered the report to Skoletjenesten", "MM outcome Mottatt", Observed(s.Mm), Absent(s.Mm), mmBasis)
            : new("mm-delivered", "MM delivered the report to Skoletjenesten", "MM outcome Mottatt", $"{last.Utfall}{(last.Aarsak is { } r ? $" ({r})" : "")}",
                events.Any(item => item.Utfall == "Mottatt") ? MessageVerificationVerdict.Passed
                    : last.Utfall is "Avvist" or "Dodbrev" ? MessageVerificationVerdict.Failed : MessageVerificationVerdict.NotEvaluated, mmBasis);
        MessageAssertion Answered200()
        {
            if (skoleAnswer is not null)
                return new("skole-200", "Skoletjenesten answered 200 Mottatt", "Mottatt", skoleAnswer.Utfall!, skoleAnswer.Utfall == "Mottatt" ? MessageVerificationVerdict.Passed : MessageVerificationVerdict.Failed,
                    "Skoletjenesten intake log");
            if (events.Any(item => item.Utfall == "Mottatt"))
                return new("skole-200", "Skoletjenesten answered 200 Mottatt", "Mottatt", "MM recorded Mottatt (MM completes only on 200 utfall Mottatt)", MessageVerificationVerdict.Passed, mmBasis);
            return new("skole-200", "Skoletjenesten answered 200 Mottatt", "Mottatt", Observed(s.Skole), Absent(s.Skole), "Skoletjenesten intake log or MM outcome event");
        }
        MessageAssertion NotVerifiable(string id, string label, string expected, string why) => new(id, label, expected, why, MessageVerificationVerdict.NotEvaluated, "No read surface");
        MessageAssertion Rejected(string id, string label, Func<MmOutcomeEvent, bool> expectedReason, string expected)
        {
            var rejection = events.LastOrDefault(item => item.Utfall == "Avvist");
            if (rejection is not null)
                return new(id, label, expected, $"Avvist ({rejection.Aarsak ?? "no reason"})", expectedReason(rejection) ? MessageVerificationVerdict.Passed : MessageVerificationVerdict.Failed, mmBasis);
            if (skoleAnswer is { Utfall: "Avvist" } skoleRejection && id != "mm-own-rejection")
                return new(id, label, expected, $"Avvist ({skoleRejection.Aarsakskode})",
                    expectedReason(new MmOutcomeEvent(skoleRejection.At, "Avvist", skoleRejection.Aarsakskode, null, null, null, null, null)) ? MessageVerificationVerdict.Passed : MessageVerificationVerdict.Failed,
                    "Skoletjenesten intake log");
            if (events.Any(item => item.Utfall == "Mottatt"))
                return new(id, label, expected, "Mottatt (not rejected)", MessageVerificationVerdict.Failed, mmBasis);
            return new(id, label, expected, Observed(s.Mm), Absent(s.Mm), mmBasis);
        }

        switch (s.Input.Expected)
        {
            case "valid-report":
                result.AddRange([Delivered(), Answered200(), NotVerifiable("skole-accepted", "Skoletjenesten accepted the report (Godkjent)", "Godkjent", InternalOutcome)]);
                result.Add(last?.Kvittering is { } receiptState
                    ? new("receipt-sent", "Application receipt sent to MU", "Sendt", receiptState, receiptState == "Sendt" ? MessageVerificationVerdict.Passed
                        : receiptState is "Feilet" or "IkkeSendt" ? MessageVerificationVerdict.Failed : MessageVerificationVerdict.NotEvaluated, mmBasis)
                    : new("receipt-sent", "Application receipt sent to MU", "Sendt", last is null ? Observed(s.Mm) : "Not recorded on the outcome event", last is null ? Absent(s.Mm) : MessageVerificationVerdict.NotEvaluated, mmBasis));
                break;
            case "person-not-found":
                result.AddRange([Delivered(), Answered200(), NotVerifiable("skole-held", "Report held until the child is known (Holdt)", "Holdt",
                    "Skoletjenesten answers 200 Mottatt for accepted and held reports alike; whether this one is held is stored only in its database.")]);
                break;
            case "duplicate-report":
            {
                var deliveries = events.Where(item => item.Utfall == "Mottatt").Select(item => item.KoeMeldingId).Distinct().Count();
                result.Add(new("repeat-answered", "Both deliveries answered with the stored outcome", "Two or more MM outcomes Mottatt for this message id",
                    events.Count == 0 ? Observed(s.Mm) : $"{deliveries} distinct delivery(ies) answered Mottatt",
                    deliveries >= 2 ? MessageVerificationVerdict.Passed : events.Count == 0 ? Absent(s.Mm) : MessageVerificationVerdict.NotEvaluated, mmBasis));
                result.Add(NotVerifiable("single-record", "Only one report is stored", "Exactly one stored report",
                    "Repeated answers do not show how many records exist; that is stored only in Skoletjenesten's database."));
                break;
            }
            case "correction":
                result.AddRange([Delivered(), NotVerifiable("effective-version", "The corrected version is the effective one", "Newest version effective",
                    "Which version is current is stored only in Skoletjenesten's database; a delivered correction does not show it.")]);
                break;
            case "invalid-xsd":
                result.Add(Rejected("mm-xsd-rejection", "MM rejected the report at XSD validation", item => item.Aarsak is "SkjemaBrudd" or "UstoettetVersjon", "Avvist (SkjemaBrudd or UstoettetVersjon)"));
                result.Add(s.SkoleEvents.Count > 0
                    ? new("not-forwarded", "The report was not forwarded to Skoletjenesten", "No Skoletjenesten record", "Skoletjenesten logged this message id", MessageVerificationVerdict.Failed, "Skoletjenesten intake log")
                    : result[^1].Verdict == MessageVerificationVerdict.Passed
                        ? new("not-forwarded", "The report was not forwarded to Skoletjenesten", "No Skoletjenesten record", "MM rejected before its call to Skoletjenesten (MM checks XSD first)", MessageVerificationVerdict.Passed, mmBasis)
                        : new("not-forwarded", "The report was not forwarded to Skoletjenesten", "No Skoletjenesten record", "Not shown by the evidence", MessageVerificationVerdict.NotEvaluated, mmBasis));
                break;
            case "temporary-failure":
            {
                var temporary = events.FirstOrDefault(item => item.Utfall == "Midlertidig");
                result.Add(new("temporary-observed", "A temporary failure was observed", "MM outcome Midlertidig", temporary is null ? (events.Count == 0 ? Observed(s.Mm) : "No Midlertidig outcome") : $"Midlertidig ({temporary.Aarsak ?? "no reason"})",
                    temporary is not null ? MessageVerificationVerdict.Passed : events.Count == 0 ? Absent(s.Mm) : MessageVerificationVerdict.NotEvaluated, mmBasis));
                var after = temporary is null ? null : events.FirstOrDefault(item => item.At >= temporary.At && item.Utfall is "Mottatt" or "Dodbrev");
                result.Add(new("retried-completed", "Retried and then delivered", "A later MM outcome Mottatt", after is null ? "No later final outcome in the window" : after.Utfall,
                    after?.Utfall == "Mottatt" ? MessageVerificationVerdict.Passed : after?.Utfall == "Dodbrev" ? MessageVerificationVerdict.Failed : MessageVerificationVerdict.NotEvaluated, mmBasis));
                break;
            }
            case "mm-rejection":
                result.Add(Rejected("mm-own-rejection", "MM rejected the message with its own reason", item => item.Aarsak is { } reason && MmOwnReasons.Contains(reason), $"Avvist ({string.Join(", ", MmOwnReasons)})"));
                break;
            case "skole-rejection":
                result.Add(Rejected("skole-rejection", "Skoletjenesten rejected the report with a reason code", item => item.Aarsak is { } reason && SkoleReasons.Contains(reason), "Avvist with a published reason code (422)"));
                break;
            case "period-locked":
                result.Add(Rejected("outside-window", "Rejected as outside the validity window", item => item.Aarsak == "UTENFOR_GYLDIGHETSVINDU", "Avvist (UTENFOR_GYLDIGHETSVINDU)"));
                result.Add(new("period-status", "The period is locked", "Locked", "Period status needs an approved GraphQL client", MessageVerificationVerdict.Blocked, "Skoletjenesten GraphQL"));
                break;
            case "coverage":
                result.Add(new("coverage", "Coverage for the period", "Expected = received + rejected only + missing", "No approved GraphQL client and role", MessageVerificationVerdict.Blocked, "Skoletjenesten GraphQL"));
                break;
            case "bufdata-delivery":
                result.Add(new("bufdata-received", "Bufdata received the delivery", "Receipt from Bufdata",
                    "No Bufdata evidence provider; Skoletjenesten prepares delivery rows (Klargjort) only", MessageVerificationVerdict.NotEvaluated, "No read surface"));
                break;
        }
        return result;
    }

    /// <summary>Failed if any assertion failed; Passed only if every assertion passed; Blocked if access is missing; otherwise Not evaluated.</summary>
    public static MessageVerificationVerdict Verdict(IReadOnlyList<MessageAssertion> assertions, out string reason)
    {
        if (assertions.Count == 0) { reason = "No assertion applies."; return MessageVerificationVerdict.NotEvaluated; }
        if (assertions.FirstOrDefault(item => item.Verdict == MessageVerificationVerdict.Failed) is { } failed) { reason = $"Failed: {failed.Label} — observed {failed.Observed}."; return MessageVerificationVerdict.Failed; }
        if (assertions.All(item => item.Verdict == MessageVerificationVerdict.Passed)) { reason = "Every assertion is proven by the evidence."; return MessageVerificationVerdict.Passed; }
        var open = assertions.Where(item => item.Verdict != MessageVerificationVerdict.Passed).Select(item => item.Label).ToArray();
        if (assertions.Any(item => item.Verdict == MessageVerificationVerdict.Blocked)) { reason = $"Blocked: evidence access is missing for {string.Join("; ", open)}."; return MessageVerificationVerdict.Blocked; }
        reason = $"Not evaluated: the evidence does not prove {string.Join("; ", open)}.";
        return MessageVerificationVerdict.NotEvaluated;
    }

    private static IReadOnlyList<MessageMissingEvidence> MissingEvidence(MessageEvidenceSnapshot s)
    {
        var result = new List<MessageMissingEvidence>();
        if (s.Input.MessageId is null)
            result.Add(new("MM and Skoletjenesten evidence", "No MU message id: an Altinn run does not record Meldingshode/Id.", "The MU message id (Meldingshode/Id) of the message",
                "BirkNext operator; Altinn app owner", "Enter the MU message id if it is known from MU or MM operations."));
        foreach (var provider in new[] { s.Mm, s.Skole }.Where(item => item.State is MessageEvidenceProviderState.NotConfigured or MessageEvidenceProviderState.Blocked or MessageEvidenceProviderState.AccessDenied))
            result.Add(new(provider.DisplayName, provider.Detail,
                "An enabled IQR integration for the component, enrolled in the trusted environment, on a platform with its Log Analytics workspace or Application Insights resource, and read access for BirkNext's backend identity.",
                provider.ResponsibleTeam, provider.State == MessageEvidenceProviderState.AccessDenied ? "Grant BirkNext's backend identity read access to the telemetry source." : "Configure and enrol the component's integration and telemetry source."));
        result.Add(new("Skoletjenesten internal outcome", InternalOutcome, "A read contract for report state", "Skole team", "Agree a read-only report status surface."));
        result.Add(new("Period and coverage", "No approved GraphQL client and role.", "Skole:SeDekningsstatistikk / Skole:SeOppfolgingsliste for BirkNext", "Skole team", "Agree a read-only coverage client."));
        result.Add(new("Bufdata receipt", "Only prepared delivery rows exist (ADR-028 pending).", "A Bufdata receipt or delivery status surface", "Skole team; Bufdata team", "Agree a receipt surface."));
        return result;
    }
}
