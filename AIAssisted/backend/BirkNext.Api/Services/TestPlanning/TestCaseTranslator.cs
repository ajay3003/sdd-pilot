using System.Net.Http.Json;
using System.Text.Json;
using BirkNext.TestPlanning;

namespace BirkNext.Api.Services.TestPlanning;

/// <summary>
/// Translates test-case text from one language to another (default English → Norwegian bokmål). Texts arrive with technical identifiers already
/// replaced by ⟦n⟧ placeholders (<see cref="ScenarioPlanning.Protect"/>); an implementation returns the same number of texts and keeps every
/// placeholder. A translation is never a review decision and never replaces the original.
/// </summary>
public interface ITestCaseTranslator
{
    TranslationAvailability Availability { get; }
    /// <summary>One translated text per input text (null in = null out). <paramref name="source"/> may be "auto". Throws
    /// <see cref="TestCaseTranslationException"/> when the service fails.</summary>
    Task<TranslatedTexts> TranslateAsync(IReadOnlyList<string?> texts, string source, string target, CancellationToken ct);
}

/// <summary>The translated texts and the service that produced them (recorded with the translation).</summary>
public sealed record TranslatedTexts(List<string?> Texts, string Provider);

public sealed class TestCaseTranslationException(string message) : Exception(message);

/// <summary>
/// The chosen online service with the local LibreTranslate server as offline fallback: when the online service has no key or cannot be reached,
/// the local server is used if it answers. The result names the service that translated. Both failing → one message naming both.
/// </summary>
public sealed class FallbackTestCaseTranslator(ITestCaseTranslator primary, ITestCaseTranslator offline) : ITestCaseTranslator
{
    public TranslationAvailability Availability { get; } = primary.Availability.Available
        ? primary.Availability with { Reason = primary.Availability.Reason + " If it cannot be reached, the local LibreTranslate server is used (offline)." }
        : new(true, offline.Availability.Provider, $"{primary.Availability.Reason.Split(" You can", 2)[0]} The local LibreTranslate server is used instead (offline), if it is running.");

    public async Task<TranslatedTexts> TranslateAsync(IReadOnlyList<string?> texts, string source, string target, CancellationToken ct)
    {
        string? first = null;
        if (primary.Availability.Available)
        {
            try { return await primary.TranslateAsync(texts, source, target, ct); }
            catch (TestCaseTranslationException e) { first = e.Message; }
        }
        else first = primary.Availability.Reason;
        try { return await offline.TranslateAsync(texts, source, target, ct); }
        catch (TestCaseTranslationException e)
        {
            throw new TestCaseTranslationException($"{first?.Split(" Nothing was changed", 2)[0].Split(" You can", 2)[0]} The offline fallback failed too: {e.Message}");
        }
    }
}

/// <summary>No translation service: the text can still be written by hand.</summary>
public sealed class UnavailableTestCaseTranslator(string reason) : ITestCaseTranslator
{
    public TranslationAvailability Availability { get; } = new(false, null, reason);
    public Task<TranslatedTexts> TranslateAsync(IReadOnlyList<string?> texts, string source, string target, CancellationToken ct) => throw new TestCaseTranslationException(reason);
}

/// <summary>
/// The translator the current settings choose (System Settings → Translation, else the server configuration, else the default: LibreTranslate
/// on this PC, English → Norwegian bokmål). Read on every request, so a change takes effect without a restart. Nothing is called to decide this.
/// </summary>
public sealed class TestCaseTranslatorResolver(TranslationSettingsStore store, IConfiguration config, IHttpClientFactory http)
{
    public ITestCaseTranslator Current() => TestCaseTranslators.Create(store.Effective().Settings, store.AzureKey(), config, http);
    public TranslationSettings Settings() => store.Effective().Settings;
}

public static class TestCaseTranslators
{
    public const string NotEnabled = "Translation is switched off (System Settings → Translation). You can write the translated text by hand.";
    public const string NoAnthropicKey = "Anthropic is chosen for translation but no Anthropic API key is configured on the server. You can write the translated text by hand.";

    public static ITestCaseTranslator Create(TranslationSettings s, string? azureKey, IConfiguration config, IHttpClientFactory http)
    {
        var chosen = Single(s, azureKey, config, http);
        return s.OfflineFallback && s.Provider is TranslationProviderKind.AzureTranslator or TranslationProviderKind.Anthropic
            ? new FallbackTestCaseTranslator(chosen, LibreTranslateTestCaseTranslator.Create(s.EffectiveLibreTranslateUrl, config["ManualTesting:Translation:LibreTranslate:ApiKey"], http))
            : chosen;
    }

    private static ITestCaseTranslator Single(TranslationSettings s, string? azureKey, IConfiguration config, IHttpClientFactory http) => s.Provider switch
    {
        TranslationProviderKind.AzureTranslator => AzureTranslatorTestCaseTranslator.Create(azureKey, s.AzureRegion, s.AzureEndpoint, http),
        TranslationProviderKind.LibreTranslate => LibreTranslateTestCaseTranslator.Create(s.EffectiveLibreTranslateUrl, config["ManualTesting:Translation:LibreTranslate:ApiKey"], http),
        TranslationProviderKind.Anthropic => string.IsNullOrWhiteSpace(config["Anthropic:ApiKey"]) ? new UnavailableTestCaseTranslator(NoAnthropicKey)
            : new AnthropicTestCaseTranslator(http, string.IsNullOrWhiteSpace(config["Anthropic:Model"]) ? "claude-sonnet-4-6" : config["Anthropic:Model"]!),
        _ => new UnavailableTestCaseTranslator(NotEnabled),
    };

    /// <summary>Settings from the server configuration (ManualTesting:Translation:*), used until System Settings saves its own; null = none configured.</summary>
    public static TranslationSettings? FromConfiguration(IConfiguration config)
    {
        var provider = config["ManualTesting:Translation:Provider"];
        if (string.IsNullOrWhiteSpace(provider)) return null;
        var kind = Enum.TryParse<TranslationProviderKind>(provider, true, out var k) ? k : TranslationProviderKind.Off;
        var url = config["ManualTesting:Translation:LibreTranslate:Url"];
        var source = config["ManualTesting:Translation:Source"];
        var target = config["ManualTesting:Translation:Target"];
        return new TranslationSettings
        {
            Provider = kind,
            SourceLanguage = TranslationLanguages.IsCode(source) ? source! : TranslationLanguages.English,
            TargetLanguages = [TranslationLanguages.IsCode(target) ? target! : TranslationLanguages.NorwegianBokmal],
            LibreTranslateManaged = string.IsNullOrWhiteSpace(url), LibreTranslateUrl = url?.Trim() ?? "",
            AzureRegion = config["ManualTesting:Translation:Azure:Region"], AzureEndpoint = config["ManualTesting:Translation:Azure:Endpoint"],
        };
    }
}

/// <summary>
/// Translation through the Anthropic Messages API, the AI service BirkNext already uses (Anthropic:ApiKey, Anthropic:Model in the server
/// configuration). Scenario text is only sent when a person chooses Translate.
/// </summary>
public sealed class AnthropicTestCaseTranslator(IHttpClientFactory http, string model) : ITestCaseTranslator
{
    public TranslationAvailability Availability { get; } = new(true, $"Anthropic {model}", $"Translation by Anthropic {model} (online); scenario text is sent to the service only when you choose Translate.");

    private static readonly object Tool = new
    {
        name = "submit_translations",
        description = "Return the translation of every input text, in the same order.",
        input_schema = new { type = "object", properties = new { translations = new { type = "array", items = new { type = "string" } } }, required = new[] { "translations" } },
    };

    public async Task<TranslatedTexts> TranslateAsync(IReadOnlyList<string?> texts, string source, string target, CancellationToken ct)
    {
        var indexes = texts.Select((t, i) => (t, i)).Where(x => !string.IsNullOrWhiteSpace(x.t)).Select(x => x.i).ToList();
        var result = texts.Select(_ => (string?)null).ToList();
        if (indexes.Count == 0) return new(result, Availability.Provider!);
        var from = source == TranslationLanguages.Auto ? "its language" : $"{TranslationLanguages.Name(source)} ({source})";
        var prompt = $"Translate each text in the JSON array below from {from} to {TranslationLanguages.Name(target)} ({target}), for manual testers. Keep the meaning exact. "
            + "Copy every placeholder such as ⟦0⟧ exactly once, unchanged. Do not translate anything inside placeholders and do not add or remove texts. "
            + "Return the same number of texts in the same order.\n\n" + JsonSerializer.Serialize(indexes.Select(i => texts[i]));
        try
        {
            var client = http.CreateClient("Anthropic");
            using var response = await client.PostAsJsonAsync("https://api.anthropic.com/v1/messages", new
            {
                model, max_tokens = 4000, tools = new[] { Tool }, tool_choice = new { type = "tool", name = "submit_translations" },
                messages = new[] { new { role = "user", content = prompt } },
            }, ct);
            if (!response.IsSuccessStatusCode) throw new TestCaseTranslationException($"The translation service answered {(int)response.StatusCode}. Nothing was changed.");
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            foreach (var block in doc.RootElement.GetProperty("content").EnumerateArray())
            {
                if (block.GetProperty("type").GetString() != "tool_use") continue;
                var list = block.GetProperty("input").GetProperty("translations").EnumerateArray().Select(e => e.GetString()).ToList();
                if (list.Count != indexes.Count) throw new TestCaseTranslationException("The translation service returned a different number of texts. Nothing was changed.");
                for (var k = 0; k < indexes.Count; k++) result[indexes[k]] = list[k];
                return new(result, Availability.Provider!);
            }
            throw new TestCaseTranslationException("The translation service returned no translation. Nothing was changed.");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or System.Collections.Generic.KeyNotFoundException or InvalidOperationException)
        {
            throw new TestCaseTranslationException("The translation service could not be reached or answered unexpectedly. Nothing was changed.");
        }
    }
}
