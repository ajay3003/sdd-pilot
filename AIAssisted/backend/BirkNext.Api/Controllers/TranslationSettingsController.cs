using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.LocalSecurity;
using BirkNext.Api.Services.TestPlanning;
using BirkNext.Api.Services.TestPlanning.OfflineTranslation;
using BirkNext.TestPlanning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// System Settings → Translation: the translator for test-case text (default: the embedded offline translator packaged with BirkNext, English →
/// Norwegian bokmål) and, under Advanced, optional online services and a LibreTranslate server. Reading is open; changing settings and starting
/// or stopping the local LibreTranslate server need the system-configuration permission. The Azure key is write-only.
/// </summary>
[ApiController]
[Route("api/translation-settings")]
public sealed class TranslationSettingsController(TranslationSettingsStore store, TestCaseTranslatorResolver resolver, LibreTranslateServiceManager libre,
    IConfiguration config, IAuthorizationService authorization, LocalTrustedExecution local, OfflineTranslationEngine offline, TimeProvider? clock = null) : ControllerBase
{
    private string Actor => SystemConfigurationPermissions.Actor(User,
        local.EvaluateFor(SystemConfigurationPermissions.LocalSetting, SystemConfigurationPermissions.LocalCapability, HttpContext).Allowed) ?? "not attributed";
    private DateTimeOffset Now => (clock ?? TimeProvider.System).GetUtcNow();

    [HttpGet]
    public async Task<ActionResult<TranslationSettingsView>> Get()
    {
        var (settings, origin, by, at) = store.Effective();
        var offlineStatus = await offline.StatusAsync(probe: false, HttpContext?.RequestAborted ?? default);
        return new TranslationSettingsView
        {
            Settings = settings, Origin = origin, UpdatedBy = by, UpdatedAt = at, AzureKeySet = store.AzureKeySet,
            AnthropicKeyConfigured = !string.IsNullOrWhiteSpace(config["Anthropic:ApiKey"]), Availability = resolver.Current().Availability,
            CanManageLibreTranslate = libre.CanManage, Offline = offlineStatus,
            CanWrite = (await authorization.AuthorizeAsync(User, null, SystemConfigurationPermissions.Policy)).Succeeded,
        };
    }

    [HttpPut]
    [Authorize(Policy = SystemConfigurationPermissions.Policy)]
    public async Task<ActionResult<TranslationSettingsView>> Save([FromBody] SaveTranslationSettingsRequest request)
    {
        if (store.Save(request, Actor, Now) is { } problem) return BadRequest(new { message = problem });
        // Saving LibreTranslate (managed) starts the local server when it is not running yet (in the background; its status shows it).
        var saved = store.Effective().Settings;
        if (saved.Provider == TranslationProviderKind.LibreTranslate && saved.LibreTranslateManaged && libre.CanManage)
            _ = Task.Run(async () => { try { await libre.EnsureRunningAsync(saved, CancellationToken.None); } catch (Exception) { } });
        // The new "keep loaded" choice applies to a running offline translator at once (an earlier scheduled stop is replaced), and choosing the
        // offline translator loads it now in the background, so the first translation after switching does not wait for the model.
        offline.SettingsChanged();
        if (OfflineTranslationWarmup.Wanted(saved))
            _ = Task.Run(async () => { try { await offline.StatusAsync(probe: true, CancellationToken.None); } catch (Exception) { } });
        return await Get();
    }

    /// <summary>The embedded offline translator. <paramref name="probe"/>: start it if needed and translate the probe sentence ("Ready" only then);
    /// <paramref name="recheck"/>: verify the package files again first (after the package was repaired). Nothing is downloaded.</summary>
    [HttpGet("offline")]
    public async Task<OfflineTranslationStatus> Offline([FromQuery] bool probe = false, [FromQuery] bool recheck = false, CancellationToken ct = default)
    {
        if (recheck) offline.Forget();
        return await offline.StatusAsync(probe, ct);
    }

    [HttpGet("libretranslate")]
    public Task<LibreTranslateServiceStatus> LibreTranslate(CancellationToken ct) => libre.StatusAsync(store.Effective().Settings, ct);

    [HttpPost("libretranslate/pull")]
    [Authorize(Policy = SystemConfigurationPermissions.Policy)]
    public async Task<ActionResult> Pull() => Ok(new { message = await libre.PullAsync(store.Effective().Settings) });

    [HttpPost("libretranslate/start")]
    [Authorize(Policy = SystemConfigurationPermissions.Policy)]
    public async Task<ActionResult> Start() => Ok(new { message = await libre.StartAsync(store.Effective().Settings) });

    [HttpPost("libretranslate/stop")]
    [Authorize(Policy = SystemConfigurationPermissions.Policy)]
    public async Task<ActionResult> Stop() => Ok(new { message = await libre.StopAsync() });

    public const string Sample = "Prepare a child past compulsory school age, lock the period and check that the child is not expected (FR-027).";

    /// <summary>Translates one fixed sample sentence with the current settings and says which service answered (a person's explicit action).</summary>
    [HttpPost("test")]
    [Authorize(Policy = SystemConfigurationPermissions.Policy)]
    public async Task<TranslationTestResult> Test(CancellationToken ct)
    {
        var translator = resolver.Current();
        var settings = resolver.Settings();
        if (!translator.Availability.Available) return new(false, null, Sample, null, translator.Availability.Reason);
        var (masked, tokens) = ScenarioPlanning.Protect(Sample);
        try
        {
            var result = await translator.TranslateAsync([masked], settings.SourceLanguage, settings.DefaultTarget, ct);
            var restored = result.Texts[0] is { } t ? ScenarioPlanning.Restore(t, tokens) : null;
            return restored is null
                ? new(false, result.Provider, Sample, null, "The service changed or lost an identifier (FR-027), so its translation would not be used.")
                : new(true, result.Provider, Sample, restored, $"Translated by {result.Provider} ({TranslationLanguages.Name(settings.SourceLanguage)} → {TranslationLanguages.Name(settings.DefaultTarget)}).");
        }
        catch (TestCaseTranslationException e) { return new(false, null, Sample, null, e.Message); }
    }
}
