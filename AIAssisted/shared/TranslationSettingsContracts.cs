using System.Text.Json.Serialization;

namespace BirkNext.TestPlanning;

// ── Translation settings (System Settings → Translation) ───────────────────────────────────────────────────────────────────
// One installation-wide choice of translator for test-case text, with its languages. Not a Target Environment setting: the translator serves
// every project and environment. Secrets (the Azure key) are stored encrypted on the server and never returned. The default (2026-11-21) is the
// LibreTranslate server (local, 127.0.0.1), falling back to the embedded offline translator packaged with BirkNext when the server cannot be reached.

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TranslationProviderKind
{
    /// <summary>No translation service: the language action opens the fields for writing the text by hand.</summary>
    Off,
    /// <summary>A LibreTranslate server (the local one BirkNext runs with Podman; offline once its language models are installed). The default.</summary>
    LibreTranslate,
    /// <summary>Azure AI Translator (online; key and region). Optional (Advanced).</summary>
    AzureTranslator,
    /// <summary>The Anthropic client BirkNext already has (online; Anthropic:ApiKey in the server configuration). Optional (Advanced).</summary>
    Anthropic,
    /// <summary>The offline translator packaged with BirkNext (CTranslate2 + the HPLT English → Norwegian bokmål model, CPU only). The fallback of a LibreTranslate server that cannot be reached.</summary>
    EmbeddedOffline,
}

/// <summary>One glossary term: <see cref="Source"/> (any case, whole words) is always written as <see cref="Target"/>. A term that is already
/// Norwegian (Source = Target) is kept unchanged. Used by the embedded offline translator; never a business rule.</summary>
public sealed record TranslationGlossaryEntry(string Source, string Target);

public static class TranslationGlossary
{
    public const int MaxEntries = 200;

    /// <summary>Starting terms for English → Norwegian bokmål test text (System Settings → Translation → Advanced, editable): only the terms the
    /// packaged model gets wrong or inconsistent on its own (school attendance → "skolen", reporting obligation → "meldeplikt", correction →
    /// "korreksjon", withdrawal → "uttak", educational relationship → sometimes "pedagogisk forhold"). Terms it already translates correctly
    /// (fosterhjem, periodelåsen, kode 6/7, forventet rapportering) are left to the model, which then also inflects them correctly. Norwegian terms written in English text are kept as they are. Measured in
    /// docs/audits/birknext-embedded-offline-translation-2026-11-20.md.</summary>
    public static readonly IReadOnlyList<TranslationGlossaryEntry> DefaultEnNb =
    [
        new("school attendance", "skolenærvær"), new("reporting obligation", "rapporteringsplikt"), new("obligation to report", "rapporteringsplikt"),
        new("correction", "korrigering"), new("withdrawal", "tilbaketrekking"),
        new("educational relationship", "utdanningsforhold"), new("education relationship", "utdanningsforhold"),
        new("Skolenærvær", "Skolenærvær"), new("Utdanningsforhold", "Utdanningsforhold"), new("Periodelås", "Periodelås"), new("Rapporteringsplikt", "Rapporteringsplikt"),
        new("Fosterhjem", "Fosterhjem"), new("Korrigering", "Korrigering"), new("Tilbaketrekking", "Tilbaketrekking"), new("Kode 6/7", "Kode 6/7"),
    ];
}

public sealed record TranslationLanguage(string Code, string Name);

public static class TranslationLanguages
{
    public const string Auto = "auto";
    public const string English = "en";
    public const string NorwegianBokmal = "nb";

    /// <summary>Languages offered by name. Codes are ISO 639-1 as Azure AI Translator and LibreTranslate use them; whether the chosen service
    /// supports a pair is checked against the service, never assumed.</summary>
    public static readonly IReadOnlyList<TranslationLanguage> Known =
    [
        new("en", "English"), new("nb", "Norwegian (bokmål)"), new("nn", "Norwegian (nynorsk)"), new("sv", "Swedish"), new("da", "Danish"),
        new("fi", "Finnish"), new("de", "German"), new("fr", "French"), new("es", "Spanish"), new("pl", "Polish"), new("uk", "Ukrainian"),
        new("lt", "Lithuanian"), new("ar", "Arabic"), new("so", "Somali"),
    ];

    public static string Name(string? code) => code == Auto ? "Detect automatically"
        : Known.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase))?.Name ?? (code ?? "");

    /// <summary>A two- or three-letter language code (optionally with a region or script part, e.g. "zh-Hans").</summary>
    public static bool IsCode(string? code) => code is { Length: >= 2 and <= 12 } && code.All(c => char.IsAsciiLetter(c) || c == '-') && char.IsAsciiLetter(code[0]);
}

public sealed record TranslationSettings
{
    /// <summary>Default (2026-11-23): the offline translator packaged with BirkNext — it needs no server, container, key or network. Saved
    /// settings keep their choice. (2026-11-21 to 11-23 the default was the LibreTranslate server, which needs Podman.)</summary>
    public TranslationProviderKind Provider { get; init; } = TranslationProviderKind.EmbeddedOffline;
    /// <summary>For LibreTranslate and the online services: when the service has no key or cannot be reached, translate offline instead (the embedded
    /// translator when it is installed; for an online service without it, the local LibreTranslate server). Never the other way round: the embedded translator never falls back
    /// to the internet. The result always names the service that translated.</summary>
    public bool OfflineFallback { get; init; } = true;
    /// <summary>Minutes the offline translator stays loaded with no translation before it stops and frees its memory (about 125 MB); 0 = never
    /// stops while BirkNext runs. A stopped translator needs the model load again (seconds when warm, longer on a fresh AVD session).</summary>
    public int OfflineIdleStopMinutes { get; init; } = DefaultOfflineIdleStopMinutes;
    /// <summary>The language of the original text ("auto" = the service detects it).</summary>
    public string SourceLanguage { get; init; } = TranslationLanguages.English;
    /// <summary>The languages a tester can translate into; the first is the default.</summary>
    public List<string> TargetLanguages { get; init; } = [TranslationLanguages.NorwegianBokmal];
    /// <summary>LibreTranslate: BirkNext runs the server itself with Podman (on 127.0.0.1:<see cref="LibreTranslatePort"/>), or uses <see cref="LibreTranslateUrl"/>.</summary>
    public bool LibreTranslateManaged { get; init; } = true;
    public string LibreTranslateUrl { get; init; } = "";
    public int LibreTranslatePort { get; init; } = DefaultLibreTranslatePort;
    public string LibreTranslateImage { get; init; } = DefaultLibreTranslateImage;
    public string? AzureRegion { get; init; }
    public string? AzureEndpoint { get; init; }
    /// <summary>Terms the embedded offline translator always writes the same way (see <see cref="TranslationGlossary.DefaultEnNb"/>).</summary>
    public List<TranslationGlossaryEntry> Glossary { get; init; } = [.. TranslationGlossary.DefaultEnNb];

    public const int DefaultLibreTranslatePort = 5050;
    public const int DefaultOfflineIdleStopMinutes = 60;
    /// <summary>The choices offered in System Settings → Translation (0 = never).</summary>
    public static readonly IReadOnlyList<int> OfflineIdleStopChoices = [10, 30, 60, 240, 480, 0];
    public const string DefaultLibreTranslateImage = "docker.io/libretranslate/libretranslate:v1.6.0";

    [JsonIgnore] public string DefaultTarget => TargetLanguages.FirstOrDefault() ?? TranslationLanguages.NorwegianBokmal;
    [JsonIgnore] public string EffectiveLibreTranslateUrl => LibreTranslateManaged ? $"http://127.0.0.1:{LibreTranslatePort}" : LibreTranslateUrl;
}

/// <summary>What System Settings shows: the saved choice, whether secrets are set (never their value), and what the choice resolves to now.</summary>
public sealed record TranslationSettingsView
{
    public TranslationSettings Settings { get; init; } = new();
    public bool AzureKeySet { get; init; }
    /// <summary>Anthropic:ApiKey is configured on the server (it is configured there, not here).</summary>
    public bool AnthropicKeyConfigured { get; init; }
    public TranslationAvailability Availability { get; init; } = new(false, null, "");
    /// <summary>"saved" (System Settings), "configuration" (server configuration, not yet saved here) or "default".</summary>
    public string Origin { get; init; } = "default";
    public string? UpdatedBy { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    /// <summary>Whether BirkNext can run the local LibreTranslate server here (Podman runner); false in the Tester Package (native runner).</summary>
    public bool CanManageLibreTranslate { get; init; }
    /// <summary>The embedded offline translator, as far as known without starting it (the package check); "Ready" only after a translation probe.</summary>
    public OfflineTranslationStatus Offline { get; init; } = new();
    public bool CanWrite { get; init; }
    public IReadOnlyList<TranslationLanguage> Languages { get; init; } = TranslationLanguages.Known;
}

/// <summary><see cref="AzureKey"/>: null = keep the stored key, "" = remove it, otherwise the new key (stored encrypted, never returned).</summary>
public sealed record SaveTranslationSettingsRequest(TranslationSettings Settings, string? AzureKey = null);

/// <summary>The managed local LibreTranslate server, fact by fact.</summary>
public sealed record LibreTranslateServiceStatus
{
    public bool PodmanAvailable { get; init; }
    public string PodmanDetail { get; init; } = "";
    public string Image { get; init; } = "";
    public bool ImagePresent { get; init; }
    public bool ContainerExists { get; init; }
    public bool Running { get; init; }
    /// <summary>The server answered GET /languages.</summary>
    public bool Answering { get; init; }
    public List<string> Languages { get; init; } = [];
    /// <summary>The configured source → default target pair is offered by the server.</summary>
    public bool PairAvailable { get; init; }
    public string Url { get; init; } = "";
    public string Detail { get; init; } = "";
}

/// <summary>One fixed sample sentence translated with the current settings (what System Settings → Test translation shows).</summary>
public sealed record TranslationTestResult(bool Ok, string? Provider, string Source, string? Translated, string Message);

/// <summary>
/// The embedded offline translator, fact by fact. <see cref="State"/>: "Ready" (the packaged model loaded and translated the probe sentence),
/// "Installed" (the package is complete and unchanged; not started yet), "NotInstalled", "Damaged" or "Failed" (it did not load or translate).
/// </summary>
public sealed record OfflineTranslationStatus
{
    public string State { get; init; } = "NotInstalled";
    public string Summary { get; init; } = "";
    public string? Detail { get; init; }
    public string? Model { get; init; }
    public string? ModelLicense { get; init; }
    public string? Attribution { get; init; }
    public string? Engine { get; init; }
    public string? Languages { get; init; }
    /// <summary>The probe sentence and its translation, when probed.</summary>
    public string? ProbeSource { get; init; }
    public string? ProbeResult { get; init; }
    public double? ProbeSeconds { get; init; }
    public DateTimeOffset? CheckedAt { get; init; }
    [JsonIgnore] public bool Ready => State == "Ready";
}
