using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using BirkNext.Api.Services.ContainerRuntime;
using BirkNext.TestPlanning;
using Microsoft.AspNetCore.DataProtection;

namespace BirkNext.Api.Services.TestPlanning;

/// <summary>
/// The installation's translation settings (System Settings → Translation): one JSON file under App_Data, and the Azure AI Translator key
/// encrypted with ASP.NET Core Data Protection (a key ring for this purpose only; DPAPI-protected on Windows) — never in appsettings, the
/// database or a response. Until System Settings saves its own, the server configuration (ManualTesting:Translation:*) applies, else the default
/// (the embedded offline translator, English → Norwegian bokmål; no internet fallback). Saved settings keep their own choice.
/// </summary>
public sealed partial class TranslationSettingsStore
{
    public const string ProtectionPurpose = "BirkNext.Translation.AzureKey.v1";
    public const int MaxTargets = 6;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    private readonly string _settingsPath;
    private readonly string _keyPath;
    private readonly IDataProtector _protector;
    private readonly IConfiguration _config;
    private readonly object _gate = new();

    private sealed record Saved(TranslationSettings Settings, string UpdatedBy, DateTimeOffset UpdatedAt);

    /// <summary>
    /// False where BirkNext cannot run the LibreTranslate server itself (the Windows tester package: no Podman). There the default, and a saved
    /// "LibreTranslate run by BirkNext" that can never start, mean the offline translator packaged with BirkNext — instead of a translator that
    /// always reads "not running" and falls back anyway (2026-11-23, AVD tester). A LibreTranslate server at an address someone gave is kept.
    /// </summary>
    private readonly bool _canManageLibreTranslate = true;

    public TranslationSettingsStore(string directory, IConfiguration config, bool canManageLibreTranslate) : this(directory, config) =>
        _canManageLibreTranslate = canManageLibreTranslate;

    public TranslationSettingsStore(string directory, IConfiguration config)
    {
        Directory.CreateDirectory(directory);
        _config = config;
        _settingsPath = System.IO.Path.Combine(directory, "translation-settings.json");
        _keyPath = System.IO.Path.Combine(directory, "azure-translator-key.protected");
        var provider = DataProtectionProvider.Create(new DirectoryInfo(System.IO.Path.Combine(directory, "keys")), builder =>
        {
            builder.SetApplicationName("BirkNext.Translation");
            if (OperatingSystem.IsWindows()) builder.ProtectKeysWithDpapi();
        });
        _protector = provider.CreateProtector(ProtectionPurpose);
    }

    public static string DefaultDirectory(IHostEnvironment environment) => System.IO.Path.Combine(AppDataLocation.Root(environment), "translation");

    /// <summary>The settings in force and where they come from ("saved", "configuration" or "default").</summary>
    public (TranslationSettings Settings, string Origin, string? UpdatedBy, DateTimeOffset? UpdatedAt) Effective()
    {
        if (Load() is { } saved) return (Runnable(saved.Settings), "saved", saved.UpdatedBy, saved.UpdatedAt);
        if (TestCaseTranslators.FromConfiguration(_config) is { } configured) return (Runnable(configured), "configuration", null, null);
        return (Runnable(new TranslationSettings()), "default", null, null);
    }

    /// <summary>A LibreTranslate server BirkNext would have to run itself, where it cannot, becomes the packaged offline translator.</summary>
    private TranslationSettings Runnable(TranslationSettings settings) =>
        !_canManageLibreTranslate && settings.Provider == TranslationProviderKind.LibreTranslate && settings.LibreTranslateManaged
            ? settings with { Provider = TranslationProviderKind.EmbeddedOffline }
            : settings;

    /// <summary>The Azure key: the one saved here (decrypted in memory only), else the server configuration's; null when none.</summary>
    internal string? AzureKey()
    {
        lock (_gate)
        {
            if (File.Exists(_keyPath))
                try { return _protector.Unprotect(File.ReadAllText(_keyPath)); } catch (CryptographicException) { return null; }
        }
        return string.IsNullOrWhiteSpace(_config["ManualTesting:Translation:Azure:Key"]) ? null : _config["ManualTesting:Translation:Azure:Key"];
    }

    public bool AzureKeySet => AzureKey() is not null;

    /// <summary>Saves the settings (validated) and, when given, the Azure key ("" removes it). Returns the problem when refused.</summary>
    public string? Save(SaveTranslationSettingsRequest request, string actor, DateTimeOffset now)
    {
        var (settings, problem) = Validate(request.Settings);
        if (problem is not null) return problem;
        if (request.AzureKey is { } key && key.Length > 0 && (key.Trim().Length is < 16 or > 256 || key.Any(char.IsControl) || key.Trim().Contains(' ')))
            return "The Azure key does not look like a Translator resource key (one value, 16–256 characters).";
        lock (_gate)
        {
            Write(_settingsPath, JsonSerializer.Serialize(new Saved(settings!, actor, now), Json));
            if (request.AzureKey is { } k)
            {
                if (k.Length == 0) { if (File.Exists(_keyPath)) File.Delete(_keyPath); }
                else Write(_keyPath, _protector.Protect(k.Trim()));
            }
        }
        return null;
    }

    [GeneratedRegex("^[a-z0-9-]{2,40}$")] private static partial Regex RegionPattern();

    public static (TranslationSettings? Settings, string? Problem) Validate(TranslationSettings s)
    {
        if (!Enum.IsDefined(s.Provider)) return (null, "Choose a translation service.");
        var source = (s.SourceLanguage ?? "").Trim();
        if (source != TranslationLanguages.Auto && !TranslationLanguages.IsCode(source)) return (null, "The \"From\" language is not a language code.");
        var targets = (s.TargetLanguages ?? []).Select(t => (t ?? "").Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (targets.Count == 0) return (null, "Choose at least one \"To\" language.");
        if (targets.Count > MaxTargets) return (null, $"At most {MaxTargets} \"To\" languages.");
        if (targets.FirstOrDefault(t => !TranslationLanguages.IsCode(t)) is { } bad) return (null, $"\"{bad}\" is not a language code.");
        if (targets.Any(t => string.Equals(t, source, StringComparison.OrdinalIgnoreCase))) return (null, "A \"To\" language is the same as the \"From\" language.");
        if (!TranslationSettings.OfflineIdleStopChoices.Contains(s.OfflineIdleStopMinutes))
            return (null, "Choose how long the offline translator stays loaded: 10, 30, 60, 240 or 480 minutes, or never stop.");
        if (s.LibreTranslatePort is < 1024 or > 65535 or 5000 or 5173) return (null, "The local LibreTranslate port must be 1024–65535 and not 5000 or 5173 (BirkNext's own ports).");
        if (!ContainerArgumentRules.IsImage(s.LibreTranslateImage ?? "")) return (null, "The LibreTranslate image must be a pinned, fully qualified image (no \":latest\").");
        var url = (s.LibreTranslateUrl ?? "").Trim();
        if (!s.LibreTranslateManaged && (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")))
            return (null, "Give the LibreTranslate server's address (http or https), or let BirkNext run it locally.");
        var glossary = (s.Glossary ?? []).Where(g => g is not null && !string.IsNullOrWhiteSpace(g.Source) && !string.IsNullOrWhiteSpace(g.Target))
            .Select(g => new TranslationGlossaryEntry(g.Source.Trim(), g.Target.Trim())).DistinctBy(g => g.Source, StringComparer.OrdinalIgnoreCase).ToList();
        if (glossary.Count > TranslationGlossary.MaxEntries) return (null, $"At most {TranslationGlossary.MaxEntries} glossary terms.");
        if (glossary.FirstOrDefault(g => g.Source.Length > 100 || g.Target.Length > 100 || g.Source.Any(char.IsControl) || g.Target.Any(char.IsControl) || g.Source.Contains('⟦') || g.Source.Contains('<')) is { } badTerm)
            return (null, $"The glossary term \"{(badTerm.Source.Length > 40 ? badTerm.Source[..40] + "…" : badTerm.Source)}\" is not valid (one line, at most 100 characters each side).");
        var region = s.AzureRegion?.Trim();
        if (!string.IsNullOrEmpty(region) && !RegionPattern().IsMatch(region)) return (null, "The Azure region is a name such as \"norwayeast\" or \"westeurope\".");
        var endpoint = s.AzureEndpoint?.Trim();
        if (!string.IsNullOrEmpty(endpoint) && (!Uri.TryCreate(endpoint, UriKind.Absolute, out var e) || e.Scheme != Uri.UriSchemeHttps))
            return (null, "The Azure endpoint must be an https address (or empty for the global endpoint).");
        return (s with
        {
            SourceLanguage = source, TargetLanguages = targets, LibreTranslateUrl = url, AzureRegion = string.IsNullOrEmpty(region) ? null : region,
            AzureEndpoint = string.IsNullOrEmpty(endpoint) ? null : endpoint, LibreTranslateImage = s.LibreTranslateImage!.Trim(), Glossary = glossary,
        }, null);
    }

    private Saved? Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_settingsPath)) return null;
            try { return JsonSerializer.Deserialize<Saved>(File.ReadAllText(_settingsPath), Json); }
            catch (JsonException) { return null; }
        }
    }

    private static void Write(string path, string content)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, true);
    }
}
