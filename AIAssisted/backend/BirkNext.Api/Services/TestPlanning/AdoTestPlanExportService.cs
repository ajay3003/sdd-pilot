using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.TestPlanning;
using Microsoft.EntityFrameworkCore;
using E = BirkNext.Api.Services.TestPlanning.TestPlanSuggestionEngine;

namespace BirkNext.Api.Services.TestPlanning;

/// <summary>
/// Azure DevOps Test Plans export for Manual Testing → Plan &amp; prepare: reads the SAVED plan through <see cref="TestPlanSuggestionService"/>
/// (the authoritative plan store), validates and builds rows with <see cref="AdoTestPlanExporter"/> and writes the same rows as XLSX or CSV.
/// Read-only towards plans and cases; records which file was generated (not the file, not an import). Project-bound; no remote call.
/// </summary>
public sealed class AdoTestPlanExportService(AppDbContext db, TestPlanSuggestionService plans, TimeProvider? clock = null)
{
    private DateTimeOffset Now => (clock ?? TimeProvider.System).GetUtcNow();

    public async Task<AdoExportProfile> ProfileAsync(string projectKey, CancellationToken ct)
    {
        var key = TestScenarioDiscoveryService.Key(projectKey);
        var row = await db.AdoExportProfiles.AsNoTracking().SingleOrDefaultAsync(r => r.ProjectKey == key, ct);
        if (row is null) return new AdoExportProfile();
        try { return JsonSerializer.Deserialize<AdoExportProfile>(row.ProfileJson, E.Json) ?? new(); }
        catch (JsonException) { return new AdoExportProfile(); }
    }

    private static AdoExportProfile? Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<AdoExportProfile>(json, E.Json); }
        catch (JsonException) { return null; }
    }

    public async Task<AdoExportProfile> SaveProfileAsync(string projectKey, AdoExportProfile profile, string actor, CancellationToken ct)
    {
        var key = TestScenarioDiscoveryService.Key(projectKey);
        var clean = profile with
        {
            DestinationLabel = Trim(profile.DestinationLabel, 300), AreaPath = Trim(profile.AreaPath, 4000), AssignedTo = Trim(profile.AssignedTo, 256), State = Trim(profile.State, 128) ?? "Design",
        };
        var row = await db.AdoExportProfiles.SingleOrDefaultAsync(r => r.ProjectKey == key, ct);
        if (row is null) db.AdoExportProfiles.Add(row = new AdoExportProfileRecord { ProjectKey = key });
        // Remembered Azure DevOps ids change only through a confirmed update export, never through a profile save from a page that loaded earlier.
        clean = clean with { KnownTestCaseIds = (row.ProfileJson.Length > 0 ? Deserialize(row.ProfileJson) : null)?.KnownTestCaseIds ?? [] };
        row.ProfileJson = JsonSerializer.Serialize(clean, E.Json);
        row.UpdatedBy = actor.Length <= 200 ? actor : actor[..200];
        row.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);
        return clean;
    }

    public async Task<AdoExportPreview> PreviewAsync(AdoExportRequest request, CancellationToken ct) => AdoTestPlanExporter.Preview(await PlanAsync(request, ct), request);

    /// <summary>The import file. Refused (409) while anything blocks it; the preview names what.</summary>
    public async Task<AdoExportFile> FileAsync(AdoExportRequest request, string actor, CancellationToken ct)
    {
        var plan = await PlanAsync(request, ct);
        var preview = AdoTestPlanExporter.Preview(plan, request);
        if (!preview.CanDownload)
            throw new TestPlanException(409, "export-blocked", string.Join(" ", preview.Issues.Where(i => i.Level == AdoIssueLevel.Blocking).Select(i => i.Message).Distinct().Take(5)));
        var xlsx = request.Profile.Format == AdoExportFormat.Xlsx;
        var bytes = xlsx ? AdoTestPlanExporter.Xlsx(preview.Rows, preview.Headers) : AdoTestPlanExporter.Csv(preview.Rows, preview.Headers, request.Profile.CsvSpreadsheetSafe);
        var name = $"{FileStem(plan.PlanName)}-azure-test-plans-r{plan.Revision}.{(xlsx ? "xlsx" : "csv")}";
        var ready = preview.Cases.Where(c => c.Included && c.Issues.All(i => i.Level != AdoIssueLevel.Blocking)).ToList();
        var revisions = Revisions(plan);
        db.AdoExports.Add(new AdoExportRecord
        {
            Id = Guid.NewGuid(), ProjectKey = plan.ProjectKey!, DraftId = plan.Id, PlanName = plan.PlanName.Length <= 300 ? plan.PlanName : plan.PlanName[..300], PlanRevision = plan.Revision,
            Format = request.Profile.Format.ToString(), Mode = request.Mode.ToString(), Cases = ready.Count, Rows = preview.Rows.Count, Destination = Trim(request.Profile.DestinationLabel, 300),
            ContentJson = JsonSerializer.Serialize(new { cases = ready.Select(c => $"{c.CaseId}@{revisions.GetValueOrDefault(c.CaseId)}"), profile = request.Profile with { }, issues = preview.Issues.Count }, E.Json),
            ExportedBy = actor.Length <= 200 ? actor : actor[..200], ExportedAt = Now,
        });
        // Ids the tester confirmed for an update export are remembered (by case key) so they are not typed again next time.
        if (request.Mode == AdoExportMode.UpdateExisting && request.ExistingIdsConfirmed && ready.Any(c => c.AzureDevOpsId is not null))
        {
            var key = TestScenarioDiscoveryService.Key(request.ProjectKey);
            var profileRow = await db.AdoExportProfiles.SingleOrDefaultAsync(r => r.ProjectKey == key, ct);
            var stored = profileRow is null ? request.Profile : (Deserialize(profileRow.ProfileJson) ?? request.Profile);
            var known = new Dictionary<string, string>(stored.KnownTestCaseIds, StringComparer.Ordinal);
            foreach (var c in ready.Where(c => c.AzureDevOpsId is not null)) known[c.CaseKey] = c.AzureDevOpsId!;
            if (profileRow is null) db.AdoExportProfiles.Add(profileRow = new AdoExportProfileRecord { ProjectKey = key });
            profileRow.ProfileJson = JsonSerializer.Serialize(stored with { KnownTestCaseIds = known }, E.Json);
            profileRow.UpdatedBy = actor.Length <= 200 ? actor : actor[..200];
            profileRow.UpdatedAt = Now;
        }
        await db.SaveChangesAsync(ct);
        return new AdoExportFile(name, xlsx ? "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" : "text/csv", Convert.ToBase64String(bytes), preview.Rows.Count, ready.Count);
    }

    /// <summary>The mapping report: what the import file cannot carry (requirements, Test Data names, preconditions, BirkNext identity).</summary>
    public async Task<AdoExportFile> ReportAsync(AdoExportRequest request, CancellationToken ct)
    {
        var plan = await PlanAsync(request, ct);
        var preview = AdoTestPlanExporter.Preview(plan, request);
        var bytes = AdoTestPlanExporter.MappingReport(preview, Revisions(plan));
        return new AdoExportFile($"{FileStem(plan.PlanName)}-azure-devops-mapping-report-r{plan.Revision}.csv", "text/csv", Convert.ToBase64String(bytes), preview.Cases.Count, preview.ReadyCases);
    }

    public async Task<List<AdoExportHistoryEntry>> HistoryAsync(string projectKey, CancellationToken ct)
    {
        var key = TestScenarioDiscoveryService.Key(projectKey);
        var rows = await db.AdoExports.AsNoTracking().Where(r => r.ProjectKey == key).OrderByDescending(r => r.ExportedAt).Take(100).ToListAsync(ct);
        return [.. rows.Select(r => new AdoExportHistoryEntry(r.Id, r.DraftId, r.PlanName, r.PlanRevision, r.Format, r.Mode, r.Cases, r.Rows, r.Destination, r.ExportedBy, r.ExportedAt, Cases(r.ContentJson)))];
    }

    /// <summary>The saved plan of the request's project at the reviewed revision (a newer save is refused so the file matches the preview).</summary>
    private async Task<TestPlanDraft> PlanAsync(AdoExportRequest request, CancellationToken ct)
    {
        var key = TestScenarioDiscoveryService.Key(request.ProjectKey);
        var plan = await plans.DraftAsync(request.DraftId, ct);
        if (plan.ProjectKey != key) throw new TestPlanException(404, "draft-not-found", "No such test plan in this project.");
        if (request.PlanRevision != plan.Revision)
            throw new TestPlanException(409, "plan-changed", $"The plan was saved again (revision {plan.Revision}); review the export again so the file matches what you checked.");
        return plan;
    }

    private static Dictionary<string, string> Revisions(TestPlanDraft plan) =>
        plan.Cases.ToDictionary(c => c.Source.ScenarioId, c => ManualCaseIdentity.Fingerprint(c)[..12], StringComparer.Ordinal);

    private static List<string> Cases(string json)
    {
        try { return JsonDocument.Parse(json).RootElement.GetProperty("cases").EnumerateArray().Select(e => e.GetString() ?? "").ToList(); }
        catch (Exception e) when (e is JsonException or System.Collections.Generic.KeyNotFoundException or InvalidOperationException) { return []; }
    }

    private static string FileStem(string name)
    {
        var stem = new string(name.Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-').ToArray());
        while (stem.Contains("--", StringComparison.Ordinal)) stem = stem.Replace("--", "-", StringComparison.Ordinal);
        stem = stem.Trim('-');
        return stem.Length == 0 ? "test-plan" : stem[..Math.Min(stem.Length, 60)];
    }

    private static string? Trim(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, max)];
}
