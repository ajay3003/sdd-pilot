using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using BirkNext.TestPlanning;

namespace BirkNext.Api.Services.TestPlanning;

/// <summary>
/// Offline translation through a self-hosted LibreTranslate server (open source; e.g. a local Podman container). Once its language models are
/// installed it makes no external calls. Set in System Settings → Translation: BirkNext runs it with Podman on 127.0.0.1 (managed), or uses the
/// address of another LibreTranslate server; an API key only if that server requires one (ManualTesting:Translation:LibreTranslate:ApiKey).
/// Machine translation models do not reliably keep placeholders, so each text is split at the protected identifiers and only the plain-language
/// pieces between them are sent (<c>POST {url}/translate</c>, JSON <c>q</c> as an array, <c>format: "text"</c>); the identifiers never reach the
/// server and are put back unchanged. Text is sent only when a person chooses Translate.
/// </summary>
public sealed partial class LibreTranslateTestCaseTranslator : ITestCaseTranslator
{
    public const string ProviderName = "LibreTranslate";
    public const string NoUrl = "LibreTranslate has no server address (System Settings → Translation). You can write the translated text by hand.";
    public const string BadUrl = "The LibreTranslate address (System Settings → Translation) is not an http(s) address. You can write the translated text by hand.";

    private readonly IHttpClientFactory _http;
    private readonly Uri _translate;
    private readonly string? _apiKey;

    public TranslationAvailability Availability { get; }

    public LibreTranslateTestCaseTranslator(IHttpClientFactory http, Uri url, string? apiKey)
    {
        _http = http;
        _translate = new Uri(url.AbsoluteUri.TrimEnd('/') + "/translate");
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        var local = url.IsLoopback;
        Availability = new(true, $"LibreTranslate{(local ? " (local, offline)" : $" ({url.Host})")}",
            local ? $"Offline translation by the local LibreTranslate server ({url.Authority}); nothing leaves this PC."
                  : $"Translation by the LibreTranslate server at {url.Host}; scenario text is sent to it only when you choose Translate.");
    }

    public static ITestCaseTranslator Create(string? url, string? apiKey, IHttpClientFactory http)
    {
        if (string.IsNullOrWhiteSpace(url)) return new UnavailableTestCaseTranslator(NoUrl);
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return new UnavailableTestCaseTranslator(BadUrl);
        return new LibreTranslateTestCaseTranslator(http, uri, apiKey);
    }

    [GeneratedRegex("(⟦\\d+⟧)")]
    private static partial Regex Placeholder();

    /// <summary>A text split into plain-language pieces (translated) and placeholders (kept), in order. Whitespace around a piece is kept apart.</summary>
    public static List<(string Text, bool Translate)> Split(string text) =>
        [.. Placeholder().Split(text).Where(p => p.Length > 0).Select(p => (p, !Placeholder().IsMatch(p) && p.Any(char.IsLetter)))];

    public async Task<TranslatedTexts> TranslateAsync(IReadOnlyList<string?> texts, string source, string target, CancellationToken ct)
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
                var body = new Dictionary<string, object> { ["q"] = pieces, ["source"] = source, ["target"] = target, ["format"] = "text" };
                if (_apiKey is not null) body["api_key"] = _apiKey;
                using var response = await client.PostAsJsonAsync(_translate, body, ct);
                if (!response.IsSuccessStatusCode)
                {
                    string? error = null;
                    try { error = (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("error").GetString(); } catch (Exception e) when (e is JsonException or InvalidOperationException or System.Collections.Generic.KeyNotFoundException) { }
                    throw new TestCaseTranslationException($"LibreTranslate answered {(int)response.StatusCode}{(error is null ? "" : $": {error}")}. Is the {source}→{target} language model installed? Nothing was changed.");
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
        return new(result, Availability.Provider!);
    }
}
