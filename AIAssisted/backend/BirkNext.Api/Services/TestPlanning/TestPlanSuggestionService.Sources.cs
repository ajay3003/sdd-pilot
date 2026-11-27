using System.Text.Json;
using BirkNext.Api.Models;
using BirkNext.TestPlanning;
using Microsoft.EntityFrameworkCore;
using D = BirkNext.Api.Services.TestPlanning.TestScenarioDiscoveryEngine;
using E = BirkNext.Api.Services.TestPlanning.TestPlanSuggestionEngine;

namespace BirkNext.Api.Services.TestPlanning;

/// <summary>
/// Source-driven planning (2026-11-23): a project plan follows its <b>application</b> (the logical identity of its application source), so a later
/// import of the same application compares the plan against the newer source. Every comparison is against the application's <b>current</b>
/// discovery (basis = analysis baseline) — never a historical inspection. A case whose scenario is gone is "removed" only after a complete,
/// successful comparison; otherwise its source is "unavailable". Nothing saved is rewritten by a comparison: changes are flags, an impact analysis
/// and an explicit Update.
/// </summary>
public sealed partial class TestPlanSuggestionService
{
    /// <summary>Finds a case's scenario in a discovery: by candidate id (same import), else by project-independent identity or a merged identity.</summary>
    internal sealed class CandidateIndex
    {
        private readonly Dictionary<string, DiscoveredScenario> _byId;
        private readonly Dictionary<string, DiscoveredScenario> _bySource;
        public IReadOnlyList<DiscoveredScenario> All { get; }

        public CandidateIndex(IEnumerable<DiscoveredScenario> candidates)
        {
            All = [.. candidates];
            _byId = All.GroupBy(c => c.CandidateId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            _bySource = new(StringComparer.Ordinal);
            foreach (var c in All)
            {
                if (c.SourceIdentity.Length > 0) _bySource.TryAdd(c.SourceIdentity, c);
                foreach (var merged in c.MergedFrom) _bySource.TryAdd(merged, c);
                foreach (var former in c.FormerIdentities) _bySource.TryAdd(former, c);
            }
        }

        /// <summary>The source identity first (the same scenario in any import, robust to positional renumbering); a case prepared before identities
        /// existed falls back to its candidate id (same import only).</summary>
        public DiscoveredScenario? Find(TestCaseDraft c)
        {
            if (c.Generated.SourceIdentity is not { Length: > 0 } identity) return _byId.TryGetValue(c.Source.ScenarioId, out var byId) ? byId : null;
            if (_bySource.TryGetValue(identity, out var bySource)) return bySource;
            // Scenarios with the same situation carry their outcome as a suffix; when one of the twins is added or removed the other's suffix
            // appears or disappears. The same scenario is then the one with the same situation and outcome — or the only one with that situation.
            var cut = identity.IndexOf('|');
            var situation = cut < 0 ? identity : identity[..cut];
            var same = All.Where(x => x.SourceIdentity == situation || x.SourceIdentity.StartsWith(situation + "|", StringComparison.Ordinal)).ToList();
            if (cut >= 0) return same.FirstOrDefault(x => D.TwinSuffix(x.ExpectedBehaviour) == identity[(cut + 1)..]);
            return same.Count == 1 ? same[0] : null;
        }
    }

    /// <summary>The application's current discovery for a plan (a later import of the same application included); never a historical inspection.
    /// V10 (2026-11-27): the current discovery of the plan's OWN selection (module + specification of the discovery it was made from) when one is
    /// known. Before, the newest discovery of any selection was taken: a plan of one module, opened after the tester looked at another selection,
    /// had its cases marked «Changed», approval refused as «source changed», and that selection's scenarios counted as «not in the plan».</summary>
    private ScenarioDiscoveryResult? CurrentFor(TestPlanDraft plan)
    {
        if (discoveries is null || plan.ProjectKey is not { } key) return null;
        var scope = plan.DiscoveryId is { } made ? discoveries.Find(key, made)?.SelectionScope : null;
        if (scope is not null)
        {
            var mine = discoveries.LatestForScope(key, scope);
            var app = (plan.ApplicationKey ?? mine?.Basis?.ApplicationKey) is { } appKey ? discoveries.CurrentForApplication(appKey) : null;
            if (app is not null && app.SelectionScope == scope && (mine is null || app.DiscoveredAt >= mine.DiscoveredAt)) return app;
            if (mine is not null) return mine;
        }
        var own = discoveries.Latest(key);
        var application = plan.ApplicationKey ?? own?.Basis?.ApplicationKey;
        var shared = application is null ? null : discoveries.CurrentForApplication(application);
        return shared is not null && (own is null || shared.DiscoveredAt >= own.DiscoveredAt) ? shared : own;
    }

    /// <summary>Restores (after a restart) the project's and its application's current discovery.</summary>
    private async Task RestoreCurrentAsync(TestPlanDraft plan, CancellationToken ct)
    {
        if (plan.ProjectKey is null || discoveryStore is null) return;
        await discoveryStore.RestoreAsync(plan.ProjectKey, null, ct);
        await discoveryStore.RestoreScopesAsync(plan.ProjectKey, ct);
        var application = plan.ApplicationKey ?? discoveries?.Latest(plan.ProjectKey)?.Basis?.ApplicationKey;
        if (application is not null) await discoveryStore.RestoreApplicationAsync(application, ct);
    }

    /// <summary>
    /// Why a case's scenario is not in a discovery: Removed only when the comparison is complete (every source of the basis resolved, the case's
    /// own source and specification part of it, and its analyzer ran without failure); otherwise SourceUnavailable with the reason.
    /// </summary>
    internal static (CaseSourceState State, string Reason) Absence(TestCaseDraft c, ScenarioDiscoveryResult current, DiscoverySourceBasis? planBasis)
    {
        if (current.Basis is not { } basis) return (CaseSourceState.SourceUnavailable, "Kilde mangler: den gjeldende oppdagelsen har ikke et registrert kildegrunnlag. Oppdag testscenarier på nytt.");
        if (!basis.Complete)
            return (CaseSourceState.SourceUnavailable, "Kilde mangler: " + (basis.Problems.FirstOrDefault() ?? "en kilde i grunnlaget er ikke valgt."));
        // A case prepared before scenarios had a project-independent identity can only be found in its own import (its id is project-scoped).
        if (c.Generated.SourceIdentity is null && !string.Equals(current.ProjectKey, c.Source.Owner, StringComparison.Ordinal))
            return (CaseSourceState.SourceUnavailable, "Kilde mangler: testtilfellet ble forberedt før scenarioer fikk en identitet på tvers av importer, og kan ikke sammenlignes med en nyere import. Forbered scenarioet på nytt fra gjeldende oppdagelse.");
        var capability = current.Capabilities.FirstOrDefault(x => x.Id == c.Generated.Capability);
        if (capability is null || capability.State is DiscoveryCapabilityState.Failed or DiscoveryCapabilityState.NotApplicable || capability.PartialFailure)
            return (CaseSourceState.SourceUnavailable, $"Kilde mangler: {(capability?.Label ?? c.Generated.Capability ?? "kilden")} kunne ikke leses i gjeldende kildeversjon. Det betyr ikke at scenarioet er fjernet.");
        if (c.Generated.Capability == D.Specification && basis.SpecificationOrigin == SpecificationOrigin.None)
            return (CaseSourceState.SourceUnavailable, "Kilde mangler: gjeldende grunnlag har ingen spesifikasjon.");
        if (c.Generated.Capability == D.Specification)
        {
            // The scenario's own specification file must be one the current discovery read. A discovery made with another specification selected
            // (2026-11-24, M2LB SkoleService: spec.md vs docs/avhengigheter-bestilling.md) does not contain it, and its absence proves nothing —
            // before, every case of the other specification was shown as «Utgått scenario».
            var own = SpecificationPathOf(c);
            var read = current.Candidates.Where(x => x.CapabilityId == D.Specification).Select(SpecificationPathOf).OfType<string>().ToList();
            if (own is null)
                return (CaseSourceState.SourceUnavailable, "Kilde mangler: det er ikke registrert hvilken spesifikasjon scenarioet kom fra, så det kan ikke avgjøres om det er fjernet.");
            // Another specification is selected in Finn og forstå (it reads one at a time): the case is outside this comparison — not missing.
            if (!read.Any(p => BirkNext.Specifications.SpecificationCatalogRules.SamePath(p, own)))
                return (CaseSourceState.NotCompared, $"Ikke sammenlignet: spesifikasjonen testtilfellet kommer fra ({own}) er ikke lest for gjeldende kildeversjon. Testtilfellet vises slik det er lagret; velg spesifikasjonen i Finn og forstå for å sammenligne.");
        }
        if (c.Generated.Capability != D.Specification)
        {
            // The source the scenario was read from must be known and part of the current basis; otherwise its absence proves nothing.
            var repository = planBasis?.Members.FirstOrDefault(m => m.SnapshotId?.ToString() == c.Generated.SnapshotKey)?.RepositoryKey
                ?? basis.Members.FirstOrDefault(m => m.SnapshotId?.ToString() == c.Generated.SnapshotKey)?.RepositoryKey;
            if (repository is null)
                return (CaseSourceState.SourceUnavailable, "Kilde mangler: kildeversjonen scenarioet ble lest fra er ikke lenger kjent i grunnlaget, så det kan ikke avgjøres om scenarioet er fjernet.");
            if (basis.Members.All(m => m.RepositoryKey != repository || !m.Resolved))
                return (CaseSourceState.SourceUnavailable, "Kilde mangler: kilden scenarioet kom fra er ikke med i gjeldende grunnlag.");
        }
        var where = c.Generated.Capability == D.Specification ? $"spesifikasjonen ({basis.SpecificationReference})"
            : string.Join(", ", basis.Members.Where(m => m.Resolved).Select(m => m.ArchiveName ?? m.DisplayName));
        return (CaseSourceState.Removed, $"Utgått scenario: finnes ikke lenger i gjeldende kildeversjon ({where}). Opprinnelig kilde: {c.Generated.Evidence.FirstOrDefault()?.Reference ?? c.Source.ScenarioId}.");
    }

    /// <summary>The specification file a specification scenario was read from: its user story's file, else the path part of its first evidence
    /// reference ("spec.md#scenario-1"). Null when neither is recorded.</summary>
    internal static string? SpecificationPathOf(TestCaseDraft c) =>
        c.Generated.Story?.SpecificationPath is { Length: > 0 } path ? path : c.Generated.Evidence.Select(e => PathPart(e.Reference)).FirstOrDefault(x => x is not null);

    internal static string? SpecificationPathOf(DiscoveredScenario s) =>
        s.Story?.SpecificationPath is { Length: > 0 } path ? path : s.Evidence.Select(e => PathPart(e.Reference)).FirstOrDefault(x => x is not null);

    private static string? PathPart(string? reference) =>
        reference is { Length: > 0 } r && r.IndexOf('#') is var hash and > 0 ? r[..hash] : null;

    /// <summary>Whether a discovery read this specification file (its specification scenarios came from it).</summary>
    private static bool ReadSpecification(ScenarioDiscoveryResult result, string path) =>
        result.Candidates.Any(x => x.CapabilityId == D.Specification && SpecificationPathOf(x) is { } p && BirkNext.Specifications.SpecificationCatalogRules.SamePath(p, path));

    /// <summary>
    /// The discovery to compare one case with (2026-11-25): the current one, or — when the case's own specification was not read by it because
    /// another specification is selected in Finn og forstå — the newest current discovery of the SAME source version (same content fingerprints)
    /// that read the case's specification. Never another source version, never a historical inspection; null when none exists.
    /// </summary>
    private ScenarioDiscoveryResult? OwnSpecificationDiscovery(TestCaseDraft c, ScenarioDiscoveryResult latest)
    {
        if (c.Generated.Capability != D.Specification || SpecificationPathOf(c) is not { } own || ReadSpecification(latest, own)) return latest;
        if (discoveries is null || latest.Basis is not { } basis) return null;
        // Only when the current discovery DID read specifications (another one): a failed or partial read is reported as such, never hidden
        // behind an older discovery.
        if (latest.Capabilities.FirstOrDefault(x => x.Id == D.Specification) is not { State: DiscoveryCapabilityState.Available, PartialFailure: false }) return null;
        var content = ScenarioDiscoveryStore.Content(basis);
        return discoveries.CurrentResults(latest.ProjectKey).FirstOrDefault(r => r.Basis is { } b && b.Complete && b.ApplicationKey == basis.ApplicationKey
            && ScenarioDiscoveryStore.Content(b).SetEquals(content) && ReadSpecification(r, own));
    }

    /// <summary>Source state flags of a project plan against its application's current discovery (read-time; nothing is rewritten). Each case is
    /// compared with a discovery of its OWN specification (<see cref="OwnSpecificationDiscovery"/>), never with whatever is selected now.</summary>
    private TestPlanDraft FlagProject(TestPlanDraft plan)
    {
        if (plan.ProjectKey is null || CurrentFor(plan) is not { } current) return plan;
        var indexes = new Dictionary<string, (CandidateIndex Index, IReadOnlyDictionary<string, DiscoveredScenario> Moves)>(StringComparer.Ordinal);
        (CandidateIndex Index, IReadOnlyDictionary<string, DiscoveredScenario> Moves) For(ScenarioDiscoveryResult r) =>
            indexes.TryGetValue(r.DiscoveryId, out var held) ? held : indexes[r.DiscoveryId] = (new CandidateIndex(r.Candidates), Renumbered(plan, r.Candidates));
        return E.AssessAll(plan with
        {
            Cases = [.. plan.Cases.Select(c =>
            {
                if (c.Source.Kind != ScenarioSourceKind.ProjectDiscovery || c.Review == TestCaseReviewState.Superseded)
                    return c with { SourceChanged = false, SourceState = null, SourceStateReason = null };
                var latest = OwnSpecificationDiscovery(c, current) ?? current;
                var (index, moves) = For(latest);
                // A renumbering is a change within one import's specification; a later import's ids are always different (followed by identity).
                if (moves.TryGetValue(c.Source.ScenarioId, out var moved) && moved.ProjectKey == c.Source.Owner && ReferenceEquals(Locate(c, index, moves), moved))
                    return c with { SourceChanged = true, SourceState = CaseSourceState.Changed, SourceStateReason = "Samme scenariotekst har fått en ny plass i spesifikasjonen." };
                if (Locate(c, index, moves) is { } now)
                {
                    // Changed in the evidence, moved to another user story (a story identified for the first time is not a change), or a new conflict.
                    var changed = now.Fingerprint != c.Source.Fingerprint || c.Generated.Story is not null && now.Story?.Key != c.Generated.Story.Key;
                    var conflict = now.Conflicts.Count > 0 && ConflictKey(now.Conflicts) != ConflictKey(c.Generated.Conflicts);
                    return c with
                    {
                        SourceChanged = changed || conflict,
                        SourceState = changed || conflict ? CaseSourceState.Changed : CaseSourceState.Current,
                        SourceStateReason = conflict ? "Ny kildekonflikt: kildene beskriver nå ulike forventede resultater." : changed ? "Scenarioet er endret i gjeldende kildeversjon." : null,
                    };
                }
                if (latest.Basis is null) return c with { SourceChanged = false, SourceState = null, SourceStateReason = null }; // older result: not comparable
                var (state, reason) = Absence(c, latest, plan.DiscoveryBasis);
                return c with { SourceChanged = state == CaseSourceState.Removed, SourceState = state, SourceStateReason = reason };
            })],
        });
    }

    /// <summary>The case's scenario in a discovery — the one rule status, impact and refresh share (2026-11-23): by identity (or same-import id),
    /// else an unambiguous move (the unique specification scenario with exactly the same text, <see cref="Renumbered"/>: renumbered in its file,
    /// or its file moved in a new version). Before, impact followed a move only for cases without an identity while status and refresh followed it
    /// for every case, so one plan read "endret", "utgått" and "unchanged" at the same time.
    /// A case without an identity (prepared before identities existed) has a positional id that may now name another scenario: its move comes first.</summary>
    private static DiscoveredScenario? Locate(TestCaseDraft c, CandidateIndex index, IReadOnlyDictionary<string, DiscoveredScenario> moves) =>
        c.Generated.SourceIdentity is null
            ? (moves.TryGetValue(c.Source.ScenarioId, out var renumbered) ? renumbered : index.Find(c))
            : index.Find(c) ?? (moves.TryGetValue(c.Source.ScenarioId, out var moved) ? moved : null);

    private static string ConflictKey(IEnumerable<ScenarioConflict>? conflicts) =>
        conflicts is null ? "" : string.Join("|", conflicts.SelectMany(k => k.Claims).Select(x => $"{x.SourceCategory}:{x.Expected}").Order(StringComparer.Ordinal));

    /// <summary>
    /// Refuses an Update that would read an older version of a source than the plan was prepared from (a rollback): only when a person selected an
    /// older analysis baseline. The plan is never moved back silently.
    /// </summary>
    public static string? Rollback(DiscoverySourceBasis? planBasis, DiscoverySourceBasis? current)
    {
        if (planBasis is null || current is null) return null;
        foreach (var held in planBasis.Members.Where(m => m.Resolved))
            if (current.Members.FirstOrDefault(m => m.RepositoryKey == held.RepositoryKey && m.Resolved) is { } now && now.AnalyzedAt < held.AnalyzedAt)
                return $"{held.DisplayName}: gjeldende analysegrunnlag ({now.ArchiveName}) er eldre enn kildeversjonen planen ble laget fra ({held.ArchiveName}). "
                    + "Oppdatering ville rulle planen tilbake; velg kildeversjon på nytt under Kildeversjoner hvis det er meningen.";
        return null;
    }

    /// <summary>Rebuilds generated content from the application's current discovery. Human edits, links, Test Data, translations and review history are
    /// kept; an accepted case whose scenario changed goes back to review; a case whose scenario is gone after a complete comparison becomes Superseded
    /// ("Utgått", content kept); a case whose source could not be compared is left as it is and flagged.</summary>
    private async Task<TestPlanDraft> RefreshProjectAsync(TestPlanDraftRecord row, TestPlanDraft current, string actor, CancellationToken ct)
    {
        await RestoreCurrentAsync(current, ct);
        var latest = CurrentFor(current) ?? throw new TestPlanException(409, "discovery-missing", "Discover test scenarios for this project first, then update the plan.");
        if (Rollback(current.DiscoveryBasis, latest.Basis) is { } rollback) throw new TestPlanException(409, "basis-older", rollback);
        var index = new CandidateIndex(latest.Candidates);
        var moves = Renumbered(current, latest.Candidates);
        // One scenario of the current basis is followed by one case: when several cases reach the same scenario (the source merged them), the case
        // already holding its id — else the first — follows it; the others keep their identity and are flagged, never given a duplicate id.
        var active = current.Cases.Where(c => c.Source.Kind == ScenarioSourceKind.ProjectDiscovery && c.Review != TestCaseReviewState.Superseded).ToList();
        var targets = active.Select(c => (Id: c.Source.ScenarioId, Target: Locate(c, index, moves)))
            .Where(t => t.Target is not null).ToList();
        var follower = targets.GroupBy(t => t.Target!.CandidateId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.FirstOrDefault(t => t.Id == g.Key).Id ?? g.First().Id, StringComparer.Ordinal);
        // A case of an earlier import continues under the same scenario of this import: its id follows the scenario (recorded), like a renumbering.
        foreach (var (id, target) in targets)
            if (!moves.ContainsKey(id) && target!.CandidateId != id && follower[target.CandidateId] == id && current.Cases.All(o => o.Source.ScenarioId != target.CandidateId))
                moves[id] = target;
        foreach (var (id, target) in targets.Where(t => follower[t.Target!.CandidateId] != t.Id).ToList()) moves.Remove(id);
        var cases = current.Cases.Select(c =>
        {
            if (c.Source.Kind == ScenarioSourceKind.Manual && c.ScenarioKey is { } under && moves.TryGetValue(under, out var to)) return c with { ScenarioKey = to.CandidateId };
            if (c.Source.Kind != ScenarioSourceKind.ProjectDiscovery || c.Review == TestCaseReviewState.Superseded) return E.Assess(c with { SourceChanged = false, SourceState = null, SourceStateReason = null });
            var candidate = Locate(c, index, moves);
            if (candidate is not null && follower.TryGetValue(candidate.CandidateId, out var holder) && holder != c.Source.ScenarioId)
            {
                const string merged = "Scenarioet er slått sammen med et annet scenario i gjeldende kilde (samme situasjon og forventet resultat). Vurder om dette testtilfellet fortsatt trengs.";
                return E.Assess(c with
                {
                    SourceChanged = true, SourceState = CaseSourceState.Changed, SourceStateReason = merged,
                    Review = c.Review == TestCaseReviewState.Accepted ? TestCaseReviewState.UnderReview : c.Review,
                    ReviewHistory = c.Review == TestCaseReviewState.Accepted ? [.. c.ReviewHistory, new TestCaseReviewEvent(TestCaseReviewState.UnderReview, "BirkNext", merged, Now)] : c.ReviewHistory,
                });
            }
            if (candidate is null)
            {
                if (latest.Basis is null) return E.Assess(c with { SourceChanged = false });
                var (state, reason) = Absence(c, latest, current.DiscoveryBasis);
                if (state != CaseSourceState.Removed) return E.Assess(c with { SourceChanged = false, SourceState = state, SourceStateReason = reason });
                return E.Assess(c with
                {
                    Review = TestCaseReviewState.Superseded, SourceChanged = false, SourceState = CaseSourceState.Removed, SourceStateReason = reason,
                    ReviewHistory = [.. c.ReviewHistory, new TestCaseReviewEvent(TestCaseReviewState.Superseded, "BirkNext", reason, Now)],
                });
            }
            var follows = candidate.CandidateId != c.Source.ScenarioId;
            var history = c.EditHistory;
            if (follows) history = [.. history.TakeLast(99), new TestCaseEditEvent("BirkNext", Now, [moves.ContainsKey(c.Source.ScenarioId) && candidate.ProjectKey == c.Source.Owner
                ? Renumbering(c, candidate) : $"Fulgt til samme scenario i en nyere import ({latest.SnapshotLabel ?? candidate.ProjectKey})"])];
            var sameText = candidate.Fingerprint == c.Source.Fingerprint;
            var storyMoved = StoryKey(c.Generated.Story) != StoryKey(candidate.Story);
            if (storyMoved && !(follows && moves.ContainsKey(c.Source.ScenarioId) && candidate.ProjectKey == c.Source.Owner))
                history = [.. history.TakeLast(99), new TestCaseEditEvent("BirkNext", Now, [StoryMove(c.Generated.Story, candidate.Story)])];
            var generated = sameText ? c.Generated with
            {
                Story = candidate.Story, NumberInStory = candidate.NumberInStory, Evidence = D.Content(candidate).Evidence, SourceIdentity = candidate.SourceIdentity,
                SnapshotKey = candidate.SnapshotKey, BasisFingerprint = latest.Basis?.Fingerprint, Conflicts = candidate.Conflicts.Count > 0 ? [.. candidate.Conflicts] : null,
            } : D.Content(candidate) with { BasisFingerprint = latest.Basis?.Fingerprint };
            var review = c.Review;
            var reviews = c.ReviewHistory;
            var conflictChanged = ConflictKey(candidate.Conflicts) != ConflictKey(c.Generated.Conflicts);
            if (!sameText || conflictChanged)
            {
                if (review == TestCaseReviewState.Accepted)
                {
                    review = TestCaseReviewState.UnderReview;
                    reviews = [.. reviews, new TestCaseReviewEvent(TestCaseReviewState.UnderReview, "BirkNext", "The scenario changed in the latest discovery; review it again.", Now)];
                }
            }
            return E.Assess(c with
            {
                Source = c.Source with { ScenarioId = candidate.CandidateId, Owner = candidate.ProjectKey, Fingerprint = candidate.Fingerprint },
                Generated = generated, Review = review, ReviewHistory = reviews, SourceChanged = false, SourceState = CaseSourceState.Current, SourceStateReason = null,
                // A resolution made for other claims no longer applies; the person's own expected result (Edits) stays.
                ConflictResolution = conflictChanged ? null : c.ConflictResolution,
                EditHistory = history,
            });
        }).ToList();
        var suites = current.Suites.Concat(D.ProjectSuites(cases.Select(c => c.Generated)).Where(s => current.Suites.All(x => x.Id != s.Id))).ToList();
        var plan = current with
        {
            Cases = cases, Suites = suites, SnapshotKey = latest.SnapshotKey, DiscoveryId = latest.DiscoveryId, RuleVersion = E.RuleVersion, Revision = row.Revision + 1, UpdatedBy = actor, UpdatedAt = Now,
            ApplicationKey = latest.Basis?.ApplicationKey ?? current.ApplicationKey, DiscoveryBasis = latest.Basis ?? current.DiscoveryBasis,
        };
        return await WriteAsync(row, plan, ct, "Updated from the current source basis");
    }

    // ── Conflicts ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A reviewer records which expected result applies to a case with conflicting sources (a claim or own wording) and why. The claims and
    /// their provenance stay on the case; the chosen result becomes the person's expected result.</summary>
    public async Task<TestPlanDraft> ResolveConflictAsync(Guid id, ResolveConflictRequest request, string actor, CancellationToken ct)
    {
        var row = await Mutable(id, ct);
        if (row.Revision != request.BaseRevision) throw Stale(row);
        var plan = Read(row);
        var target = plan.Cases.FirstOrDefault(c => c.Source.ScenarioId == request.CaseId) ?? throw new TestPlanException(404, "case-not-found", "No such case in this plan.");
        if (target.Generated.Conflicts is not { Count: > 0 } conflicts) throw new TestPlanException(400, "no-conflict", "Testtilfellet har ingen kildekonflikt å avklare.");
        var expected = request.ExpectedResult?.Trim();
        var rationale = request.Rationale?.Trim();
        if (string.IsNullOrWhiteSpace(expected)) throw new TestPlanException(400, "expected-required", "Skriv hvilket forventet resultat som gjelder.");
        if (string.IsNullOrWhiteSpace(rationale)) throw new TestPlanException(400, "rationale-required", "Begrunn avklaringen (hvem bekreftet det, og hvor).");
        if (request.ChosenCandidateId is { Length: > 0 } chosen && conflicts.SelectMany(k => k.Claims).All(x => x.CandidateId != chosen))
            throw new TestPlanException(400, "claim-unknown", "Den valgte kilden er ikke en av kildene i konflikten.");
        var resolution = new ConflictResolution(expected[..Math.Min(expected.Length, 4000)], request.ChosenCandidateId, rationale[..Math.Min(rationale.Length, 1000)], actor, Now);
        var updated = E.Assess(target with
        {
            ConflictResolution = resolution, Edits = target.Edits with { ExpectedResult = resolution.ExpectedResult },
            EditHistory = [.. target.EditHistory.TakeLast(99), new TestCaseEditEvent(actor, Now, ["Expected result", "Kildekonflikt avklart"])],
        });
        plan = plan with { Cases = [.. plan.Cases.Select(c => c.Source.ScenarioId == request.CaseId ? updated : c)], Revision = row.Revision + 1, UpdatedBy = actor, UpdatedAt = Now };
        return await WriteAsync(row, plan, ct, "Kildekonflikt avklart");
    }

    // ── Impact analysis ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A reviewable comparison of a plan with its application's current discovery: per saved case what kind of change the newer basis makes
    /// (expected result, prerequisite, requirement, evidence, conflict, wording only, removed, unavailable) and the scenarios that are new. Read-only.
    /// </summary>
    public async Task<PlanImpactAnalysis> ImpactAsync(Guid id, CancellationToken ct)
    {
        var plan = Read(await Row(id, ct));
        if (plan.ProjectKey is null) throw new TestPlanException(400, "not-a-project-plan", "Impact analysis is available for project plans.");
        await RestoreCurrentAsync(plan, ct);
        var latest = CurrentFor(plan);
        if (latest is null)
            return new PlanImpactAnalysis { DraftId = plan.Id, PlanRevision = plan.Revision, PlanBasis = plan.DiscoveryBasis, Summary = "Ikke sammenlignet: oppdag testscenarier for gjeldende kildeversjon først." };
        var index = new CandidateIndex(latest.Candidates);
        var moves = Renumbered(plan, latest.Candidates);
        var impacts = new List<CaseImpact>();
        var matched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in plan.Cases.Where(c => c.Source.Kind == ScenarioSourceKind.ProjectDiscovery && c.Review is not (TestCaseReviewState.Superseded or TestCaseReviewState.Excluded)))
        {
            var reviewed = c.ReviewHistory.Any(e => e.State == TestCaseReviewState.Accepted) || c.VerifiedAzureDevOpsTestCaseId is not null;
            if (Locate(c, index, moves) is not { } now)
            {
                if (latest.Basis is null) continue;
                var (state, reason) = Absence(c, latest, plan.DiscoveryBasis);
                if (state == CaseSourceState.NotCompared) continue; // another specification: outside this comparison, not a missing source
                impacts.Add(new CaseImpact
                {
                    CaseId = c.Source.ScenarioId, Title = c.EffectiveTitle, ReviewedBefore = reviewed, Summary = reason,
                    Kinds = [state == CaseSourceState.Removed ? CaseImpactKind.ScenarioRemoved : CaseImpactKind.SourceUnavailable],
                });
                continue;
            }
            matched.Add(now.CandidateId);
            if (moves.TryGetValue(c.Source.ScenarioId, out var movedTo) && ReferenceEquals(movedTo, now) && (c.Generated.SourceIdentity is null || index.Find(c) is null)
                && now.Fingerprint == c.Source.Fingerprint)
            {
                // The same text in a new place (renumbered, or its specification file moved): a change of place, never a removal.
                impacts.Add(new CaseImpact
                {
                    CaseId = c.Source.ScenarioId, Title = c.EffectiveTitle, ReviewedBefore = reviewed, Kinds = [CaseImpactKind.EvidenceChanged],
                    Summary = $"Samme scenariotekst har fått en ny plass: {now.Evidence.FirstOrDefault(e => e.Kind == "specification")?.Reference ?? now.CandidateId}. Innholdet er uendret.",
                });
                continue;
            }
            var conflictNow = ConflictKey(now.Conflicts) != ConflictKey(c.Generated.Conflicts) && now.Conflicts.Count > 0;
            if (now.Fingerprint == c.Source.Fingerprint && !conflictNow) continue;
            var after = D.Content(now);
            var before = c.Generated;
            var kinds = new List<CaseImpactKind>();
            var changes = new List<CaseImpactChange>();
            void Compare(CaseImpactKind kind, string field, string? a, string? b)
            {
                if (Norm(a) == Norm(b)) return;
                changes.Add(new(field, a, b));
                if (!kinds.Contains(kind)) kinds.Add(kind);
            }
            Compare(CaseImpactKind.ExpectedResultChanged, "Forventet resultat", before.ExpectedResult, after.ExpectedResult);
            Compare(CaseImpactKind.ExpectedResultChanged, "Forventet resultat per steg", string.Join("\n", before.StepExpected), string.Join("\n", after.StepExpected));
            Compare(CaseImpactKind.PrerequisiteChanged, "Forutsetninger", before.Preconditions, after.Preconditions);
            Compare(CaseImpactKind.PrerequisiteChanged, "Testdata", before.RequiredTestData, after.RequiredTestData);
            Compare(CaseImpactKind.PrerequisiteChanged, "Miljø", before.EnvironmentPrerequisites, after.EnvironmentPrerequisites);
            Compare(CaseImpactKind.RequirementChanged, "Krav", string.Join(", ", before.Requirements.Select(r => r.RuleId).Order()), string.Join(", ", after.Requirements.Select(r => r.RuleId).Order()));
            Compare(CaseImpactKind.EvidenceChanged, "Teststeg", string.Join("\n", before.Steps), string.Join("\n", after.Steps));
            Compare(CaseImpactKind.EvidenceChanged, "Kildebevis", string.Join("\n", before.Evidence.Where(e => e.Kind != "story").Select(e => e.Reference).Order()),
                string.Join("\n", after.Evidence.Where(e => e.Kind != "story").Select(e => e.Reference).Order()));
            if (conflictNow) { kinds.Add(CaseImpactKind.ConflictIntroduced); changes.Add(new("Kildekonflikt", null, string.Join(" / ", now.Conflicts.SelectMany(k => k.Claims).Select(x => $"{x.SourceCategory}: {x.Expected}")))); }
            if (kinds.Count == 0)
            {
                Compare(CaseImpactKind.WordingOnly, "Tittel", before.Title, after.Title);
                Compare(CaseImpactKind.WordingOnly, "Formål", before.Purpose, after.Purpose);
                if (kinds.Count == 0) kinds.Add(CaseImpactKind.WordingOnly);
            }
            impacts.Add(new CaseImpact
            {
                CaseId = c.Source.ScenarioId, Title = c.EffectiveTitle, Kinds = kinds, Changes = changes, ReviewedBefore = reviewed,
                Summary = kinds.SequenceEqual([CaseImpactKind.WordingOnly]) ? "Kun ordlyd er endret; innholdet er det samme." : "Må gjennomgås: " + string.Join(", ", kinds.Select(ImpactLabel)) + ".",
            });
        }
        // FUN-017 (2026-11-27): «not in this plan yet» — every scenario of the current source the plan holds neither by a case NOR as a scenario-only item
        // (before, a scenario planned without a case was listed here too). This is NOT «new since the previous source version» (see the panel text).
        var planned = plan.Cases.Select(c => c.Source.ScenarioId).Concat((plan.PlannedScenarios ?? []).Select(p => p.ScenarioId)).ToHashSet(StringComparer.Ordinal);
        var plannedIdentities = plan.Cases.Select(p => p.Generated.SourceIdentity).Concat((plan.PlannedScenarios ?? []).Select(p => p.SourceIdentity))
            .OfType<string>().Where(s => s.Length > 0).ToHashSet(StringComparer.Ordinal);
        var fresh = latest.Candidates.Where(c => !matched.Contains(c.CandidateId) && !planned.Contains(c.CandidateId) && !plannedIdentities.Contains(c.SourceIdentity))
            .Select(c => new CaseImpact { CaseId = c.CandidateId, Title = c.Title, Kinds = [CaseImpactKind.NewScenario], Summary = $"Ikke i testplanen: scenario fra {c.SourceCategory}." }).ToList();
        var removed = impacts.Count(i => i.Kinds.Contains(CaseImpactKind.ScenarioRemoved));
        var unavailable = impacts.Count(i => i.Kinds.Contains(CaseImpactKind.SourceUnavailable));
        var substantive = impacts.Count(i => !i.Kinds.SequenceEqual([CaseImpactKind.WordingOnly]) && !i.Kinds.Contains(CaseImpactKind.ScenarioRemoved) && !i.Kinds.Contains(CaseImpactKind.SourceUnavailable));
        return new PlanImpactAnalysis
        {
            DraftId = plan.Id, PlanRevision = plan.Revision, PlanBasis = plan.DiscoveryBasis, CurrentBasis = latest.Basis, CurrentDiscoveryId = latest.DiscoveryId,
            Cases = impacts, NewScenarios = fresh,
            Summary = $"{substantive} endret, {removed} utgått, {unavailable} uten tilgjengelig kilde, {impacts.Count(i => i.Kinds.SequenceEqual([CaseImpactKind.WordingOnly]))} kun ordlyd, {fresh.Count} nye scenarier.",
        };
    }

    public static string ImpactLabel(CaseImpactKind kind) => kind switch
    {
        CaseImpactKind.ExpectedResultChanged => "forventet resultat endret",
        CaseImpactKind.ScenarioRemoved => "utgått scenario",
        CaseImpactKind.PrerequisiteChanged => "forutsetning endret",
        CaseImpactKind.RequirementChanged => "krav endret",
        CaseImpactKind.EvidenceChanged => "teststeg eller kildebevis endret",
        CaseImpactKind.ConflictIntroduced => "ny kildekonflikt",
        CaseImpactKind.WordingOnly => "kun ordlyd",
        CaseImpactKind.SourceUnavailable => "kilde mangler",
        CaseImpactKind.NewScenario => "nytt scenario",
        _ => kind.ToString(),
    };

    private static string Norm(string? text) => string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();

    // ── Source status (freshness) ────────────────────────────────────────────────────────────────────────────

    /// <summary>The source status of one plan against its application's current discovery and the sources imported since (live, no rediscovery).</summary>
    internal async Task<PlanSourceStatus> StatusAsync(TestPlanDraft plan, CancellationToken ct)
    {
        // The plan's own project and application, as stored (after a restart the cache may hold only another project's older result).
        await RestoreCurrentAsync(plan, ct);
        var latest = CurrentFor(plan);
        var newer = new List<DiscoverySourceMember>();
        var basisForFreshness = latest?.Basis ?? plan.DiscoveryBasis;
        if (basisResolver is not null && basisForFreshness is not null) newer = await basisResolver.NewerSourcesAsync(basisForFreshness, ct);
        if (latest is null)
            return new PlanSourceStatus
            {
                State = newer.Count > 0 ? "Ny kildeversjon" : "Ikke sammenlignet", PlanBasisFingerprint = plan.DiscoveryBasis?.Fingerprint, NewerSources = newer,
                Message = newer.Count > 0 ? $"Ny kildeversjon importert ({string.Join(", ", newer.Select(m => m.NewerArchiveName))}). Oppdag testscenarier for å se hva som er endret."
                    : "Planen er ikke sammenlignet med gjeldende kilder ennå. Oppdag testscenarier.",
            };
        var flagged = FlagProject(plan);
        var active = flagged.Cases.Where(c => c.Review is not (TestCaseReviewState.Superseded or TestCaseReviewState.Excluded)).ToList();
        var changed = active.Count(c => c.SourceState == CaseSourceState.Changed);
        var removed = active.Count(c => c.SourceState == CaseSourceState.Removed);
        var unavailable = active.Count(c => c.SourceState == CaseSourceState.SourceUnavailable);
        var notCompared = active.Count(c => c.SourceState == CaseSourceState.NotCompared);
        var conflicts = active.Count(c => c.HasUnresolvedConflict);
        var index = new CandidateIndex(latest.Candidates);
        // FUN-017: a scenario-only item covers its scenario too (the same rule as the impact view).
        var covered = plan.Cases.Select(c => index.Find(c)?.CandidateId).OfType<string>().Concat((plan.PlannedScenarios ?? []).Select(p => p.ScenarioId)).ToHashSet(StringComparer.Ordinal);
        var coveredIdentities = (plan.PlannedScenarios ?? []).Select(p => p.SourceIdentity).OfType<string>().Where(s => s.Length > 0).ToHashSet(StringComparer.Ordinal);
        // New scenarios: only from the plan's own specifications (a scenario of the specification selected now is not new for this plan).
        var ownSpecs = plan.Cases.Where(c => c.Generated.Capability == D.Specification).Select(SpecificationPathOf).OfType<string>().ToList();
        var fresh = latest.Candidates.Count(c => !covered.Contains(c.CandidateId) && !coveredIdentities.Contains(c.SourceIdentity) && (c.CapabilityId != D.Specification || ownSpecs.Count == 0
            || SpecificationPathOf(c) is { } p && ownSpecs.Any(o => BirkNext.Specifications.SpecificationCatalogRules.SamePath(o, p))));
        // Another source version = other source content; the specification selected in Finn og forstå is part of the basis fingerprint, not of it.
        var basisChanged = plan.DiscoveryBasis is not null && latest.Basis is not null
            && !ScenarioDiscoveryStore.Content(plan.DiscoveryBasis).SetEquals(ScenarioDiscoveryStore.Content(latest.Basis));
        var incomplete = latest.Basis is { Complete: false };
        var (state, message) = (incomplete || unavailable > 0) ? ("Kilde mangler", incomplete ? "Kilde mangler: " + (latest.Basis!.Problems.FirstOrDefault() ?? "grunnlaget er ufullstendig.") : $"{unavailable} testtilfeller kunne ikke sammenlignes fordi kilden mangler.")
            : (changed + removed + conflicts) > 0 ? ("Må gjennomgås", $"{changed} endret, {removed} utgått, {conflicts} med kildekonflikt. Se konsekvensanalysen før du oppdaterer planen.")
            : newer.Count > 0 ? ("Ny kildeversjon", $"Ny kildeversjon importert ({string.Join(", ", newer.Select(m => m.NewerArchiveName))}). Velg den som analysegrunnlag og oppdag testscenarier på nytt.")
            : basisChanged ? ("Ny kildeversjon", "Planen er laget fra en annen kildeversjon enn gjeldende. Oppdater planen fra gjeldende grunnlag.")
            // Cases of a specification not read for the current source version were not compared: never "i samsvar" for them (2026-11-25).
            : notCompared > 0 && notCompared == active.Count(c => c.Source.Kind == ScenarioSourceKind.ProjectDiscovery)
                ? ("Ikke sammenlignet", "Spesifikasjonen testplanen kommer fra er ikke lest for gjeldende kildeversjon. Velg den i Finn og forstå for å sammenligne.")
            : notCompared > 0 ? ("Klar", $"De sammenlignede testtilfellene er i samsvar med gjeldende kildegrunnlag. {notCompared} testtilfeller fra en annen spesifikasjon er ikke sammenlignet.")
            : ("Klar", "Planen er i samsvar med gjeldende kildegrunnlag.");
        return new PlanSourceStatus
        {
            State = state, Message = message, PlanBasisFingerprint = plan.DiscoveryBasis?.Fingerprint, CurrentBasisFingerprint = latest.Basis?.Fingerprint,
            Changed = changed, Removed = removed, Unavailable = unavailable, Conflicts = conflicts, NewScenarios = fresh, NewerSources = newer,
        };
    }

    /// <summary>The project plans of the same application from other imports of it (a plan follows the application). Unreadable rows are skipped.</summary>
    private async Task<List<(TestPlanDraft Plan, TestPlanDraftRecord Row)>> ApplicationDraftsAsync(string applicationKey, string exceptProjectKey, CancellationToken ct)
    {
        var list = new List<(TestPlanDraft Plan, TestPlanDraftRecord Row)>();
        foreach (var row in await db.TestPlanDrafts.AsNoTracking().Where(d => d.GuideKey.StartsWith("project:")).ToListAsync(ct))
        {
            try
            {
                var plan = Read(row);
                if (plan.ProjectKey is not null && plan.ProjectKey != exceptProjectKey && plan.ApplicationKey == applicationKey) list.Add((plan, row));
            }
            catch (JsonException) { }
        }
        return list;
    }
}
