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

    /// <summary>Saves the Target Environments. Another browser tab may have saved since this one loaded: changes to different environments are
    /// merged; the same environment changed in both tabs throws <see cref="TargetEnvironmentSaveConflictException"/> and nothing is written.</summary>
    Task SaveAsync(IJSRuntime js);

    /// <summary>Discards this tab's unsaved Target Environment changes and reads what is stored (after a save conflict).</summary>
    Task ReloadAsync(IJSRuntime js) => Task.CompletedTask;

    /// <summary>False while the settings are the built-in seed profiles held only in memory (nothing stored yet), so their ids would be
    /// minted again on the next load.</summary>
    bool IsMaterialized => true;

    /// <summary>Stores the canonical built-in defaults, exactly as seeded, when <paramref name="environmentId"/> is one of them and nothing
    /// is stored yet, so their ids survive a reload. Called before an operation that attaches backend-owned state to that environment. Any
    /// other in-memory change (an unsaved profile, an unsaved edit) is not stored. A no-op once stored or for any other id.</summary>
    Task MaterializeAsync(IJSRuntime js, string environmentId) => Task.CompletedTask;

    /// <summary>Raised after the Target Environments were persisted (created, edited, deleted, active one switched, reset), so readers
    /// such as project applicability and Recommended Workflow follow without polling or navigation.</summary>
    event Action? Changed;

    // Detection snapshots: safe public discovery evidence, persisted separately from Target Environment configuration (a distinct
    // storage key/interop) so that running Detect never modifies or "saves" the saved profile. No credential is ever stored.
    Task LoadDetectionSnapshotsAsync(IJSRuntime js);
    DetectionSnapshot? GetDetectionSnapshot(string profileId);
    Task SaveDetectionSnapshotAsync(IJSRuntime js, string profileId, DetectionSnapshot snapshot);
    Task RemoveDetectionSnapshotAsync(IJSRuntime js, string profileId);
    /// <summary>Local data reset: replaces every Target Environment with the generic seed profiles (no project URLs, integrations,
    /// thresholds, authentication or expectations) and forgets detection snapshots.</summary>
    Task ResetForLocalDataResetAsync(IJSRuntime js) => Task.CompletedTask;
    /// <summary>"Reset project data, keep settings": every Target Environment keeps its settings; only its link to the deleted project goes.</summary>
    Task ClearProjectBindingsForLocalDataResetAsync(IJSRuntime js) => Task.CompletedTask;

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

    // What this tab last read from or wrote to storage: the common base for merging when another tab saved in between (multi-tab safety).
    private string? _baseJson;
    // Every stored content this tab itself read or wrote: finding one of these in storage means no other tab contributed anything new.
    private readonly HashSet<string> _ownContent = new(StringComparer.Ordinal);

    public FrontendAnalysisSettings  Settings      => _settings;
    public bool                      IsLoaded      { get; private set; }
    public bool                      IsMaterialized { get; private set; }

    // The built-in defaults as seeded on this load, kept until stored: what MaterializeAsync stores and which ids it accepts.
    private string? _seedJson;
    private HashSet<string> _seedProfileIds = new(StringComparer.Ordinal);
    public FrontendAnalysisProfile?  ActiveProfile =>
        _settings.ActiveResolutionError is not null || string.IsNullOrWhiteSpace(_settings.ActiveProfileId)
            ? null
            : _settings.Profiles.Where(p => p.Id == _settings.ActiveProfileId).ToList() is { Count: 1 } matches
                ? matches[0] : null;

    // ── Persistence ───────────────────────────────────────────────────────────

    public async Task LoadAsync(IJSRuntime js)
    {
        if (IsLoaded) return;
        // Observed authentication is part of the effective settings every review reads, so the snapshots load with the settings.
        if (!_snapshotsLoaded) await LoadDetectionSnapshotsAsync(js);

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
                    _baseJson = json;
                    _ownContent.Add(json);
                    MigrateLegacyAuthenticationExpectations(loaded);
                    IsLoaded  = true;
                    IsMaterialized = true;
                    return;
                }
            }
        }
        catch
        {
            _settings = new() { ActiveResolutionError = "Target Environment settings could not be loaded. Reload the application or repair the saved settings." };
            return;
        }

        // Seed profiles get fresh random ids and are not stored until saved or until backend configuration is attached to one of them
        // (see MaterializeAsync); ids are never fixed or shared, because backend records are keyed by environment id alone.
        _settings = BuildSeedSettings();
        _seedJson = JsonSerializer.Serialize(_settings, JsonOptions);
        _seedProfileIds = new(_settings.Profiles.Select(profile => profile.Id), StringComparer.Ordinal);
        IsLoaded  = true;
    }

    /// <summary>Move legacy auth expectations into Authentication without overriding values users already configured there.</summary>
    internal static void MigrateLegacyAuthenticationExpectations(FrontendAnalysisSettings settings)
    {
        foreach (var profile in settings.Profiles)
        {
            var authentication = profile.Authentication;
            var legacy = profile.Security;
            authentication.ExpectedAuthority ??= legacy.ExpectedAuthority;
            authentication.ExpectedTenant ??= legacy.ExpectedTenant;
            authentication.ExpectedClientId ??= legacy.ExpectedClientId;
            if (authentication.AllowedRedirectUrls.Count == 0 && legacy.AllowedRedirectUrls.Count > 0)
                authentication.AllowedRedirectUrls = [.. legacy.AllowedRedirectUrls];
        }
    }

    public event Action? Changed;

    public Task SaveAsync(IJSRuntime js) => SaveCoreAsync(js, merge: true);

    private async Task SaveCoreAsync(IJSRuntime js, bool merge)
    {
        // Another tab (same browser storage) may have saved since this tab read the settings: merge per environment instead of overwriting.
        string? stored = null;
        if (merge) try { stored = await js.InvokeAsync<string?>("birkNextStorage.getItem", StorageKey); }
        catch (JSException) { stored = null; }
        if (merge && !string.IsNullOrWhiteSpace(stored) && stored != _baseJson && !_ownContent.Contains(stored))
        {
            var theirs = JsonSerializer.Deserialize<FrontendAnalysisSettings>(stored, JsonOptions);
            if (theirs is not null)
            {
                var @base = (_baseJson ?? _seedJson) is { Length: > 0 } baseJson ? JsonSerializer.Deserialize<FrontendAnalysisSettings>(baseJson, JsonOptions) : null;
                _settings = Merge(@base ?? new FrontendAnalysisSettings(), theirs, _settings); // throws on a same-environment conflict; nothing written
            }
        }
        var json = JsonSerializer.Serialize(_settings, JsonOptions);
        await js.InvokeVoidAsync("birkNextStorage.setItem", StorageKey, json);
        _baseJson = json;
        _ownContent.Add(json);
        MarkMaterialized();
        Changed?.Invoke();
    }

    public async Task ReloadAsync(IJSRuntime js)
    {
        IsLoaded = false;
        await LoadAsync(js);
        Changed?.Invoke();
    }

    /// <summary>
    /// Three-way merge of Target Environment settings saved in another tab (<paramref name="theirs"/>) with this tab's (<paramref name="mine"/>),
    /// both from <paramref name="base"/>: per environment (by id), the side that changed it wins; added and deleted environments are kept as
    /// changed; the same environment changed differently on both sides is a conflict. The active environment and package catalogs follow the
    /// side that changed them.
    /// </summary>
    internal static FrontendAnalysisSettings Merge(FrontendAnalysisSettings @base, FrontendAnalysisSettings theirs, FrontendAnalysisSettings mine)
    {
        string Json(object? value) => JsonSerializer.Serialize(value, JsonOptions);
        Dictionary<string, FrontendAnalysisProfile> ById(FrontendAnalysisSettings s) => s.Profiles.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        var (b, t, m) = (ById(@base), ById(theirs), ById(mine));
        var conflicts = new List<string>();
        FrontendAnalysisProfile? Pick(string id)
        {
            b.TryGetValue(id, out var bp); t.TryGetValue(id, out var tp); m.TryGetValue(id, out var mp);
            var (bj, tj, mj) = (Json(bp), Json(tp), Json(mp));
            if (mj == bj) return tp;          // unchanged here: take the other tab's version (or its deletion)
            if (tj == bj || tj == mj) return mp; // unchanged there, or the same change: take this tab's
            conflicts.Add(mp?.Name ?? tp?.Name ?? bp?.Name ?? id);
            return mp;
        }
        var order = theirs.Profiles.Select(p => p.Id).Concat(mine.Profiles.Select(p => p.Id)).Concat(@base.Profiles.Select(p => p.Id)).Distinct(StringComparer.Ordinal).ToList();
        var profiles = order.Select(Pick).Where(p => p is not null).Select(p => p!).ToList();
        if (Json(mine.PackageCatalogs) != Json(@base.PackageCatalogs) && Json(theirs.PackageCatalogs) != Json(@base.PackageCatalogs)
            && Json(theirs.PackageCatalogs) != Json(mine.PackageCatalogs)) conflicts.Add("Project package catalogs");
        if (conflicts.Count > 0) throw new TargetEnvironmentSaveConflictException(conflicts);
        return new FrontendAnalysisSettings
        {
            Profiles = profiles,
            ActiveProfileId = mine.ActiveProfileId != @base.ActiveProfileId ? mine.ActiveProfileId : theirs.ActiveProfileId,
            ActiveResolutionError = mine.ActiveResolutionError != @base.ActiveResolutionError ? mine.ActiveResolutionError : theirs.ActiveResolutionError,
            PackageCatalogs = Json(mine.PackageCatalogs) != Json(@base.PackageCatalogs) ? mine.PackageCatalogs : theirs.PackageCatalogs,
        };
    }

    public async Task MaterializeAsync(IJSRuntime js, string environmentId)
    {
        if (IsMaterialized || _seedJson is null || !_seedProfileIds.Contains(environmentId)) return;
        await js.InvokeVoidAsync("birkNextStorage.setItem", StorageKey, _seedJson);
        _baseJson = _seedJson;
        _ownContent.Add(_seedJson);
        MarkMaterialized();
        Changed?.Invoke();
    }

    private void MarkMaterialized()
    {
        IsMaterialized = true;
        _seedJson = null;
        _seedProfileIds = new(StringComparer.Ordinal);
    }

    // ── Detection snapshots (separate evidence store, never part of the saved profile) ──────────

    private Dictionary<string, DetectionSnapshot> _detectionSnapshots = new(StringComparer.Ordinal);

    public async Task ResetForLocalDataResetAsync(IJSRuntime js)
    {
        // The Development environment's own configured frontend URL survives the reset (it is configuration of this installation, e.g.
        // https://m2lbdev.bufetat.no — never a built-in default); everything else returns to the generic seed.
        _settings = BuildSeedSettings(ConfiguredDevelopmentUrl(_settings));
        IsLoaded = true;
        _detectionSnapshots = new(StringComparer.Ordinal);
        await SaveCoreAsync(js, merge: false); // a reset replaces whatever any tab stored; it is never merged
    }

    public async Task ClearProjectBindingsForLocalDataResetAsync(IJSRuntime js)
    {
        if (!IsLoaded) await LoadAsync(js);
        foreach (var profile in _settings.Profiles)
        {
            profile.Project = null;          // the imported project and its evidence bindings were deleted
            profile.ArchivedProjects = null;
        }
        _detectionSnapshots = new(StringComparer.Ordinal);
        await SaveCoreAsync(js, merge: false); // a reset replaces whatever any tab stored; it is never merged
    }

    private bool _snapshotsLoaded;

    public async Task LoadDetectionSnapshotsAsync(IJSRuntime js)
    {
        _snapshotsLoaded = true;
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
            Features        = GetDefaultFeatureToggles(),
            EngineRequirements = FrontendQualityEngineRequirementSettings.Recommended()
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
        {
            var old = _settings.Profiles[idx].Security;
            foreach (var field in Enum.GetValues<BirkNext.SecurityExpectations.SecurityExpectationField>())
            {
                var before = BirkNext.SecurityExpectations.SecurityExpectationValues.Values(old, field).ToList();
                foreach (var value in BirkNext.SecurityExpectations.SecurityExpectationValues.Values(profile.Security, field).Where(v => !before.Contains(v)))
                {
                    var normalized = BirkNext.SecurityExpectations.SecurityExpectationValues.Normalize(field, value);
                    if (normalized is null) continue;
                    profile.Security.Origins.RemoveAll(p => p.FieldType == field && p.NormalizedValue == normalized);
                    profile.Security.Origins.Add(new(field, normalized, BirkNext.SecurityExpectations.SecurityExpectationOrigin.Manual));
                }
            }
            _settings.Profiles[idx] = profile;
        }
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
        CompressionMinPayloadBytes      = 1024,
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
        CompressionMinPayloadBytes      = 1024,
        MaxWasmRuntimeSizeBytes         = 2L  * 1024 * 1024,
        MaxFrameworkSizeBytes           = 3L  * 1024 * 1024,
        MaxApplicationAssemblySizeBytes = 2L  * 1024 * 1024,
        MaxIndividualAssetSizeBytes     = 1L  * 1024 * 1024,
        // Browser Performance Quality (strict preset)
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

    public FrontendSecuritySettings GetDefaultSecuritySettings() => new() {
        Origins = BirkNext.SecurityExpectations.ApprovedSecurityExpectations.DefaultHeaders.Select(h =>
            new BirkNext.SecurityExpectations.SecurityExpectationProvenance(BirkNext.SecurityExpectations.SecurityExpectationField.SecurityHeader, h,
                BirkNext.SecurityExpectations.SecurityExpectationOrigin.Default)).ToList()
    };
    /// <summary>Recommended engine selection for a new Target Environment. Existing profiles keep their saved choices.</summary>
    public FrontendAnalysisFeatureToggles GetDefaultFeatureToggles()   => FrontendAnalysisFeatureToggles.Recommended();

    // ── Restore actions ───────────────────────────────────────────────────────

    public void RestoreDefaultThresholds(string profileId)
        => GetProfileOrThrow(profileId).Performance = GetDefaultThresholds();

    public void RestoreStrictThresholds(string profileId)
        => GetProfileOrThrow(profileId).Performance = GetStrictThresholds();

    public void RestoreDefaultCoreWebVitals(string profileId)
        => GetProfileOrThrow(profileId).CoreWebVitals = GetDefaultCoreWebVitals();

    public void RestoreDefaultSecurityExpectations(string profileId)
        => GetProfileOrThrow(profileId).Security = GetDefaultSecuritySettings();

    /// <summary>Restore recommended defaults: the recommended engine selection AND coverage policy for this one Target Environment.</summary>
    public void RestoreDefaultFeatureToggles(string profileId)
    {
        var profile = GetProfileOrThrow(profileId);
        profile.Features = GetDefaultFeatureToggles();
        profile.EngineRequirements = FrontendQualityEngineRequirementSettings.Recommended();
    }

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
        if (perf.CompressionMinPayloadBytes      <  0) errors.Add("Compression Minimum Payload cannot be negative.");
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
            warnings.Add($"This target is marked {profile.EnvironmentType}, but the hostname looks like a {inferredType.Value} environment. Check the target application on the Target Application tab to review the suggested type; no changes will be saved automatically.");
        }
        else if (profile.EnvironmentType == FrontendEnvironmentType.Local &&
                 !string.IsNullOrWhiteSpace(profile.TargetUrl) &&
                 !TargetEnvironmentTypeClassifier.IsRecognizedLocalUrl(profile.TargetUrl))
        {
            warnings.Add("This target is marked Local, but its hostname is not a recognized local or loopback address. Check the target application on the Target Application tab to review its environment type; no changes will be saved automatically.");
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

        // The Frontend Quality Review engines are the only feature activation a Target Environment carries. The
        // thirteen legacy flags this used to classify had no consumer and were removed.
        void Classify(bool on, string name) { if (on) enabled.Add(name); else disabled.Add(name); }
        foreach (var id in Enum.GetValues<FrontendQualityEngineId>())
            Classify(FrontendQualityActiveEngines.IsEnabled(id, f), FrontendQualityActiveEngines.DisplayName(id));

        return new FrontendAnalysisDiagnostics
        {
            ActiveProfileName  = active.Name,
            Environment        = active.EnvironmentType.ToString(),
            TargetUrl          = active.TargetUrl,
            AuthenticationType = EffectiveAuthentication.Resolve(active, GetDetectionSnapshot(active.Id)).Provider.ToString(),
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

    /// <summary>Seed placeholder URLs ("https://example-dev.local", "https://localhost:5001"): not a configured target.</summary>
    internal static bool IsSeedPlaceholder(string? url) =>
        string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Host.StartsWith("example", StringComparison.OrdinalIgnoreCase) && uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase);

    /// <summary>The frontend URL configured on the existing Development environment, when it is a real target (not a seed placeholder).</summary>
    internal static string? ConfiguredDevelopmentUrl(FrontendAnalysisSettings? settings) =>
        settings?.Profiles.Where(p => p.EnvironmentType == FrontendEnvironmentType.Development).Select(p => p.TargetUrl)
            .FirstOrDefault(url => !IsSeedPlaceholder(url));

    /// <summary>
    /// The generic seed: Local, Development, QA and Production. Development is the default — active, and therefore selected — on a fresh
    /// installation and after a complete data reset; Local is available but never chosen automatically. A saved selection is never
    /// overridden: the seed is used only when nothing is saved, or by the reset.
    /// </summary>
    private static FrontendAnalysisSettings BuildSeedSettings(string? developmentUrl = null)
    {
        var localId = Guid.NewGuid().ToString("N");
        var devId   = Guid.NewGuid().ToString("N");
        var qaId    = Guid.NewGuid().ToString("N");
        var prodId  = Guid.NewGuid().ToString("N");

        return new FrontendAnalysisSettings
        {
            ActiveProfileId = devId,
            Profiles =
            [
                MakeProfile(localId, "Local",       FrontendEnvironmentType.Local,       "Local development environment", "https://localhost:5001"),
                MakeProfile(devId,   "Development", FrontendEnvironmentType.Development, "Development environment",       developmentUrl ?? "https://example-dev.local"),
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
            Features        = FrontendAnalysisFeatureToggles.Recommended(),
            Authentication  = new FrontendAuthenticationSettings { AuthenticatedTestingMethod = BirkNext.LocalHttpsProxy.LocalHttpsProxyEnvironmentPolicy.DefaultMethodFor(env.ToString()) }
        };
}

/// <summary>The same Target Environment was changed in this tab and saved in another tab meanwhile; nothing was written. Reload to see the other
/// tab's version, then apply this tab's change again.</summary>
public sealed class TargetEnvironmentSaveConflictException(IReadOnlyList<string> environments) : InvalidOperationException(
    $"{string.Join(", ", environments)} {(environments.Count == 1 ? "was" : "were")} changed in another browser tab after this page loaded. Your change was not saved, so the other change is not lost. Reload the settings and apply your change again.")
{
    public IReadOnlyList<string> Environments { get; } = environments;
}
