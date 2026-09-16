using System.Text.Json;
using BirkNext.Web.Models;
using Microsoft.JSInterop;

namespace BirkNext.Web.Services;

public interface IFrontendAnalysisSettingsService
{
    FrontendAnalysisSettings    Settings      { get; }
    FrontendAnalysisProfile?    ActiveProfile { get; }
    bool                        IsLoaded      { get; }

    Task LoadAsync(IJSRuntime js);
    Task SaveAsync(IJSRuntime js);

    // Detection snapshots: safe public discovery evidence, persisted separately from Target Environment configuration (a distinct
    // storage key/interop) so that running Detect never modifies or "saves" the saved profile. No credential is ever stored.
    Task LoadDetectionSnapshotsAsync(IJSRuntime js);
    DetectionSnapshot? GetDetectionSnapshot(string profileId);
    Task SaveDetectionSnapshotAsync(IJSRuntime js, string profileId, DetectionSnapshot snapshot);
    Task RemoveDetectionSnapshotAsync(IJSRuntime js, string profileId);

    FrontendAnalysisProfile CreateProfile(string name, FrontendEnvironmentType environmentType);
    void                    DeleteProfile(string profileId);
    FrontendAnalysisProfile DuplicateProfile(string profileId);
    void                    SelectActiveProfile(string profileId);
    void                    UpdateProfile(FrontendAnalysisProfile profile);

    FrontendPerformanceThresholds  GetDefaultThresholds();
    FrontendPerformanceThresholds  GetStrictThresholds();
    CoreWebVitalsThresholds        GetDefaultCoreWebVitals();
    FrontendSecuritySettings       GetDefaultSecuritySettings();
    FrontendAnalysisFeatureToggles GetDefaultFeatureToggles();

    void RestoreDefaultThresholds(string profileId);
    void RestoreStrictThresholds(string profileId);
    void RestoreDefaultCoreWebVitals(string profileId);
    void RestoreDefaultSecurityExpectations(string profileId);
    void RestoreDefaultFeatureToggles(string profileId);
    void ResetProfile(string profileId);

    ProfileValidationResult    ValidateProfile(FrontendAnalysisProfile profile);
    FrontendAnalysisDiagnostics GetDiagnostics();
}

public sealed class FrontendAnalysisSettingsService : IFrontendAnalysisSettingsService
{
    private const string StorageKey = "birknext:frontend-analysis-settings";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private FrontendAnalysisSettings _settings = new();

    public FrontendAnalysisSettings  Settings      => _settings;
    public bool                      IsLoaded      { get; private set; }
    public FrontendAnalysisProfile?  ActiveProfile =>
        _settings.ActiveResolutionError is not null || string.IsNullOrWhiteSpace(_settings.ActiveProfileId)
            ? null
            : _settings.Profiles.Where(p => p.Id == _settings.ActiveProfileId).ToList() is { Count: 1 } matches
                ? matches[0] : null;

    // ── Persistence ───────────────────────────────────────────────────────────

    public async Task LoadAsync(IJSRuntime js)
    {
        if (IsLoaded) return;

        try
        {
            var json = await js.InvokeAsync<string?>("birkNextStorage.getItem", StorageKey);
            if (!string.IsNullOrWhiteSpace(json))
            {
                var loaded = JsonSerializer.Deserialize<FrontendAnalysisSettings>(json, JsonOptions);
                if (loaded is not null)
                {
                    using var document = JsonDocument.Parse(json);
                    if (document.RootElement.TryGetProperty("profiles", out var profiles) &&
                        profiles.EnumerateArray().Count(p => p.EnumerateObject().Any(field =>
                            field.Name.Equals("isActive", StringComparison.OrdinalIgnoreCase) &&
                            field.Value.ValueKind == JsonValueKind.True)) > 1)
                        loaded.ActiveResolutionError = "Multiple active Target Environments were found in saved settings. Select a Target Environment and choose Set as Active.";
                    _settings = loaded;
                    IsLoaded  = true;
                    return;
                }
            }
        }
        catch
        {
            _settings = new() { ActiveResolutionError = "Target Environment settings could not be loaded. Reload the application or repair the saved settings." };
            return;
        }

        _settings = BuildSeedSettings();
        IsLoaded  = true;
    }

    public async Task SaveAsync(IJSRuntime js)
    {
        var json = JsonSerializer.Serialize(_settings, JsonOptions);
        await js.InvokeVoidAsync("birkNextStorage.setItem", StorageKey, json);
    }

    // ── Detection snapshots (separate evidence store, never part of the saved profile) ──────────

    private Dictionary<string, DetectionSnapshot> _detectionSnapshots = new(StringComparer.Ordinal);

    public async Task LoadDetectionSnapshotsAsync(IJSRuntime js)
    {
        string? json;
        try { json = await js.InvokeAsync<string?>("birkNextStorage.getSnapshots"); }
        catch { return; /* interop unavailable or refused: no restored snapshots. */ }
        _detectionSnapshots = ParseSnapshots(json);
    }

    /// <summary>
    /// Parses the persisted snapshot store per entry so one malformed or schema-drifted snapshot (for example one written by an older
    /// build) cannot throw and wipe restoration for every environment. Entries that do not deserialize, carry an unsupported schema
    /// version, or lack a detection result are skipped; the rest are restored. Never throws.
    /// </summary>
    internal static Dictionary<string, DetectionSnapshot> ParseSnapshots(string? json)
    {
        var result = new Dictionary<string, DetectionSnapshot>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json)) return result;
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException) { return result; }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) return result;
            foreach (var entry in document.RootElement.EnumerateObject())
            {
                DetectionSnapshot? snapshot;
                try { snapshot = entry.Value.Deserialize<DetectionSnapshot>(JsonOptions); }
                catch (JsonException) { continue; }   // skip a single unreadable entry, keep the rest
                if (snapshot is { Version: DetectionSnapshot.CurrentVersion, Result: not null })
                    result[entry.Name] = snapshot;
            }
        }
        return result;
    }

    public DetectionSnapshot? GetDetectionSnapshot(string profileId) => _detectionSnapshots.GetValueOrDefault(profileId);

    public async Task SaveDetectionSnapshotAsync(IJSRuntime js, string profileId, DetectionSnapshot snapshot)
    {
        _detectionSnapshots[profileId] = snapshot;
        await PersistSnapshotsAsync(js);
    }

    public async Task RemoveDetectionSnapshotAsync(IJSRuntime js, string profileId)
    {
        if (_detectionSnapshots.Remove(profileId))
            await PersistSnapshotsAsync(js);
    }

    private async Task PersistSnapshotsAsync(IJSRuntime js)
    {
        try
        {
            await js.InvokeVoidAsync("birkNextStorage.setSnapshots", JsonSerializer.Serialize(_detectionSnapshots, JsonOptions));
        }
        catch { }
    }

    // ── Profile management ────────────────────────────────────────────────────

    public FrontendAnalysisProfile CreateProfile(string name, FrontendEnvironmentType environmentType)
    {
        var profile = new FrontendAnalysisProfile
        {
            Id              = Guid.NewGuid().ToString("N"),
            Name            = name,
            EnvironmentType = environmentType,
            Performance     = GetDefaultThresholds(),
            CoreWebVitals   = GetDefaultCoreWebVitals(),
            Security        = GetDefaultSecuritySettings(),
            Features        = GetDefaultFeatureToggles()
        };
        // A brand-new environment starts on the environment-type default method (Local HTTPS proxy for DEV/QA/Test/RC, CDP otherwise).
        // This is an explicit choice on a new profile, not a migration: the persisted-model default stays ManagedEdgeCdp so legacy JSON
        // without the field still deserializes to ManagedEdgeCdp.
        profile.Authentication.AuthenticatedTestingMethod = BirkNext.LocalHttpsProxy.LocalHttpsProxyEnvironmentPolicy.DefaultMethodFor(environmentType.ToString());
        _settings.Profiles.Add(profile);
        return profile;
    }

    public void DeleteProfile(string profileId)
    {
        _settings.Profiles.RemoveAll(p => p.Id == profileId);
        if (_settings.ActiveProfileId == profileId)
            _settings.ActiveProfileId = null;
    }

    public FrontendAnalysisProfile DuplicateProfile(string profileId)
    {
        var source = GetProfileOrThrow(profileId);
        var json   = JsonSerializer.Serialize(source, JsonOptions);
        var copy   = JsonSerializer.Deserialize<FrontendAnalysisProfile>(json, JsonOptions)!;
        copy.Id    = Guid.NewGuid().ToString("N");
        copy.Name  = $"{source.Name} (Copy)";
        _settings.Profiles.Add(copy);
        return copy;
    }

    public void SelectActiveProfile(string profileId)
    {
        if (_settings.Profiles.Count(p => p.Id == profileId) == 1)
        {
            _settings.ActiveProfileId = profileId;
            _settings.ActiveResolutionError = null;
        }
    }

    public void UpdateProfile(FrontendAnalysisProfile profile)
    {
        var idx = _settings.Profiles.FindIndex(p => p.Id == profile.Id);
        if (idx >= 0)
            _settings.Profiles[idx] = profile;
    }

    // ── Presets ───────────────────────────────────────────────────────────────

    public FrontendPerformanceThresholds GetDefaultThresholds() => new()
    {
        Mode                           = FrontendThresholdMode.Default,
        MaxStartupSizeBytes             = 8L  * 1024 * 1024,
        MaxStartupRequests              = 30,
        MaxStartupApiCalls              = 10,
        MaxRestPayloadBytes             = 500L * 1024,
        MaxGraphQlPayloadBytes          = 1024L * 1024,
        MaxAverageApiLatencyMs          = 500,
        MaxSingleRequestLatencyMs       = 1500,
        MaxWasmRuntimeSizeBytes         = 3L  * 1024 * 1024,
        MaxFrameworkSizeBytes           = 5L  * 1024 * 1024,
        MaxApplicationAssemblySizeBytes = 3L  * 1024 * 1024,
        MaxIndividualAssetSizeBytes     = 2L  * 1024 * 1024
    };

    public FrontendPerformanceThresholds GetStrictThresholds() => new()
    {
        Mode                           = FrontendThresholdMode.Strict,
        MaxStartupSizeBytes             = 5L  * 1024 * 1024,
        MaxStartupRequests              = 20,
        MaxStartupApiCalls              = 6,
        MaxRestPayloadBytes             = 250L * 1024,
        MaxGraphQlPayloadBytes          = 500L * 1024,
        MaxAverageApiLatencyMs          = 300,
        MaxSingleRequestLatencyMs       = 1000,
        MaxWasmRuntimeSizeBytes         = 2L  * 1024 * 1024,
        MaxFrameworkSizeBytes           = 3L  * 1024 * 1024,
        MaxApplicationAssemblySizeBytes = 2L  * 1024 * 1024,
        MaxIndividualAssetSizeBytes     = 1L  * 1024 * 1024,
        // BirkNext Performance Quality (strict preset)
        ApiResponseWarningMs            = 300,
        ApiResponsePoorMs               = 800,
        PageStabilizationGoodMs         = 1500,
        PageStabilizationPoorMs         = 4000,
        MaxJsTransferBytes              = 1536L * 1024,
        SlowResourceMs                  = 1500,
        MaxLongTasks                    = 2,
        MainThreadBlockingWarningMs     = 200,
        MaxIdenticalApiCalls            = 1
    };

    public CoreWebVitalsThresholds GetDefaultCoreWebVitals() => new()
    {
        LcpGoodMs = 2500,
        LcpPoorMs = 4000,
        InpGoodMs = 200,
        InpPoorMs = 500,
        ClsGood   = 0.1,
        ClsPoor   = 0.25
    };

    public FrontendSecuritySettings       GetDefaultSecuritySettings() => new();
    public FrontendAnalysisFeatureToggles GetDefaultFeatureToggles()   => new();

    // ── Restore actions ───────────────────────────────────────────────────────

    public void RestoreDefaultThresholds(string profileId)
        => GetProfileOrThrow(profileId).Performance = GetDefaultThresholds();

    public void RestoreStrictThresholds(string profileId)
        => GetProfileOrThrow(profileId).Performance = GetStrictThresholds();

    public void RestoreDefaultCoreWebVitals(string profileId)
        => GetProfileOrThrow(profileId).CoreWebVitals = GetDefaultCoreWebVitals();

    public void RestoreDefaultSecurityExpectations(string profileId)
        => GetProfileOrThrow(profileId).Security = GetDefaultSecuritySettings();

    public void RestoreDefaultFeatureToggles(string profileId)
        => GetProfileOrThrow(profileId).Features = GetDefaultFeatureToggles();

    public void ResetProfile(string profileId)
    {
        var p = GetProfileOrThrow(profileId);
        p.Performance   = GetDefaultThresholds();
        p.CoreWebVitals = GetDefaultCoreWebVitals();
        p.Security      = GetDefaultSecuritySettings();
        p.Features      = GetDefaultFeatureToggles();
        // Restore the environment-type default authenticated testing method. Name, environment type and target URL are left untouched.
        p.Authentication.AuthenticatedTestingMethod = BirkNext.LocalHttpsProxy.LocalHttpsProxyEnvironmentPolicy.DefaultMethodFor(p.EnvironmentType.ToString());
    }

    // ── Validation ────────────────────────────────────────────────────────────

    public ProfileValidationResult ValidateProfile(FrontendAnalysisProfile profile)
    {
        var errors   = new List<string>();
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(profile.Name))
            errors.Add("Profile name is required.");

        var nameDuplicate = _settings.Profiles.Any(p =>
            p.Id != profile.Id &&
            string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase));
        if (nameDuplicate)
            errors.Add($"A profile named \"{profile.Name}\" already exists.");

        if (!string.IsNullOrWhiteSpace(profile.TargetUrl) && !IsValidUrl(profile.TargetUrl))
            errors.Add("Target URL is not a valid absolute URL.");

        if (!string.IsNullOrWhiteSpace(profile.RestBaseUrl) && !IsValidUrl(profile.RestBaseUrl))
            errors.Add("REST Base URL is not a valid absolute URL.");

        if (!string.IsNullOrWhiteSpace(profile.HealthEndpoint) && !IsValidUrl(profile.HealthEndpoint))
            errors.Add("Health Endpoint is not a valid absolute URL.");

        if (!string.IsNullOrWhiteSpace(profile.SwaggerUrl) && !IsValidUrl(profile.SwaggerUrl))
            errors.Add("Swagger / OpenAPI URL is not a valid absolute URL.");

        if (!string.IsNullOrWhiteSpace(profile.GraphQlEndpoint) && !IsValidUrl(profile.GraphQlEndpoint))
            errors.Add("GraphQL Endpoint is not a valid absolute URL.");

        if (profile.RequestTimeoutSeconds <= 0)
            errors.Add("Request timeout must be a positive value.");

        if (profile.RetryCount < 0)
            errors.Add("Retry count must be zero or greater.");

        if (!string.IsNullOrWhiteSpace(profile.Authentication.ExpectedAuthority) &&
            !IsValidUrl(profile.Authentication.ExpectedAuthority))
            errors.Add("Expected Authority is not a valid absolute URL.");

        foreach (var url in profile.Authentication.AllowedRedirectUrls)
        {
            if (!string.IsNullOrWhiteSpace(url) && !IsValidUrl(url))
                errors.Add($"Redirect URL \"{url}\" is not a valid absolute URL.");
        }

        // Approved MCAS proxy trust correlates an HTTPS *.access.mcas.ms delivery to the target host; a non-HTTPS target is a configuration smell, not an error.
        if (profile.Authentication.BrowserDeliveryTrust == BirkNext.ManagedEdge.ManagedEdgeTrustModel.ApprovedMcasProxyOrigin &&
            !string.IsNullOrWhiteSpace(profile.TargetUrl) &&
            profile.TargetUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            warnings.Add("Browser delivery trust permits an approved Microsoft Defender for Cloud Apps proxy, but the Frontend URL is not HTTPS. Exact-origin delivery is still preferred.");

        // The Local HTTPS proxy intercepts authenticated traffic and is a DEV/non-production testing method only. Production (and Custom) fail closed.
        if (profile.Authentication.AuthenticatedTestingMethod == BirkNext.LocalHttpsProxy.AuthenticatedTestingMethod.LocalHttpsProxy)
        {
            if (!BirkNext.LocalHttpsProxy.LocalHttpsProxyEnvironmentPolicy.IsAllowed(profile.EnvironmentType.ToString()))
                errors.Add("Local HTTPS proxy is available only for Local, Development, QA, Test and RC environments. Choose another authenticated testing method for this environment type.");
            if (!string.IsNullOrWhiteSpace(profile.TargetUrl) && !profile.TargetUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                errors.Add("Local HTTPS proxy requires an HTTPS Frontend URL.");
        }

        var perf = profile.Performance;
        if (perf.MaxAverageApiLatencyMs          <= 0) errors.Add("Maximum Average API Latency must be a positive value.");
        if (perf.MaxSingleRequestLatencyMs       <= 0) errors.Add("Maximum Single Request Latency must be a positive value.");
        if (perf.MaxRestPayloadBytes             <= 0) errors.Add("Maximum REST Payload must be a positive value.");
        if (perf.MaxGraphQlPayloadBytes          <= 0) errors.Add("Maximum GraphQL Payload must be a positive value.");
        if (perf.MaxStartupSizeBytes             <= 0) errors.Add("Maximum Startup Size must be a positive value.");
        if (perf.MaxStartupRequests              <= 0) errors.Add("Maximum Startup Requests must be a positive value.");
        if (perf.MaxStartupApiCalls              <= 0) errors.Add("Maximum Startup API Calls must be a positive value.");
        if (perf.MaxWasmRuntimeSizeBytes         <= 0) errors.Add("Maximum WASM Runtime Size must be a positive value.");
        if (perf.MaxFrameworkSizeBytes           <= 0) errors.Add("Maximum Framework Size must be a positive value.");
        if (perf.MaxApplicationAssemblySizeBytes <= 0) errors.Add("Maximum Application Assembly Size must be a positive value.");
        if (perf.MaxIndividualAssetSizeBytes     <= 0) errors.Add("Maximum Individual Asset Size must be a positive value.");

        if (profile.EnvironmentType == FrontendEnvironmentType.Production &&
            !string.IsNullOrWhiteSpace(profile.TargetUrl) &&
            (profile.TargetUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
             profile.TargetUrl.Contains("127.0.0.1")))
        {
            warnings.Add("Production profile is targeting a localhost URL.");
        }

        if (profile.EnvironmentType != FrontendEnvironmentType.Local &&
            !string.IsNullOrWhiteSpace(profile.TargetUrl) &&
            profile.TargetUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add("HTTPS is recommended for non-Local environments.");
        }

        var inferredType = TargetEnvironmentTypeClassifier.InferFromUrl(profile.TargetUrl);
        if (inferredType.HasValue &&
            profile.EnvironmentType != FrontendEnvironmentType.Custom &&
            inferredType.Value != profile.EnvironmentType)
        {
            warnings.Add($"This target is marked {profile.EnvironmentType}, but the hostname looks like a {inferredType.Value} environment. Use Detect settings to review the suggested type; no changes will be saved automatically.");
        }
        else if (profile.EnvironmentType == FrontendEnvironmentType.Local &&
                 !string.IsNullOrWhiteSpace(profile.TargetUrl) &&
                 !TargetEnvironmentTypeClassifier.IsRecognizedLocalUrl(profile.TargetUrl))
        {
            warnings.Add("This target is marked Local, but its hostname is not a recognized local or loopback address. Use Detect settings to review its environment type; no changes will be saved automatically.");
        }

        return new ProfileValidationResult { Errors = errors, Warnings = warnings };
    }

    // ── Diagnostics ───────────────────────────────────────────────────────────

    public FrontendAnalysisDiagnostics GetDiagnostics()
    {
        var active = ActiveProfile;
        if (active is null)
            return new FrontendAnalysisDiagnostics { ActiveProfileName = "(None)" };

        var f       = active.Features;
        var enabled  = new List<string>();
        var disabled = new List<string>();

        void Classify(bool on, string name) { if (on) enabled.Add(name); else disabled.Add(name); }
        Classify(f.AssetDiscovery,              "Asset Discovery");
        Classify(f.StartupAnalysis,             "Startup Analysis");
        Classify(f.RestAnalysis,                "REST Analysis");
        Classify(f.GraphQlAnalysis,             "GraphQL Analysis");
        Classify(f.CachingReview,               "Caching Review");
        Classify(f.CompressionReview,           "Compression Review");
        Classify(f.BlazorArchitectureReview,    "Blazor Architecture Review");
        Classify(f.SecurityHeaderReview,        "Security Header Review");
        Classify(f.ConfigurationExposureReview, "Configuration Exposure Review");
        Classify(f.PerformanceReadiness,        "Performance Readiness");
        Classify(f.AuthenticatedBrowserReview,  "Authenticated Browser Review");
        Classify(f.LighthouseIntegration,       "Lighthouse Integration");
        Classify(f.PlaywrightRuntimeInspection, "Playwright Runtime Inspection");

        return new FrontendAnalysisDiagnostics
        {
            ActiveProfileName  = active.Name,
            Environment        = active.EnvironmentType.ToString(),
            TargetUrl          = active.TargetUrl,
            AuthenticationType = active.Authentication.AuthenticationType.ToString(),
            ThresholdMode      = active.Performance.Mode.ToString(),
            EnabledFeatures    = enabled,
            DisabledFeatures   = disabled,
            ValidationStatus   = ValidateProfile(active)
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private FrontendAnalysisProfile GetProfileOrThrow(string profileId) =>
        _settings.Profiles.FirstOrDefault(p => p.Id == profileId)
            ?? throw new InvalidOperationException($"Profile '{profileId}' not found.");

    public static bool IsValidUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static FrontendAnalysisSettings BuildSeedSettings()
    {
        var localId = Guid.NewGuid().ToString("N");
        var devId   = Guid.NewGuid().ToString("N");
        var qaId    = Guid.NewGuid().ToString("N");
        var prodId  = Guid.NewGuid().ToString("N");

        return new FrontendAnalysisSettings
        {
            ActiveProfileId = null,
            Profiles =
            [
                MakeProfile(localId, "Local",       FrontendEnvironmentType.Local,       "Local development environment", "https://localhost:5001"),
                MakeProfile(devId,   "Development", FrontendEnvironmentType.Development, "Development environment",       "https://example-dev.local"),
                MakeProfile(qaId,    "QA",          FrontendEnvironmentType.QA,          "QA environment",                "https://example-qa.local"),
                MakeProfile(prodId,  "Production",  FrontendEnvironmentType.Production,  "Production environment",        "https://example.local")
            ]
        };
    }

    private static FrontendAnalysisProfile MakeProfile(
        string id, string name, FrontendEnvironmentType env, string description, string targetUrl) =>
        new()
        {
            Id              = id,
            Name            = name,
            EnvironmentType = env,
            Description     = description,
            TargetUrl       = targetUrl,
            Performance     = new FrontendPerformanceThresholds(),
            CoreWebVitals   = new CoreWebVitalsThresholds(),
            Security        = new FrontendSecuritySettings(),
            Features        = new FrontendAnalysisFeatureToggles(),
            Authentication  = new FrontendAuthenticationSettings { AuthenticatedTestingMethod = BirkNext.LocalHttpsProxy.LocalHttpsProxyEnvironmentPolicy.DefaultMethodFor(env.ToString()) }
        };
}
