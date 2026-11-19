namespace BirkNext.Updater;

/// <summary>What the updater will do with one package, decided before anything changes. <see cref="Blocker"/> non-null = refused.</summary>
public sealed record UpdatePlan(
    PackageInfo Package, InstalledVersion? Installed, PackageVersion? LastUsed, UpgradePath Path, TrustVerdict Trust, string TrustDetail,
    IReadOnlyList<string> MigrationsAdded, bool BirkNextRunning, string? Blocker)
{
    /// <summary>The database layout changes: a verified backup is taken first, and going back to the previous version needs that backup.</summary>
    public bool ChangesDatabase => MigrationsAdded.Count > 0;
    public bool CanApply => Blocker is null;
}

public sealed record UpdateResult(bool Ok, string Message, string? Url = null, string? Action = null);

/// <summary>Progress in the tester's words (one line per phase), and an optional fraction within a phase.</summary>
public interface IUpdateProgress
{
    void Phase(string text);
    void Fraction(int done, int total);
}

/// <summary>
/// One update, start to finish. The order is fixed so a failure before activation never changes the installed BirkNext or its data:
/// <list type="number">
/// <item>Check the package: archive safety, manifests, every listed file, version and the trust policy (nothing is written).</item>
/// <item>Check compatibility: upgrade path against the installed version and against the build that last used the data folder, and the database
/// migrations the new version adds.</item>
/// <item>Install into a staging folder, verifying every file's SHA-256, then move it to its version folder. Nothing is stopped yet.</item>
/// <item>Stop BirkNext through its own stop request (only the instance of this data folder; never by killing processes).</item>
/// <item>Activate: installations.json now names the new version (atomic replace); the previous version stays installed.</item>
/// <item>Start the new version. Its launcher takes the verified pre-upgrade backup (pg_dump, checked with pg_restore --list) before its backend
/// applies the database updates — the same path as every BirkNext upgrade; the updater never touches the database itself.</item>
/// </list>
/// Each stage is journaled, so an interrupted update is recognised and resolved (<see cref="Recover"/>).
/// </summary>
public sealed class UpdateEngine(InstallationStore store, InstanceControl instance, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    public static readonly TimeSpan StopTimeout = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(15);

    public UpdatePlan Plan(string zipPath)
    {
        var package = PackageInspector.Inspect(zipPath);
        var state = store.Read();
        var installed = state.Current;
        var lastUsed = instance.LastUsedBuild();
        var (trust, trustDetail) = TrustPolicy.Load(store.TrustPolicyFile).Evaluate(DownloadOrigin.Read(zipPath));
        var path = VersionRules.Compare(installed?.Version, package.Version);
        var added = installed is null ? [] : package.Migrations.Except(installed.Migrations, StringComparer.Ordinal).ToList();
        var removed = installed is null ? [] : installed.Migrations.Except(package.Migrations, StringComparer.Ordinal).ToList();

        string? blocker = trust != TrustVerdict.PipelineOrigin
            ? $"This package was not downloaded from the approved Azure DevOps pipeline. {trustDetail} Download the BirkNext artifact from the pipeline in your browser and select that file."
            : path is UpgradePath.SameBuild or UpgradePath.Downgrade or UpgradePath.NotComparable ? VersionRules.Explain(path, installed?.Version, package.Version)
            : removed.Count > 0 ? $"This package does not know {removed.Count} database update(s) the installed BirkNext already applied. Installing it could damage your data."
            : null;
        // Data used by a newer build than the candidate (a version extracted by hand, outside the updater) is never downgraded either.
        if (blocker is null && lastUsed is not null && VersionRules.Compare(lastUsed, package.Version) == UpgradePath.Downgrade)
            blocker = $"Your BirkNext data was last opened by {lastUsed.Display}, which is newer than this package ({package.Version.Display}). The updater does not install older versions.";
        return new(package, installed, lastUsed, path, trust, trustDetail, added, instance.IsRunning, blocker);
    }

    public async Task<UpdateResult> ApplyAsync(UpdatePlan plan, IUpdateProgress progress, bool customDataRoot, bool startAfter, bool noBrowser, CancellationToken ct)
    {
        if (!plan.CanApply) return new(false, plan.Blocker!);
        if (store.Journal() is { Stage: not UpdateStage.Completed } open)
            return new(false, $"An earlier update ({open.ToBuild}) did not finish.", Action: "Run Update-BirkNext.exe --recover first.");
        var journal = new UpdateJournal(Guid.NewGuid().ToString("N"), UpdateStage.Validated, plan.Installed?.BuildVersion, plan.Package.Version.BuildVersion, null, null,
            plan.MigrationsAdded, _clock.GetUtcNow(), _clock.GetUtcNow());
        store.Write(journal);
        store.Log($"Update {journal.Id}: {journal.FromBuild ?? "(none)"} -> {journal.ToBuild}; migrations added {plan.MigrationsAdded.Count}; trust {plan.Trust}.");

        var folderName = plan.Package.Version.FolderName;
        var target = store.FolderOf(folderName);
        var staging = store.NewStagingFolder();
        try
        {
            // 3 · Install side by side (nothing is stopped yet: a damaged package is found while BirkNext still runs).
            progress.Phase("Installing the new version");
            store.Write(journal = journal with { Stage = UpdateStage.Staging, StagingFolder = staging });
            PackageInspector.Extract(plan.Package, staging, progress.Fraction, ct);
            if (Directory.Exists(target))
            {
                // A version folder without an installation record (an earlier update stopped after moving it): replaced by the verified copy.
                if (string.Equals(store.Read().Current?.Folder, folderName, StringComparison.OrdinalIgnoreCase) || string.Equals(store.Read().Previous?.Folder, folderName, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("That version folder is in use.");
                Directory.Delete(target, recursive: true);
            }
            Directory.Move(staging, target);
            store.Write(journal = journal with { Stage = UpdateStage.Staged, StagingFolder = null, CandidateFolder = folderName });
        }
        catch (Exception e) when (e is PackageRejectedException or IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
        {
            TryDelete(staging);
            store.ClearJournal();
            store.Log($"Update {journal.Id} stopped before anything changed: {e.Message}");
            return e is OperationCanceledException ? new(false, "The update was cancelled. Nothing was changed.")
                : new(false, e is PackageRejectedException ? e.Message : $"The new version could not be installed: {e.Message}", Action: "Nothing was changed. Free some disk space or download the package again, then try again.");
        }

        // 4 · Stop BirkNext (only this data folder's instance, through its own stop request).
        var wasRunning = instance.IsRunning;
        if (wasRunning)
        {
            progress.Phase("Stopping BirkNext");
            store.Write(journal = journal with { Stage = UpdateStage.Stopping });
            if (!await instance.StopAsync(StopTimeout, ct))
            {
                store.ClearJournal();
                store.Log($"Update {journal.Id}: BirkNext did not stop; the installed version stays active.");
                return new(false, "BirkNext did not stop, so the update was not applied. Your BirkNext and its data are unchanged.",
                    Action: "Close the BirkNext window, then run the update again. The new version is already unpacked, so it will be quick.");
            }
        }
        store.Write(journal = journal with { Stage = UpdateStage.Stopped });

        // 5 · Activate (one atomic replace of installations.json; the previous version stays installed).
        progress.Phase("Activating the new version");
        var state = store.Read();
        store.Write(new InstallationState(InstalledVersion.From(plan.Package, folderName, _clock.GetUtcNow()), state.Current ?? state.Previous));
        store.Write(journal = journal with { Stage = UpdateStage.Activated });
        store.Log($"Update {journal.Id}: activated {folderName}.");
        RemoveObsolete();
        ShortcutsAndUpdater(target);

        if (!startAfter)
        {
            store.Write(journal with { Stage = UpdateStage.Completed, Message = "Installed; not started." });
            store.ClearJournal();
            return new(true, $"BirkNext {plan.Package.Version.Display} is installed." + (plan.ChangesDatabase ? " Its first start backs up your data and updates the database." : ""));
        }

        // 6 · Start (first start: verified backup, then database updates — done by the new launcher and backend).
        progress.Phase(plan.ChangesDatabase ? "Starting BirkNext (backing up your data and updating the database)" : "Starting BirkNext");
        var (ready, url, problem) = await instance.StartAsync(target, customDataRoot, noBrowser, StartTimeout, ct);
        store.Write(journal = journal with { Stage = ready ? UpdateStage.Completed : UpdateStage.Started, Message = problem });
        if (!ready)
        {
            store.Log($"Update {journal.Id}: the new version did not start: {problem}");
            return new(false, $"BirkNext {plan.Package.Version.Display} is installed, but it did not start: {problem}", Action: plan.ChangesDatabase
                ? $"Your data was backed up before the database update ({instance.BackupsFolder}). Send the diagnostics (Start-BirkNext.exe --diagnostics) to the BirkNext owner. Do not start the previous version: the database may already be updated."
                : "Run Update-BirkNext.exe --rollback to go back to the previous version (the database was not changed), and send the diagnostics to the BirkNext owner.");
        }
        store.ClearJournal();
        store.Log($"Update {journal.Id}: completed; running at {url}.");
        return new(true, $"BirkNext {plan.Package.Version.Display} is running.", url);
    }

    /// <summary>
    /// Resolves an update that was interrupted (power loss, closed window, crash), from its journal:
    /// <list type="bullet">
    /// <item>before activation: the installed BirkNext was never switched — the unpacked files are removed and nothing else changed;</item>
    /// <item>after activation: the new version is the active one and the data was not touched by the updater — start BirkNext to finish (its first
    /// start backs up and updates the database).</item>
    /// </list>
    /// </summary>
    public UpdateResult Recover()
    {
        var journal = store.Journal();
        if (journal is null) return new(true, "No unfinished update.");
        store.Log($"Recovering update {journal.Id} interrupted at {journal.Stage}.");
        if (journal.Stage < UpdateStage.Activated)
        {
            if (journal.StagingFolder is { } staging) TryDelete(staging);
            var state = store.Read();
            if (journal.CandidateFolder is { } candidate && !string.Equals(state.Current?.Folder, candidate, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(state.Previous?.Folder, candidate, StringComparison.OrdinalIgnoreCase))
                TryDelete(store.FolderOf(candidate));
            store.ClearJournal();
            return new(true, $"The update to {journal.ToBuild} was interrupted before it was applied. Your BirkNext ({journal.FromBuild ?? "as installed"}) and its data are unchanged.",
                Action: "Run the update again.");
        }
        store.ClearJournal();
        return new(true, $"The update to {journal.ToBuild} was applied before the interruption.", Action: "Start BirkNext. Its first start backs up your data and finishes the update.");
    }

    /// <summary>
    /// Makes the previous version active again — only when the active version added no database updates (the previous version then reads the data
    /// unchanged). Otherwise refused: the old version would refuse the newer database anyway, and the way back is the pre-upgrade backup.
    /// </summary>
    public async Task<UpdateResult> RollbackAsync(CancellationToken ct)
    {
        var state = store.Read();
        if (state.Current is not { } current || state.Previous is not { } previous) return new(false, "There is no previous version to go back to.");
        var added = current.Migrations.Except(previous.Migrations, StringComparer.Ordinal).ToList();
        if (added.Count > 0)
            return new(false, $"BirkNext {current.BuildVersion} updated the database ({added.Count} change(s)), so {previous.BuildVersion} cannot use your data.",
                Action: $"Keep using {current.BuildVersion}. To go back anyway, the BirkNext owner restores the pre-upgrade backup from {instance.BackupsFolder} (see the upgrade guide).");
        if (!Directory.Exists(store.FolderOf(previous.Folder))) return new(false, $"The previous version's folder ({previous.Folder}) is missing.");
        if (instance.IsRunning && !await instance.StopAsync(StopTimeout, ct)) return new(false, "BirkNext did not stop.", Action: "Close the BirkNext window and try again.");
        store.Write(new InstallationState(previous, current));
        store.Log($"Rolled back to {previous.Folder} (no database changes in {current.Folder}).");
        ShortcutsAndUpdater(store.FolderOf(previous.Folder));
        return new(true, $"BirkNext {previous.BuildVersion} is active again. Your data is unchanged.");
    }

    /// <summary>Old version folders (not current, not previous) are removed; a folder still in use is left for the next update.</summary>
    private void RemoveObsolete()
    {
        foreach (var folder in store.Obsolete(store.Read()).ToList())
        {
            try { Directory.Delete(folder, recursive: true); store.Log($"Removed old version {Path.GetFileName(folder)}."); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { store.Log($"Kept old version {Path.GetFileName(folder)} (in use: {e.Message})."); }
        }
    }

    /// <summary>The stable updater copy (outside every version folder, so it still runs when the active version cannot start) and the Start menu
    /// shortcuts. Failures here never fail the update: they are logged and the tester can still use the version folder directly.</summary>
    private void ShortcutsAndUpdater(string active)
    {
        try
        {
            var source = Path.Combine(active, "Update-BirkNext.exe");
            var running = Environment.ProcessPath;
            if (File.Exists(source) && !string.Equals(Path.GetFullPath(running ?? ""), store.StableUpdater, StringComparison.OrdinalIgnoreCase))
                File.Copy(source, store.StableUpdater, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { store.Log($"Stable updater copy not refreshed: {e.Message}"); }
        Shortcuts.Write(Path.Combine(active, "Start-BirkNext.exe"), store.StableUpdater, store.Log);
    }

    private static void TryDelete(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
