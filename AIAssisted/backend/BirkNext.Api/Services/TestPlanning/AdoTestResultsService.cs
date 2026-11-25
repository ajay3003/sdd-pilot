using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.DeliveryReporting.AzureDevOps;
using BirkNext.TestPlanning;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.TestPlanning;

/// <summary>
/// Phase 2 — «Hent resultater fra Azure DevOps» (2026-11-25): reads the last result of each test point in the suites the plan's cases were sent to
/// (read-only), and stores each result once in <c>external_test_results</c>, apart from BirkNext's own runs. A result is mapped to a BirkNext case
/// only through a verified link — BirkNext's own transfer (<see cref="AdoTestCaseLinkRecord"/>) or an id the tester confirmed in an update export —
/// never by title. It counts for the current case only while the case's text equals its text at the last export or transfer; otherwise it is «fra
/// en tidligere versjon». Azure DevOps' outcome word is kept as recorded (an unknown outcome is never shown as Bestått). Nothing is written to
/// Azure DevOps, and BirkNext plans, cases and runs are not changed.
/// </summary>
public sealed class AdoTestResultsService(AppDbContext db, AdoTestPlanExportService export, AzureDevOpsTestPlansClient ado, AzureDevOpsConnectionStore connections,
    AdoTestPlanSettingsService settings, TimeProvider clock, ILogger<AdoTestResultsService> logger)
{
    private sealed record Target(Guid ConnectionId, string Organization, string Project, int PlanId, int SuiteId);

    public async Task<AdoResultsFetchResult> FetchAsync(AdoResultsFetchRequest request, string actor, CancellationToken ct)
    {
        // Ids the tester confirmed in an update export are read in the destination the plan uses: the one given, else the saved default.
        if (request.Destination is null && (await settings.EffectiveAsync(request.ProjectKey, ct)).Destination is { } effective) request = request with { Destination = effective };
        var plan = await export.CurrentPlanAsync(request.ProjectKey, request.DraftId, ct);
        var key = TestScenarioDiscoveryService.Key(request.ProjectKey);
        var prefix = $"{plan.Id:N}/";
        var links = await db.AdoTestCaseLinks.AsNoTracking()
            .Where(l => l.ProjectKey == key && l.State == nameof(AdoLinkState.Linked) && l.TestCaseId != null && l.CaseKey.StartsWith(prefix)).ToListAsync(ct);
        var exported = await ExportedRevisionsAsync(key, plan.Id, ct);
        var profile = await export.ProfileAsync(request.ProjectKey, ct);
        var stored = connections.List();
        Guid? Connection(string organization) =>
            request.Destination is { } d && string.Equals(d.Organization, organization, StringComparison.OrdinalIgnoreCase) && stored.Any(c => c.Id == d.ConnectionId) ? d.ConnectionId
            : stored.FirstOrDefault(c => string.Equals(c.Organization, organization, StringComparison.OrdinalIgnoreCase))?.Id;

        // Verified ids per test case id: the transfer links (each with its own destination) and, for the chosen destination, ids the tester confirmed.
        var targets = new Dictionary<Target, Dictionary<int, string>>();
        void Add(Target t, int testCaseId, string caseKey)
        {
            if (!targets.TryGetValue(t, out var map)) targets[t] = map = [];
            map.TryAdd(testCaseId, caseKey);
        }
        var problems = new List<string>();
        foreach (var l in links.Where(l => l.AdoPlanId is not null && l.AdoSuiteId is not null))
        {
            if (Connection(l.Organization) is not { } c) { problems.Add($"Ingen tilkobling til {l.Organization}: resultatene der kan ikke hentes."); continue; }
            Add(new(c, l.Organization, l.AdoProject, l.AdoPlanId!.Value, l.AdoSuiteId!.Value), l.TestCaseId!.Value, l.CaseKey);
        }
        if (request.Destination is { Complete: true } dest && stored.Any(c => c.Id == dest.ConnectionId))
        {
            var t = new Target(dest.ConnectionId, dest.Organization, dest.Project, dest.PlanId, dest.SuiteId);
            foreach (var c in plan.Cases)
            {
                var caseKey = prefix + c.Source.ScenarioId;
                var confirmed = profile.KnownTestCaseIds.GetValueOrDefault(caseKey) ?? c.VerifiedAzureDevOpsTestCaseId;
                if (int.TryParse(confirmed, out var id) && id > 0) Add(t, id, caseKey);
            }
            if (!targets.ContainsKey(t)) targets[t] = [];
        }
        if (targets.Count == 0)
            return new AdoResultsFetchResult { Problems = ["Ingen testtilfeller i planen er koblet til Azure DevOps ennå. Send dem direkte, eller bekreft id-ene i en oppdateringseksport."] };

        int read = 0, added = 0, known = 0, notLinked = 0, neverRun = 0;
        var now = clock.GetUtcNow();
        var who = actor.Length <= 200 ? actor : actor[..200];
        foreach (var (t, map) in targets)
        {
            List<AzureDevOpsTestPlansClient.TestPointResult> points;
            try { points = await ado.PointsAsync(t.ConnectionId, t.Project, t.PlanId, t.SuiteId, ct); }
            catch (AzureDevOpsTestPlansException e) { problems.Add($"{t.Organization}/{t.Project}, testplan {t.PlanId}: {e.Message}"); continue; }
            foreach (var p in points)
            {
                read++;
                if (!map.TryGetValue(p.TestCaseId, out var caseKey)) { notLinked++; continue; }
                if (p.RunId is not { } runId || p.ResultId is not { } resultId) { neverRun++; continue; }
                var exists = await db.ExternalTestResults.AnyAsync(r => r.Source == "AzureDevOps" && r.Organization == t.Organization && r.AdoProject == t.Project
                    && r.RunId == runId && r.ResultId == resultId, ct);
                if (exists) { known++; continue; }
                var caseId = caseKey[prefix.Length..];
                db.ExternalTestResults.Add(new ExternalTestResultRecord
                {
                    Id = Guid.NewGuid(), ProjectKey = key, DraftId = plan.Id, CaseKey = caseKey, Organization = t.Organization, AdoProject = t.Project, TestCaseId = p.TestCaseId,
                    PlanId = t.PlanId, SuiteId = t.SuiteId, PointId = p.PointId, RunId = runId, ResultId = resultId, Outcome = Cut(p.Outcome, 40), Tester = p.Tester is null ? null : Cut(p.Tester, 200),
                    Configuration = p.Configuration is null ? null : Cut(p.Configuration, 200), CompletedAt = p.CompletedAt,
                    ExportedRevision = links.FirstOrDefault(l => l.CaseKey == caseKey)?.CaseRevision ?? exported.GetValueOrDefault(caseId), FetchedAt = now, FetchedBy = who,
                });
                added++;
            }
        }
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Azure DevOps results fetched for plan {DraftId}: {Read} points read, {Added} new results, {Known} already known", plan.Id, read, added, known);
        return new AdoResultsFetchResult { Read = read, New = added, AlreadyKnown = known, NotLinked = notLinked, NeverRun = neverRun, Problems = problems, Results = await ListAsync(request.ProjectKey, plan.Id, ct) };
    }

    /// <summary>The stored Azure DevOps results of the plan, newest first, each marked current or «fra en tidligere versjon».</summary>
    public async Task<List<AdoCaseResult>> ListAsync(string projectKey, Guid draftId, CancellationToken ct)
    {
        var plan = await export.CurrentPlanAsync(projectKey, draftId, ct);
        var revisions = AdoTestPlanExportService.Revisions(plan);
        var key = TestScenarioDiscoveryService.Key(projectKey);
        var rows = await db.ExternalTestResults.AsNoTracking().Where(r => r.ProjectKey == key && r.DraftId == plan.Id).ToListAsync(ct);
        var prefix = $"{plan.Id:N}/";
        return [.. rows.OrderByDescending(r => r.CompletedAt ?? r.FetchedAt).Select(r =>
        {
            var caseId = r.CaseKey.StartsWith(prefix, StringComparison.Ordinal) ? r.CaseKey[prefix.Length..] : r.CaseKey;
            return new AdoCaseResult
            {
                CaseId = caseId, CaseKey = r.CaseKey, TestCaseId = r.TestCaseId, PlanId = r.PlanId, SuiteId = r.SuiteId, PointId = r.PointId, RunId = r.RunId, ResultId = r.ResultId,
                Outcome = r.Outcome, Tester = r.Tester, Configuration = r.Configuration, CompletedAt = r.CompletedAt, FetchedAt = r.FetchedAt, ExportedRevision = r.ExportedRevision,
                WebUrl = ado.ResultUrl(r.Organization, r.AdoProject, r.RunId, r.ResultId),
                Current = r.ExportedRevision is { } rev && revisions.TryGetValue(caseId, out var now) && now == rev,
            };
        })];
    }

    /// <summary>Case id → text revision at its latest export or transfer (from the export history: "caseId@revision").</summary>
    private async Task<Dictionary<string, string>> ExportedRevisionsAsync(string key, Guid draftId, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var rows = await db.AdoExports.AsNoTracking().Where(r => r.ProjectKey == key && r.DraftId == draftId).OrderByDescending(r => r.ExportedAt).Select(r => r.ContentJson).ToListAsync(ct);
        foreach (var json in rows)
        {
            try
            {
                if (!JsonDocument.Parse(json).RootElement.TryGetProperty("cases", out var cases) || cases.ValueKind != JsonValueKind.Array) continue;
                foreach (var entry in cases.EnumerateArray().Select(e => e.GetString()).OfType<string>())
                {
                    var at = entry.LastIndexOf('@');
                    if (at > 0) result.TryAdd(entry[..at], entry[(at + 1)..]);
                }
            }
            catch (JsonException) { }
        }
        return result;
    }

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];
}
