using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using BirkNext.TestPlanning;

namespace BirkNext.Api.Services.TestPlanning;

/// <summary>
/// Translation through Azure AI Translator (Translator Text REST API v3, <c>POST {endpoint}/translate?api-version=3.0&amp;to=nb</c>). Configured with:
/// <list type="bullet">
/// <item>ManualTesting:Translation:Provider = "AzureTranslator" (explicit opt-in)</item>
/// <item>ManualTesting:Translation:Azure:Key — the Translator resource key (user secrets / environment, never in appsettings.json)</item>
/// <item>ManualTesting:Translation:Azure:Region — the resource region (required for a regional or multi-service resource, e.g. "norwayeast")</item>
/// <item>ManualTesting:Translation:Azure:Endpoint — optional; default https://api.cognitive.microsofttranslator.com (a custom-domain endpoint
/// of the organisation's own resource can be given instead)</item>
/// </list>
/// Texts are sent as HTML with every protected identifier placeholder inside <c>&lt;span class="notranslate"&gt;</c>, so the service leaves them
/// as they are; BirkNext still checks every placeholder afterwards (<see cref="ScenarioPlanning.Restore"/>). Text is sent only when a person
/// chooses Translate.
/// </summary>
public sealed partial class AzureTranslatorTestCaseTranslator : ITestCaseTranslator
{
    public const string ProviderName = "AzureTranslator";
    public const string DefaultEndpoint = "https://api.cognitive.microsofttranslator.com";
    public const string NoKey = "Azure AI Translator is chosen for Manual Testing but no key is configured (ManualTesting:Translation:Azure:Key). You can write the Norwegian text by hand.";
    public const string BadEndpoint = "The Azure AI Translator endpoint (ManualTesting:Translation:Azure:Endpoint) is not an https address. You can write the Norwegian text by hand.";
    /// <summary>The service accepts at most 1000 texts and 50 000 characters per request; one test case stays far below that, larger input is refused.</summary>
    public const int MaxTexts = 1000;
    public const int MaxCharacters = 45_000;

    private readonly IHttpClientFactory _http;
    private readonly string _key;
    private readonly string? _region;
    private readonly Uri _translate;

    public TranslationAvailability Availability { get; }

    public AzureTranslatorTestCaseTranslator(IHttpClientFactory http, string key, string? region, Uri endpoint)
    {
        _http = http; _key = key; _region = string.IsNullOrWhiteSpace(region) ? null : region.Trim();
        _translate = new Uri(endpoint.AbsoluteUri.TrimEnd('/') + (endpoint.AbsolutePath.Contains("/translator/text/v3.0", StringComparison.OrdinalIgnoreCase) ? "" :
            endpoint.Host.EndsWith(".cognitiveservices.azure.com", StringComparison.OrdinalIgnoreCase) ? "/translator/text/v3.0" : "")
            + "/translate?api-version=3.0&to=nb&textType=html");
        Availability = new(true, $"Azure AI Translator{(_region is null ? "" : $" ({_region})")}",
            $"Translation by Azure AI Translator ({endpoint.Host}); scenario text is sent to the service only when you choose Translate.");
    }

    public static ITestCaseTranslator FromConfiguration(IConfiguration config, IHttpClientFactory http)
    {
        var key = config["ManualTesting:Translation:Azure:Key"];
        if (string.IsNullOrWhiteSpace(key)) return new UnavailableTestCaseTranslator(NoKey);
        var raw = config["ManualTesting:Translation:Azure:Endpoint"];
        if (!Uri.TryCreate(string.IsNullOrWhiteSpace(raw) ? DefaultEndpoint : raw.Trim(), UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps)
            return new UnavailableTestCaseTranslator(BadEndpoint);
        return new AzureTranslatorTestCaseTranslator(http, key.Trim(), config["ManualTesting:Translation:Azure:Region"], endpoint);
    }

    [GeneratedRegex("⟦\\d+⟧")]
    private static partial Regex Placeholder();

    [GeneratedRegex("<span class=\"notranslate\">(⟦\\d+⟧)</span>", RegexOptions.IgnoreCase)]
    private static partial Regex Protected();

    /// <summary>Plain text (with placeholders) → the HTML sent: escaped, each placeholder marked notranslate, line breaks kept.</summary>
    public static string ToHtml(string text) =>
        Placeholder().Replace(WebUtility.HtmlEncode(text).Replace("\r\n", "\n").Replace("\n", "<br>"), m => $"<span class=\"notranslate\">{m.Value}</span>");

    /// <summary>The returned HTML → plain text: the notranslate marks removed, line breaks restored, entities decoded.</summary>
    public static string FromHtml(string html) =>
        WebUtility.HtmlDecode(Regex.Replace(Protected().Replace(html, "$1"), "<br\\s*/?>", "\n", RegexOptions.IgnoreCase));

    public async Task<List<string?>> TranslateAsync(IReadOnlyList<string?> texts, CancellationToken ct)
    {
        var result = texts.Select(_ => (string?)null).ToList();
        var indexes = texts.Select((t, i) => (t, i)).Where(x => !string.IsNullOrWhiteSpace(x.t)).Select(x => x.i).ToList();
        if (indexes.Count == 0) return result;
        var body = indexes.Select(i => new Dictionary<string, string> { ["Text"] = ToHtml(texts[i]!) }).ToList(); // the documented body: [{"Text": "..."}]
        if (body.Count > MaxTexts || body.Sum(b => b["Text"].Length) > MaxCharacters)
            throw new TestCaseTranslationException("This test case is too long to translate in one request. Nothing was changed; write the Norwegian text by hand.");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _translate) { Content = JsonContent.Create(body) };
            request.Headers.Add("Ocp-Apim-Subscription-Key", _key);
            if (_region is not null) request.Headers.Add("Ocp-Apim-Subscription-Region", _region);
            request.Headers.Add("X-ClientTraceId", Guid.NewGuid().ToString());
            var client = _http.CreateClient(nameof(AzureTranslatorTestCaseTranslator));
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                throw new TestCaseTranslationException((int)response.StatusCode switch
                {
                    401 => "Azure AI Translator refused the key (401). Check ManualTesting:Translation:Azure:Key and Region. Nothing was changed.",
                    403 => "Azure AI Translator refused the request (403): check the resource's region and quota. Nothing was changed.",
                    429 => "Azure AI Translator is rate limited (429). Try again shortly. Nothing was changed.",
                    var code => $"Azure AI Translator answered {code}. Nothing was changed.",
                });
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var items = doc.RootElement.EnumerateArray().ToList();
            if (items.Count != indexes.Count) throw new TestCaseTranslationException("Azure AI Translator returned a different number of texts. Nothing was changed.");
            for (var k = 0; k < indexes.Count; k++)
            {
                var translation = items[k].GetProperty("translations").EnumerateArray().FirstOrDefault(t => t.GetProperty("to").GetString() == "nb");
                if (translation.ValueKind != JsonValueKind.Object) throw new TestCaseTranslationException("Azure AI Translator returned no Norwegian text. Nothing was changed.");
                result[indexes[k]] = FromHtml(translation.GetProperty("text").GetString() ?? "");
            }
            return result;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or System.Collections.Generic.KeyNotFoundException or InvalidOperationException)
        {
            throw new TestCaseTranslationException("Azure AI Translator could not be reached or answered unexpectedly. Nothing was changed.");
        }
    }
}
