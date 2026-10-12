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
/// real fødselsnummer, D-number or H-number is refused. A syntactically valid synthetic identifier is not a registered test person: whether
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
        return day is >= 1 and <= 31 && month is >= 81 and <= 92 && Control(value, K1, 9) == value[9] - '0' && Control(value, K2, 10) == value[10] - '0';
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
    public const string PriorReportKey = "priorReport";
    public const string Registered = "registered";
    public const string Supplied = "supplied";
    public const string Generated = "generated";

    public static JourneyInputPreparation Prepare(JourneyScenarioDescriptor scenario, IReadOnlyDictionary<string, string> inputs)
    {
        var errors = new List<string>();
        var source = inputs.GetValueOrDefault(IdentitySourceKey)?.Trim() ?? "";
        var allowed = scenario.Inputs?.FirstOrDefault(item => item.Key == IdentitySourceKey)?.Options?.Select(item => item.Value).ToArray() ?? [];
        if (!allowed.Contains(source, StringComparer.Ordinal)) errors.Add($"The identity source \"{source}\" is not allowed for this scenario.");

        var identifier = inputs.GetValueOrDefault(SkoleNaervaerAltinnForm.ChildIdentifier)?.Trim() ?? "";
        if (source == Generated)
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
            [IdentitySourceKey] = source, [SkoleNaervaerAltinnForm.ChildIdentifier] = identifier,
            [SkoleNaervaerAltinnForm.PeriodFrom] = from!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            [SkoleNaervaerAltinnForm.PeriodTo] = to!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            [SkoleNaervaerAltinnForm.PlannedHours] = planned!.Value.ToString("R", CultureInfo.InvariantCulture),
            [SkoleNaervaerAltinnForm.ActualHours] = actual!.Value.ToString("R", CultureInfo.InvariantCulture),
        };
        var declaration = source switch
        {
            Registered => "declared registered synthetic test person (known child; not verified by BirkNext)",
            Supplied => "supplied synthetic identifier, declared not registered in Person (unknown child; not verified by BirkNext)",
            _ => "generated synthetic identifier, assumed not registered in Person (unknown child; not verified by BirkNext)",
        };
        return new JourneyInputPreparation(values, [],
        [
            $"Child: synthetic identifier {SyntheticPersonIdentifier.Reference(identifier)} — {declaration}.",
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
/// Performs the six Altinn steps of Rapportmottak through the Altinn 3 App API, after the tester signed in with ID-porten TEST:
/// authenticate (token accepted, party resolved) → create instance → store form data → validate → advance the process → read the receipt.
/// Each step is its own evidence; a step that does not progress stops the run (nothing is retried or resent). Every policy is re-checked
/// here before the first call. Only safe facts are recorded: HTTP status, a short instance reference, validation codes and process state —
/// never the token, the party id, the form payload or the child's identifier. Altinn's receipt satisfies the Altinn steps only; MU, MM,
/// Skolenærværsrapport and Person steps stay Not assessed.
/// </summary>
public sealed class SkoleNaervaerAltinnExecutor(IServiceScopeFactory scopes, IAltinnTestTokens tokens, AltinnAppApiClient altinn, TimeProvider clock) : IJourneyStepExecutor
{
    public const string AuthStep = "altinn-auth";
    public const string InstanceStep = "altinn-instance";
    public const string DataStep = "altinn-data";
    public const string ValidateStep = "altinn-validate";
    public const string SubmitStep = "altinn-submit";
    public const string ReceiptStep = "altinn-receipt";
    private static readonly string[] Steps = [AuthStep, InstanceStep, DataStep, ValidateStep, SubmitStep, ReceiptStep];
    private const string Source = "Altinn TT02 App API";
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(15);

    private sealed class Session
    {
        public required Uri AppBase { get; init; }
        public required string Token { get; init; }
        public required string FormDataTypeId { get; init; }
        public required string ContentType { get; init; }
        public string? XmlRootElement { get; init; }
        public string? XmlNamespace { get; init; }
        public bool XmlElementsQualified { get; init; }
        public required IReadOnlyList<AltinnSchemaField> Fields { get; init; }
        public required IReadOnlyList<AltinnProcessTask> Tasks { get; init; }
        public required DateTimeOffset Created { get; init; }
        public long PartyId { get; set; }
        public AltinnInstanceState? Instance { get; set; }
    }

    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();

    public bool CanExecute(JourneyStepContext context, out string reason)
    {
        var mine = context.Journey.PackId == SkoleNaervaerJourneyPack.Id && context.Journey.JourneyId == SkoleNaervaerJourneyPack.RapportmottakId && Steps.Contains(context.Step.StepId);
        reason = mine ? "Performed through the Altinn TT02 App API after ID-porten TEST sign-in." : "Not an Altinn step of Rapportmottak.";
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
        if (context.InputValues.Count == 0) return Blocked(context, "The run has no validated report inputs.");
        if (contract.SubmissionContentType is not { } contentType) return Blocked(context, "The form's submission format is not known.");
        // The form is built (and checked against the schema's types) before anything is sent, so an unusable input never leaves an instance behind.
        if (BuildModel(SkoleNaervaerReportInput.Read(context.InputValues), contract.Fields).Problem is { } formProblem) return Blocked(context, formProblem);
        if (context.Initiator is not { Length: > 0 } initiator || tokens.Get(initiator, context.EnvironmentId) is not { } token)
            return Blocked(context, "Not signed in to ID-porten TEST for this environment (Skolenærvær Testing → Rapportmottak → Sign in with TestID).");

        var appBase = AltinnTestingPolicy.AppBase(app.Org, app.App);
        var (result, party) = await altinn.CurrentPartyAsync(appBase, token, ct);
        if (!result.Success || party is null) return Result(context, JourneyStepState.NotVerified, Http(result), result.Detail);
        _sessions[context.RunId] = new Session
        {
            AppBase = appBase, Token = token, FormDataTypeId = contract.FormDataTypeId!, Fields = contract.Fields, ContentType = contentType,
            XmlRootElement = contract.XmlRootElement, XmlNamespace = contract.XmlNamespace, XmlElementsQualified = contract.XmlElementsQualified, Tasks = contract.ProcessTasks, Created = clock.GetUtcNow(), PartyId = party.Value,
        };
        return Result(context, JourneyStepState.Verified, $"{Http(result)} · app {app.Org}/{app.App} (TT02)", "Authenticated: Altinn TT02 accepted the ID-porten TEST token and resolved the signed-in party. Not a submission.");
    }

    private async Task<JourneyStepResult> CreateInstanceAsync(JourneyStepContext context, Session session, CancellationToken ct)
    {
        var (result, instance) = await altinn.CreateInstanceAsync(session.AppBase, session.Token, session.PartyId, session.FormDataTypeId, ct);
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
        var (result, dataGuid) = await altinn.PutFormDataAsync(session.AppBase, session.Token, session.Instance!, session.FormDataTypeId, body, session.ContentType, ct);
        if (!result.Success) return Result(context, JourneyStepState.NotVerified, Http(result), result.Detail);
        session.Instance = session.Instance! with { FormDataGuid = dataGuid };
        return Result(context, JourneyStepState.Verified, $"{Http(result)} · data type {session.FormDataTypeId} · {session.ContentType} · 5 fields",
            "Form data stored on the instance. Not yet validated or submitted.", Reference(session.Instance));
    }

    private async Task<JourneyStepResult> ValidateAsync(JourneyStepContext context, Session session, CancellationToken ct)
    {
        var (result, summary) = await altinn.ValidateAsync(session.AppBase, session.Token, session.Instance!, ct);
        if (!result.Success || summary is null) return Result(context, JourneyStepState.NotVerified, Http(result), result.Detail);
        var codes = summary.Codes.Count > 0 ? $" · codes {string.Join(", ", summary.Codes)}" : "";
        return summary.Errors == 0
            ? Result(context, JourneyStepState.Verified, $"{Http(result)} · 0 errors, {summary.Warnings} warning(s){codes}", "Altinn's app validation found no errors. Not yet submitted.", Reference(session.Instance!))
            : Result(context, JourneyStepState.UnexpectedResult, $"{Http(result)} · {summary.Errors} error(s), {summary.Warnings} warning(s){codes}",
                "Altinn's app validation reported errors; the report was not submitted.", Reference(session.Instance!));
    }

    private async Task<JourneyStepResult> SubmitAsync(JourneyStepContext context, Session session, CancellationToken ct)
    {
        DateTimeOffset? ended = null;
        for (var index = 0; index < session.Tasks.Count; index++)
        {
            var (result, current, end) = await altinn.ProcessNextAsync(session.AppBase, session.Token, session.Instance!, ct);
            if (!result.Success) return Result(context, JourneyStepState.NotVerified, $"{Http(result)} · after {index} of {session.Tasks.Count} process step(s)", result.Detail, Reference(session.Instance!));
            var expected = index + 1 < session.Tasks.Count ? session.Tasks[index + 1].TaskId : null;
            if (expected is not null && current != expected)
                return Result(context, JourneyStepState.UnexpectedResult, $"{Http(result)} · process at {current ?? "(none)"}", $"The process did not move to {expected} as the contract says.", Reference(session.Instance!));
            ended = end;
        }
        return ended is not null
            ? Result(context, JourneyStepState.Verified, $"Process advanced {session.Tasks.Count} time(s) · ended {ended:yyyy-MM-dd HH:mm:ss} UTC",
                "Submitted in Altinn: the instance process has ended. Not received by MU and not processed by MM or Skolenærværsrapport.", Reference(session.Instance!))
            : Result(context, JourneyStepState.UnexpectedResult, $"Process advanced {session.Tasks.Count} time(s) · not ended", "The process did not end after the contract's tasks.", Reference(session.Instance!));
    }

    private async Task<JourneyStepResult> ReceiptAsync(JourneyStepContext context, Session session, CancellationToken ct)
    {
        var (result, instance) = await altinn.GetInstanceAsync(session.AppBase, session.Token, session.Instance!, session.FormDataTypeId, ct);
        if (!result.Success || instance is null) return Result(context, JourneyStepState.NotVerified, Http(result), result.Detail, Reference(session.Instance!));
        return instance.Ended is { } ended
            ? Result(context, JourneyStepState.Verified,
                $"{Http(result)} · receipt: instance {Reference(instance)} · completed {ended:yyyy-MM-dd HH:mm:ss} UTC · end event {instance.EndEvent ?? "(not given)"}",
                "Altinn receipt: the instance is completed in Altinn. This is not evidence that MU received, MM processed or Skolenærværsrapport stored the report.", Reference(instance))
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

    private JourneyStepResult Result(JourneyStepContext context, JourneyStepState state, string evidence, string reason, string? correlation = null) => new()
    {
        StepId = context.Step.StepId, Label = context.Step.Label, IntegrationId = context.Integration?.Id, State = state, Evidence = evidence,
        Verification = state == JourneyStepState.Verified ? "Altinn App API response" : "", Reason = reason, EvidenceSource = Source,
        Correlation = correlation ?? context.RunId.ToString("N"), CapturedAt = clock.GetUtcNow(),
    };

    private static string Http(AltinnCallResult result) => $"HTTP {(int)result.Status}";
    private static string Reference(AltinnInstanceState instance) => $"inst-{instance.InstanceGuid.ToString("N")[..8]}";

    private void Prune()
    {
        var now = clock.GetUtcNow();
        foreach (var (run, session) in _sessions) if (now - session.Created > SessionLifetime) _sessions.TryRemove(run, out _);
    }
}
