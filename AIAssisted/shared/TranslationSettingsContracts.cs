using System.Text.Json.Serialization;

namespace BirkNext.TestPlanning;

// ── Translation settings (System Settings → Translation) ───────────────────────────────────────────────────────────────────
// One installation-wide choice of translator for test-case text, with its languages. Not a Target Environment setting: the translator serves
// every project and environment. Secrets (the Azure key) are stored encrypted on the server and never returned.

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TranslationProviderKind
{
    /// <summary>No translation service: the language action opens the fields for writing the text by hand.</summary>
    Off,
    /// <summary>A LibreTranslate server (the local one BirkNext runs with Podman; offline once its language models are installed).</summary>
    LibreTranslate,
    /// <summary>Azure AI Translator (online; key and region). The default.</summary>
    AzureTranslator,
    /// <summary>The Anthropic client BirkNext already has (online; Anthropic:ApiKey in the server configuration).</summary>
    Anthropic,
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
    /// <summary>Default: Azure AI Translator (online). Until its key is saved, translation is unavailable (or the offline fallback is used).</summary>
    public TranslationProviderKind Provider { get; init; } = TranslationProviderKind.AzureTranslator;
    /// <summary>When the chosen online service has no key or cannot be reached, use the local LibreTranslate server (offline) if it answers.
    /// The result always names the service that translated.</summary>
    public bool OfflineFallback { get; init; } = true;
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

    public const int DefaultLibreTranslatePort = 5050;
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
