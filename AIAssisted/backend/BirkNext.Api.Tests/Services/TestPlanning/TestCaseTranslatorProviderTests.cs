using System.Net;
using System.Text;
using System.Text.Json;
using BirkNext.Api.Services.TestPlanning;
using BirkNext.TestPlanning;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BirkNext.Api.Tests.Services.TestPlanning;

/// <summary>
/// The translation providers behind Plan &amp; prepare's translate icon: chosen only by explicit configuration (unavailable with the reason
/// otherwise, nothing called to decide), Azure AI Translator over its documented v3 REST API with identifiers marked notranslate, LibreTranslate
/// (offline, local) receiving only the plain-language pieces so identifiers never reach it, and every failure reported without a partial result.
/// A fake HTTP handler stands in for both services: no network.
/// </summary>
public sealed class TestCaseTranslatorProviderTests
{
    private sealed class Handler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Calls { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request, body));
            return respond(request, body);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

    private static readonly Factory NoNetwork = new(new Handler((_, _) => throw new InvalidOperationException("no call expected")));

    // ── Choice of provider ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Without_an_explicit_choice_and_its_settings_translation_is_unavailable_with_the_reason()
    {
        AnthropicTestCaseTranslator.FromConfiguration(Config(), NoNetwork).Availability.Should().Match<TranslationAvailability>(a => !a.Available && a.Reason.Contains("write the Norwegian text by hand"));
        AnthropicTestCaseTranslator.FromConfiguration(Config(("Anthropic:ApiKey", "k")), NoNetwork).Availability.Available.Should().BeFalse("a key alone is not an opt-in");
        AnthropicTestCaseTranslator.FromConfiguration(Config((AnthropicTestCaseTranslator.ProviderSetting, "AzureTranslator")), NoNetwork).Availability.Reason.Should().Be(AzureTranslatorTestCaseTranslator.NoKey);
        AnthropicTestCaseTranslator.FromConfiguration(Config((AnthropicTestCaseTranslator.ProviderSetting, "AzureTranslator"), ("ManualTesting:Translation:Azure:Key", "k"),
            ("ManualTesting:Translation:Azure:Endpoint", "http://plain.example")), NoNetwork).Availability.Reason.Should().Be(AzureTranslatorTestCaseTranslator.BadEndpoint);
        AnthropicTestCaseTranslator.FromConfiguration(Config((AnthropicTestCaseTranslator.ProviderSetting, "LibreTranslate")), NoNetwork).Availability.Reason.Should().Be(LibreTranslateTestCaseTranslator.NoUrl);

        var azure = AnthropicTestCaseTranslator.FromConfiguration(Config((AnthropicTestCaseTranslator.ProviderSetting, "AzureTranslator"), ("ManualTesting:Translation:Azure:Key", "k"), ("ManualTesting:Translation:Azure:Region", "norwayeast")), NoNetwork);
        azure.Should().BeOfType<AzureTranslatorTestCaseTranslator>();
        azure.Availability.Provider.Should().Be("Azure AI Translator (norwayeast)");
        var libre = AnthropicTestCaseTranslator.FromConfiguration(Config((AnthropicTestCaseTranslator.ProviderSetting, "LibreTranslate"), ("ManualTesting:Translation:LibreTranslate:Url", "http://localhost:5050")), NoNetwork);
        libre.Availability.Provider.Should().Be("LibreTranslate (local, offline)");
        libre.Availability.Reason.Should().Contain("nothing leaves this PC");
    }

    // ── Azure AI Translator ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Azure_receives_html_with_identifiers_marked_notranslate_and_the_key_and_region_headers()
    {
        var handler = new Handler((_, body) =>
        {
            var texts = JsonDocument.Parse(body).RootElement.EnumerateArray().Select(e => e.GetProperty("Text").GetString()!).ToList();
            // The service translates around the notranslate spans and leaves them as they are.
            return Json(texts.Select(t => new { translations = new[] { new { text = t.Replace("Check", "Kontroller").Replace("the report", "rapporten").Replace("&lt;", "&lt;"), to = "nb" } } }));
        });
        var azure = new AzureTranslatorTestCaseTranslator(new Factory(handler), "secret-key", "norwayeast", new Uri("https://api.cognitive.microsofttranslator.com"));
        var (masked, tokens) = ScenarioPlanning.Protect("Check the report FR-027 in SkoleService/specs/001/spec.md & <done>");

        var result = await azure.TranslateAsync([masked, null, ""], default);

        var call = handler.Calls.Single();
        call.Request.RequestUri!.ToString().Should().Be("https://api.cognitive.microsofttranslator.com/translate?api-version=3.0&to=nb&textType=html");
        call.Request.Headers.GetValues("Ocp-Apim-Subscription-Key").Single().Should().Be("secret-key");
        call.Request.Headers.GetValues("Ocp-Apim-Subscription-Region").Single().Should().Be("norwayeast");
        var sentText = JsonDocument.Parse(call.Body).RootElement[0].GetProperty("Text").GetString();
        sentText.Should().Contain("<span class=\"notranslate\">⟦0⟧</span>").And.Contain("&amp;").And.Contain("&lt;done&gt;").And.NotContain("FR-027", "identifiers are replaced before sending");
        JsonDocument.Parse(call.Body).RootElement.GetArrayLength().Should().Be(1, "empty texts are not sent");
        result[1].Should().BeNull();
        var restored = ScenarioPlanning.Restore(result[0]!, tokens);
        restored.Should().Be("Kontroller rapporten FR-027 in SkoleService/specs/001/spec.md & <done>");
    }

    [Fact]
    public void A_custom_domain_endpoint_uses_the_resource_path()
    {
        var azure = new AzureTranslatorTestCaseTranslator(NoNetwork, "k", null, new Uri("https://bufdir-translator.cognitiveservices.azure.com/"));
        azure.Availability.Reason.Should().Contain("bufdir-translator.cognitiveservices.azure.com");
        AzureTranslatorTestCaseTranslator.ToHtml("a\nb ⟦3⟧").Should().Be("a<br>b <span class=\"notranslate\">⟦3⟧</span>");
        AzureTranslatorTestCaseTranslator.FromHtml("a<br>b <span class=\"notranslate\">⟦3⟧</span> &amp; ø").Should().Be("a\nb ⟦3⟧ & ø");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "refused the key")]
    [InlineData(HttpStatusCode.TooManyRequests, "rate limited")]
    [InlineData(HttpStatusCode.InternalServerError, "answered 500")]
    public async Task An_Azure_error_is_reported_and_nothing_is_returned(HttpStatusCode status, string message)
    {
        var azure = new AzureTranslatorTestCaseTranslator(new Factory(new Handler((_, _) => new HttpResponseMessage(status))), "k", null, new Uri(AzureTranslatorTestCaseTranslator.DefaultEndpoint));
        var act = () => azure.TranslateAsync(["Check the report"], default);
        (await act.Should().ThrowAsync<TestCaseTranslationException>()).Which.Message.Should().Contain(message).And.Contain("Nothing was changed");
    }

    [Fact]
    public async Task An_unreachable_service_is_reported_as_such()
    {
        var azure = new AzureTranslatorTestCaseTranslator(new Factory(new Handler((_, _) => throw new HttpRequestException("offline"))), "k", null, new Uri(AzureTranslatorTestCaseTranslator.DefaultEndpoint));
        (await azure.Invoking(a => a.TranslateAsync(["Check"], default)).Should().ThrowAsync<TestCaseTranslationException>()).Which.Message.Should().Contain("could not be reached");
    }

    // ── LibreTranslate (offline) ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LibreTranslate_receives_only_the_plain_language_pieces_and_identifiers_come_back_unchanged()
    {
        var handler = new Handler((_, body) =>
        {
            var q = JsonDocument.Parse(body).RootElement.GetProperty("q").EnumerateArray().Select(e => e.GetString()!).ToList();
            return Json(new { translatedText = q.Select(t => t.Replace("Check the report", "Kontroller rapporten").Replace("and the field", "og feltet")).ToList() });
        });
        var libre = new LibreTranslateTestCaseTranslator(new Factory(handler), new Uri("http://localhost:5050"), null, null, null);
        var (masked, tokens) = ScenarioPlanning.Protect("Check the report FR-027 and the field `childId`");

        var result = await libre.TranslateAsync([masked, "Check the report", null], default);

        var call = handler.Calls.Single();
        call.Request.RequestUri!.ToString().Should().Be("http://localhost:5050/translate");
        var sent = JsonDocument.Parse(call.Body).RootElement;
        sent.GetProperty("source").GetString().Should().Be("en");
        sent.GetProperty("target").GetString().Should().Be("nb");
        sent.GetProperty("format").GetString().Should().Be("text");
        sent.GetProperty("q").EnumerateArray().Select(e => e.GetString()).Should().Equal("Check the report", "and the field");
        call.Body.Should().NotContain("⟦").And.NotContain("FR-027").And.NotContain("childId");
        ScenarioPlanning.Restore(result[0]!, tokens).Should().Be("Kontroller rapporten FR-027 og feltet `childId`");
        result[1].Should().Be("Kontroller rapporten");
        result[2].Should().BeNull();
    }

    [Fact]
    public async Task A_LibreTranslate_error_names_the_missing_language_model_and_a_stopped_server()
    {
        var refusing = new LibreTranslateTestCaseTranslator(new Factory(new Handler((_, _) => Json(new { error = "nb is not supported" }, HttpStatusCode.BadRequest))), new Uri("http://localhost:5050"), null, null, null);
        (await refusing.Invoking(l => l.TranslateAsync(["Check"], default)).Should().ThrowAsync<TestCaseTranslationException>()).Which.Message
            .Should().Contain("nb is not supported").And.Contain("language model installed");
        var stopped = new LibreTranslateTestCaseTranslator(new Factory(new Handler((_, _) => throw new HttpRequestException("refused"))), new Uri("http://localhost:5050"), null, null, null);
        (await stopped.Invoking(l => l.TranslateAsync(["Check"], default)).Should().ThrowAsync<TestCaseTranslationException>()).Which.Message.Should().Contain("Is it running?");
    }

    [Fact]
    public async Task A_text_with_only_identifiers_needs_no_call()
    {
        var libre = new LibreTranslateTestCaseTranslator(NoNetwork, new Uri("http://localhost:5050"), null, null, null);
        var (masked, tokens) = ScenarioPlanning.Protect("FR-027 202");
        var result = await libre.TranslateAsync([masked], default);
        ScenarioPlanning.Restore(result[0]!, tokens).Should().Be("FR-027 202");
    }
}
