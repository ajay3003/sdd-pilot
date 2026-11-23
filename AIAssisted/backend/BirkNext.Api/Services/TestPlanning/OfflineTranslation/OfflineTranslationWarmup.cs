namespace BirkNext.Api.Services.TestPlanning.OfflineTranslation;

/// <summary>
/// Starts the offline translator in the background shortly after BirkNext starts, when it is the translator in use (the default) or the
/// fallback of LibreTranslate. The first start verifies the 160 MB package and loads the model; on a fresh AVD session antivirus scans those
/// files as they are read, which took more than 90 seconds and made a tester's first translation fail (2026-11-23). Done here, that wait
/// happens before anyone asks for a translation. Nothing is downloaded and nothing leaves the PC; a failure is logged and shown in System
/// Settings → Translation exactly as before.
/// </summary>
public sealed class OfflineTranslationWarmup(OfflineTranslationEngine engine, TranslationSettingsStore settings, IConfiguration config,
    ILogger<OfflineTranslationWarmup> log) : BackgroundService
{
    /// <summary>Let the backend finish starting (database, first page) before the translator competes for disk and CPU.</summary>
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(20);

    /// <summary>The settings that warm the offline translator: it is the provider, or it is the offline fallback of LibreTranslate.</summary>
    public static bool Wanted(BirkNext.TestPlanning.TranslationSettings s) =>
        s.Provider == BirkNext.TestPlanning.TranslationProviderKind.EmbeddedOffline
        || s.Provider == BirkNext.TestPlanning.TranslationProviderKind.LibreTranslate && s.OfflineFallback;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Off by configuration, and never inside a test run (dotnet test hosts the backend in "testhost"; a Python start there only costs CPU).
        if (!config.GetValue("ManualTesting:Translation:WarmUpOffline", true)
            || System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name is "testhost" or "ReSharperTestRunner") return;
        try
        {
            await Task.Delay(Delay, stoppingToken);
            if (!Wanted(settings.Effective().Settings)) return;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var status = await engine.StatusAsync(probe: true, stoppingToken);
            log.LogInformation("Offline translator warm-up: {State} after {Seconds:0.0} s. {Detail}", status.State, watch.Elapsed.TotalSeconds, status.Detail);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception e) { log.LogWarning(e, "Offline translator warm-up failed; it starts on the first translation instead."); }
    }
}
