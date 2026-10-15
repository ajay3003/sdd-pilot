using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using BirkNext.Api.Services.AltinnTesting;
using BirkNext.Api.Services.Integrations;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.IntegrationJourneys.Packs.SkoleNaervaer;

/// <summary>
/// The fields a Skolenærvær report submission fills, at the element paths of the Altinn form model
/// (bufdir.barnevern.tiltak.skolenaervaer v2.0.0.altinn, read from the buf/skolenaervaer app's public TT02 schema on 2026-10-12). They are
/// still only expectations: the contract service checks each path and type against the app's schema at every evaluation, and execution
/// stays Not ready until all match. The MU message (.flat) nests the hours differently; that is not the form.
/// </summary>
public sealed class SkoleNaervaerAltinnForm : IAltinnFormBinding
{
    public const string ChildIdentifier = "childIdentifier";
    public const string PeriodFrom = "periodFrom";
    public const string PeriodTo = "periodTo";
    public const string PlannedHours = "plannedHours";
    public const string ActualHours = "actualHours";

    public IReadOnlyList<AltinnFormFieldExpectation> Fields { get; } =
    [
        new(ChildIdentifier, "Barnets fødselsnummer", "Klient.Identifikator.Fodselsnummer", "string"),
        new(PeriodFrom, "Dato fra", "Periode.StartDato", "string", ["date", "date-time"]),
        new(PeriodTo, "Dato til", "Periode.SluttDato", "string", ["date", "date-time"]),
        new(PlannedHours, "Antall timer etter planen for perioden", "AntallTimerEtterPlanen", "number"),
        new(ActualHours, "Antall timer faktisk nærvær for perioden", "AntallTimerFaktiskNaervaer", "number"),
    ];

    public string BindingBasis => "Expected paths come from the buf/skolenaervaer form model (v2.0.0.altinn); they are used only when the app's schema has them.";
}

/// <summary>
/// Synthetic Norwegian person identifiers. Only the Test-Norge synthetic form is accepted — month + 80, valid mod-11 control digits — so a
/// real fødselsnummer, D-number or H-number is refused. The synthetic form exists for both fødselsnummer (day 01–31) and D-number (day + 40,
/// 41–71); with month + 80 neither can be a real person. A syntactically valid synthetic identifier is not a registered test person: whether
/// a child is known to Person is the tester's declaration, never inferred from the number.
/// </summary>
public static class SyntheticPersonIdentifier
{
    private static readonly int[] K1 = [3, 7, 6, 1, 8, 9, 4, 5, 2];
    private static readonly int[] K2 = [5, 4, 3, 2, 7, 6, 5, 4, 3, 2];

    public static bool IsSynthetic(string? value)
    {
        if (value is not { Length: 11 } || !value.All(char.IsAsciiDigit)) return false;
        var day = int.Parse(value[..2], CultureInfo.InvariantCulture);
        var month = int.Parse(value[2..4], CultureInfo.InvariantCulture);
        return day is (>= 1 and <= 31) or (>= 41 and <= 71) && month is >= 81 and <= 92 && Control(value, K1, 9) == value[9] - '0' && Control(value, K2, 10) == value[10] - '0';
    }

    /// <summary>A new synthetic identifier (born 2008–2019, school age). Not looked up anywhere; it is unregistered only by assumption.</summary>
    public static string Generate()
    {
        while (true)
        {
            var day = RandomNumberGenerator.GetInt32(1, 29);
            var month = RandomNumberGenerator.GetInt32(1, 13) + 80;
            var year = RandomNumberGenerator.GetInt32(8, 20);
            var individual = RandomNumberGenerator.GetInt32(500, 1000);
            var nine = $"{day:00}{month:00}{year:00}{individual:000}";
            var k1 = Control(nine, K1, 9);
            if (k1 < 0) continue;
            var k2 = Control(nine + k1, K2, 10);
            if (k2 < 0) continue;
            return $"{nine}{k1}{k2}";
        }
    }

    /// <summary>A short, non-reversible-for-display reference ("#1a2b3c4d") so history can show which identifier a run used without it.</summary>
    public static string Reference(string value) => "#" + Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(value)))[..8].ToLowerInvariant();

    private static int Control(string digits, int[] weights, int count)
    {
        var sum = 0;
        for (var i = 0; i < count; i++) sum += (digits[i] - '0') * weights[i];
        var control = 11 - sum % 11;
        return control == 11 ? 0 : control == 10 ? -1 : control;
    }
}

/// <summary>Typed, validated report inputs (in memory for the run only). Business rules belong to Skolenærværsrapport; only form-level checks here.</summary>
public sealed record SkoleNaervaerReportInput(string IdentitySource, string ChildIdentifier, DateOnly From, DateOnly To, double PlannedHours, double ActualHours)
{
    public const string IdentitySourceKey = "identitySource";
    /// <summary>The Altinn reporter: an approved synthetic TEST reporter alias (unattended) or the interactive fallback. Never an identifier.</summary>
    public const string ReporterKey = "reporter";
    public const string PriorReportKey = "priorReport";
    public const string Registered = "registered";
    public const string Supplied = "supplied";
    public const string Generated = "generated";
    /// <summary>The child comes from the approved test data, chosen by alias (<see cref="ChildKey"/>); its identifier is resolved in the backend.</summary>
    public const string Approved = "approved";
    public const string ChildKey = "child";

    /// <summary>The reporter must be one of the scenario's offered options (the configured aliases and, when offered, the interactive fallback).</summary>
    public static (string Value, string Summary) PrepareReporter(JourneyScenarioDescriptor scenario, IReadOnlyDictionary<string, string> inputs, List<string> errors)
    {
        var value = inputs.GetValueOrDefault(ReporterKey)?.Trim() ?? "";
        var option = scenario.Inputs?.FirstOrDefault(item => item.Key == ReporterKey)?.Options?.FirstOrDefault(item => item.Value == value);
        if (option is null) { errors.Add("The reporter is not one of the approved synthetic TEST reporters for this scenario."); return ("", ""); }
        return (value, value == AltinnTestingPolicy.InteractiveReporter
            ? "Reporter: the tester's own interactive ID-porten TEST sign-in (fallback)."
            : $"Reporter: synthetic TEST reporter alias {value} (unattended ID-porten TEST; the TestID identifier is not recorded).");
    }

    /// <param name="approvedChild">Resolves an approved child's alias to its identifier for this scenario (null when not approved). Server-side only.</param>
    public static JourneyInputPreparation Prepare(JourneyScenarioDescriptor scenario, IReadOnlyDictionary<string, string> inputs, Func<string, string?>? approvedChild = null)
    {
        var errors = new List<string>();
        var reporter = PrepareReporter(scenario, inputs, errors);
        var source = inputs.GetValueOrDefault(IdentitySourceKey)?.Trim() ?? "";
        var allowed = scenario.Inputs?.FirstOrDefault(item => item.Key == IdentitySourceKey)?.Options?.Select(item => item.Value).ToArray() ?? [];
        if (!allowed.Contains(source, StringComparer.Ordinal)) errors.Add($"The identity source \"{source}\" is not allowed for this scenario.");

        var identifier = inputs.GetValueOrDefault(SkoleNaervaerAltinnForm.ChildIdentifier)?.Trim() ?? "";
        var childAlias = "";
        if (source == Approved)
        {
            childAlias = inputs.GetValueOrDefault(ChildKey)?.Trim() ?? "";
            var offered = scenario.Inputs?.FirstOrDefault(item => item.Key == ChildKey)?.Options?.Any(item => item.Value == childAlias) == true;
            if (identifier.Length > 0) errors.Add("Leave the child identifier empty when an approved synthetic child is used.");
            if (!offered) errors.Add("Choose one of the approved synthetic children for this scenario.");
            identifier = offered ? approvedChild?.Invoke(childAlias) ?? "" : "";
            if (offered && !SyntheticPersonIdentifier.IsSynthetic(identifier)) errors.Add("The approved synthetic child is not available in the approved test data.");
        }
        else if (source == Generated)
        {
            if (identifier.Length > 0) errors.Add("Leave the child identifier empty when BirkNext generates one.");
            identifier = SyntheticPersonIdentifier.Generate();
        }
        else if (!SyntheticPersonIdentifier.IsSynthetic(identifier))
            errors.Add("The child identifier must be a synthetic test identifier (11 digits, month + 80, valid control digits). Real identifiers are never accepted.");

        static DateOnly? Date(string? value) => DateOnly.TryParseExact(value?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
        static double? Hours(string? value) => double.TryParse(value?.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var hours) && double.IsFinite(hours) ? hours : null;
        var from = Date(inputs.GetValueOrDefault(SkoleNaervaerAltinnForm.PeriodFrom));
        var to = Date(inputs.GetValueOrDefault(SkoleNaervaerAltinnForm.PeriodTo));
        var planned = Hours(inputs.GetValueOrDefault(SkoleNaervaerAltinnForm.PlannedHours));
        var actual = Hours(inputs.GetValueOrDefault(SkoleNaervaerAltinnForm.ActualHours));
        if (from is null) errors.Add("Dato fra must be a date (yyyy-MM-dd).");
        if (to is null) errors.Add("Dato til must be a date (yyyy-MM-dd).");
        if (from is { } f && to is { } t && f > t) errors.Add("Dato fra must be on or before Dato til.");
        if (planned is null or < 0) errors.Add("Antall timer etter planen must be a finite, non-negative number.");
        if (actual is null or < 0) errors.Add("Antall timer faktisk nærvær must be a finite, non-negative number.");
        if (scenario.Inputs?.Any(item => item.Key == PriorReportKey) == true && inputs.GetValueOrDefault(PriorReportKey) != "declared")
            errors.Add("Declare that an earlier report with an overlapping period exists for this child.");
        if (errors.Count > 0) return new JourneyInputPreparation(new Dictionary<string, string>(), errors, []);

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ReporterKey] = reporter.Value, [IdentitySourceKey] = source, [SkoleNaervaerAltinnForm.ChildIdentifier] = identifier, [ChildKey] = childAlias,
            [SkoleNaervaerAltinnForm.PeriodFrom] = from!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            [SkoleNaervaerAltinnForm.PeriodTo] = to!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            [SkoleNaervaerAltinnForm.PlannedHours] = planned!.Value.ToString("R", CultureInfo.InvariantCulture),
            [SkoleNaervaerAltinnForm.ActualHours] = actual!.Value.ToString("R", CultureInfo.InvariantCulture),
        };
        var declaration = source switch
        {
            Approved => $"approved synthetic child {childAlias} from the approved test data, declared {(scenario.ScenarioId == "person-not-found" ? "not registered" : "registered")} in Person (not verified by BirkNext)",
            Registered => "declared registered synthetic test person (known child; not verified by BirkNext)",
            Supplied => "supplied synthetic identifier, declared not registered in Person (unknown child; not verified by BirkNext)",
            _ => "generated synthetic identifier, assumed not registered in Person (unknown child; not verified by BirkNext)",
        };
        return new JourneyInputPreparation(values, [],
        [
            reporter.Summary,
            source == Approved ? $"Child: {declaration}." : $"Child: synthetic identifier {SyntheticPersonIdentifier.Reference(identifier)} — {declaration}.",
            $"Period: {values[SkoleNaervaerAltinnForm.PeriodFrom]} – {values[SkoleNaervaerAltinnForm.PeriodTo]}.",
            $"Planned hours: {values[SkoleNaervaerAltinnForm.PlannedHours]} · actual attendance hours: {values[SkoleNaervaerAltinnForm.ActualHours]}.",
            .. scenario.Inputs?.Any(item => item.Key == PriorReportKey) == true ? ["Tester declared an earlier overlapping report for this child (not verified by BirkNext)."] : Array.Empty<string>(),
        ]);
    }

    public static SkoleNaervaerReportInput Read(IReadOnlyDictionary<string, string> values) => new(
        values[IdentitySourceKey], values[SkoleNaervaerAltinnForm.ChildIdentifier],
        DateOnly.ParseExact(values[SkoleNaervaerAltinnForm.PeriodFrom], "yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateOnly.ParseExact(values[SkoleNaervaerAltinnForm.PeriodTo], "yyyy-MM-dd", CultureInfo.InvariantCulture),
        double.Parse(values[SkoleNaervaerAltinnForm.PlannedHours], CultureInfo.InvariantCulture),
        double.Parse(values[SkoleNaervaerAltinnForm.ActualHours], CultureInfo.InvariantCulture));
}

/// <summary>
/// Performs the six Altinn steps of Rapportmottak through the Altinn 3 App API with the run's reporter token — the unattended synthetic TEST
/// reporter (headless ID-porten TEST) by default, or the tester's interactive sign-in as a fallback:
/// authenticate (token obtained, party resolved) → create instance → store form data → validate → advance the process → read the receipt.
/// Each step is its own evidence; a step that does not progress stops the run (nothing is retried or resent). Every policy is re-checked
/// here before the first call. Only safe facts are recorded: HTTP status, a short instance reference, validation codes, process state and a
/// redacted payload evidence snapshot, redacted before it is recorded (Payload &amp; Mapping Flow) — never the token, the party id, the
/// unredacted payload (nor a hash of it) or the child's identifier. Altinn's receipt satisfies the Altinn steps only; MU, MM,
/// Skolenærværsrapport and Person steps stay Not assessed.
/// </summary>
public sealed class SkoleNaervaerAltinnExecutor(IServiceScopeFactory scopes, AltinnTestAuthenticator authenticator, AltinnAppApiClient altinn, TimeProvider clock) : IJourneyStepExecutor
{
    public const string AuthStep = "altinn-auth";
    internal static IReadOnlyList<string> AltinnSteps => Steps;
    public const string InstanceStep = "altinn-instance";
    public const string DataStep = "altinn-data";
    public const string ValidateStep = "altinn-validate";
    public const string SubmitStep = "altinn-submit";
    public const string ReceiptStep = "altinn-receipt";
    private static readonly string[] Steps = [AuthStep, InstanceStep, DataStep, ValidateStep, SubmitStep, ReceiptStep];
    private const string Source = "Altinn TT02 App API";
    internal const string ReporterIsChild = "The child must not be the reporter's own synthetic person: the reporter is the Altinn actor, the child is form data.";
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(15);

    private sealed class Session
    {
        /// <summary>The app's addresses from its current contract (deployed OpenAPI paths), kept inside the approved app.</summary>
        public required AltinnAppEndpoints Endpoints { get; init; }
        public required string Token { get; init; }
        public required string FormDataTypeId { get; init; }
        public required string ContentType { get; init; }
        public string? XmlRootElement { get; init; }
        public string? XmlNamespace { get; init; }
        public bool XmlElementsQualified { get; init; }
        public required IReadOnlyList<AltinnSchemaField> Fields { get; init; }
        /// <summary>The contract snapshot <see cref="Fields"/> come from (its fingerprint goes into the payload evidence), when known.</summary>
        public AltinnContractSnapshot? SchemaSource { get; init; }
        /// <summary>The tasks process/next transitions from, in order, as the confirmed process contract plans them.</summary>
        public required IReadOnlyList<AltinnDeclaredProcessTask> Plan { get; init; }
        /// <summary>The execution bound on process/next calls for this run.</summary>
        public required int MaxTransitions { get; init; }
        public required DateTimeOffset Created { get; init; }
        public long PartyId { get; set; }
        public AltinnInstanceState? Instance { get; set; }
        /// <summary>Set only when Altinn's validation of this run's instance returned no errors.</summary>
        public bool Validated { get; set; }
        /// <summary>process/next calls made in this run (never more than <see cref="MaxTransitions"/>).</summary>
        public int Transitions { get; set; }
    }

    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();

    public bool CanExecute(JourneyStepContext context, out string reason)
    {
        // The read-only instance-evidence scenario is never executed: its steps are only observed (SkoleNaervaerAltinnInstanceObserver).
        var mine = context.Journey.PackId == SkoleNaervaerJourneyPack.Id && context.Journey.JourneyId == SkoleNaervaerJourneyPack.RapportmottakId && Steps.Contains(context.Step.StepId)
            && context.Scenario?.ScenarioId is not (SkoleNaervaerJourneyPack.InstanceEvidenceScenario or SkoleNaervaerJourneyPack.MessageVerificationScenario);
        reason = mine ? "Performed through the Altinn TT02 App API with the run's ID-porten TEST reporter." : "Not an Altinn step of Rapportmottak.";
        return mine;
    }

    public async Task<JourneyStepResult> ExecuteAsync(JourneyStepContext context, CancellationToken ct)
    {
        Prune();
        try
        {
            return context.Step.StepId switch
            {
                AuthStep => await AuthenticateAsync(context, ct),
                InstanceStep => await WithSession(context, CreateInstanceAsync, ct),
                DataStep => await WithSession(context, StoreDataAsync, ct),
                ValidateStep => await WithSession(context, ValidateAsync, ct),
                SubmitStep => await WithSession(context, SubmitAsync, ct),
                _ => await WithSession(context, ReceiptAsync, ct),
            };
        }
        catch (AltinnSignInException ex) { return Result(context, JourneyStepState.Unavailable, "", ex.Message); }
        catch (HttpRequestException) { return Result(context, JourneyStepState.Unavailable, "", "Altinn TT02 could not be reached; nothing further was sent."); }
        catch (System.Text.Json.JsonException) { return Result(context, JourneyStepState.UnexpectedResult, "", "Altinn answered with an unreadable document; nothing further was sent."); }
        finally
        {
            if (context.Step.StepId == ReceiptStep) _sessions.TryRemove(context.RunId, out _);
        }
    }

    private async Task<JourneyStepResult> AuthenticateAsync(JourneyStepContext context, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var services = scope.ServiceProvider;
        var package = await services.GetRequiredService<IIntegrationMessageFlowStore>().GetAsync(context.EnvironmentId, ct);
        var policy = services.GetRequiredService<AltinnTestingPolicy>();
        var contract = services.GetRequiredService<AltinnAppContractService>().Evaluate(context.EnvironmentId, package.AltinnConfiguration.AppIdentifier, package.AppContract);
        // Re-checked at execution: the trusted target, the Altinn environment, the approved app and the complete contract.
        if (AltinnTestingPolicy.TargetBlock(context.Environment, "No trusted environment.") is { } target) return Blocked(context, target);
        if (AltinnTestingPolicy.AltinnEnvironmentBlock(package.AltinnConfiguration) is { } environment) return Blocked(context, environment);
        if (policy.ApprovedApp(package.AltinnConfiguration.AppIdentifier, out var appReason) is not { } app) return Blocked(context, appReason);
        if (!contract.Complete) return Blocked(context, $"The Altinn app contract is incomplete: {string.Join(" ", contract.Missing)}");
        // The process operation must be confirmed and bounded: a declared task alone never opens process/next.
        if (!contract.Process.Confirmed || contract.Process.Transitions < 1 || contract.Process.Transitions > policy.MaxProcessTransitions)
            return Blocked(context, $"The process operation is not confirmed within the execution bound: {contract.Process.Summary}");
        // A state-changing submission needs the service owner's confirmation of the app's authorization policy: re-checked here, not only in readiness.
        if (context.Scenario?.StateChanging != false)
        {
            if (policy.AppPolicy(package.AltinnConfiguration.AppIdentifier, package.AppContract?.ObservedPolicy, out var policyDetail) is not { } confirmation) return Blocked(context, policyDetail);
            var role = context.InputValues.GetValueOrDefault(SkoleNaervaerReportInput.ReporterKey) is { } selected && selected != AltinnTestingPolicy.InteractiveReporter
                ? policy.Reporter(selected, out _)?.AltinnRole ?? "PRIV" : "PRIV";
            if (AltinnTestingPolicy.PolicyProblem(confirmation, role) is { } policyProblem) return Blocked(context, policyProblem);
        }
        if (context.InputValues.Count == 0) return Blocked(context, "The run has no validated report inputs.");
        if (contract.SubmissionContentType is not { } contentType) return Blocked(context, "The form's submission format is not known.");
        // The form is built (and checked against the schema's types) before anything is sent, so an unusable input never leaves an instance behind.
        if (BuildModel(SkoleNaervaerReportInput.Read(context.InputValues), contract.Fields).Problem is { } formProblem) return Blocked(context, formProblem);
        if (context.InputValues.GetValueOrDefault(SkoleNaervaerReportInput.ReporterKey) is not { Length: > 0 } reporter) return Blocked(context, "The run has no reporter.");
        if (policy.IsReporterIdentity(reporter, context.InputValues.GetValueOrDefault(SkoleNaervaerAltinnForm.ChildIdentifier)))
            return Blocked(context, ReporterIsChild);

        var auth = await authenticator.AuthenticateAsync(new AltinnAuthenticationRequest(context.EnvironmentId, context.Initiator, reporter, package.AltinnConfiguration), ct);
        var mode = new JourneyEvidenceFact("Authentication", auth.Mode == "unattended" ? "Unattended ID-porten TEST (synthetic reporter)" : "Interactive ID-porten TEST (fallback)");
        if (!auth.Success)
        {
            var stage = new JourneyEvidenceFact("Failed stage", AltinnAuthenticationResult.StageLabel(auth.Stage));
            return auth.Stage == AltinnAuthenticationStage.Configuration
                ? Result(context, JourneyStepState.Unavailable, $"Failed stage: {stage.Value}", $"Not executed: {auth.Detail} Nothing was sent to Altinn.", facts: [mode, stage])
                : Result(context, JourneyStepState.NotVerified, $"Failed stage: {stage.Value}", $"{auth.Detail} Nothing was sent to the Altinn app.", facts: [mode, stage]);
        }

        var appBase = policy.Endpoints.AppBase(app.Org, app.App);
        var (result, party) = await altinn.CurrentPartyAsync(appBase, auth.Token!, ct);
        if (!result.Success || party is null)
            return Result(context, JourneyStepState.NotVerified, $"{Http(result)} · failed stage: Altinn app authorization",
                result.Status is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized
                    ? $"The Altinn app did not accept the reporter's TT02 token ({Http(result)}): app authorization denied or token not accepted. {result.Detail}"
                    : result.Detail,
                facts: [mode, new("Failed stage", "Altinn app authorization (current party)")]);
        var token = auth.Token!;
        _sessions[context.RunId] = new Session
        {
            Endpoints = AltinnAppEndpoints.Create(appBase, $"{app.Org}/{app.App}", contract.Operations, contract.OperationsSource), Token = token, FormDataTypeId = contract.FormDataTypeId!, Fields = contract.Fields, ContentType = contentType,
            XmlRootElement = contract.XmlRootElement, XmlNamespace = contract.XmlNamespace, XmlElementsQualified = contract.XmlElementsQualified, SchemaSource = SkoleNaervaerPayloadFlow.SchemaSource(contract), Plan = contract.Process.PlannedTasks, MaxTransitions = policy.MaxProcessTransitions,
            Created = clock.GetUtcNow(), PartyId = party.Value,
        };
        return Result(context, JourneyStepState.Verified, $"{Http(result)} · app {app.Org}/{app.App} (TT02)",
            $"Authenticated: {auth.Detail} Altinn TT02 accepted the token and resolved the reporter's party. Not a submission.",
            facts: [mode, new("ID-porten TEST", "token obtained"), new("Altinn TT02 exchange", "token obtained"), new("Altinn app", "party resolved")]);
    }

    private async Task<JourneyStepResult> CreateInstanceAsync(JourneyStepContext context, Session session, CancellationToken ct)
    {
        var (result, instance) = await altinn.CreateInstanceAsync(session.Endpoints, session.Token, session.PartyId, session.FormDataTypeId, ct);
        if (!result.Success || instance is null) return Result(context, JourneyStepState.NotVerified, Http(result), result.Detail);
        session.Instance = instance;
        return Result(context, JourneyStepState.Verified, $"{Http(result)} · instance {Reference(instance)}", "Instance created. Not yet submitted.", Reference(instance));
    }

    private async Task<JourneyStepResult> StoreDataAsync(JourneyStepContext context, Session session, CancellationToken ct)
    {
        var input = SkoleNaervaerReportInput.Read(context.InputValues);
        var (model, problem) = BuildModel(input, session.Fields);
        if (model is null) return Result(context, JourneyStepState.NotVerified, "", problem!);
        var body = session.ContentType == "application/xml"
            ? ToXml(model, session.Fields, session.XmlRootElement!, session.XmlNamespace!, session.XmlElementsQualified)
            : model.ToJsonString();
        // The redacted evidence snapshot of this very body (sent unchanged), redacted here, before the step result is recorded; the unredacted
        // body is never kept, logged or hashed.
        var capture = SkoleNaervaerPayloadFlow.Capture(input, model, session.Fields, session.ContentType, session.XmlRootElement, session.XmlNamespace, session.XmlElementsQualified,
            session.FormDataTypeId, System.Text.Encoding.UTF8.GetByteCount(body), sent: true, clock.GetUtcNow(), session.SchemaSource);
        var (result, dataGuid) = await altinn.PutFormDataAsync(session.Endpoints, session.Token, session.Instance!, session.FormDataTypeId, body, session.ContentType, ct);
        if (!result.Success) return Result(context, JourneyStepState.NotVerified, Http(result), result.Detail) with { Payload = capture };
        session.Instance = session.Instance! with { FormDataGuid = dataGuid };
        return Result(context, JourneyStepState.Verified, $"{Http(result)} · data type {session.FormDataTypeId} · {session.ContentType} · 5 fields",
            "Form data stored on the instance. Not yet validated or submitted.", Reference(session.Instance)) with { Payload = capture };
    }

    private async Task<JourneyStepResult> ValidateAsync(JourneyStepContext context, Session session, CancellationToken ct)
    {
        var (result, summary) = await altinn.ValidateAsync(session.Endpoints, session.Token, session.Instance!, ct);
        if (!result.Success || summary is null) return Result(context, JourneyStepState.NotVerified, Http(result), result.Detail);
        var codes = summary.Codes.Count > 0 ? $" · codes {string.Join(", ", summary.Codes)}" : "";
        session.Validated = summary.Errors == 0;
        return summary.Errors == 0
            ? Result(context, JourneyStepState.Verified, $"{Http(result)} · 0 errors, {summary.Warnings} warning(s){codes}", "Altinn's app validation found no errors. Not yet submitted.", Reference(session.Instance!),
                [new("Validated", $"0 errors, {summary.Warnings} warning(s)")])
            : Result(context, JourneyStepState.UnexpectedResult, $"{Http(result)} · {summary.Errors} error(s), {summary.Warnings} warning(s){codes}",
                "Altinn's app validation reported errors; the report was not submitted.", Reference(session.Instance!));
    }

    /// <summary>
    /// The bounded process operation: for each planned task (one for a first run), read the live instance and check it is at that task, of
    /// that type and completable; make exactly one process/next; classify the returned AppProcessState. Only "process ended" (or, inside a
    /// verified multi-task plan, the planned next task) continues. Anything else stops the run; process/next is never repeated or retried.
    /// </summary>
    private async Task<JourneyStepResult> SubmitAsync(JourneyStepContext context, Session session, CancellationToken ct)
    {
        if (!session.Validated)
            return Result(context, JourneyStepState.Unavailable, "", "Not executed: Altinn's validation of this instance did not pass in this run, so process/next was not called.", Reference(session.Instance!));
        if (session.Plan.Count < 1 || session.Plan.Count > session.MaxTransitions)
            return Result(context, JourneyStepState.Unavailable, "", $"Not executed: the process plan ({session.Plan.Count} transition(s)) is outside the execution bound of {session.MaxTransitions}.", Reference(session.Instance!));
        var facts = new List<JourneyEvidenceFact>();
        for (var index = 0; index < session.Plan.Count; index++)
        {
            var expected = session.Plan[index];
            // The contract says which task the process should be at; only the live instance says where it is.
            var (read, live) = await altinn.GetInstanceAsync(session.Endpoints, session.Token, session.Instance!, session.FormDataTypeId, ct);
            if (!read.Success || live is null)
                return Result(context, JourneyStepState.NotVerified, Http(read), $"The live process state could not be read, so process/next was not called. {read.Detail}", Reference(session.Instance!), facts);
            if (AltinnProcessContractEvaluator.PreTransitionProblem(live, expected) is { } problem)
                return Result(context, JourneyStepState.UnexpectedResult, $"{Http(read)} · live task {live.CurrentTaskId ?? "(none)"} ({live.CurrentTaskType ?? "type not given"})",
                    $"Not advanced: {problem} process/next was not called.", Reference(live), facts);
            facts.Add(new("Live task before transition", $"{expected.TaskId} · {expected.TaskType}"));
            if (session.Transitions >= session.MaxTransitions)
                return Result(context, JourneyStepState.UnexpectedResult, "", $"Not advanced: the execution bound of {session.MaxTransitions} process transition(s) is reached.", Reference(live), facts);

            session.Transitions++;
            var (result, state) = await altinn.ProcessNextAsync(session.Endpoints, session.Token, live, ct);
            if (!result.Success || state is null)
                return Result(context, JourneyStepState.NotVerified, $"{Http(result)} · process/next {session.Transitions} of {session.Plan.Count}",
                    $"{result.Detail} Not retried; nothing further was sent.", Reference(live), facts);
            facts.Add(new("Process advanced", $"{session.Transitions} of at most {session.MaxTransitions} transition(s)"));
            var nextTask = index + 1 < session.Plan.Count ? session.Plan[index + 1].TaskId : null;
            switch (AltinnProcessContractEvaluator.Classify(state, nextTask))
            {
                case AltinnTransitionOutcome.ProcessEnded:
                    facts.Add(new("Process ended", $"end event {state.EndEvent} · {state.Ended:yyyy-MM-dd HH:mm:ss} UTC"));
                    facts.Add(new("Submitted", "process ended (Altinn only)"));
                    return Result(context, JourneyStepState.Verified, $"{Http(result)} · process/next × {session.Transitions} · ended {state.Ended:yyyy-MM-dd HH:mm:ss} UTC · end event {state.EndEvent}",
                        "Submitted in Altinn: the returned process state shows the process ended. Not received by MU and not processed by MM or Skolenærværsrapport.", Reference(live), facts);
                case AltinnTransitionOutcome.MovedToExpectedTask:
                    continue;
                case AltinnTransitionOutcome.UnexpectedNextTask:
                    facts.Add(new("Unexpected next task", $"{state.CurrentTaskId} ({state.CurrentTaskType ?? "type not given"})"));
                    return Result(context, JourneyStepState.UnexpectedResult, $"{Http(result)} · process at {state.CurrentTaskId} ({state.CurrentTaskType ?? "type not given"})",
                        $"Stopped: after process/next the instance is at {state.CurrentTaskId}, and no further transition is approved. No second process/next was made.", Reference(live), facts);
                default:
                    facts.Add(new("Unexpected process state", "neither ended nor at a task"));
                    return Result(context, JourneyStepState.UnexpectedResult, $"{Http(result)} · ended {(state.Ended is null ? "not set" : "set")} · end event {state.EndEvent ?? "not set"}",
                        "Stopped: the returned process state shows neither a completed process (ended, endEvent, no current task) nor a current task. Nothing further was sent.", Reference(live), facts);
            }
        }
        return Result(context, JourneyStepState.UnexpectedResult, "", "Stopped: the process plan finished without the process ending.", Reference(session.Instance!), facts);
    }

    private async Task<JourneyStepResult> ReceiptAsync(JourneyStepContext context, Session session, CancellationToken ct)
    {
        var (result, instance) = await altinn.GetInstanceAsync(session.Endpoints, session.Token, session.Instance!, session.FormDataTypeId, ct);
        if (!result.Success || instance is null) return Result(context, JourneyStepState.NotVerified, Http(result), result.Detail, Reference(session.Instance!));
        return instance.Ended is { } ended
            ? Result(context, JourneyStepState.Verified,
                $"{Http(result)} · receipt: instance {Reference(instance)} · completed {ended:yyyy-MM-dd HH:mm:ss} UTC · end event {instance.EndEvent ?? "(not given)"}",
                "Altinn receipt: the instance is completed in Altinn. This is not evidence that MU received, MM processed or Skolenærværsrapport stored the report.", Reference(instance),
                [new("Receipt confirmed", "instance completed in Altinn (read back)")])
            : Result(context, JourneyStepState.NotVerified, $"{Http(result)} · instance {Reference(instance)} · not completed (current task {instance.CurrentTaskId ?? "none"})",
                "Altinn does not show the instance as completed.", Reference(instance));
    }

    /// <summary>The typed form model at the verified schema paths, with dates in the schema's own format (date or date-time).</summary>
    internal static (JsonObject? Model, string? Problem) BuildModel(SkoleNaervaerReportInput input, IReadOnlyList<AltinnSchemaField> fields)
    {
        var form = new SkoleNaervaerAltinnForm();
        var root = new JsonObject();
        foreach (var expected in form.Fields)
        {
            var field = fields.FirstOrDefault(item => item.Path == expected.Path);
            if (field is null) return (null, $"The app's schema has no field {expected.Path}.");
            JsonNode value;
            switch (expected.Key)
            {
                case SkoleNaervaerAltinnForm.ChildIdentifier: value = JsonValue.Create(input.ChildIdentifier)!; break;
                case SkoleNaervaerAltinnForm.PeriodFrom or SkoleNaervaerAltinnForm.PeriodTo:
                    var date = expected.Key == SkoleNaervaerAltinnForm.PeriodFrom ? input.From : input.To;
                    if (field.Format is not ("date" or "date-time")) return (null, $"{expected.Path} has no date or date-time format in the app's schema; the date format is not guessed.");
                    value = JsonValue.Create(field.Format == "date" ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : $"{date:yyyy-MM-dd}T00:00:00")!;
                    break;
                default:
                    var hours = expected.Key == SkoleNaervaerAltinnForm.PlannedHours ? input.PlannedHours : input.ActualHours;
                    if (field.Type == "integer" && hours != Math.Floor(hours)) return (null, $"{expected.Path} is an integer in the app's schema; {hours} hours is not.");
                    value = field.Type == "integer" ? JsonValue.Create((long)hours) : JsonValue.Create(hours);
                    break;
            }
            var segments = expected.Path.Split('.');
            var node = root;
            foreach (var segment in segments[..^1]) node = (node[segment] as JsonObject) ?? (JsonObject)(node[segment] = new JsonObject());
            node[segments[^1]] = value;
        }
        return (root, null);
    }

    /// <summary>
    /// The form as XML exactly as the app's schema declares it: the schema's root element in its namespace, child elements qualified when
    /// ElementFormDefault is Qualified, and elements in schema order (an XSD sequence is order-sensitive).
    /// </summary>
    internal static string ToXml(JsonObject model, IReadOnlyList<AltinnSchemaField> fields, string rootElement, string ns, bool qualified)
    {
        System.Xml.Linq.XNamespace target = ns;
        var child = qualified ? target : System.Xml.Linq.XNamespace.None;
        var root = new System.Xml.Linq.XElement(target + rootElement);
        Add(root, model, "");
        return new System.Xml.Linq.XDocument(new System.Xml.Linq.XDeclaration("1.0", "utf-8", null), root).Declaration + root.ToString(System.Xml.Linq.SaveOptions.DisableFormatting);

        int Order(string path)
        {
            var index = fields.ToList().FindIndex(field => field.Path == path || field.Path.StartsWith(path + ".", StringComparison.Ordinal));
            return index < 0 ? int.MaxValue : index;
        }

        void Add(System.Xml.Linq.XElement parent, JsonObject node, string prefix)
        {
            foreach (var (name, value) in node.OrderBy(item => Order(prefix + item.Key)).ToArray())
            {
                var element = new System.Xml.Linq.XElement(child + name);
                if (value is JsonObject nested) Add(element, nested, $"{prefix}{name}.");
                else if (value is JsonValue leaf)
                    // Strings as they are; numbers as their invariant JSON number text (whatever CLR type backs the node).
                    element.Value = leaf.TryGetValue<string>(out var text) ? text : leaf.ToJsonString();
                parent.Add(element);
            }
        }
    }

    private async Task<JourneyStepResult> WithSession(JourneyStepContext context, Func<JourneyStepContext, Session, CancellationToken, Task<JourneyStepResult>> step, CancellationToken ct) =>
        _sessions.TryGetValue(context.RunId, out var session) && (context.Step.StepId == InstanceStep || session.Instance is not null)
            ? await step(context, session, ct)
            : Result(context, JourneyStepState.Unavailable, "", "No Altinn session for this run: an earlier Altinn step did not complete.");

    private JourneyStepResult Blocked(JourneyStepContext context, string reason) =>
        Result(context, JourneyStepState.Unavailable, "", $"Not executed: {reason} Nothing was sent to Altinn.");

    private JourneyStepResult Result(JourneyStepContext context, JourneyStepState state, string evidence, string reason, string? correlation = null,
        IReadOnlyList<JourneyEvidenceFact>? facts = null) => new()
    {
        StepId = context.Step.StepId, Label = context.Step.Label, IntegrationId = context.Integration?.Id, State = state, Evidence = evidence,
        Verification = state == JourneyStepState.Verified ? "Altinn App API response" : "", Reason = reason, EvidenceSource = Source,
        Correlation = correlation ?? context.RunId.ToString("N"), CapturedAt = clock.GetUtcNow(), Facts = facts ?? [],
    };

    private static string Http(AltinnCallResult result) => $"HTTP {(int)result.Status}";
    private static string Reference(AltinnInstanceState instance) => $"inst-{instance.InstanceGuid.ToString("N")[..8]}";

    private void Prune()
    {
        var now = clock.GetUtcNow();
        foreach (var (run, session) in _sessions) if (now - session.Created > SessionLifetime) _sessions.TryRemove(run, out _);
    }
}
