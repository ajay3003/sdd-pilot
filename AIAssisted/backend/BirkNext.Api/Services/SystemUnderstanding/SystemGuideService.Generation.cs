using System.Text.Json;
using BirkNext.Api.Models;
using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Api.Services.TestPlanning;
using BirkNext.Integrations;
using BirkNext.SystemUnderstanding;
using BirkNext.TestPlanning;
using Microsoft.EntityFrameworkCore;
using D = BirkNext.Api.Services.TestPlanning.TestScenarioDiscoveryEngine;

namespace BirkNext.Api.Services.SystemUnderstanding;

/// <summary>
/// Source-driven System Understanding (2026-11-23). One generator, fed by the existing discovery providers' normalised scenarios (never a parser of
/// its own), producing an unreviewed candidate per guide and source version:
/// <list type="bullet">
/// <item>An imported application gets its own <b>application guide</b> (key "app-…", no curated seed). A registered guide with a curated seed (the
/// legacy Skolenærvær JSON) is used instead only when its source roles recognise the import — so no static JSON is needed for a new application.</item>
/// <item>The candidate is a <b>changeset</b> against the guide readers see: generated scenarios are matched to existing ones (by source identity,
/// else requirement id + situation; never by title alone); matches propose a changed expected result (or a conflict with a reviewed scenario),
/// unmatched ones are new, several matches are "ambiguous", and a generated scenario no longer in the source is "evidence removed". Nothing is
/// appended twice and nothing is applied automatically.</item>
/// <item>Reviewers decide per change (accept, reject, edit, defer, needs clarification) and apply the decided changes into the open draft (merged,
/// never overwritten) or a new draft. Approval and publishing stay the guide's own lifecycle.</item>
/// <item>Generation is a durable job (survives restarts, keeps diagnostics, can be retried, never produces a partial candidate); a newer source
/// version makes older pending jobs and unreviewed candidates of the same guide stale.</item>
/// </list>
/// </summary>
public sealed partial class SystemGuideService
{
    public const string ApplicationRole = "application";
    public const string GeneratorVersion = "source-guide-2";
    public static readonly string[] Decisions = ["Accepted", "Rejected", "Edited", "Deferred", "NeedsClarification"];

    public static bool IsApplicationGuide(string guideKey) => guideKey.StartsWith("app-", StringComparison.Ordinal);

    /// <summary>The guide key of an application (its logical source identity), short enough for the key column.</summary>
    public static string ApplicationGuideKey(string applicationKey) => "app-" + Sha(applicationKey)[..24];

    private static string Sha(string text) => TestPlanSuggestionEngine.Sha(text);

    /// <summary>Application guides known to this installation (they have a job, a candidate or a revision).</summary>
    public async Task<List<string>> ApplicationGuideKeysAsync(CancellationToken ct)
    {
        var keys = await db.SystemGuideGenerationJobs.AsNoTracking().Select(j => j.GuideKey).Distinct().ToListAsync(ct);
        keys.AddRange(await db.SystemGuideCandidates.AsNoTracking().Select(c => c.GuideKey).Distinct().ToListAsync(ct));
        keys.AddRange(await db.SystemGuideRevisions.AsNoTracking().Select(r => r.GuideKey).Distinct().ToListAsync(ct));
        return [.. keys.Where(IsApplicationGuide).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    private async Task<string> ApplicationTitleAsync(string guideKey, CancellationToken ct)
    {
        var label = await db.SystemGuideGenerationJobs.AsNoTracking().Where(j => j.GuideKey == guideKey).OrderByDescending(j => j.CreatedAt).Select(j => j.ApplicationKey).FirstOrDefaultAsync(ct);
        var candidate = await db.SystemGuideCandidates.AsNoTracking().Where(c => c.GuideKey == guideKey).OrderByDescending(c => c.CreatedAt).Select(c => c.ContentJson).FirstOrDefaultAsync(ct);
        var title = candidate is null ? null : JsonSerializer.Deserialize<SystemGuideContent>(candidate, Json)?.Title;
        return title is { Length: > 0 } ? title : label is { Length: > 0 } ? $"Systemforståelse: {label}" : "Systemforståelse (generert fra kilder)";
    }

    /// <summary>The (not yet saved) empty revision 0 of an application guide that has no revision: what readers see before anyone reviewed a proposal.</summary>
    private async Task<SystemGuideRevision> EmptyRevisionAsync(string guideKey, CancellationToken ct)
    {
        var content = new SystemGuideContent { Title = await ApplicationTitleAsync(guideKey, ct), Purpose = "Generert fra importerte kilder. Ingen revisjon er gjennomgått ennå." };
        return new SystemGuideRevision { GuideKey = guideKey, Revision = 0, State = GuideRevisionState.Draft, Content = content, ContentFingerprint = Fingerprint(content), CreatedBy = "BirkNext" };
    }

    /// <summary>The guides a snapshot feeds: registered (legacy seeded) guides whose roles recognise its application source; else its own application guide.</summary>
    public IReadOnlyList<(string GuideKey, string Role)> GuidesFor(IqrSourceSnapshot snapshot)
    {
        var result = new List<(string, string)>();
        foreach (var entry in catalog.Where(e => !string.IsNullOrEmpty(e.SeedResource)))
        {
            var roles = Roles(entry, LoadSeed(entry.SeedResource));
            var match = roles.Where(r => !r.RequiresInfrastructureAsCode && TestPackSources.Recognizes(r, snapshot)).Select(r => r.Key).Order(StringComparer.Ordinal).FirstOrDefault();
            if (match is not null) result.Add((entry.GuideKey, match));
        }
        if (result.Count == 0) result.Add((ApplicationGuideKey(SourceBaselineService.KeyOf(snapshot)), ApplicationRole));
        return result;
    }

    // ── Durable jobs ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Records generation jobs for an imported snapshot (fast; the worker runs them). Idempotent per guide, snapshot and generator version.
    /// Older pending jobs of the same guide become Stale: the newer source version is what needs a proposal.</summary>
    public async Task<IReadOnlyList<SystemGuideGenerationJobView>> EnqueueGenerationAsync(IqrSourceSnapshot snapshot, CancellationToken ct)
    {
        var views = new List<SystemGuideGenerationJobView>();
        var application = SourceBaselineService.KeyOf(snapshot);
        foreach (var (key, _) in GuidesFor(snapshot))
        {
            var job = await db.SystemGuideGenerationJobs.FirstOrDefaultAsync(j => j.GuideKey == key && j.SnapshotId == snapshot.Id && j.GeneratorVersion == GeneratorVersion, ct);
            if (job is null)
            {
                foreach (var older in await db.SystemGuideGenerationJobs.Where(j => j.GuideKey == key && j.Status == "Pending").ToListAsync(ct))
                { older.Status = "Stale"; older.LastError = $"En nyere kildeversjon ({snapshot.Archive.FileName}) ble importert før denne ble generert."; older.UpdatedAt = Now; }
                job = new SystemGuideGenerationJobRecord
                {
                    Id = Guid.NewGuid(), GuideKey = key, ApplicationKey = application, SnapshotId = snapshot.Id, SnapshotLabel = $"{snapshot.Archive.FileName} · {snapshot.AnalyzedAt:yyyy-MM-dd HH:mm}",
                    Status = "Pending", GeneratorVersion = GeneratorVersion, CreatedAt = Now, UpdatedAt = Now,
                };
                db.SystemGuideGenerationJobs.Add(job);
                try { await db.SaveChangesAsync(ct); }
                catch (DbUpdateException) { db.ChangeTracker.Clear(); job = await db.SystemGuideGenerationJobs.AsNoTracking().FirstAsync(j => j.GuideKey == key && j.SnapshotId == snapshot.Id && j.GeneratorVersion == GeneratorVersion, ct); }
            }
            views.Add(JobView(job));
        }
        return views;
    }

    public async Task<IReadOnlyList<SystemGuideGenerationJobView>> JobsAsync(string guideKey, CancellationToken ct) =>
        [.. (await db.SystemGuideGenerationJobs.AsNoTracking().Where(j => j.GuideKey == guideKey).OrderByDescending(j => j.CreatedAt).Take(20).ToListAsync(ct)).Select(JobView)];

    /// <summary>A failed (or stale) job back to Pending: the worker runs it again. A job that is pending, running or done is left as it is.</summary>
    public async Task<SystemGuideGenerationJobView> RetryJobAsync(string guideKey, Guid jobId, CancellationToken ct)
    {
        var job = await db.SystemGuideGenerationJobs.SingleOrDefaultAsync(j => j.Id == jobId && j.GuideKey == guideKey, ct) ?? throw new SystemGuideException(404, "Genereringen finnes ikke.");
        if (job.Status is "Failed" or "Stale") { job.Status = "Pending"; job.UpdatedAt = Now; await db.SaveChangesAsync(ct); }
        return JobView(job);
    }

    /// <summary>The next job to run (oldest pending, or one left running by a stopped process), marked Running. Null when there is none.</summary>
    public async Task<SystemGuideGenerationJobRecord?> ClaimNextJobAsync(CancellationToken ct)
    {
        var abandoned = Now.AddMinutes(-15);
        var job = await db.SystemGuideGenerationJobs.Where(j => j.Status == "Pending" || (j.Status == "Running" && j.UpdatedAt < abandoned)).OrderBy(j => j.CreatedAt).FirstOrDefaultAsync(ct);
        if (job is null) return null;
        job.Status = "Running"; job.Attempts++; job.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);
        return job;
    }

    public async Task FinishJobAsync(Guid jobId, Guid? candidateId, string? error, CancellationToken ct)
    {
        var job = await db.SystemGuideGenerationJobs.SingleAsync(j => j.Id == jobId, ct);
        job.Status = job.Status == "Stale" ? "Stale" : error is null ? "Succeeded" : "Failed";
        job.LastError = error is null ? null : error[..Math.Min(error.Length, 2000)];
        job.CandidateId = candidateId ?? job.CandidateId;
        job.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);
    }

    private static SystemGuideGenerationJobView JobView(SystemGuideGenerationJobRecord j) =>
        new(j.Id, j.GuideKey, j.SnapshotId, j.SnapshotLabel, j.Status, j.Attempts, j.LastError, j.GeneratorVersion, j.CandidateId, j.CreatedAt, j.UpdatedAt);

    // ── The generator ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The one candidate generator: from the discovery providers' scenarios of one source version (already normalised and consolidated), a changeset
    /// against the guide readers see. Specification acceptance scenarios and integration journeys become scenarios; requirements no scenario names
    /// become business rules; contracts, UI and tests are recorded as supporting evidence only (Test Plan Suggestions offers them directly). No
    /// expected result is invented: a scenario without one is "needs clarification". Idempotent per guide, source content, specification and guide state.
    /// </summary>
    public async Task<SystemGuideCandidateView?> GenerateCandidateAsync(string guideKey, string role, IqrSourceSnapshot snapshot, IReadOnlyList<DiscoveredScenario> discovered,
        string? specificationFingerprint, CancellationToken ct, IReadOnlySet<string>? evaluated = null)
    {
        var entry = Entry(guideKey);
        await EnsureSeededAsync(entry, ct);
        var (shown, _) = await ShownAsync(guideKey, ct);
        var identity = ReviewSourceEvidenceProvider.Identity(snapshot);
        var label = snapshot.Archive.FileName;
        GuideEvidence Evidence(DiscoveredScenario c, GuideEvidenceClass cls)
        {
            var reference = c.Evidence.FirstOrDefault(e => e.Kind is "specification" or "requirement" or "journey" or "contract-operation")?.Reference ?? c.CanonicalKey;
            var file = reference.Contains('#') ? reference[..reference.IndexOf('#')] : null;
            return new GuideEvidence
            {
                Class = cls, SourceRole = role, SourceLabel = label, File = file, RequirementId = c.RequirementIds.FirstOrDefault(),
                Note = $"{c.SourceCategory}: {reference} (generert fra kildeversjon {label})",
            };
        }
        var generated = new List<GuideTestScenario>();
        var rules = new List<GuideRule>();
        var supporting = new List<string>();
        foreach (var c in discovered)
        {
            if (c.CapabilityId == D.Specification && c.CanonicalKey.StartsWith("scenario:", StringComparison.Ordinal))
                generated.Add(Scenario(c, "spec-", Evidence(c, GuideEvidenceClass.Specified)));
            else if (c.CapabilityId == D.IntegrationJourneys)
                generated.Add(Scenario(c, "flow-", Evidence(c, c.Engine.StartsWith("Test pack", StringComparison.Ordinal) ? GuideEvidenceClass.TestDefined : GuideEvidenceClass.Inferred)));
            else if (c.CapabilityId == D.Specification && c.CanonicalKey.StartsWith("requirement:", StringComparison.Ordinal) && c.RequirementIds.FirstOrDefault() is { } requirement)
                rules.Add(new GuideRule
                {
                    Id = "req:" + requirement, Category = "Krav", Title = requirement, Summary = c.Purpose, ExpectedBehavior = c.ExpectedBehaviour,
                    Implementation = GuideEvidenceClass.Specified, Evidence = [Evidence(c, GuideEvidenceClass.Specified)],
                });
        }
        foreach (var group in discovered.Where(c => c.CapabilityId is D.ApiContract or D.UiSource or D.DeveloperTests).GroupBy(c => c.SourceCategory))
            supporting.Add($"{group.Key}: {group.Count()} scenarioer (støttende bevis — tilbys direkte i Testplanforslag, ikke kopiert inn i veiledningen).");
        var tests = discovered.SelectMany(c => c.ExistingTests).DistinctBy(t => t.TestId).Count();
        if (tests > 0) supporting.Add($"Eksisterende automatiske tester: {tests} tester refererer krav eksplisitt (testdekning, ikke scenarioer).");

        var current = shown.Content;
        if (shown.Revision == 0)
            current = current with
            {
                Title = $"Systemforståelse: {identity.DisplayName}", Purpose = "Generert fra importerte kilder. Gjennomgå forslagene før veiledningen brukes som fasit.",
                SourceRoles = role == ApplicationRole ? [new GuideSourceRole(ApplicationRole, identity.DisplayName, [.. SourceBaselineService.ComponentsOf(snapshot)])] : current.SourceRoles,
            };
        var reviewed = shown.State == GuideRevisionState.Published;
        // A generated scenario "no longer in the source" is proposed only for a source that was actually read (its analyzer found evidence);
        // a specification that was not captured or an analyzer without input proves nothing about removal.
        var read = evaluated ?? new HashSet<string>(StringComparer.Ordinal);
        var changes = Reconcile(current, generated, reviewed, role, s =>
            s.Id.StartsWith("spec-", StringComparison.Ordinal) ? read.Contains(D.Specification)
            : s.Id.StartsWith("flow-", StringComparison.Ordinal) ? read.Contains(D.IntegrationJourneys) : false);
        // Rules: requirements that are new to the guide (never rewriting a written rule).
        var rulesToAdd = rules.Where(r => current.Rules.All(x => x.Id != r.Id)).ToList();
        var content = Apply(current with { Rules = [.. current.Rules, .. rulesToAdd] }, changes, null);
        var inputFingerprint = Sha(string.Join("|", guideKey, SourceBaselineService.ContentOf(snapshot), specificationFingerprint ?? "", GeneratorVersion, shown.Revision == 0 ? "no-revision" : shown.ContentFingerprint));
        var prior = await db.SystemGuideCandidates.AsNoTracking().FirstOrDefaultAsync(c => c.GuideKey == guideKey && c.InputFingerprint == inputFingerprint, ct);
        if (prior is not null) return ToCandidate(prior);
        var row = new SystemGuideCandidateRecord
        {
            Id = Guid.NewGuid(), GuideKey = guideKey, SourceSnapshotId = snapshot.Id, SourceFingerprint = SourceBaselineService.ContentOf(snapshot)[..Math.Min(100, SourceBaselineService.ContentOf(snapshot).Length)],
            InputFingerprint = inputFingerprint, ContentFingerprint = Fingerprint(content), ContentJson = JsonSerializer.Serialize(content, Json),
            ChangesJson = JsonSerializer.Serialize(changes, Json), EvidenceJson = JsonSerializer.Serialize(generated.SelectMany(g => g.Evidence).Concat(rulesToAdd.SelectMany(r => r.Evidence)), Json),
            SupportingJson = JsonSerializer.Serialize(supporting, Json), Status = changes.Count == 0 && rulesToAdd.Count == 0 ? "NoChanges" : "NeedsReview",
            CreatedAt = Now, GeneratorVersion = GeneratorVersion, SourceLabel = $"{label} · {snapshot.AnalyzedAt:yyyy-MM-dd HH:mm}",
        };
        db.SystemGuideCandidates.Add(row);
        // Unreviewed candidates of an OLDER source version of the same guide are stale (kept, never deleted). Source age decides, never the order
        // the jobs ran in: a candidate generated late for an older version (a retry) is itself stale when a newer version already has one.
        var others = await db.SystemGuideCandidates.Where(c => c.GuideKey == guideKey && c.SourceSnapshotId != snapshot.Id).ToListAsync(ct);
        var ids = others.Select(c => c.SourceSnapshotId).Distinct().ToList();
        var analyzed = await db.IqrSourceSnapshots.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => new { x.Id, x.AnalyzedAt }).ToDictionaryAsync(x => x.Id, x => x.AnalyzedAt, ct);
        var newer = others.Where(c => analyzed.TryGetValue(c.SourceSnapshotId, out var at) && at > snapshot.AnalyzedAt).OrderByDescending(c => analyzed[c.SourceSnapshotId]).FirstOrDefault();
        if (newer is not null) { row.Status = "Stale"; row.SupersededBy = newer.Id; }
        foreach (var older in others.Where(c => c.Status == "NeedsReview" && analyzed.TryGetValue(c.SourceSnapshotId, out var at) && at < snapshot.AnalyzedAt))
        { older.Status = "Stale"; older.SupersededBy = row.Id; }
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            var won = await db.SystemGuideCandidates.AsNoTracking().FirstOrDefaultAsync(c => c.GuideKey == guideKey && c.InputFingerprint == inputFingerprint, ct);
            return won is null ? null : ToCandidate(won);
        }
        return ToCandidate(row);
    }

    private static GuideTestScenario Scenario(DiscoveredScenario c, string prefix, GuideEvidence evidence) => new()
    {
        Id = prefix + c.SourceIdentity.Replace("tss-", "", StringComparison.Ordinal), Title = c.Title, Objective = c.Purpose,
        Preconditions = c.Preconditions.Count == 0 ? null : string.Join("\n", c.Preconditions), TestData = c.TestDataNeeds.Count == 0 ? null : string.Join("; ", c.TestDataNeeds),
        ExpectedOutcome = c.ExpectedBehaviour, State = c.ExpectedBehaviour is null ? GuideScenarioState.NeedsClarification : GuideScenarioState.Suggested,
        FollowUp = c.OpenQuestions.FirstOrDefault(), ExistingTests = [.. c.ExistingTests.Select(t => t.Name)], Evidence = [evidence], SourceIdentity = c.SourceIdentity,
    };

    /// <summary>The changeset of generated scenarios against a guide's content (see the class summary). Pure; unit-testable.</summary>
    public static List<SystemGuideCandidateChange> Reconcile(SystemGuideContent current, IReadOnlyList<GuideTestScenario> generated, bool reviewed, string role,
        Func<GuideTestScenario, bool>? removable = null)
    {
        var changes = new List<SystemGuideCandidateChange>();
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        string Words(GuideTestScenario s) => $"{s.Title} {s.Preconditions}";
        HashSet<string> Requirements(GuideTestScenario s) => [.. s.Evidence.Select(e => e.RequirementId).OfType<string>()
            .Concat(current.Rules.Where(r => r.TestScenarioIds.Contains(s.Id, StringComparer.Ordinal)).Select(r => r.Id)).Select(x => x.ToUpperInvariant())];
        foreach (var g in generated)
        {
            var match = current.Scenarios.FirstOrDefault(s => s.SourceIdentity == g.SourceIdentity);
            var basis = "samme kildeidentitet";
            List<GuideTestScenario> alternatives = [];
            if (match is null)
            {
                var wanted = Requirements(g);
                var situation = ScenarioConsolidation.Words(Words(g));
                alternatives = [.. current.Scenarios.Where(s => s.SourceIdentity is null && !claimed.Contains(s.Id)).Where(s =>
                {
                    var similarity = ScenarioConsolidation.Similarity(situation, ScenarioConsolidation.Words(Words(s)));
                    return wanted.Count > 0 && Requirements(s).Overlaps(wanted) ? similarity >= ScenarioConsolidation.SimilarSituation : similarity >= ScenarioConsolidation.SameSituation;
                })];
                if (alternatives.Count == 1) { match = alternatives[0]; basis = wanted.Count > 0 && Requirements(match).Overlaps(wanted) ? "samme krav og lik situasjon" : "lik situasjon"; }
            }
            if (match is null && alternatives.Count > 1)
            {
                changes.Add(Change("AmbiguousMatch", g.Id, g.Title, null, g.ExpectedOutcome, [], g.Evidence, "flere skrevne scenarier kan være dette", g, [.. alternatives.Select(a => a.Id)]));
                continue;
            }
            if (match is null) { changes.Add(Change("NewScenario", g.Id, g.Title, null, g.ExpectedOutcome, [], g.Evidence, "ingen tilsvarende scenario i veiledningen", g)); continue; }
            claimed.Add(match.Id);
            // The written scenario keeps its own words and decisions; only the expected result, the evidence and the source link are proposed.
            var proposed = match with { ExpectedOutcome = g.ExpectedOutcome ?? match.ExpectedOutcome, Evidence = [.. match.Evidence.Where(e => e.SourceRole != role || e.SourceLabel is null), .. g.Evidence], SourceIdentity = g.SourceIdentity };
            if (string.Equals(Norm(match.ExpectedOutcome), Norm(g.ExpectedOutcome), StringComparison.OrdinalIgnoreCase) || g.ExpectedOutcome is null)
            {
                if (match.SourceIdentity is null) changes.Add(Change("LinkedToSource", match.Id, match.Title, match.ExpectedOutcome, match.ExpectedOutcome, match.Evidence, g.Evidence, basis, proposed));
                continue;
            }
            var conflict = reviewed && ScenarioConsolidation.Contradict(match.ExpectedOutcome, g.ExpectedOutcome);
            changes.Add(Change(conflict ? "ConflictsWithReviewed" : "ChangedExpectedResult", match.Id, match.Title, match.ExpectedOutcome, g.ExpectedOutcome, match.Evidence, g.Evidence, basis, proposed));
        }
        // A scenario generated earlier from this source that the source no longer has: proposed for review (never deleted automatically).
        var now = generated.Select(g => g.SourceIdentity).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var gone in current.Scenarios.Where(s => s.SourceIdentity is { } id && !now.Contains(id) && s.Evidence.Any(e => e.SourceRole == role) && (removable?.Invoke(s) ?? true)))
            changes.Add(Change("EvidenceRemoved", gone.Id, gone.Title, gone.ExpectedOutcome, null, gone.Evidence, [], "finnes ikke lenger i kilden",
                gone with { State = GuideScenarioState.NeedsClarification, FollowUp = "Scenarioet finnes ikke lenger i den importerte kilden. Avklar om det fortsatt gjelder." }));
        return changes;
    }

    private static string Norm(string? text) => string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static SystemGuideCandidateChange Change(string kind, string scenarioId, string title, string? previous, string? proposed, IReadOnlyList<GuideEvidence> previousEvidence,
        IReadOnlyList<GuideEvidence> proposedEvidence, string basis, GuideTestScenario scenario, IReadOnlyList<string>? alternatives = null) =>
        new(kind, scenarioId, title, previous, proposed, previousEvidence, proposedEvidence, Sha($"{kind}|{scenarioId}|{proposed}")[..12], basis, scenario, alternatives);

    /// <summary>
    /// Applies changes to guide content. <paramref name="decisions"/> null = every change as proposed (the candidate's preview); otherwise only the
    /// accepted, edited and needs-clarification ones (an accepted ambiguous match is added as its own scenario; an accepted removal removes it).
    /// </summary>
    public static SystemGuideContent Apply(SystemGuideContent content, IReadOnlyList<SystemGuideCandidateChange> changes, IReadOnlyDictionary<string, GuideChangeDecision>? decisions)
    {
        var scenarios = content.Scenarios.ToList();
        foreach (var change in changes)
        {
            if (change.Proposed is not { } proposed) continue;
            GuideChangeDecision? decision = null;
            if (decisions is not null && (change.ChangeId is null || !decisions.TryGetValue(change.ChangeId, out decision) || decision.Decision is "Rejected" or "Deferred")) continue;
            if (decision?.Decision == "Edited" && decision.EditedExpected is { Length: > 0 } edited) proposed = proposed with { ExpectedOutcome = edited, State = GuideScenarioState.Suggested };
            if (decision?.Decision == "NeedsClarification") proposed = proposed with { State = GuideScenarioState.NeedsClarification, FollowUp = decision.Rationale ?? proposed.FollowUp };
            var index = scenarios.FindIndex(s => s.Id == change.ScenarioId);
            switch (change.Kind)
            {
                case "EvidenceRemoved" when decisions is not null && decision?.Decision is "Accepted":
                    if (index >= 0) scenarios.RemoveAt(index);
                    break;
                case "NewScenario" or "AmbiguousMatch":
                    if (index < 0 && (change.Kind == "NewScenario" || decisions is not null)) scenarios.Add(proposed);
                    break;
                default:
                    if (index >= 0) scenarios[index] = SourceOwned(scenarios[index], proposed, change.Kind);
                    break;
            }
        }
        return content with { Scenarios = scenarios };
    }

    /// <summary>
    /// A written scenario with only what the source owns taken from the proposal: the expected result (not for a link or a removal notice), the
    /// source's own evidence and the source link, and the state/follow-up a decision or removal sets. Title, objective, preconditions, test data,
    /// steps and every other word of the draft stay as the person wrote them.
    /// </summary>
    private static GuideTestScenario SourceOwned(GuideTestScenario written, GuideTestScenario proposed, string kind) => written with
    {
        ExpectedOutcome = kind is "LinkedToSource" or "EvidenceRemoved" ? written.ExpectedOutcome : proposed.ExpectedOutcome,
        Evidence = [.. written.Evidence.Where(e => proposed.Evidence.All(p => p.SourceRole != e.SourceRole || p.SourceLabel is null || e.SourceLabel is null || p.SourceLabel == e.SourceLabel)),
            .. proposed.Evidence.Where(p => !written.Evidence.Contains(p))],
        SourceIdentity = proposed.SourceIdentity ?? written.SourceIdentity,
        State = proposed.State == GuideScenarioState.NeedsClarification ? GuideScenarioState.NeedsClarification : written.State,
        FollowUp = proposed.State == GuideScenarioState.NeedsClarification ? proposed.FollowUp ?? written.FollowUp : written.FollowUp,
    };

    /// <summary>Compatibility entry for callers that hold an import's documents (older tests and tools): the specification provider's scenarios,
    /// through the same generator.</summary>
    public async Task<IReadOnlyList<SystemGuideCandidateView>> GenerateSpecificationCandidatesAsync(IqrSourceSnapshot snapshot, IEnumerable<(string Path, string Content)> documents, CancellationToken ct)
    {
        var evidence = Specifications.SpecificationEvidenceBuilder.BuildAll(documents.Select(d => new Specifications.SpecificationCatalogService.Document(d.Path, d.Content)));
        if (evidence.Count == 0) return [];
        var result = new List<SystemGuideCandidateView>();
        foreach (var (key, role) in GuidesFor(snapshot))
        {
            var discovered = D.Finalize(key, snapshot.Id.ToString(), evidence.SelectMany(e => D.FromSpecification(key, e.Evidence with { ArtifactScope = evidence.Count > 1 ? e.Path : null }).Candidates), []);
            if (await GenerateCandidateAsync(key, role, snapshot, discovered, Sha(string.Join("\n", evidence.Select(e => e.ContentSha256))), ct,
                new HashSet<string>(StringComparer.Ordinal) { D.Specification }) is { } view) result.Add(view);
        }
        return result;
    }

    // ── Review of a candidate ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Records a reviewer's decision on one proposed change (the latest decision per change wins; earlier ones are kept in order).</summary>
    public async Task<SystemGuideCandidateView> DecideAsync(string guideKey, Guid candidateId, DecideGuideChangeRequest request, string actor, CancellationToken ct)
    {
        Entry(guideKey);
        var candidate = await db.SystemGuideCandidates.SingleOrDefaultAsync(c => c.GuideKey == guideKey && c.Id == candidateId, ct) ?? throw new SystemGuideException(404, "Forslaget finnes ikke.");
        if (candidate.Status is "Stale") throw new SystemGuideException(409, "Forslaget er utdatert: en nyere kildeversjon har et eget forslag.");
        var changes = JsonSerializer.Deserialize<List<SystemGuideCandidateChange>>(candidate.ChangesJson, Json) ?? [];
        if (changes.All(c => c.ChangeId != request.ChangeId)) throw new SystemGuideException(404, "Endringen finnes ikke i forslaget.");
        if (!Decisions.Contains(request.Decision)) throw new SystemGuideException(400, "Ukjent beslutning.");
        var rationale = string.IsNullOrWhiteSpace(request.Rationale) ? null : request.Rationale.Trim()[..Math.Min(request.Rationale.Trim().Length, 1000)];
        if (request.Decision == "Edited" && string.IsNullOrWhiteSpace(request.EditedExpected)) throw new SystemGuideException(400, "Skriv det forventede resultatet du vil bruke.");
        if (request.Decision is "Rejected" or "NeedsClarification" && rationale is null) throw new SystemGuideException(400, "Begrunn beslutningen.");
        var decisions = (candidate.DecisionsJson is null ? [] : JsonSerializer.Deserialize<List<GuideChangeDecision>>(candidate.DecisionsJson, Json) ?? []).ToList();
        decisions.Add(new GuideChangeDecision(request.ChangeId, request.Decision, request.EditedExpected?.Trim(), rationale, actor, Now));
        candidate.DecisionsJson = JsonSerializer.Serialize(decisions, Json);
        await db.SaveChangesAsync(ct);
        return ToCandidate(candidate);
    }

    /// <summary>
    /// Merges the decided changes of a candidate into the open guide draft — the draft's own edits stay; only the decided scenarios change — or, when
    /// none is open, into a new draft from the guide readers see. A revision in review must be returned to draft first; an approved revision must be
    /// published first. Never approves or publishes anything.
    /// </summary>
    public async Task<SystemGuideRevision> ApplyCandidateAsync(string guideKey, Guid candidateId, ApplyGuideCandidateRequest request, string actor, CancellationToken ct)
    {
        var entry = Entry(guideKey);
        await EnsureSeededAsync(entry, ct);
        var candidate = await db.SystemGuideCandidates.SingleOrDefaultAsync(c => c.GuideKey == guideKey && c.Id == candidateId, ct) ?? throw new SystemGuideException(404, "Forslaget finnes ikke.");
        if (candidate.Status == "Stale") throw new SystemGuideException(409, "Forslaget er utdatert: en nyere kildeversjon har et eget forslag.");
        var changes = JsonSerializer.Deserialize<List<SystemGuideCandidateChange>>(candidate.ChangesJson, Json) ?? [];
        var decisions = (candidate.DecisionsJson is null ? [] : JsonSerializer.Deserialize<List<GuideChangeDecision>>(candidate.DecisionsJson, Json) ?? [])
            .GroupBy(d => d.ChangeId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        if (!decisions.Values.Any(d => d.Decision is "Accepted" or "Edited" or "NeedsClarification"))
            throw new SystemGuideException(400, decisions.Values.Any(d => d.Decision == "Applied")
                ? "De godtatte endringene er allerede lagt inn. Ta stilling til de øvrige endringene først." : "Ingen endring er godtatt ennå. Ta stilling til endringene først.");
        var rows = await db.SystemGuideRevisions.Where(r => r.GuideKey == guideKey).ToListAsync(ct);
        if (rows.Any(r => r.State == nameof(GuideRevisionState.InReview))) throw new SystemGuideException(409, "En revisjon er til gjennomgang. Send den tilbake til utkast før endringer legges inn.");
        if (rows.Any(r => r.State == nameof(GuideRevisionState.Approved))) throw new SystemGuideException(409, "En godkjent revisjon venter på publisering. Publiser den først.");
        var open = rows.FirstOrDefault(r => r.State == nameof(GuideRevisionState.Draft));
        if (open is not null && request.BaseFingerprint is null)
            throw new SystemGuideException(409, "Et utkast er åpent. Last inn veiledningen og sammenlign med utkastet før endringene legges inn.");
        if (open is not null && request.BaseFingerprint is { } baseFingerprint && baseFingerprint != open.ContentFingerprint)
            throw new SystemGuideException(409, "Utkastet er endret siden du sammenlignet. Last inn på nytt og se over endringene igjen.");
        var proposal = JsonSerializer.Deserialize<SystemGuideContent>(candidate.ContentJson, Json) ?? new();
        var baseContent = open is not null ? Content(open)
            : rows.Count > 0 ? (await ShownAsync(guideKey, ct)).Shown.Content
            : proposal with { Scenarios = [] }; // a new application guide: its metadata and rules from the proposal, scenarios only as decided
        // Rules and source roles the proposal adds are carried over (additive); the decided scenarios are merged.
        var merged = Apply(baseContent with
        {
            Rules = [.. baseContent.Rules, .. proposal.Rules.Where(r => baseContent.Rules.All(x => x.Id != r.Id))],
            SourceRoles = baseContent.SourceRoles.Count > 0 ? baseContent.SourceRoles : proposal.SourceRoles,
        }, changes, decisions);
        var problems = SystemGuideValidation.Problems(merged);
        if (problems.Count > 0) throw new SystemGuideException(422, "Endringene kan ikke legges inn: " + string.Join("; ", problems.Take(5)));
        var applied = changes.Count(c => c.ChangeId is { } id && decisions.TryGetValue(id, out var d) && d.Decision is "Accepted" or "Edited" or "NeedsClarification");
        var summary = $"{applied} endring{(applied == 1 ? "" : "er")} fra kildeforslag {candidate.Id} ({candidate.SourceLabel ?? candidate.SourceSnapshotId.ToString()}) lagt inn av {actor}.";
        var basis = new GuideSourceBasis(guideKey.StartsWith("app-", StringComparison.Ordinal) ? ApplicationRole : "source", candidate.SourceSnapshotId, candidate.SourceFingerprint, $"Kildeversjon {candidate.SourceLabel}");
        SystemGuideRevisionRecord row;
        if (open is not null)
        {
            open.ContentJson = JsonSerializer.Serialize(merged, Json);
            open.ContentFingerprint = Fingerprint(merged);
            var held = JsonSerializer.Deserialize<List<GuideSourceBasis>>(open.SourceBasisJson, Json) ?? [];
            open.SourceBasisJson = JsonSerializer.Serialize(held.Where(b => b.SnapshotId != basis.SnapshotId).Append(basis).ToList(), Json);
            open.ChangeSummary = string.Join(" ", new[] { open.ChangeSummary, summary }.Where(x => !string.IsNullOrWhiteSpace(x)))[..Math.Min(1000, string.Join(" ", new[] { open.ChangeSummary, summary }.Where(x => !string.IsNullOrWhiteSpace(x))).Length)];
            open.CreatedBy = open.CreatedBy == SeedAuthor ? $"{SeedAuthor}; edited by {actor}" : open.CreatedBy;
            row = open;
        }
        else
        {
            row = new SystemGuideRevisionRecord
            {
                Id = Guid.NewGuid(), GuideKey = guideKey, Revision = (rows.Count == 0 ? 0 : rows.Max(r => r.Revision)) + 1, State = nameof(GuideRevisionState.Draft),
                ContentJson = JsonSerializer.Serialize(merged, Json), ContentFingerprint = Fingerprint(merged), SourceBasisJson = JsonSerializer.Serialize(new[] { basis }, Json),
                CreatedBy = actor, CreatedAt = Now, ChangeSummary = summary,
            };
            db.SystemGuideRevisions.Add(row);
        }
        // The applied changes are recorded as such: applying again never repeats them (a new decision on a change makes it applicable again).
        var log = (candidate.DecisionsJson is null ? [] : JsonSerializer.Deserialize<List<GuideChangeDecision>>(candidate.DecisionsJson, Json) ?? []).ToList();
        foreach (var change in changes.Where(c => c.ChangeId is { } id && decisions.TryGetValue(id, out var d) && d.Decision is "Accepted" or "Edited" or "NeedsClarification"))
            log.Add(new GuideChangeDecision(change.ChangeId!, "Applied", null, $"Lagt inn i utkast {row.Revision}", actor, Now));
        candidate.DecisionsJson = JsonSerializer.Serialize(log, Json);
        candidate.Status = decisions.Count >= changes.Count ? "Applied" : "PartiallyApplied";
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); throw new SystemGuideException(409, "Veiledningen ble endret samtidig. Last inn på nytt og prøv igjen."); }
        return ToRevision(row);
    }
}
