namespace BirkNext.Api.Services;

/// <summary>
/// Manages auto-save operations with event-based throttling.
/// Triggers auto-save after user inactivity, with a maximum frequency of once per throttle window.
///
/// Usage:
/// 1. Subscribe to artifact changes: _artifactRepository.ArtifactChanged += OnArtifactChanged
/// 2. On artifact change, call StartAutoSaveTimerAsync()
/// 3. Timer fires after inactivity → calls callback to trigger actual save
/// 4. Throttle prevents more than one save per 30-second window
/// </summary>
public interface IAutoSaveService
{
    /// <summary>
    /// Start (or restart) the auto-save timer.
    /// Cancels any pending timer and starts a new one.
    /// When timer fires, calls onAutoSaveAsync if throttle allows.
    /// </summary>
    void StartAutoSaveTimer(Func<Task> onAutoSaveAsync);

    /// <summary>
    /// Cancel any pending auto-save timer.
    /// </summary>
    void CancelAutoSaveTimer();

    /// <summary>
    /// Check if auto-save is currently allowed (not throttled).
    /// </summary>
    bool CanAutoSave();

    /// <summary>
    /// Get milliseconds until next auto-save is allowed.
    /// Returns 0 if auto-save is allowed now.
    /// </summary>
    long GetThrottleWaitMs();
}

public class AutoSaveService : IAutoSaveService
{
    private readonly IConfiguration _config;
    private readonly ILogger<AutoSaveService> _logger;
    private Timer? _autoSaveTimer;
    private DateTimeOffset _lastAutoSaveTime = DateTimeOffset.UtcNow.AddHours(-1);
    private int _autoSaveIntervalMs;
    private int _autoSaveThrottleMs;
    private Func<Task>? _pendingCallback;

    public AutoSaveService(IConfiguration config, ILogger<AutoSaveService> logger)
    {
        _config = config;
        _logger = logger;
        LoadConfiguration();
    }

    private void LoadConfiguration()
    {
        _autoSaveIntervalMs = _config.GetValue("WorkspacePersistence:AutoSaveIntervalMs", 3000);
        _autoSaveThrottleMs = _config.GetValue("WorkspacePersistence:AutoSaveThrottleMs", 30000);

        _logger.LogInformation(
            "AutoSaveService configured: interval={IntervalMs}ms, throttle={ThrottleMs}ms",
            _autoSaveIntervalMs, _autoSaveThrottleMs);
    }

    public void StartAutoSaveTimer(Func<Task> onAutoSaveAsync)
    {
        // Cancel any existing timer
        CancelAutoSaveTimer();

        _pendingCallback = onAutoSaveAsync;

        // Start new timer that fires after inactivity
        _autoSaveTimer = new Timer(
            async state =>
            {
                if (CanAutoSave())
                {
                    _logger.LogDebug("Auto-save timer fired and throttle allows save");
                    try
                    {
                        await _pendingCallback?.Invoke()!;
                        _lastAutoSaveTime = DateTimeOffset.UtcNow;
                        _logger.LogInformation("Auto-save completed");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Auto-save failed");
                    }
                }
                else
                {
                    _logger.LogDebug("Auto-save timer fired but throttled, waiting for next inactivity");
                }
            },
            null,
            _autoSaveIntervalMs,
            Timeout.Infinite);
    }

    public void CancelAutoSaveTimer()
    {
        _autoSaveTimer?.Dispose();
        _autoSaveTimer = null;
        _pendingCallback = null;
    }

    public bool CanAutoSave()
    {
        var elapsed = DateTimeOffset.UtcNow - _lastAutoSaveTime;
        return elapsed.TotalMilliseconds >= _autoSaveThrottleMs;
    }

    public long GetThrottleWaitMs()
    {
        var elapsed = DateTimeOffset.UtcNow - _lastAutoSaveTime;
        var wait = _autoSaveThrottleMs - (long)elapsed.TotalMilliseconds;
        return Math.Max(0, wait);
    }
}
