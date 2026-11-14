namespace BirkNext.Web.Services;

/// <summary>
/// Frontend auto-save timer service.
/// Monitors artifact changes and triggers auto-save with event-based throttling.
///
/// Responsibilities:
/// 1. Start/stop auto-save timer on artifact changes
/// 2. Throttle to prevent more than one save per 30 seconds
/// 3. Integrate with workspace persistence backend
/// </summary>
/// <summary>Honest save state of the workspace: never "Saved" after a failed save; a failure keeps the local state and offers a retry.</summary>
/// <remarks>Interrupted: the save was aborted because the page was unloading; like Failed, the changes stay unsaved and retryable.</remarks>
public enum WorkspaceSaveState { Idle, Pending, Saving, Saved, Failed, Interrupted }

public interface IWorkspaceAutoSaveService
{
    /// <summary>Current save state (see <see cref="WorkspaceSaveState"/>).</summary>
    WorkspaceSaveState SaveState { get; }

    /// <summary>Why the last save failed (when <see cref="SaveState"/> is Failed).</summary>
    string? LastSaveError { get; }

    /// <summary>Raised whenever <see cref="SaveState"/> changes.</summary>
    event EventHandler? SaveStateChanged;

    /// <summary>
    /// Start monitoring for auto-save. Call from component OnInitialized.
    /// </summary>
    Task StartMonitoringAsync();

    /// <summary>
    /// Stop monitoring for auto-save. Call from component Dispose.
    /// </summary>
    Task StopMonitoringAsync();

    /// <summary>
    /// Notify that an artifact has changed.
    /// Restarts the auto-save timer.
    /// </summary>
    void OnArtifactChanged();

    /// <summary>
    /// Check if auto-save is currently throttled.
    /// </summary>
    bool IsThrottled { get; }

    /// <summary>
    /// Milliseconds until next auto-save is allowed (0 if allowed now).
    /// </summary>
    long ThrottleWaitMs { get; }

    /// <summary>
    /// Persist the current workspace identity and artifacts immediately, bypassing the debounce/throttle. Used for
    /// explicit user choices (selecting or clearing a Sample Project) so the choice is durably saved before the UI
    /// reports it as selected. Returns false when the backend rejected or could not receive the save.
    /// </summary>
    Task<bool> SaveNowAsync();

    /// <summary>
    /// True while a change exists that the backend has not confirmed: set by every change, cleared only when a save that included it
    /// succeeded. A failed, refused or interrupted save leaves it set.
    /// </summary>
    bool HasUnsavedChanges => false;

    /// <summary>
    /// Saves now if there are unconfirmed changes (waits for an in-flight save first, then saves only if it did not already cover the latest
    /// change). Used on in-app navigation. Returns true when nothing is left unsaved.
    /// </summary>
    Task<bool> FlushAsync() => Task.FromResult(!HasUnsavedChanges);

    Task PauseForResetAsync();
    void ResumeAfterReset();

    /// <summary>
    /// Raised when auto-save completes successfully.
    /// </summary>
    event EventHandler? AutoSaveCompleted;
}

public class WorkspaceAutoSaveService : IWorkspaceAutoSaveService
{
    private readonly IWorkspaceArtifactRepository _artifactRepository;
    private readonly IWorkspacePersistenceApiService _persistence;
    private readonly IWorkspaceSessionRestoreService _restore;
    private readonly IWorkspaceUpdateCoordinator _updates;
    private readonly ILogger<WorkspaceAutoSaveService> _logger;

    private System.Threading.Timer? _autoSaveTimer;
    private DateTimeOffset _lastAutoSaveTime = DateTimeOffset.UtcNow.AddHours(-1);
    private readonly int AutoSaveIntervalMs;  // Wait after last change (default 3 seconds)
    private readonly int AutoSaveThrottleMs;  // Max one save per window (default 30 seconds)
    private bool _isMonitoring = false;
    private volatile bool _resetInProgress;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public event EventHandler? AutoSaveCompleted;
    public event EventHandler? SaveStateChanged;
    public WorkspaceSaveState SaveState { get; private set; } = WorkspaceSaveState.Idle;
    public string? LastSaveError { get; private set; }

    private void SetState(WorkspaceSaveState state, string? error = null)
    {
        if (SaveState == state && LastSaveError == error) return;
        SaveState = state;
        LastSaveError = error;
        SaveStateChanged?.Invoke(this, EventArgs.Empty);
    }

    // Change sequence: every change increments _changeSequence; a save captures the sequence before it snapshots the workspace and, only when
    // the backend confirms it, advances _confirmedSequence to that captured value (never backwards). A confirmation for an older sequence
    // therefore never marks a newer change as saved, and the state after it is Pending (newer change waiting), not Saved.
    private long _changeSequence;
    private long _confirmedSequence;

    public bool HasUnsavedChanges => Interlocked.Read(ref _changeSequence) > Interlocked.Read(ref _confirmedSequence);

    private void Confirm(long sequence)
    {
        long current;
        while ((current = Interlocked.Read(ref _confirmedSequence)) < sequence
               && Interlocked.CompareExchange(ref _confirmedSequence, sequence, current) != current) { }
    }

    /// <summary>
    /// After a save attempt for <paramref name="sequence"/>: Saved only when the backend confirmed it and nothing newer is waiting (Pending
    /// otherwise); Idle when there was nothing to save; Interrupted when the page unload aborted it; Failed with the persistence layer's reason.
    /// Only a confirmation clears the unsaved state; local state is never cleared.
    /// </summary>
    private bool SettleAfterSave(long sequence, SavedWorkspaceDto? result)
    {
        if (result is not null)
        {
            Confirm(sequence);
            _lastAutoSaveTime = DateTimeOffset.UtcNow;
            SetState(HasUnsavedChanges ? WorkspaceSaveState.Pending : WorkspaceSaveState.Saved);
            OnAutoSaveCompleted();
            return true;
        }

        if (_persistence.LastSaveInterrupted)
        {
            _logger.LogInformation("Workspace save interrupted by page unload; the changes stay unsaved");
            SetState(WorkspaceSaveState.Interrupted, _persistence.LastSaveError ?? "The save was interrupted; the last changes are not confirmed as saved.");
            return false;
        }

        var error = _persistence.LastSaveError;
        if (error is null)
        {
            // Nothing to save (the backend answered 204: no project, no artifacts, no workspace): the backend matches this state.
            Confirm(sequence);
            SetState(HasUnsavedChanges ? WorkspaceSaveState.Pending : WorkspaceSaveState.Idle);
            return false;
        }

        SetState(WorkspaceSaveState.Failed, error);
        return false;
    }

    /// <summary>
    /// The one save path, serialized by the save gate so saves never overlap and responses arrive in request order. With
    /// <paramref name="onlyWhenUnsaved"/>, a save that finds every change already confirmed (an earlier save covered it) is skipped, so queued
    /// triggers coalesce into one request carrying the newest state.
    /// </summary>
    private async Task<bool> SaveAsync(bool onlyWhenUnsaved, string trigger)
    {
        await _saveGate.WaitAsync();
        try
        {
            if (_resetInProgress) return false;
            if (onlyWhenUnsaved && !HasUnsavedChanges) return true;

            var sequence = Interlocked.Read(ref _changeSequence); // captured before the snapshot is taken
            SetState(WorkspaceSaveState.Saving);
            SavedWorkspaceDto? result;
            try
            {
                result = await _persistence.AutoSaveAsync();
            }
            catch (Exception ex)
            {
                SetState(WorkspaceSaveState.Failed, "The workspace could not be saved.");
                _logger.LogError(ex, "Workspace save ({Trigger}) failed", trigger);
                return false;
            }

            var saved = SettleAfterSave(sequence, result);
            if (!saved && SaveState == WorkspaceSaveState.Failed)
                _logger.LogWarning("Workspace save ({Trigger}) failed: {Error}", trigger, LastSaveError);
            return saved;
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public bool IsThrottled => !CanAutoSave();

    public long ThrottleWaitMs
    {
        get
        {
            var elapsed = DateTimeOffset.UtcNow - _lastAutoSaveTime;
            var wait = AutoSaveThrottleMs - (long)elapsed.TotalMilliseconds;
            return Math.Max(0, wait);
        }
    }

    public WorkspaceAutoSaveService(
        IWorkspaceArtifactRepository artifactRepository,
        IWorkspacePersistenceApiService persistence,
        IWorkspaceSessionRestoreService restore,
        IWorkspaceUpdateCoordinator updates,
        ILogger<WorkspaceAutoSaveService> logger,
        int autoSaveIntervalMs = 3000,
        int autoSaveThrottleMs = 30000)
    {
        _artifactRepository = artifactRepository;
        _persistence = persistence;
        _restore = restore;
        _updates = updates;
        _logger = logger;
        AutoSaveIntervalMs = Math.Max(1, autoSaveIntervalMs);
        AutoSaveThrottleMs = Math.Max(0, autoSaveThrottleMs);

        // Subscribe to artifacts changed events
        _updates.ArtifactsChanged += OnArtifactsChanged;

        // Also subscribe to project selection changes to persist project identity
        if (artifactRepository is WorkspaceArtifactRepository repo)
        {
            repo.ProjectSelectionChanged += OnArtifactsChanged;
        }
    }

    public async Task StartMonitoringAsync()
    {
        System.Diagnostics.Debug.WriteLine("DIAG: [AutoSave] StartMonitoringAsync CALLED");
        if (_isMonitoring)
        {
            System.Diagnostics.Debug.WriteLine("DIAG: [AutoSave] Already monitoring, returning");
            return;
        }

        _isMonitoring = true;
        System.Diagnostics.Debug.WriteLine("DIAG: [AutoSave] Monitoring NOW ENABLED");
        await Task.CompletedTask;
    }

    public async Task StopMonitoringAsync()
    {
        if (!_isMonitoring) return;

        System.Diagnostics.Debug.WriteLine("DIAG: [AutoSave] StopMonitoringAsync CALLED");
        var stackTrace = new System.Diagnostics.StackTrace(true);
        for (int i = 0; i < Math.Min(10, stackTrace.FrameCount); i++)
        {
            var frame = stackTrace.GetFrame(i);
            var method = frame?.GetMethod();
            if (method != null)
            {
                System.Diagnostics.Debug.WriteLine($"DIAG: [AutoSave]   Stack[{i}]: {method.DeclaringType?.Name}.{method.Name}");
            }
        }

        CancelAutoSaveTimer();
        _isMonitoring = false;
        _updates.ArtifactsChanged -= OnArtifactsChanged;
        System.Diagnostics.Debug.WriteLine("DIAG: [AutoSave] StopMonitoringAsync COMPLETED");
        _logger.LogInformation("Stopped auto-save monitoring");
        await Task.CompletedTask;
    }

    private void OnArtifactsChanged(object? sender, EventArgs e)
    {
        System.Diagnostics.Debug.WriteLine("DIAG: [AutoSave] OnArtifactsChanged event ENTERED");
        OnArtifactChanged();
    }

    public void OnArtifactChanged()
    {
        if (_resetInProgress) return;
        System.Diagnostics.Debug.WriteLine($"DIAG: [AutoSave] OnArtifactChanged ENTERED, _isMonitoring={_isMonitoring}");
        if (!_isMonitoring)
        {
            System.Diagnostics.Debug.WriteLine("DIAG: [AutoSave] OnArtifactChanged RETURNED EARLY, starting monitoring");
            // Auto-enable monitoring on first change (project selection or artifact update)
            // This ensures identity-only persistence works without explicit StartMonitoringAsync
            _isMonitoring = true;
        }

        Interlocked.Increment(ref _changeSequence);

        // Restart the timer
        if (SaveState != WorkspaceSaveState.Saving) SetState(WorkspaceSaveState.Pending, LastSaveError);
        ScheduleAutoSave(AutoSaveIntervalMs);
    }

    private void ScheduleAutoSave(int delayMs)
    {
        CancelAutoSaveTimer();
        System.Diagnostics.Debug.WriteLine($"DIAG: [AutoSave] Debounce timer CREATED ({delayMs} ms)");

        _autoSaveTimer = new System.Threading.Timer(
            async state =>
            {
                System.Diagnostics.Debug.WriteLine("DIAG: [AutoSave] Debounce timer FIRED");
                if (CanAutoSave())
                {
                    System.Diagnostics.Debug.WriteLine("DIAG: [AutoSave] PerformAutoSaveAsync ENTERING");
                    await PerformAutoSaveAsync();
                }
                else
                {
                    // Throttled: a pending change must never be dropped (a dropped save loses the user's selection on
                    // restart). Re-arm the timer for the remaining throttle window instead.
                    var wait = (int)Math.Min(int.MaxValue, Math.Max(1, ThrottleWaitMs));
                    System.Diagnostics.Debug.WriteLine($"DIAG: [AutoSave] Timer fired but throttled; retrying in {wait} ms");
                    ScheduleAutoSave(wait);
                }
            },
            null,
            Math.Max(1, delayMs),
            Timeout.Infinite);
    }

    public async Task<bool> SaveNowAsync()
    {
        if (_resetInProgress) return false;
        // An explicit user choice supersedes any pending debounced save of the same state. It always sends (never skipped as already saved).
        CancelAutoSaveTimer();
        _isMonitoring = true;
        return await SaveAsync(onlyWhenUnsaved: false, trigger: "explicit");
    }

    public async Task<bool> FlushAsync()
    {
        if (_resetInProgress) return false;
        if (!HasUnsavedChanges) return true;
        // Navigation flush: bypasses the debounce and throttle; waits for an in-flight save, then saves only what is still unconfirmed.
        CancelAutoSaveTimer();
        await SaveAsync(onlyWhenUnsaved: true, trigger: "navigation");
        return !HasUnsavedChanges;
    }

    public async Task PauseForResetAsync()
    {
        _resetInProgress = true;
        CancelAutoSaveTimer();
        await _saveGate.WaitAsync();
        _saveGate.Release();
    }

    public void ResumeAfterReset()
    {
        // The reset discarded this browser's project state: nothing from before it is waiting to be saved.
        Confirm(Interlocked.Read(ref _changeSequence));
        _resetInProgress = false;
        SetState(WorkspaceSaveState.Idle);
    }

    private void CancelAutoSaveTimer()
    {
        _autoSaveTimer?.Dispose();
        _autoSaveTimer = null;
    }

    private bool CanAutoSave()
    {
        var elapsed = DateTimeOffset.UtcNow - _lastAutoSaveTime;
        return elapsed.TotalMilliseconds >= AutoSaveThrottleMs;
    }

    private async Task PerformAutoSaveAsync()
    {
        _logger.LogInformation("TRACE: [WorkspaceAutoSaveService.PerformAutoSaveAsync] Count={Count}", _artifactRepository.GetAllArtifacts().Count());
        // Debounced save: coalesces with any save that already covered the latest change.
        await SaveAsync(onlyWhenUnsaved: true, trigger: "auto-save");
    }

    protected virtual void OnAutoSaveCompleted()
    {
        AutoSaveCompleted?.Invoke(this, EventArgs.Empty);
    }
}
