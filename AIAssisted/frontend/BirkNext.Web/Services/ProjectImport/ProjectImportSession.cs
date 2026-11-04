using BirkNext.Integrations;
using BirkNext.ProjectActivity;
using BirkNext.ProjectImport;
using BirkNext.Web.Services.Operations;
using BirkNext.Web.Services.ProjectActivity;

namespace BirkNext.Web.Services.ProjectImport;

public enum ProjectImportPhase { Choose, Validating, Classifying, Preview, Importing, Result }

/// <summary>
/// The current Project Import session of this browser tab — upload and check, document classification, commit and activation — owned here,
/// not by the Import Project page. The page renders this state and starts/cancels work; leaving the page (Dashboard, Delivery Overview …),
/// switching Workspace View or selecting another project never stops the work, and returning shows the same stage and progress.
/// <para>Root cause it fixes: the page held the whole session in its own fields and ran the import in its own methods. Leaving it disposed the
/// state (progress gone, the page offered a fresh upload — a duplicate import), the upload read the file through the page's file input (removed
/// with the page, so the upload failed), and the commit that kept running activated its project later, even over a project the user had
/// selected meanwhile.</para>
/// <para>Each run is also an <see cref="BackgroundOperationService"/> operation (Activity Center) with measured progress: bytes uploaded,
/// documents classified, Source Analysis stage n of N.</para>
/// </summary>
public sealed class ProjectImportSession(IProjectImportApiService api, ProjectImportActivation activation, WorkspaceArtifactRepository repository,
    ProjectApplicabilityState applicability, IWorkspaceAutoSaveService autoSave, BackgroundOperationService operations,
    Microsoft.Extensions.Logging.ILogger<ProjectImportSession>? logger = null, BirkNext.Web.Services.ProjectEvidence.IProjectEvidenceService? evidence = null,
    IIntegrationJourneysApiService? journeys = null, ProjectActivityRecorder? activity = null)
{
    public const string Route = "project-import";
    public const long MaxUploadBytes = 50L * 1024 * 1024;

    public ProjectImportPhase Phase { get; set; } = ProjectImportPhase.Choose;
    public string? FileName { get; set; }
    public ProjectImportPreview? Preview { get; set; }
    public ProjectImportArtifactDiscovery? Discovery { get; set; }
    public ProjectImportCommitResult? Commit { get; set; }
    public SourceUploadFailure? Failure { get; set; }
    public bool SaveWarning { get; set; }
    public ProjectImportCommitEvent? Progress { get; set; }
    public int Classified { get; set; }
    public int ClassifyTotal { get; set; }
    public long UploadedBytes { get; set; }
    public long UploadTotalBytes { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public ProjectImportDuplicate? Duplicate { get; set; }
    public bool ImportAgain { get; set; }
    /// <summary>Changes on every reset or failure: the page re-creates its file input, so choosing the very same ZIP again fires a change.</summary>
    public int InputGeneration { get; set; }
    public bool ShowUpload { get; set; }
    /// <summary>Which logical application the next import is evidence for: the current one ("Import more evidence", default) or a new one
    /// ("Import another application"). Chosen by the tester before choosing the ZIP; reset to the default after each import.</summary>
    public ImportTarget Target { get; set; } = ImportTarget.CurrentApplication;
    /// <summary>The import's one-line activity summary ("Supporting/shared source · 4 documents · Source Analysis Partial · 3 items need attention").</summary>
    public ImportActivitySummary? ResultSummary { get; private set; }
    /// <summary>Set when the import finished after the user had selected another project: the import is recorded, the selection is kept.</summary>
    public string? KeptSelectionNote { get; set; }
    /// <summary>The page showed the result. Reopening Import Project after that shows the upload view again (the import is the current project
    /// card); a result that finished while the page was not shown is shown once on return.</summary>
    public bool ResultSeen { get; set; }
    /// <summary>The Activity Center operation of the running (or last) step.</summary>
    public Guid? OperationId { get; private set; }

    public bool IsBusy => Phase is ProjectImportPhase.Validating or ProjectImportPhase.Classifying or ProjectImportPhase.Importing;

    /// <summary>Raised after every state change; the page re-renders. Unsubscribing (leaving the page) never affects the work.</summary>
    public event Action? Changed;

    private void Notify()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    private Task<OperationSnapshot>? _running;

    /// <summary>The project the user has selected right now (Sample Project or current import) — captured to protect it from a late import.</summary>
    private string SelectionKey() => $"{repository.ProjectName}|{repository.SddLifecycle.CurrentProjectImportId}";

    /// <summary>Upload and check the chosen archive, then classify its documents. A second call while one runs returns the running one.</summary>
    public Task<OperationSnapshot> StartPreviewAsync(string fileName, long size, Func<CancellationToken, Task<Stream>> open, SourceSetupView? setup)
    {
        if (IsBusy && _running is { } running) return running;
        // A new archive starts a new import session: nothing of the previous result (commit, warnings, duplicate choice) carries over.
        Commit = null; SaveWarning = false; Duplicate = null; ImportAgain = false; Failure = null; KeptSelectionNote = null;
        Preview = null; Discovery = null; Classified = 0; ClassifyTotal = 0; UploadedBytes = 0; UploadTotalBytes = size; GitRevision = null;
        FileName = fileName;
        Phase = ProjectImportPhase.Validating;
        Progress = new ProjectImportCommitEvent { Stage = "Uploading and validating the archive", Detail = "Reading the ZIP and checking its entries." };
        StartedAt = DateTimeOffset.UtcNow;
        Notify();
        var run = operations.Start(new OperationRequest(OperationKind.ProjectImportUpload, $"Checking {fileName}", new OperationContext(fileName, "project-import:upload"), Route)
        {
            IdempotencyKey = "project-import", Cancellable = true,
        }, (progress, ct) => PreviewWorkAsync(fileName, size, open, setup, progress, ct));
        OperationId = run.Operation.Id;
        return _running = run.Completion;
    }

    private async Task<OperationOutcome> PreviewWorkAsync(string fileName, long size, Func<CancellationToken, Task<Stream>> open, SourceSetupView? setup,
        IOperationProgress progress, CancellationToken ct)
    {
        try
        {
            progress.Stage("Uploading the archive", fileName, 0, size, "bytes");
            await using var source = await open(ct);
            await using var counted = new CountingStream(source, read =>
            {
                UploadedBytes = read;
                progress.Stage("Uploading the archive", fileName, read, size, "bytes");
                Notify();
            });
            var (preview, error) = await api.PreviewAsync(fileName, counted, ct);
            if (preview is null) return Fail(error ?? new("IMPORT_PREVIEW_FAILED", "validation", "The archive could not be previewed. Nothing was imported."));
            return await AdoptAsync(preview, setup, progress, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (Preview is { } staged) _ = DiscardQuietlyAsync(staged.StagingId);
            Reset();
            ShowUpload = true;
            Notify();
            throw;
        }
        catch (IOException) { return Fail(new("ARCHIVE_TOO_LARGE", "upload", "The selected ZIP exceeds the 50 MB upload limit.", Limit: MaxUploadBytes)); }
        catch (HttpRequestException ex) { return Fail(new("IMPORT_UNAVAILABLE", "upload", $"{BackendRequestClassifier.FromException("Project import", ex).UserMessage} Nothing was imported.")); }
        catch (TaskCanceledException) { return Fail(new("IMPORT_TIMEOUT", "upload", "The archive check did not finish before the request timed out. Nothing was imported; retry.")); }
        catch (Exception ex) when (BackendRequestClassifier.IsRequestFailure(ex)) { return Fail(new("IMPORT_PREVIEW_FAILED", "validation", BackendRequestClassifier.FromException("Project import", ex).UserMessage)); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Type only: the message can carry paths or response text.
            Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ProjectImportSession>.Instance,
                "Project import preview failed before a backend answer. Exception type {ExceptionType}", ex.GetType().FullName);
            return Fail(new("IMPORT_PREVIEW_FAILED", "validation", "The archive could not be previewed. Nothing was imported; choose it again and retry."));
        }

        OperationOutcome Fail(SourceUploadFailure failure)
        {
            StartedAt = null; Failure = failure; InputGeneration++; Phase = ProjectImportPhase.Choose; ShowUpload = true;
            Notify();
            return OperationOutcome.Failure(failure.Message, canRetry: false);
        }
    }

    /// <summary>The staged preview (from a chosen ZIP or a Git revision) continues identically: document roles, duplicate check, review.</summary>
    private async Task<OperationOutcome> AdoptAsync(ProjectImportPreview preview, SourceSetupView? setup, IOperationProgress progress, CancellationToken ct)
    {
        Preview = preview;
        Phase = ProjectImportPhase.Classifying;
        ClassifyTotal = preview.Documents.Count;
        progress.Stage("Detecting document roles", null, 0, ClassifyTotal, "documents");
        Notify();
        Duplicate = ProjectImportHistory.Duplicate(preview.Archive.Sha256, activation.Imports, activation.Current?.ImportId, setup);
        Discovery = await ProjectImportArtifactDiscovery.FromAsync(preview, new Progress<(int Done, int Total)>(p =>
        {
            Classified = p.Done; ClassifyTotal = p.Total;
            progress.Stage("Detecting document roles", null, p.Done, p.Total, "documents");
            Notify();
        }));
        ct.ThrowIfCancellationRequested();
        Phase = ProjectImportPhase.Preview;
        StartedAt = null;
        Notify();
        return OperationOutcome.Success(Route, "Review the import");
    }

    /// <summary>The Git revision of the current preview (Git Sources import), shown as provenance; null for a chosen ZIP.</summary>
    public BirkNext.GitSources.GitSourceRevision? GitRevision { get; private set; }

    /// <summary>
    /// Git mode: the backend downloads one branch's current commit and stages it through the same preview; from there the import is exactly a
    /// ZIP import (roles, duplicates, commit, activation). Started only by the person's explicit choice.
    /// </summary>
    public Task<OperationSnapshot> StartGitPreviewAsync(string label, Func<CancellationToken, Task<(BirkNext.GitSources.GitImportResult? Result, string? Error)>> fetch,
        SourceSetupView? setup)
    {
        if (IsBusy && _running is { } running) return running;
        Commit = null; SaveWarning = false; Duplicate = null; ImportAgain = false; Failure = null; KeptSelectionNote = null;
        Preview = null; Discovery = null; Classified = 0; ClassifyTotal = 0; UploadedBytes = 0; UploadTotalBytes = 0; GitRevision = null;
        FileName = label;
        Phase = ProjectImportPhase.Validating;
        Progress = new ProjectImportCommitEvent { Stage = "Downloading the commit from Azure DevOps", Detail = "Resolving the branch to its commit and validating the archive." };
        StartedAt = DateTimeOffset.UtcNow;
        Notify();
        var run = operations.Start(new OperationRequest(OperationKind.ProjectImportUpload, $"Checking {label}", new OperationContext(label, "project-import:git"), Route)
        {
            IdempotencyKey = "project-import", Cancellable = true,
        }, async (progress, ct) =>
        {
            progress.Stage("Downloading the commit from Azure DevOps", label, 0, 1, "steps");
            try
            {
                var (result, error) = await fetch(ct);
                if (result?.Preview is not { } preview)
                {
                    StartedAt = null; Phase = ProjectImportPhase.Choose; ShowUpload = true; InputGeneration++;
                    Failure = new("GIT_IMPORT_FAILED", "git", (result?.Failure ?? error ?? "The commit could not be imported.") + " Nothing was imported.");
                    Notify();
                    return OperationOutcome.Failure(Failure.Message, canRetry: false);
                }
                GitRevision = result.Revision;
                FileName = preview.Archive.FileName;
                return await AdoptAsync(preview, setup, progress, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                if (Preview is { } staged) _ = DiscardQuietlyAsync(staged.StagingId);
                Reset();
                ShowUpload = true;
                Notify();
                throw;
            }
        });
        OperationId = run.Operation.Id;
        return _running = run.Completion;
    }

    /// <summary>Commit the staged archive and activate its documents. Repeated calls while it runs return the running import (no duplicate).</summary>
    public Task<OperationSnapshot> StartImportAsync()
    {
        if (IsBusy && _running is { } running) return running;
        if (Preview is not { } preview || Discovery is not { } discovery) return Task.FromResult(new OperationSnapshot { Status = OperationStatus.Failed, Error = "Nothing to import." });
        var retryOf = Failure is not null ? OperationId : null;
        Failure = null; KeptSelectionNote = null;
        Progress = new ProjectImportCommitEvent { Stage = "Starting the project import", Detail = "Sending the staged archive to the existing import service." };
        StartedAt = DateTimeOffset.UtcNow;
        Phase = ProjectImportPhase.Importing;
        Notify();
        var selectionAtStart = SelectionKey();
        var run = operations.Start(new OperationRequest(OperationKind.ProjectImport, $"Importing {preview.ProjectName}", new OperationContext(preview.ProjectName, preview.ImportId)
        {
            Revision = preview.Archive.Sha256[..Math.Min(12, preview.Archive.Sha256.Length)],
        }, Route) { IdempotencyKey = "project-import", Cancellable = true, RetryOf = retryOf },
            (progress, ct) => ImportWorkAsync(preview, discovery, selectionAtStart, progress, ct));
        OperationId = run.Operation.Id;
        return _running = run.Completion;
    }

    private async Task<OperationOutcome> ImportWorkAsync(ProjectImportPreview preview, ProjectImportArtifactDiscovery discovery, string selectionAtStart,
        IOperationProgress progress, CancellationToken ct)
    {
        ProjectImportCommitResult? commit;
        SourceUploadFailure? error;
        progress.Stage("Starting the project import");
        var events = new Progress<ProjectImportCommitEvent>(update =>
        {
            if (Phase != ProjectImportPhase.Importing) return;
            Progress = update;
            // Stage n of N is reported as text: stages differ in size, so it is not an overall percentage.
            progress.Stage(update.Stage, update.StageNumber is { } n && update.StageCount is { } c ? $"Source Analysis stage {n} of {c}" : update.Detail);
            Notify();
        });
        try { (commit, error) = await api.CommitWithProgressAsync(preview.StagingId, events, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancelled before a result: nothing is activated (Source Analysis stops between work units; a completed snapshot is reused on retry).
            Restore(new SourceUploadFailure("IMPORT_CANCELLED", "commit", "The import was cancelled. Nothing was activated in this workspace; the staged archive can be imported again."));
            throw;
        }
        catch (HttpRequestException ex) { (commit, error) = (null, new SourceUploadFailure("IMPORT_UNAVAILABLE", "upload", $"{BackendRequestClassifier.FromException("Project import", ex).UserMessage} Nothing was activated; retry.")); }
        catch (TaskCanceledException) { (commit, error) = (null, new SourceUploadFailure("IMPORT_TIMEOUT", "upload", "The import did not finish before the request timed out. Nothing was activated in this workspace; retry — the same archive reuses a snapshot that was already created.")); }
        catch (Exception ex) when (BackendRequestClassifier.IsRequestFailure(ex)) { (commit, error) = (null, new SourceUploadFailure("IMPORT_RESPONSE_INVALID", "commit", BackendRequestClassifier.FromException("Project import", ex).UserMessage)); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { (commit, error) = (null, new SourceUploadFailure("IMPORT_FAILED", "commit", "The import stopped before a result was returned. Retry the staged archive; a completed source snapshot will be reused.")); }
        if (commit is null)
        {
            // Nothing is activated without a commit result: the workspace never holds half an import.
            Restore(error);
            await RecordFailureAsync(preview, error);
            if (Phase == ProjectImportPhase.Choose) return OperationOutcome.Failure(error?.Message ?? "The import failed.", canRetry: false);
            return OperationOutcome.Failure(error?.Message ?? "The import failed.");
        }
        // Point of no return: from here the workspace is updated; a cancellation that arrives now is refused.
        if (!progress.BeginFinalization("Activating project documents", "Applying the classified candidates to the Shared Artifact Repository."))
        {
            Restore(new SourceUploadFailure("IMPORT_CANCELLED", "commit", "The import was cancelled. Nothing was activated in this workspace; the staged archive can be imported again."));
            ct.ThrowIfCancellationRequested();
        }
        Commit = commit;
        try
        {
            Progress = new ProjectImportCommitEvent { Stage = "Activating project documents", Detail = "Applying the classified candidates to the Shared Artifact Repository." };
            Notify();
            // The import belongs to its own project. If the user selected another project while it ran, it is recorded but not made current.
            var keepSelection = SelectionKey() != selectionAtStart;
            var target = Target;
            var record = activation.Activate(preview, discovery, commit, makeCurrent: !keepSelection, target: target);
            // The package's role needs the backend's classification of its snapshot: read once, then show the application's primary
            // documents (a supporting package imported last never replaces them) and refresh the application's name.
            var setup = await ReadSetupAsync();
            activation.SettleDocuments(record.ImportId, setup);
            ResultSummary = ImportActivitySummary.Of(repository.SddLifecycle, record, setup, DateTimeOffset.UtcNow);
            KeptSelectionNote = keepSelection
                ? $"“{preview.ProjectName}” was imported. You selected another project while it ran, so that selection was kept — use “Use as current project” to switch."
                : null;
            Progress = new ProjectImportCommitEvent { Stage = "Saving the workspace", Detail = "Saving the import selection and provenance." };
            Notify();
            SaveWarning = !await autoSave.SaveNowAsync();
            if (commit.Source.SnapshotId is not null) await applicability.RefreshAsync();
            // Project Evidence (every Target Environment) re-reads its sources: the new package is selectable at once, bindings unchanged.
            evidence?.Invalidate();
            Target = ImportTarget.CurrentApplication;
            // Activity only for an import whose workspace update was saved: never "Imported X" for an import that may not survive a reload.
            if (!SaveWarning && activity is not null && ResultSummary is { } summary)
                await activity.RecordAsync(summary.Request(record, commit.Provenance.ImportedAt));
            Failure = null;
            ResultSeen = false;
            Phase = ProjectImportPhase.Result;
            StartedAt = null;
            Notify();
            return OperationOutcome.Success(Route, keepSelection ? "Imported (selection kept)" : "Open the imported project");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Failure = new("PROJECT_IMPORT_ACTIVATION_FAILED", "workspace", "The workspace update did not finish. Source Analysis may already have saved its snapshot; retry this staged archive to continue.", CorrelationId: null);
            Phase = ProjectImportPhase.Preview;
            StartedAt = null;
            Notify();
            return OperationOutcome.Failure(Failure.Message);
        }
    }

    /// <summary>The backend's source setup (packs + classified snapshots); null when it cannot be read (nothing is guessed then).</summary>
    private async Task<SourceSetupView?> ReadSetupAsync()
    {
        if (journeys is null) return null;
        try { return (await journeys.SourceSetupAsync(null)).Value; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    /// <summary>
    /// "Import failed" activity: only for an import that actually started into an existing application (not a cancellation, not a validation
    /// error of the preview, never for a brand-new application that does not exist yet). One event per import attempt.
    /// </summary>
    private async Task RecordFailureAsync(ProjectImportPreview preview, SourceUploadFailure? error)
    {
        if (activity?.CurrentContextId is null || Target != ImportTarget.CurrentApplication || error?.Code == "IMPORT_CANCELLED" || OperationId is not { } attempt) return;
        await activity.RecordAsync(new ProjectActivityRecordRequest
        {
            Type = ProjectActivityType.ImportFailed, Status = ProjectActivityStatus.Failed, SourceModule = "Import Project",
            Title = $"Import failed: {preview.Archive.FileName}", Summary = error?.Message, PackageId = preview.ImportId, PackageName = preview.ProjectName,
            RelatedEntityType = "ImportAttempt", RelatedEntityId = attempt.ToString("N"), Route = Route, IdempotencyKey = $"import-failed:{attempt:N}",
        });
    }

    private void Restore(SourceUploadFailure? error)
    {
        StartedAt = null;
        Failure = error;
        Phase = error?.Code == "IMPORT_STAGING_EXPIRED" ? ProjectImportPhase.Choose : ProjectImportPhase.Preview;
        if (Phase == ProjectImportPhase.Choose) { Preview = null; Discovery = null; InputGeneration++; ShowUpload = true; }
        Notify();
    }

    /// <summary>Explicit cancellation of the running upload or import (never caused by leaving the page).</summary>
    public bool Cancel() => OperationId is { } id && operations.Cancel(id);

    public bool CanCancel => OperationId is { } id && operations.Find(id) is { CanCancel: true, IsActive: true };

    /// <summary>Clears every archive-specific value and re-creates the file input. Not allowed while work runs (cancel it first).</summary>
    public void Reset()
    {
        if (Phase is ProjectImportPhase.Importing) return;
        Preview = null; Discovery = null; Commit = null; Failure = null; SaveWarning = false; Duplicate = null; ImportAgain = false; KeptSelectionNote = null; GitRevision = null;
        ResultSummary = null; Progress = null; FileName = null; Classified = 0; ClassifyTotal = 0; UploadedBytes = 0; UploadTotalBytes = 0; StartedAt = null; ResultSeen = false;
        InputGeneration++;
        Phase = ProjectImportPhase.Choose;
        Notify();
    }

    public void NotifyChanged() => Notify();

    public async Task DiscardQuietlyAsync(Guid stagingId)
    {
        try { await api.DiscardAsync(stagingId); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { /* the staging expires on its own */ }
    }

    /// <summary>Reads the chosen browser file into memory (bounded by <see cref="MaxUploadBytes"/>), so the upload no longer needs the page's input.</summary>
    public static async Task<Stream> CopyToMemoryAsync(Stream browserFile, CancellationToken ct)
    {
        await using (browserFile)
        {
            var copy = new MemoryStream();
            await browserFile.CopyToAsync(copy, 81920, ct);
            copy.Position = 0;
            return copy;
        }
    }

    /// <summary>Counts bytes read by the upload (real progress: bytes sent of the file size).</summary>
    private sealed class CountingStream(Stream inner, Action<long> report) : Stream
    {
        private long _read;
        private long _reported;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => Count(await inner.ReadAsync(buffer.AsMemory(offset, count), ct));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => Count(await inner.ReadAsync(buffer, ct));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        private int Count(int n)
        {
            _read += n;
            if (n == 0 || _read - _reported >= 256 * 1024) { _reported = _read; report(_read); }
            return n;
        }
        public override async ValueTask DisposeAsync() { await inner.DisposeAsync(); await base.DisposeAsync(); }
    }
}
