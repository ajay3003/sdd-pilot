using System.Security.Cryptography;
using System.Text.Json;
using BirkNext.Api.Data.IntegrationJourneys;
using BirkNext.Integrations;
using Microsoft.EntityFrameworkCore;
using Path = System.IO.Path;   // HotChocolate contributes a Path type through the global usings

namespace BirkNext.Api.Services.IntegrationJourneys;

/// <summary>The latest declared-journey migration report of this process (the import runs at every start; it is idempotent).</summary>
public sealed class DeclaredIntegrationJourneyMigrationReports
{
    private DeclaredIntegrationJourneyMigrationReport _latest = new();
    public DeclaredIntegrationJourneyMigrationReport Latest => Volatile.Read(ref _latest);
    public void Set(DeclaredIntegrationJourneyMigrationReport report) => Volatile.Write(ref _latest, report);
}

/// <summary>
/// Imports the earlier machine-local JSON file of declared journeys (App_Data/integration-journeys/declared-journeys.json) into the database.
/// <list type="bullet">
/// <item>The file is only read — never written, moved or deleted (it stays as the original record; deleting it is the user's choice).</item>
/// <item>Ids are preserved: project key and journey id as stored; every entry is validated with the same rules as a new declaration.</item>
/// <item>A new journey is written as revision 1 (origin LegacyImport, declared by/at as in the file).</item>
/// <item>Idempotent: an id that already exists in that project with the same revision-1 content is AlreadyPresent; with different content it is a
/// Conflict — the database is kept, nothing is overwritten. Two entries with the same id in the file: identical → one import plus
/// DuplicateInFile; different → both Conflict, neither imported.</item>
/// <item>Never crosses projects: identity is (project key, journey id).</item>
/// </list>
/// </summary>
public sealed class DeclaredIntegrationJourneyLegacyImport(IntegrationJourneysDbContext db, DeclaredIntegrationJourneyMigrationReports reports,
    ILogger<DeclaredIntegrationJourneyLegacyImport> logger)
{
    private static readonly JsonSerializerOptions FileJson = new() { PropertyNameCaseInsensitive = true };

    public static string DefaultPath(IHostEnvironment environment) =>
        Path.Combine(AppDataLocation.Root(environment), "integration-journeys", "declared-journeys.json");

    /// <summary>Startup entry point: never throws (a failure is reported and logged, and the next start tries again).</summary>
    public async Task<DeclaredIntegrationJourneyMigrationReport> ImportAtStartupAsync(string path, DateTimeOffset now, CancellationToken ct = default)
    {
        try { return await ImportAsync(path, now, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Declared Integration Journeys: the legacy JSON import failed; it is retried at the next start. The file was not changed.");
            var failed = new DeclaredIntegrationJourneyMigrationReport { Status = "Failed", SourcePath = path, RanAt = now, Error = ex.Message };
            reports.Set(failed);
            return failed;
        }
    }

    public async Task<DeclaredIntegrationJourneyMigrationReport> ImportAsync(string path, DateTimeOffset now, CancellationToken ct = default)
    {
        now = now.ToUniversalTime();
        if (!File.Exists(path))
        {
            var none = new DeclaredIntegrationJourneyMigrationReport { Status = "NoLegacyFile", SourcePath = path, RanAt = now };
            reports.Set(none);
            logger.LogInformation("Declared Integration Journeys: no legacy JSON file at {Path}; nothing to import.", path);
            return none;
        }

        byte[] bytes;
        var info = new FileInfo(path);
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            bytes = buffer.ToArray();
        }
        var source = new DeclaredIntegrationJourneyMigrationReport
        {
            SourcePath = path, SourceSha256 = Convert.ToHexString(SHA256.HashData(bytes)), SourceBytes = bytes.LongLength,
            SourceLastWriteUtc = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), RanAt = now,
        };

        List<DeclaredIntegrationJourney?> entries;
        try { entries = bytes.Length == 0 ? [] : JsonSerializer.Deserialize<List<DeclaredIntegrationJourney?>>(bytes, FileJson) ?? []; }
        catch (JsonException ex)
        {
            var unreadable = source with { Status = "Unreadable", Error = $"The legacy file is not a JSON list of declared journeys: {ex.Message}" };
            reports.Set(unreadable);
            logger.LogWarning("Declared Integration Journeys: the legacy JSON file {Path} could not be read ({Error}); nothing imported, the file was not changed.", path, ex.Message);
            return unreadable;
        }

        var items = new List<DeclaredIntegrationJourneyMigrationItem>();
        var valid = new List<DeclaredIntegrationJourney>();
        foreach (var entry in entries)
        {
            if (entry is null) { items.Add(Item(new(), DeclaredIntegrationJourneyRules.Invalid, "An empty entry.")); continue; }
            var key = (entry.ProjectKey ?? "").Trim();
            // A hand-edited file may hold nulls where the contract has none: normalized before validation.
            var journey = entry with
            {
                ProjectKey = key, JourneyId = (entry.JourneyId ?? "").Trim(), DisplayName = entry.DisplayName ?? "", DeclaredBy = entry.DeclaredBy ?? "", Components = entry.Components ?? [],
            };
            if (!DeclaredIntegrationJourneyRules.IsJourneyId(journey.JourneyId))
            { items.Add(Item(journey, DeclaredIntegrationJourneyRules.Invalid, "The journey id is missing or not a slug.")); continue; }
            if (DeclaredIntegrationJourneyRules.Validate(RequestOf(journey)) is { } why)
            { items.Add(Item(journey, DeclaredIntegrationJourneyRules.Invalid, why)); continue; }
            valid.Add(journey);
        }

        var toImport = new List<DeclaredIntegrationJourney>();
        foreach (var group in valid.GroupBy(j => (j.ProjectKey, j.JourneyId)))
        {
            var first = group.First();
            if (group.Skip(1).Any(other => !Same(first, other)))
            {
                items.AddRange(group.Select(j => Item(j, DeclaredIntegrationJourneyRules.Conflict,
                    "The legacy file declares this id more than once with different content; none of them is imported. Declare the intended journey in BirkNext.")));
                continue;
            }
            toImport.Add(first);
            items.AddRange(group.Skip(1).Select(j => Item(j, DeclaredIntegrationJourneyRules.DuplicateInFile, "An identical repeat of an entry above; imported once.")));
        }

        var added = 0;
        foreach (var journey in toImport)
        {
            var existing = await db.Journeys.AsNoTracking().FirstOrDefaultAsync(j => j.ProjectKey == journey.ProjectKey && j.Slug == journey.JourneyId, ct);
            if (existing is not null)
            {
                var first = await db.Revisions.AsNoTracking().FirstOrDefaultAsync(r => r.JourneyId == existing.Id && r.Revision == 1, ct);
                var same = first is not null && SameAsRevision(journey, first);
                items.Add(same
                    ? Item(journey, DeclaredIntegrationJourneyRules.AlreadyPresent, first!.Origin == DeclaredIntegrationJourneyRules.OriginLegacyImport
                        ? $"Imported earlier (now revision {existing.CurrentRevision}{(existing.RetiredAt is null ? "" : ", retired")})."
                        : $"The database already declares the same journey (now revision {existing.CurrentRevision}).")
                    : Item(journey, DeclaredIntegrationJourneyRules.Conflict,
                        $"The database already has a different journey with this id (revision 1 differs); the database is kept and nothing is overwritten."));
                continue;
            }
            var at = journey.DeclaredAt == default ? source.SourceLastWriteUtc!.Value : journey.DeclaredAt.ToUniversalTime();
            var record = new DeclaredJourneyRecord
            {
                Id = Guid.NewGuid(), ProjectKey = journey.ProjectKey, Slug = journey.JourneyId, DisplayName = journey.DisplayName.Trim(), CurrentRevision = 1,
                CreatedAt = at, CreatedBy = journey.DeclaredBy.Trim(), UpdatedAt = at,
            };
            db.Journeys.Add(record);
            db.Revisions.Add(DeclaredIntegrationJourneyStore.Revision(record.Id, 1, RequestOf(journey), at, DeclaredIntegrationJourneyRules.OriginLegacyImport));
            items.Add(Item(journey, DeclaredIntegrationJourneyRules.Imported,
                journey.DeclaredAt == default ? "Imported as revision 1 (the file had no declaration time; the file's last-write time is used)." : "Imported as revision 1."));
            added++;
        }
        if (added > 0) await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();

        int Count(string outcome) => items.Count(i => i.Outcome == outcome);
        var report = source with
        {
            Read = entries.Count, Imported = Count(DeclaredIntegrationJourneyRules.Imported), AlreadyPresent = Count(DeclaredIntegrationJourneyRules.AlreadyPresent),
            Conflicts = Count(DeclaredIntegrationJourneyRules.Conflict), DuplicatesInFile = Count(DeclaredIntegrationJourneyRules.DuplicateInFile),
            Invalid = Count(DeclaredIntegrationJourneyRules.Invalid), Items = items,
        };
        report = report with { Status = report.Conflicts + report.Invalid > 0 ? "CompletedWithIssues" : "Completed" };
        reports.Set(report);
        logger.LogInformation("Declared Integration Journeys: legacy JSON import from {Path} (sha256 {Sha}): read {Read}, imported {Imported}, already present {Present}, " +
            "conflicts {Conflicts}, duplicates in file {Duplicates}, invalid {Invalid}. The file was not changed.",
            path, report.SourceSha256, report.Read, report.Imported, report.AlreadyPresent, report.Conflicts, report.DuplicatesInFile, report.Invalid);
        foreach (var issue in items.Where(i => i.Outcome is DeclaredIntegrationJourneyRules.Conflict or DeclaredIntegrationJourneyRules.Invalid))
            logger.LogWarning("Declared Integration Journeys: legacy entry {Id} — {Outcome}: {Detail}", issue.Id, issue.Outcome, issue.Detail);
        return report;
    }

    private static DeclaredIntegrationJourneyRequest RequestOf(DeclaredIntegrationJourney j) => new()
    {
        ProjectKey = j.ProjectKey, DisplayName = j.DisplayName ?? "", Description = j.Description, Components = j.Components ?? [], DeclaredBy = j.DeclaredBy ?? "",
    };

    private static bool Same(DeclaredIntegrationJourney a, DeclaredIntegrationJourney b) =>
        DeclaredIntegrationJourneyRules.SameContent(a.DisplayName, a.Description, a.Components, b.DisplayName, b.Description, b.Components)
        && string.Equals(a.DeclaredBy.Trim(), b.DeclaredBy.Trim(), StringComparison.Ordinal);

    private static bool SameAsRevision(DeclaredIntegrationJourney j, DeclaredJourneyRevisionRecord r) =>
        DeclaredIntegrationJourneyRules.SameContent(j.DisplayName, j.Description, j.Components, r.DisplayName, r.Description, DeclaredIntegrationJourneyStore.Components(r));

    private static DeclaredIntegrationJourneyMigrationItem Item(DeclaredIntegrationJourney j, string outcome, string detail) => new()
    {
        ProjectKey = j.ProjectKey ?? "", JourneyId = j.JourneyId ?? "", DisplayName = j.DisplayName ?? "", Outcome = outcome, Detail = detail,
    };
}
