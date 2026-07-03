using System.Runtime.CompilerServices;

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
public interface IWorkspaceAutoSaveService
{
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
    private const int AutoSaveIntervalMs = 3000;  // Wait 3 seconds after last change
    private const int AutoSaveThrottleMs = 30000; // Max once per 30 seconds
    private bool _isMonitoring = false;

    public event EventHandler? AutoSaveCompleted;

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
        ILogger<WorkspaceAutoSaveService> logger)
    {
        _artifactRepository = artifactRepository;
        _persistence = persistence;
        _restore = restore;
        _updates = updates;
        _logger = logger;

        // Subscribe to artifacts changed events
        _updates.ArtifactsChanged += OnArtifactsChanged;
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
        System.Diagnostics.Debug.WriteLine($"DIAG: [AutoSave] OnArtifactChanged ENTERED, _isMonitoring={_isMonitoring}");
        if (!_isMonitoring)
        {
            System.Diagnostics.Debug.WriteLine("DIAG: [AutoSave] OnArtifactChanged RETURNED EARLY");
            return;
        }

        // Restart the timer
        CancelAutoSaveTimer();
        System.Diagnostics.Debug.WriteLine("DIAG: [AutoSave] Debounce timer CREATED");

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
                    System.Diagnostics.Debug.WriteLine("DIAG: [AutoSave] Timer fired but throttled");
                }
            },
            null,
            AutoSaveIntervalMs,
            Timeout.Infinite);
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
        try
        {
            var repoHash = RuntimeHelpers.GetHashCode(_artifactRepository);
            var artifacts = _artifactRepository.GetAllArtifacts().ToList();
            var artifactCount = artifacts.Count;

            _logger.LogInformation("TRACE: [WorkspaceAutoSaveService.PerformAutoSaveAsync]");
            _logger.LogInformation("  RepositoryType={Type}", _artifactRepository.GetType().Name);
            _logger.LogInformation("  Hash={Hash}", repoHash);
            _logger.LogInformation("  Count={Count}", artifactCount);

            var result = await _persistence.AutoSaveAsync();
            if (result != null)
            {
                _lastAutoSaveTime = DateTimeOffset.UtcNow;
                OnAutoSaveCompleted();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TRACE: Auto-save failed");
        }
    }

    protected virtual void OnAutoSaveCompleted()
    {
        AutoSaveCompleted?.Invoke(this, EventArgs.Empty);
    }
}
