using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using BirkNext.TestPlanning;

namespace BirkNext.Api.Services.TestPlanning;

/// <summary>
/// Offline translation through a self-hosted LibreTranslate server (open source; e.g. a local Podman container). Once its language models are
/// installed it makes no external calls. Configured with:
/// <list type="bullet">
/// <item>ManualTesting:Translation:Provider = "LibreTranslate" (explicit opt-in)</item>
/// <item>ManualTesting:Translation:LibreTranslate:Url — e.g. http://localhost:5050 (no default: port 5000 is often the BirkNext API itself)</item>
/// <item>ManualTesting:Translation:LibreTranslate:ApiKey — only if the server requires keys</item>
/// <item>ManualTesting:Translation:LibreTranslate:Source / Target — default "en" / "nb" (Norwegian bokmål)</item>
/// </list>
/// Machine translation models do not reliably keep placeholders, so each text is split at the protected identifiers and only the plain-language
/// pieces between them are sent (<c>POST {url}/translate</c>, JSON <c>q</c> as an array, <c>format: "text"</c>); the identifiers never reach the
/// server and are put back unchanged. Text is sent only when a person chooses Translate.
/// </summary>
public sealed partial class LibreTranslateTestCaseTranslator : ITestCaseTranslator
{
    public const string ProviderName = "LibreTranslate";
    public const string NoUrl = "LibreTranslate is chosen for Manual Testing but no server address is configured (ManualTesting:Translation:LibreTranslate:Url, e.g. http://localhost:5050). You can write the Norwegian text by hand.";
    public const string BadUrl = "The LibreTranslate address (ManualTesting:Translation:LibreTranslate:Url) is not an http(s) address. You can write the Norwegian text by hand.";

    private readonly IHttpClientFactory _http;
    private readonly Uri _translate;
    private readonly string? _apiKey;
    private readonly string _source;
    private readonly string _target;

    public TranslationAvailability Availability { get; }

    public LibreTranslateTestCaseTranslator(IHttpClientFactory http, Uri url, string? apiKey, string? source, string? target)
    {
        _http = http;
        _translate = new Uri(url.AbsoluteUri.TrimEnd('/') + "/translate");
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        _source = string.IsNullOrWhiteSpace(source) ? "en" : source.Trim();
        _target = string.IsNullOrWhiteSpace(target) ? "nb" : target.Trim();
        var local = url.IsLoopback;
        Availability = new(true, $"LibreTranslate{(local ? " (local, offline)" : $" ({url.Host})")}",
            local ? $"Offline translation by the local LibreTranslate server ({url.Authority}); nothing leaves this PC."
                  : $"Translation by the LibreTranslate server at {url.Host}; scenario text is sent to it only when you choose Translate.");
    }

    public static ITestCaseTranslator FromConfiguration(IConfiguration config, IHttpClientFactory http)
    {
        var raw = config["ManualTesting:Translation:LibreTranslate:Url"];
        if (string.IsNullOrWhiteSpace(raw)) return new UnavailableTestCaseTranslator(NoUrl);
        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https")) return new UnavailableTestCaseTranslator(BadUrl);
        return new LibreTranslateTestCaseTranslator(http, url, config["ManualTesting:Translation:LibreTranslate:ApiKey"],
            config["ManualTesting:Translation:LibreTranslate:Source"], config["ManualTesting:Translation:LibreTranslate:Target"]);
    }

    [GeneratedRegex("(⟦\\d+⟧)")]
    private static partial Regex Placeholder();

    /// <summary>A text split into plain-language pieces (translated) and placeholders (kept), in order. Whitespace around a piece is kept apart.</summary>
    public static List<(string Text, bool Translate)> Split(string text) =>
        [.. Placeholder().Split(text).Where(p => p.Length > 0).Select(p => (p, !Placeholder().IsMatch(p) && p.Any(char.IsLetter)))];

    public async Task<List<string?>> TranslateAsync(IReadOnlyList<string?> texts, CancellationToken ct)
    {
        var result = texts.Select(_ => (string?)null).ToList();
        var parts = texts.Select(t => string.IsNullOrWhiteSpace(t) ? null : Split(t)).ToList();
        var pieces = parts.Where(p => p is not null).SelectMany(p => p!.Where(x => x.Translate).Select(x => x.Text.Trim())).Distinct(StringComparer.Ordinal).ToList();
        var translated = new Dictionary<string, string>(StringComparer.Ordinal);
        if (pieces.Count > 0)
        {
            try
            {
                var client = _http.CreateClient(nameof(LibreTranslateTestCaseTranslator));
                client.Timeout = TimeSpan.FromSeconds(120); // a local CPU model is slow on the first request
                var body = new Dictionary<string, object> { ["q"] = pieces, ["source"] = _source, ["target"] = _target, ["format"] = "text" };
                if (_apiKey is not null) body["api_key"] = _apiKey;
                using var response = await client.PostAsJsonAsync(_translate, body, ct);
                if (!response.IsSuccessStatusCode)
                {
                    string? error = null;
                    try { error = (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("error").GetString(); } catch (Exception e) when (e is JsonException or InvalidOperationException or System.Collections.Generic.KeyNotFoundException) { }
                    throw new TestCaseTranslationException($"LibreTranslate answered {(int)response.StatusCode}{(error is null ? "" : $": {error}")}. Is the {_source}→{_target} language model installed? Nothing was changed.");
                }
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var list = doc.RootElement.GetProperty("translatedText").EnumerateArray().Select(e => e.GetString() ?? "").ToList();
                if (list.Count != pieces.Count) throw new TestCaseTranslationException("LibreTranslate returned a different number of texts. Nothing was changed.");
                for (var i = 0; i < pieces.Count; i++) translated[pieces[i]] = list[i];
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or System.Collections.Generic.KeyNotFoundException or InvalidOperationException)
            {
                throw new TestCaseTranslationException($"The LibreTranslate server at {_translate.Authority} could not be reached or answered unexpectedly. Is it running? Nothing was changed.");
            }
        }
        for (var i = 0; i < texts.Count; i++)
        {
            if (parts[i] is not { } p) continue;
            result[i] = string.Concat(p.Select(x =>
            {
                if (!x.Translate) return x.Text;
                var lead = x.Text[..(x.Text.Length - x.Text.TrimStart().Length)];
                var trail = x.Text[x.Text.TrimEnd().Length..];
                return lead + translated[x.Text.Trim()].Trim() + trail;
            }));
        }
        return result;
    }
}
