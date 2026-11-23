using BirkNext.Api.Services.DependencyReview;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Integrations;
using BirkNext.ProjectImport;
using BirkNext.SourceEvidence;

namespace BirkNext.Api.Services.ProjectImport;

/// <summary>
/// Application identity at import (2026-11-23). The tester says whether a ZIP is "Ny applikasjon" or "Ny versjon av eksisterende applikasjon"; the
/// choice is recorded on the staged upload and becomes the snapshot's repository identity (the one every module reads through
/// <see cref="SourceBaselineService.KeyOf"/>), with the archive's own identity kept beside it. Nothing is inferred from file names, and without a
/// choice (older clients, Source Analysis uploads) the archive's own identity applies as before. Existing snapshots are never rewritten.
/// </summary>
public sealed partial class ProjectImportService
{
    private static string SourceBaselineServiceKey(IqrSourceSnapshot snapshot) => SourceBaselineService.KeyOf(snapshot);

    /// <summary>The applications already imported (one logical identity over its imports), newest first.</summary>
    public async Task<List<KnownApplication>> ApplicationsAsync(CancellationToken ct) =>
        [.. (await store.ListSourceAnalysisIdentitiesAsync(SourceBaselineService.Window, ct))
            .GroupBy(SourceBaselineService.KeyOf, StringComparer.Ordinal)
            .Select(g =>
            {
                var latest = g.OrderByDescending(s => s.AnalyzedAt).First();
                return new KnownApplication
                {
                    Key = g.Key, Name = ReviewSourceEvidenceProvider.Identity(latest).DisplayName, LatestArchive = latest.Archive.FileName,
                    LatestImportedAt = latest.AnalyzedAt, Imports = g.Count(),
                };
            })
            .OrderByDescending(a => a.LatestImportedAt)];

    /// <summary>What the import page asks: the archive's own identity, the known applications and the choice made so far. Null when the staging expired.</summary>
    public async Task<ProjectImportApplicationOptions?> ApplicationOptionsAsync(Guid stagingId, CancellationToken ct)
    {
        if (staging.Find(stagingId) is not { } staged) return null;
        var archive = SourceDependencyEvidenceExtractor.Identity(staged.FileName, staged.Bytes);
        var known = await ApplicationsAsync(ct);
        return new ProjectImportApplicationOptions
        {
            ArchiveKey = archive.Key, ArchiveName = archive.DisplayName, ArchiveBasis = archive.Basis,
            SameIdentityAs = known.FirstOrDefault(a => a.Key == archive.Key)?.Key, Applications = known, Choice = staged.Application,
        };
    }

    /// <summary>Records the tester's choice on the staged upload (before the import). A new version must name a known application; a warning is
    /// returned (never a refusal) when the archive has no project in common with that application's latest import.</summary>
    public async Task<ProjectImportApplicationChoice?> ChooseApplicationAsync(Guid stagingId, ProjectImportApplicationChoice choice, CancellationToken ct)
    {
        if (staging.Find(stagingId) is not { } staged) return null;
        if (staged.Committed is not null)
            throw new ProjectImportChoiceException("IMPORT_ALREADY_COMMITTED", "Importen er allerede gjennomført; valget kan ikke endres.");
        var known = await ApplicationsAsync(ct);
        string? warning = null, name;
        if (choice.Mode == ProjectImportApplicationMode.NewVersion)
        {
            var target = known.FirstOrDefault(a => a.Key == choice.ApplicationKey)
                ?? throw new ProjectImportChoiceException("APPLICATION_UNKNOWN", "Velg en applikasjon som allerede er importert.");
            name = target.Name;
            warning = await UnrelatedWarningAsync(staged, target, ct);
        }
        else name = (await NewApplicationIdentityAsync(staged, known, ct)).DisplayName;
        staged.Application = choice with { ApplicationKey = choice.Mode == ProjectImportApplicationMode.NewVersion ? choice.ApplicationKey : null, Warning = warning, ApplicationName = name };
        return staged.Application;
    }

    /// <summary>The identity the snapshot gets for a recorded choice; null = the archive's own identity.</summary>
    private async Task<SourceRepositoryIdentity?> DeclaredIdentityAsync(StagedProjectImport staged, ProjectImportApplicationChoice choice, CancellationToken ct)
    {
        var archive = SourceDependencyEvidenceExtractor.Identity(staged.FileName, staged.Bytes);
        var known = await ApplicationsAsync(ct);
        if (choice.Mode == ProjectImportApplicationMode.NewVersion && known.FirstOrDefault(a => a.Key == choice.ApplicationKey) is { } target)
            return target.Key == archive.Key ? null
                : new SourceRepositoryIdentity(target.Key, target.Name, $"Ny versjon av {target.Name}, valgt ved import (arkivets egen identitet: {archive.DisplayName})");
        return choice.Mode == ProjectImportApplicationMode.NewApplication ? await NewApplicationIdentityAsync(staged, known, ct) is var id && id.Key != archive.Key ? id : null : null;
    }

    /// <summary>A new application keeps the archive's identity unless that identity is already an application: then it gets a free one
    /// ("M2LB (2)"), so choosing "Ny applikasjon" never joins an existing application silently.</summary>
    private static Task<SourceRepositoryIdentity> NewApplicationIdentityAsync(StagedProjectImport staged, List<KnownApplication> known, CancellationToken ct)
    {
        var archive = SourceDependencyEvidenceExtractor.Identity(staged.FileName, staged.Bytes);
        if (known.All(a => a.Key != archive.Key)) return Task.FromResult(archive);
        for (var n = 2; ; n++)
        {
            var key = $"{archive.Key} ({n})";
            if (known.All(a => a.Key != key))
                return Task.FromResult(new SourceRepositoryIdentity(key, $"{archive.DisplayName} ({n})",
                    $"Ny applikasjon valgt ved import; navnet {archive.DisplayName} var allerede i bruk av en annen applikasjon"));
        }
    }

    /// <summary>A warning when the archive's projects and the application's latest import have nothing in common (a hint, never a decision).</summary>
    private async Task<string?> UnrelatedWarningAsync(StagedProjectImport staged, KnownApplication target, CancellationToken ct)
    {
        // Project files of the archive (.NET); an archive without any gives no warning (nothing to compare).
        var mine = (staged.Workspace.AllPaths ?? [])
            .Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase))
            .Select(p => Normalize(System.IO.Path.GetFileNameWithoutExtension(p.Replace('\\', '/')))).ToHashSet(StringComparer.Ordinal);
        var latest = (await store.ListSourceAnalysisIdentitiesAsync(SourceBaselineService.Window, ct))
            .Where(s => SourceBaselineService.KeyOf(s) == target.Key).OrderByDescending(s => s.AnalyzedAt).FirstOrDefault();
        var theirs = latest?.Projects.Select(p => Normalize(p.Name)).ToHashSet(StringComparer.Ordinal) ?? [];
        if (mine.Count == 0 || theirs.Count == 0 || mine.Overlaps(theirs)) return null;
        return $"«{staged.FileName}» har ingen prosjekter felles med siste import av {target.Name} ({target.LatestArchive}). Kontroller at det er samme applikasjon før du importerer.";
    }

    private static string Normalize(string name) => name.Trim().ToLowerInvariant();

    /// <summary>The identity outcome of the import; for a new version with "use as current", the import becomes the application's analysis baseline.</summary>
    private async Task<ProjectImportApplicationOutcome?> ApplicationOutcomeAsync(StagedProjectImport staged, ProjectImportSourceResult source,
        SourceRepositoryIdentity? declared, CancellationToken ct)
    {
        if (source.SnapshotId is not { } snapshotId || await store.FindSourceAnalysisAsync(snapshotId, ct) is not { } snapshot) return null;
        var identity = ReviewSourceEvidenceProvider.Identity(snapshot);
        var archive = snapshot.ArchiveRepository ?? identity;
        var choice = staged.Application;
        var baseline = false;
        if (choice is { Mode: ProjectImportApplicationMode.NewVersion, UseAsCurrent: true } && baselines is not null)
        {
            try
            {
                await baselines.SelectBaselineAsync(identity.Key, snapshot.Id, "Prosjektimport", $"Valgt som ny versjon ved import av {staged.FileName}", ct);
                baseline = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("The analysis baseline could not be recorded for import {ImportId}. Exception type {ExceptionType}", staged.ImportId, ex.GetType().Name);
            }
        }
        return new ProjectImportApplicationOutcome
        {
            Mode = choice?.Mode ?? (declared is null ? ProjectImportApplicationMode.NewApplication : ProjectImportApplicationMode.NewVersion),
            ApplicationKey = identity.Key, ApplicationName = identity.DisplayName, ArchiveKey = archive.Key, ArchiveName = archive.DisplayName,
            BaselineSelected = baseline, Explicit = choice is not null,
        };
    }
}

/// <summary>A refused application choice (code + Norwegian message for the import page).</summary>
public sealed class ProjectImportChoiceException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
