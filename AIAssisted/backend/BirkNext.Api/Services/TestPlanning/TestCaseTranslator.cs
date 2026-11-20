using System.Net.Http.Json;
using System.Text.Json;
using BirkNext.TestPlanning;

namespace BirkNext.Api.Services.TestPlanning;

/// <summary>
/// Translates test-case text to Norwegian (bokmål). Texts arrive with technical identifiers already replaced by ⟦n⟧ placeholders
/// (<see cref="ScenarioPlanning.Protect"/>); an implementation must return the same number of texts and keep every placeholder.
/// A translation is never a review decision and never replaces the original.
/// </summary>
public interface ITestCaseTranslator
{
    TranslationAvailability Availability { get; }
    /// <summary>One translated text per input text (null in = null out). Throws <see cref="TestCaseTranslationException"/> when the service fails.</summary>
    Task<List<string?>> TranslateAsync(IReadOnlyList<string?> texts, CancellationToken ct);
}

public sealed class TestCaseTranslationException(string message) : Exception(message);

/// <summary>The default: no translation service is configured. Norwegian text can still be written by hand.</summary>
public sealed class UnavailableTestCaseTranslator(string reason) : ITestCaseTranslator
{
    public TranslationAvailability Availability { get; } = new(false, null, reason);
    public Task<List<string?>> TranslateAsync(IReadOnlyList<string?> texts, CancellationToken ct) => throw new TestCaseTranslationException(reason);
}

/// <summary>
/// Translation through the Anthropic Messages API, the AI service BirkNext already uses (Anthropic:ApiKey, Anthropic:Model). Off unless BOTH an
/// explicit opt-in (ManualTesting:Translation:Provider = "Anthropic") and an API key are configured: scenario text is only sent to the service when
/// a person chooses Translate, never in the background.
/// </summary>
public sealed class AnthropicTestCaseTranslator(IHttpClientFactory http, string model) : ITestCaseTranslator
{
    public const string ProviderSetting = "ManualTesting:Translation:Provider";
    public const string NotEnabled = "No translation service is enabled for Manual Testing (setting ManualTesting:Translation:Provider). You can write the Norwegian text by hand.";
    public const string NoKey = "The translation service is enabled but no Anthropic API key is configured. You can write the Norwegian text by hand.";

    public TranslationAvailability Availability { get; } = new(true, $"Anthropic {model}", $"Translation by Anthropic {model}; scenario text is sent to the service only when you choose Translate.");

    /// <summary>
    /// The translator the configuration allows (ManualTesting:Translation:Provider): "AzureTranslator" with a key, or "Anthropic" with a key;
    /// anything else is unavailable with the reason. Nothing is called to decide this.
    /// </summary>
    public static ITestCaseTranslator FromConfiguration(IConfiguration config, IHttpClientFactory http)
    {
        var provider = config[ProviderSetting];
        if (string.Equals(provider, AzureTranslatorTestCaseTranslator.ProviderName, StringComparison.OrdinalIgnoreCase))
            return AzureTranslatorTestCaseTranslator.FromConfiguration(config, http);
        if (string.Equals(provider, LibreTranslateTestCaseTranslator.ProviderName, StringComparison.OrdinalIgnoreCase))
            return LibreTranslateTestCaseTranslator.FromConfiguration(config, http);
        if (!string.Equals(provider, "Anthropic", StringComparison.OrdinalIgnoreCase)) return new UnavailableTestCaseTranslator(NotEnabled);
        if (string.IsNullOrWhiteSpace(config["Anthropic:ApiKey"])) return new UnavailableTestCaseTranslator(NoKey);
        return new AnthropicTestCaseTranslator(http, string.IsNullOrWhiteSpace(config["Anthropic:Model"]) ? "claude-sonnet-4-6" : config["Anthropic:Model"]!);
    }

    private static readonly object Tool = new
    {
        name = "submit_translations",
        description = "Return the Norwegian (bokmål) translation of every input text, in the same order.",
        input_schema = new
        {
            type = "object",
            properties = new { translations = new { type = "array", items = new { type = "string" } } },
            required = new[] { "translations" },
        },
    };

    public async Task<List<string?>> TranslateAsync(IReadOnlyList<string?> texts, CancellationToken ct)
    {
        var indexes = texts.Select((t, i) => (t, i)).Where(x => !string.IsNullOrWhiteSpace(x.t)).Select(x => x.i).ToList();
        var result = texts.Select(_ => (string?)null).ToList();
        if (indexes.Count == 0) return result;
        var prompt = "Translate each text in the JSON array below from its language to Norwegian bokmål, for manual testers. Keep the meaning exact. "
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
                return result;
            }
            throw new TestCaseTranslationException("The translation service returned no translation. Nothing was changed.");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or System.Collections.Generic.KeyNotFoundException or InvalidOperationException)
        {
            throw new TestCaseTranslationException("The translation service could not be reached or answered unexpectedly. Nothing was changed.");
        }
    }
}
