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

    // Declared before Scopes: static initializers run in textual order.
    private static readonly List<string> MuAndMmWords = ["Meldingsutveksler", "Meldingsutveksleren", "MeldingsutvekslerReceiver", "Meldingsutvekslermottak"];
    private static readonly List<string> SkoleWords = ["SkoleService", "Skoletjenesten", "Skolenaervaersrapport"];

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
                new("altinn", "Altinn submission", "Altinn", ["altinn-auth", "altinn-instance", "altinn-data", "altinn-validate", "altinn-submit", "altinn-receipt"])
                { Source = new() { Tokens = ["Altinn"], ExcludedTokens = MuAndMmWords, ReferenceSource = "Altinn app reference (Skolenærvær Test Pack)", ChannelToNext = TransitionChannel.Webhook,
                    CorrelationFields = ["submissionId", "instanceId", "instanceGuid", "meldingId", "messageId"] } },
                new("mu", "MU reception", "MU", ["mu-receive", "mu-queue"])
                { Source = new() { Tokens = ["Meldingsutveksler", "Meldingsutveksleren"], ExcludedTokens = ["MeldingsutvekslerReceiver", "Meldingsutvekslermottak", "Receiver", "Mottak"],
                    ChannelToNext = TransitionChannel.Message, CorrelationFields = ["messageId", "meldingId", "submissionId"] } },
                new("mm", "MM routing and validation", "MM", ["mm-retrieve", "mm-decrypt", "mm-xsd", "mm-map"])
                { Source = new() { Tokens = ["MeldingsutvekslerReceiver", "Meldingsutvekslermottak"], ChannelToNext = TransitionChannel.HttpApi,
                    CorrelationFields = ["meldingId", "messageId", "rapportId", "reportId"] } },
                new("skole-intake", "Skoletjenesten ingestion", "Skolenærværsrapport", ["report-receive", "report-state"])
                { Source = new() { Tokens = SkoleWords, ChannelToNext = TransitionChannel.HttpApi, CorrelationFields = ["rapportId", "reportId", "barnId", "childId"] } },
                new("person-tjeneste", "Person/Tjeneste dependencies", "Person", ["report-person"])
                { Source = new() { Tokens = ["Person", "Tjeneste", "TjenesteService"], ExcludedTokens = ["Adapter", "BiRK"], ChannelToNext = TransitionChannel.Internal } },
                new("period-lock", "Period lock", "Skolenærværsrapport", ["period-close", "dataset-prepare", "publish"])
                { Source = new() { Tokens = SkoleWords, ChannelToNext = TransitionChannel.Message, ActionWords = ["lock", "close", "lukk", "avslutt", "las"],
                    CorrelationFields = ["period", "periode", "reportId", "rapportId"] } },
                new("bufdata", "Bufdata delivery", "Bufdata", ["broker", "bufdata-activity", "bufdata-processed", "dataset-verify", "dataset-pii"])
                { Source = new() { Tokens = ["Bufdata"], DatasetContractEvidence = true, ChannelToNext = TransitionChannel.Dataset } },
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
        IReadOnlyList<SuiteSourceRef> SourceRuleEvidence, IReadOnlyDictionary<string, string?> Decisions,
        IReadOnlyDictionary<string, List<SuiteSpecificationLink>>? SpecificationLinks = null, IReadOnlyDictionary<string, string?>? DecisionNotes = null);

    /// <summary>A decision note that records a confirmed specification link starts with this (the requirement id and file follow).</summary>
    public const string SpecificationNotePrefix = "Specification ";

    private static readonly string[] Classified = ["kode 6/7", "kode 6", "kode 7", "kode6", "kode7", "gradert", "graded", "classified child", "classification"];

    /// <summary>
    /// What each Kode 6/7 question is about, as term groups for <see cref="Specifications.SpecificationAnswerMatcher"/>: a requirement is proposed
    /// only when its wording has a term of every group (Kode 6/7 AND the subject AND, where it matters, the outcome).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[][]> QuestionTopics = new Dictionary<string, string[][]>(StringComparer.Ordinal)
    {
        ["security:intake-rule"] = [Classified, ["report", "rapport", "intake", "mottak"],
            ["processed like any other", "like any other report", "behandles", "stored", "lagres", "withheld", "held", "reject", "avvis"]],
        ["security:bufdata-aggregate"] = [Classified, ["population", "coverage", "dekning", "aggregate", "figure", "statistic"]],
        ["security:correction"] = [Classified, ["correction", "korriger", "replay", "resubmi", "new version"]],
        ["security:reclassification"] = [Classified, ["at lock", "frozen", "fryses", "classification changes", "change in person", "every time it is needed", "read from person"]],
        ["security:period-lock"] = [Classified, ["bufdata dataset", "dataset", "delivered", "levert"], ["lock", "lås"]],
    };

    /// <summary>The proposed specification links per Kode 6/7 question, without the requirements a tester rejected for that question.</summary>
    public static Dictionary<string, List<SuiteSpecificationLink>> SpecificationLinksFor(IEnumerable<BirkNext.Specifications.SpecificationRequirementEntry> requirements,
        IReadOnlyDictionary<string, string?> decisions)
    {
        var list = requirements.ToList();
        return QuestionTopics.ToDictionary(t => t.Key, t => Specifications.SpecificationAnswerMatcher.Match(list, t.Value, RejectedLinks(t.Key, decisions.Keys)), StringComparer.Ordinal);
    }

    /// <summary>The review subject that records a rejected specification link of one question.</summary>
    public static string RejectedLinkKey(string reviewKey, string requirementId) => $"{reviewKey}#spec:{BirkNext.Specifications.SpecificationCatalogRules.NormalizeId(requirementId)}";

    public static HashSet<string> RejectedLinks(string reviewKey, IEnumerable<string> decisionKeys) =>
        [.. decisionKeys.Where(k => k.StartsWith(reviewKey + "#spec:", StringComparison.Ordinal)).Select(k => k[(reviewKey.Length + "#spec:".Length)..])];

    /// <summary>
    /// Faglig avklart for one question: a recorded decision wins (from a confirmed specification link, or a reviewed rule); otherwise the best
    /// proposal decides between "proposed from the specification" and "open in the specification" (an OQ marker: it stays open until a decision
    /// is documented); with neither it is not clarified. A proposal never closes the question.
    /// </summary>
    public static (SuiteClarification State, string? Detail) Clarify(string? openQuestion, string? decision, string? note, IReadOnlyList<SuiteSpecificationLink> links)
    {
        if (openQuestion is null && decision is null) return (SuiteClarification.NotNeeded, null);
        if (decision is not null)
            return note is { } n && n.StartsWith(SpecificationNotePrefix, StringComparison.Ordinal)
                ? (SuiteClarification.ConfirmedFromSpecification, $"Confirmed by a tester: {n}")
                : (SuiteClarification.ReviewedRule, $"Reviewed: {decision}{(string.IsNullOrWhiteSpace(note) ? "" : $" — {note}")}");
        if (links.FirstOrDefault() is { } best)
            return best.Answer == SuiteSpecificationAnswer.OpenInSpecification
                ? (SuiteClarification.OpenInSpecification, $"{best.RequirementId} ({best.SpecificationPath}) names this as open{(best.OpenQuestion is { } oq ? $" ({oq})" : "")}: it stays open until a documented decision exists.")
                : (SuiteClarification.ProposedFromSpecification, $"{string.Join(", ", links.Where(l => l.Answer == SuiteSpecificationAnswer.Answers).Select(l => l.RequirementId))} appears to answer this. Confirm or reject the link; until then the question is open.");
        return (SuiteClarification.NotClarified, "No requirement in the project's specification was found that answers this, and no decision is recorded.");
    }

    public static (SuiteVerificationState State, string Detail) Verified(SecurityRequirementAssessment? probe) => probe?.State switch
    {
        null => (SuiteVerificationState.NotRun, "No run of this scenario is recorded."),
        SecurityRequirementState.Verified => (SuiteVerificationState.Verified, $"M01 runtime check: {probe.RuntimeTest}"),
        SecurityRequirementState.IssueDetected => (SuiteVerificationState.IssueDetected, $"M01 runtime check found an issue: {probe.RuntimeTest}"),
        SecurityRequirementState.NotTested or SecurityRequirementState.Missing => (SuiteVerificationState.NotRun, $"M01 runtime check: {probe.RuntimeTest}"),
        _ => (SuiteVerificationState.Partial, $"M01 runtime check: {SecurityClassificationAssessments.Label(probe.State)} — {probe.RuntimeTest}"),
    };

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
                Prerequisites = (r.State is SecurityRequirementState.Verified) ? [] : [r.RuntimeTest.Length > 0 ? r.RuntimeTest : r.SharedEvidence],
            });
        }

        string? Decided(string key) => i.Decisions.TryGetValue(key, out var d) ? d : null;
        SuiteSecurityScenario Variant(string id, string title, string[] levels, bool executor, bool observer, string? openQuestion, string reviewKey, string[] missing,
            string? reuses = null, string[]? refs = null)
        {
            var decided = Decided(reviewKey);
            var question = decided is "Acceptable" ? null : openQuestion;
            var links = i.SpecificationLinks?.TryGetValue(reviewKey, out var found) == true ? found : [];
            var (clarification, clarificationDetail) = Clarify(openQuestion, openQuestion is null ? null : decided, i.DecisionNotes?.TryGetValue(reviewKey, out var dn) == true ? dn : null, links);
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
                Clarification = clarification, ClarificationDetail = clarificationDetail, SpecificationLinks = openQuestion is null ? [] : links,
                Implementation = i.SourceRuleEvidence.Count > 0 ? SuiteImplementationEvidence.SourceEvidence : SuiteImplementationEvidence.NotAssessed,
                ImplementationDetail = i.SourceRuleEvidence.Count > 0
                    ? $"{i.SourceRuleEvidence.Count} classification symbol(s) in the selected source — static evidence of a mechanism, not proof that this behaviour works."
                    : "Not assessed: no classification source evidence in the selected snapshots.",
                Verification = SuiteVerificationState.NotRun, VerificationDetail = "No run of this scenario is recorded.",
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
        // Matched by the shared requirement key ("unauthorized-access", "authorized-graded-access"); the enum name never occurs in a key.
        SecurityRequirementAssessment? Probe(SecurityRequirementKind kind) => i.Consumer?.Requirements.FirstOrDefault(r => r.Key == SecurityClassificationAssessments.Key(kind));
        foreach (var (id, title, kind, key) in new[] { ("K67-06", "Unauthorized user attempts a protected-child lookup", SecurityRequirementKind.UnauthorizedRuntimeDenial, "security:unauthorized"),
                                                       ("K67-07", "Authorized user reads allowed information", SecurityRequirementKind.AuthorizedRuntimeAccess, "security:authorized") })
        {
            var probe = Probe(kind);
            var (verification, verificationDetail) = Verified(probe);
            list.Add(Variant(id, title, ["Kode 6", "Kode 7"], probe is not null, probe is not null, null, key,
                probe is null || probe.State == SecurityRequirementState.Verified ? [] : [$"M01 runtime probe: {probe.RuntimeTest} (test-lead approved context, Person endpoint, tokens supplied at run)"],
                $"Security Classification review · {kind}") with { Verification = verification, VerificationDetail = verificationDetail });
        }
        list.Add(Variant("K67-08", "BirkNext evidence and exports never expose protected identifiers (synthetic canaries)", [], true, true, null, "security:evidence-redaction", [],
            "Execution Evidence redaction — only BirkNext's own evidence and exports are inspectable; target-system logs are NOT ASSESSED"));
        list.Add(Variant("K67-09", "Period lock and downstream publication respect confidentiality", ["Kode 6", "Kode 7"], false, false,
            "Are rows for a Kode 6/7 child at lock ever delivered to Bufdata (later), and how? No approved rule is recorded.", "security:period-lock", []));
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

}
