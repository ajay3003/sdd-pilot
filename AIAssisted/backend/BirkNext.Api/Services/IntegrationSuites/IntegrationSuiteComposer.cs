using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BirkNext.Integrations;
using BirkNext.IntegrationSuites;
using BirkNext.SourceDomains;

namespace BirkNext.Api.Services.IntegrationSuites;

/// <summary>
/// The pure, deterministic rules of suite generation (no I/O): the scope catalog, seeded synthetic values from declared contract constraints,
/// stage coverage, the Kode 6/7 scenarios (reusing the M01 Security Classification requirements by id) and readiness aggregation.
/// </summary>
public static partial class IntegrationSuiteComposer
{
    // ── Scopes ───────────────────────────────────────────────────────────────────────────────────────────────────────────────

    public const string SkoleNaervaerScope = "skolenaervaer";
    public const string ApiScope = "api";

    /// <summary>Built-in scopes. Skolenærvær stages map onto the Skolenærvær journey pack's own step ids — the pack is reused, never copied.</summary>
    public static IReadOnlyList<IntegrationSuiteScope> Scopes { get; } =
    [
        new()
        {
            ScopeId = SkoleNaervaerScope, DisplayName = "Altinn → MU → MM → Skolenærvær → Bufdata",
            Description = "School attendance reports: Altinn submission, MU reception, MM routing and validation, Skoletjenesten intake with Person/Tjeneste, period lock and Bufdata delivery — plus Kode 6/7 confidentiality.",
            Modules = ["SkoleService", "Skole", "MeldingsutvekslerReceiver", "Meldingsutvekslermottak", "Person", "Tjeneste"],
            JourneyPackId = "skolenaervaer", JourneyIds = ["rapportmottak", "periodeavslutning"], IncludesSecurityClassification = true,
            Stages =
            [
                new("altinn", "Altinn submission", "Altinn", ["altinn-auth", "altinn-instance", "altinn-data", "altinn-validate", "altinn-submit", "altinn-receipt"]),
                new("mu", "MU reception", "MU", ["mu-receive", "mu-queue"]),
                new("mm", "MM routing and validation", "MM", ["mm-retrieve", "mm-decrypt", "mm-xsd", "mm-map"]),
                new("skole-intake", "Skoletjenesten ingestion", "Skolenærværsrapport", ["report-receive", "report-state"]),
                new("person-tjeneste", "Person/Tjeneste dependencies", "Person", ["report-person"]),
                new("period-lock", "Period lock", "Skolenærværsrapport", ["period-close", "dataset-prepare", "publish"]),
                new("bufdata", "Bufdata delivery", "Bufdata", ["broker", "bufdata-activity", "bufdata-processed", "dataset-verify", "dataset-pii"]),
            ],
        },
        new()
        {
            ScopeId = ApiScope, DisplayName = "API contracts of selected modules",
            Description = "REST and GraphQL contracts found in the selected source modules: one stage per operation, executed through Contract Testing.",
        },
    ];

    public static IntegrationSuiteScope? Scope(string scopeId) => Scopes.FirstOrDefault(s => string.Equals(s.ScopeId, scopeId, StringComparison.Ordinal));

    /// <summary>A file belongs to the scope when its top folder is one of the scope's modules (exact folder match, never a name similarity).</summary>
    public static bool InModules(string path, IReadOnlyCollection<string> modules) =>
        modules.Count == 0 || modules.Contains(path.Replace('\\', '/').Split('/')[0], StringComparer.OrdinalIgnoreCase);

    // ── Synthetic Test Data ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Names that hold person identity (national id, DUF, BirkId …): never synthesised here — an approved synthetic identity from Test Data
    /// is a prerequisite.</summary>
    public static bool IsIdentityField(string name) => IdentityNameRe().IsMatch(name);

    /// <summary>
    /// One value per declared parameter, only where the declared constraint is known: a declared enum (first value), a declared example/default,
    /// or a primitive type (integer, number, boolean, date, date-time, uuid, string). Seeded: the same seed and parameter give the same value.
    /// Identity fields and unknown types are refused with the reason (they become prerequisites, never guesses).
    /// </summary>
    public static (List<SuiteGeneratedValue> Values, List<string> Refused) Synthesize(IEnumerable<ContractParameter> parameters, int seed)
    {
        var values = new List<SuiteGeneratedValue>();
        var refused = new List<string>();
        foreach (var p in parameters.Where(p => p.In is "path" or "query" or "header" or "argument" or "body" || p.Required))
        {
            if (p.In == "header" && Regex.IsMatch(p.Name, "^(authorization|cookie|x-api-key)$", RegexOptions.IgnoreCase)) continue; // credentials are never Test Data
            if (IsIdentityField(p.Name)) { refused.Add($"{p.Name}: person identity — needs an approved synthetic identity from Test Data"); continue; }
            var type = (p.Type ?? "").ToLowerInvariant();
            var r = Seeded(seed, p.Name);
            if (p.Enum is { Count: > 0 } e) values.Add(new(p.Name, e[0], "first declared enum value", $"enum [{string.Join(", ", e.Take(6))}]"));
            else if (p.Example is { Length: > 0 } ex) values.Add(new(p.Name, ex, "declared example", "example in the contract"));
            else if (p.Default is { Length: > 0 } d) values.Add(new(p.Name, d, "declared default", "default in the contract"));
            else if (type.Contains("uuid")) values.Add(new(p.Name, DeterministicGuid(seed, p.Name).ToString("D"), "seeded uuid", type));
            else if (type.Contains("date-time") || type.Contains("datetime")) values.Add(new(p.Name, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(r % 525_600).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), "seeded date-time in 2026", type));
            else if (type.Contains("date")) values.Add(new(p.Name, new DateOnly(2026, 1, 1).AddDays(r % 365).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "seeded date in 2026", type));
            else if (type.StartsWith("int", StringComparison.Ordinal) || type == "long") values.Add(new(p.Name, (1 + r % 999).ToString(CultureInfo.InvariantCulture), "seeded integer 1–999", type));
            else if (type is "number" or "decimal" or "float" or "double") values.Add(new(p.Name, ((1 + r % 9999) / 100m).ToString("0.00", CultureInfo.InvariantCulture), "seeded decimal 0.01–99.99", type));
            else if (type.StartsWith("bool", StringComparison.Ordinal)) values.Add(new(p.Name, "true", "boolean true", type));
            else if (type is "string" or "id" or "")
            {
                if (type.Length == 0 && !p.Required) { refused.Add($"{p.Name}: no declared type"); continue; }
                values.Add(new(p.Name, $"synthetic-{Slug(p.Name)}-{r % 100000:00000}", "seeded synthetic string (no format declared)", type.Length == 0 ? "untyped (required)" : type));
            }
            else refused.Add($"{p.Name}: type '{p.Type}' has no safe synthetic rule");
        }
        return (values, refused);
    }

    private static int Seeded(int seed, string name) =>
        (int)(BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}|{name}")), 0) % int.MaxValue);

    private static Guid DeterministicGuid(int seed, string name) => new(SHA256.HashData(Encoding.UTF8.GetBytes($"guid|{seed}|{name}")).AsSpan(0, 16));

    private static string Slug(string s) => SlugRe().Replace(s.ToLowerInvariant(), "-").Trim('-');

    // ── Stages ───────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Coverage from the registered executors/observers of a stage's steps; no step covered → Unsupported (missing adapter), never Ready.</summary>
    public static SuiteStageCoverage Coverage(IReadOnlyList<(string Step, bool Executable, bool Observable)> steps)
    {
        if (steps.Count == 0) return SuiteStageCoverage.Unsupported;
        var exec = steps.Any(s => s.Executable);
        var obs = steps.Any(s => s.Observable);
        return exec && obs ? SuiteStageCoverage.ExecutableAndObservable : exec ? SuiteStageCoverage.Executable : obs ? SuiteStageCoverage.Observable : SuiteStageCoverage.Unsupported;
    }

    public static (SuiteItemReadiness Readiness, SuiteTriage Triage) StageReadiness(SuiteStageCoverage coverage, IReadOnlyCollection<string> blockers) =>
        coverage switch
        {
            SuiteStageCoverage.Unsupported => (SuiteItemReadiness.NotSupported, SuiteTriage.NotSupported),
            _ when blockers.Count > 0 => (SuiteItemReadiness.Blocked, SuiteTriage.WaitingForPrerequisite),
            SuiteStageCoverage.ExecutableAndObservable => (SuiteItemReadiness.Ready, SuiteTriage.CanRunNow),
            // Executable without an observer can run, but its outcome cannot be verified (never PASS).
            SuiteStageCoverage.Executable or SuiteStageCoverage.Observable => (SuiteItemReadiness.PartiallyExecutable, SuiteTriage.WaitingForPrerequisite),
            _ => (SuiteItemReadiness.Blocked, SuiteTriage.WaitingForPrerequisite),
        };

    // ── Kode 6/7 ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

    public sealed record SecurityInputs(
        SecurityClassificationConsumerAssessment? Consumer,
        bool Kode6IdentityApproved, bool Kode7IdentityApproved, bool OrdinaryChildApproved,
        bool AltinnSubmissionExecutable, bool MuMmObservable, bool IntakeObservedPerMessage, bool BufdataVerifierRegistered,
        IReadOnlyList<SuiteSourceRef> SourceRuleEvidence, IReadOnlyDictionary<string, string?> Decisions);

    /// <summary>
    /// Existing Kode 6/7 coverage (the M01 Security Classification requirements the Skolenærvær consumer declares, reused by key and run id)
    /// plus candidate variants K67-01…12. A variant is runnable only with a registered executor and observer, an approved synthetic identity
    /// per level and an approved expected outcome; otherwise it waits for the named prerequisite or needs a domain decision. Nothing here
    /// assumes rejection, storage, masking, omission or aggregation.
    /// </summary>
    public static List<SuiteSecurityScenario> Security(SecurityInputs i)
    {
        var list = new List<SuiteSecurityScenario>();
        foreach (var r in i.Consumer?.Requirements ?? [])
        {
            var ready = r.State is SecurityRequirementState.Verified ? SuiteItemReadiness.Ready
                : r.State is SecurityRequirementState.SourceEvidence or SecurityRequirementState.Partial ? SuiteItemReadiness.PartiallyExecutable
                : r.State is SecurityRequirementState.ReviewRequired ? SuiteItemReadiness.NeedsReview
                : r.State is SecurityRequirementState.NotApplicable ? SuiteItemReadiness.NotAssessed : SuiteItemReadiness.Blocked;
            list.Add(new SuiteSecurityScenario
            {
                ScenarioId = $"M01:{r.Key}", Title = r.Title, Basis = r.DomainSpecific ? SuiteScenarioBasis.SpecificationDerived : SuiteScenarioBasis.ExistingCapability,
                Reuses = $"{r.Owner} · {r.Key}", ExistingState = SecurityClassificationAssessments.Label(r.State), ReviewRunId = i.Consumer!.RunId,
                Readiness = ready, Triage = ready switch { SuiteItemReadiness.Ready => SuiteTriage.CanRunNow, SuiteItemReadiness.NeedsReview => SuiteTriage.NeedsDomainDecision, _ => SuiteTriage.WaitingForPrerequisite },
                Prerequisites = r.State is SecurityRequirementState.Verified ? [] : [r.RuntimeTest.Length > 0 ? r.RuntimeTest : r.SharedEvidence],
            });
        }

        string? Decided(string key) => i.Decisions.TryGetValue(key, out var d) ? d : null;
        SuiteSecurityScenario Variant(string id, string title, string[] levels, bool executor, bool observer, string? openQuestion, string reviewKey, string[] missing,
            string? reuses = null, string[]? refs = null)
        {
            var decided = Decided(reviewKey);
            var question = decided is "Acceptable" ? null : openQuestion;
            var prereq = new List<string>(missing);
            if (levels.Contains("Kode 6") && !i.Kode6IdentityApproved) prereq.Add("An approved synthetic Kode 6 child (Test Data capability classification-level-3) registered in the target Person service");
            if (levels.Contains("Kode 7") && !i.Kode7IdentityApproved) prereq.Add("An approved synthetic Kode 7 child (Test Data capability classification-level-2) registered in the target Person service");
            if (!executor) prereq.Add("A registered executor for this stage");
            if (!observer) prereq.Add("A registered observer that sees this outcome per message");
            // An open domain question comes first: it must be answered whatever adapters exist (the tester's "needs clarification" bucket).
            var (readiness, triage) = question is not null ? (SuiteItemReadiness.NeedsReview, SuiteTriage.NeedsDomainDecision)
                : !executor && !observer ? (SuiteItemReadiness.NotSupported, SuiteTriage.NotSupported)
                : prereq.Count > 0 ? (executor || observer ? SuiteItemReadiness.Blocked : SuiteItemReadiness.NotSupported, SuiteTriage.WaitingForPrerequisite)
                : (SuiteItemReadiness.Ready, SuiteTriage.CanRunNow);
            return new SuiteSecurityScenario
            {
                ScenarioId = id, Title = title, Basis = decided is "Acceptable" ? SuiteScenarioBasis.ReviewedBusinessRule : SuiteScenarioBasis.Candidate, Levels = [.. levels],
                Readiness = readiness, Triage = triage, Prerequisites = prereq, OpenQuestion = question, ReviewKey = reviewKey, Reuses = reuses, Evidence = [.. i.SourceRuleEvidence], References = [.. refs ?? []],
            };
        }

        const string intakeQuestion = "What must Skoletjenesten do with a report for a Kode 6/7 child at intake (store, withhold, reject)? No approved rule is recorded.";
        list.Add(Variant("K67-01", "Kode 6 child reported through Altinn", ["Kode 6"], i.AltinnSubmissionExecutable, i.IntakeObservedPerMessage, intakeQuestion, "security:intake-rule", [],
            "Skolenærvær journey pack · rapportmottak (Altinn executor)", ["E2E-05"]));
        list.Add(Variant("K67-02", "Kode 7 child reported through Altinn", ["Kode 7"], i.AltinnSubmissionExecutable, i.IntakeObservedPerMessage, intakeQuestion, "security:intake-rule", [],
            "Skolenærvær journey pack · rapportmottak (Altinn executor)", ["E2E-05"]));
        list.Add(Variant("K67-03", "Protected child at the MU/MM processing boundary", ["Kode 6", "Kode 7"], false, i.MuMmObservable, null, "security:mu-mm", []));
        list.Add(Variant("K67-04", "Protected child reaches Skoletjenesten intake with documented handling", ["Kode 6", "Kode 7"], false, i.IntakeObservedPerMessage, intakeQuestion, "security:intake-rule", []));
        list.Add(Variant("K67-05", "Protected child excluded from prohibited downstream (Bufdata) records", ["Kode 6", "Kode 7"], false, i.BufdataVerifierRegistered,
            "Are Kode 6/7 children counted in Bufdata aggregate figures, or excluded entirely? Only identifier exclusion is recorded (bufdata-exclusion).", "security:bufdata-aggregate", [],
            "Skolenærvær security consumer · bufdata-exclusion", ["DATA-03"]));
        // K67-06/07 are the M01 runtime probe (unauthorized / authorized identity): not a second implementation — they inherit its requirement state.
        SecurityRequirementAssessment? Probe(SecurityRequirementKind kind) => i.Consumer?.Requirements.FirstOrDefault(r => r.Key.Contains(kind.ToString(), StringComparison.OrdinalIgnoreCase));
        foreach (var (id, title, kind, key) in new[] { ("K67-06", "Unauthorized user attempts a protected-child lookup", SecurityRequirementKind.UnauthorizedRuntimeDenial, "security:unauthorized"),
                                                       ("K67-07", "Authorized user reads allowed information", SecurityRequirementKind.AuthorizedRuntimeAccess, "security:authorized") })
        {
            var probe = Probe(kind);
            list.Add(Variant(id, title, ["Kode 6", "Kode 7"], probe is not null, probe is not null, null, key,
                probe is null || probe.State == SecurityRequirementState.Verified ? [] : [$"M01 runtime probe: {probe.RuntimeTest} (test-lead approved context, Person endpoint, tokens supplied at run)"],
                $"Security Classification review · {kind}"));
        }
        list.Add(Variant("K67-08", "BirkNext evidence and exports never expose protected identifiers (synthetic canaries)", [], true, true, null, "security:evidence-redaction", [],
            "Execution Evidence redaction — only BirkNext's own evidence and exports are inspectable; target-system logs are NOT ASSESSED"));
        list.Add(Variant("K67-09", "Period lock and downstream publication respect confidentiality", ["Kode 6", "Kode 7"], false, false, null, "security:period-lock", []));
        list.Add(Variant("K67-10", "Correction/replay does not bypass protection handling", ["Kode 6", "Kode 7"], false, false,
            "How are corrections of a protected child's report handled? No approved rule is recorded.", "security:correction", []));
        list.Add(Variant("K67-11", "Protection classification changes between processing stages", ["Kode 6", "Kode 7"], false, false,
            "Which classification applies when it changes between submission and lock? No approved rule is recorded.", "security:reclassification", []));
        list.Add(Variant("K67-12", "Protected-child handling after retry/recovery", ["Kode 6", "Kode 7"], false, false, null, "security:recovery", []));
        return list;
    }

    // ── Readiness ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    public static (SuiteStatus Status, string Reason) Status(IReadOnlyList<SuiteReadinessDimension> dimensions, IReadOnlyCollection<SuiteReviewItem> review, IReadOnlyList<SuiteStage> stages)
    {
        var openReview = review.Count(r => r.Decision is null);
        var blocked = dimensions.Where(d => d.State is SuiteItemReadiness.Blocked).Select(d => d.Name).ToList();
        var runnable = stages.Count(s => s.Readiness is SuiteItemReadiness.Ready);
        if (dimensions.Any(d => d.Name == "Source snapshot" && d.State != SuiteItemReadiness.Ready)) return (SuiteStatus.Blocked, "The source snapshot is not usable.");
        if (openReview > 0 && runnable == 0) return (SuiteStatus.NeedsReview, $"{openReview} review item(s) open; nothing can run yet.");
        if (blocked.Count > 0 && runnable == 0) return (SuiteStatus.Blocked, $"Blocked: {string.Join(", ", blocked)}.");
        if (runnable > 0 && runnable < stages.Count) return (SuiteStatus.PartiallyExecutable, $"{runnable} of {stages.Count} stage(s) can run; the rest wait for prerequisites, adapters or decisions.");
        if (runnable > 0 && runnable == stages.Count && openReview == 0 && blocked.Count == 0) return (SuiteStatus.ReadyToExecute, "Every stage has an executor, an observer and its prerequisites.");
        return (openReview > 0 ? SuiteStatus.NeedsReview : SuiteStatus.Draft, openReview > 0 ? $"{openReview} review item(s) open." : "Generated draft; nothing reviewed or runnable yet.");
    }

    // IncludeFields: value tuples serialize their items (without it they would all be "{}" and every fingerprint would match).
    private static readonly System.Text.Json.JsonSerializerOptions FingerprintJson = new() { IncludeFields = true };

    public static string Fingerprint(object value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(value, FingerprintJson)))).ToLowerInvariant()[..32];

    [GeneratedRegex(@"(fodsels|fødsels|personnummer|personnr|\bfnr\b|^fnr|ssn|nationalid|national_id|duf|birkid|birk_id|dnummer|d-nummer)", RegexOptions.IgnoreCase)]
    private static partial Regex IdentityNameRe();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex SlugRe();
}
