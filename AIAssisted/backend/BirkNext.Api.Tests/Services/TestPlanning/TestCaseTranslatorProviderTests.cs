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
/// The translation providers behind Plan &amp; prepare's language action: chosen by the settings (default Azure AI Translator, English → Norwegian
/// bokmål, offline fallback to the local LibreTranslate server; nothing called to decide), Azure over its documented v3 REST API with the
/// language pair in the address and identifiers marked notranslate, LibreTranslate receiving only the plain-language pieces, the fallback naming
/// the service that translated, and every failure reported without a partial result. A fake HTTP handler stands in for the services: no network.
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

    /// <summary>A LibreTranslate stand-in answer: every piece prefixed "[nb] ".</summary>
    private static HttpResponseMessage LibreAnswer(string body) =>
        Json(new { translatedText = JsonDocument.Parse(body).RootElement.GetProperty("q").EnumerateArray().Select(e => "[nb] " + e.GetString()).ToList() });

    // ── Choice of provider ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_default_is_Azure_English_to_Norwegian_bokmal_with_the_local_server_as_offline_fallback()
    {
        var defaults = new TranslationSettings();
        defaults.Provider.Should().Be(TranslationProviderKind.AzureTranslator);
        defaults.SourceLanguage.Should().Be("en");
        defaults.TargetLanguages.Should().Equal("nb");
        defaults.OfflineFallback.Should().BeTrue();
        defaults.EffectiveLibreTranslateUrl.Should().Be("http://127.0.0.1:5050");

        var translator = TestCaseTranslators.Create(defaults, azureKey: null, Config(), NoNetwork);
        translator.Should().BeOfType<FallbackTestCaseTranslator>();
        translator.Availability.Available.Should().BeTrue("the local server may still translate");
        translator.Availability.Provider.Should().Be("LibreTranslate (local, offline)");
        translator.Availability.Reason.Should().Contain("no key").And.Contain("local LibreTranslate server is used instead");

        TestCaseTranslators.Create(defaults with { OfflineFallback = false }, null, Config(), NoNetwork).Availability
            .Should().Match<TranslationAvailability>(a => !a.Available && a.Reason == AzureTranslatorTestCaseTranslator.NoKey);
        TestCaseTranslators.Create(defaults with { Provider = TranslationProviderKind.Off }, null, Config(), NoNetwork).Availability.Reason.Should().Be(TestCaseTranslators.NotEnabled);
        TestCaseTranslators.Create(defaults with { Provider = TranslationProviderKind.Anthropic, OfflineFallback = false }, null, Config(), NoNetwork).Availability.Reason.Should().Be(TestCaseTranslators.NoAnthropicKey);
        TestCaseTranslators.Create(defaults with { Provider = TranslationProviderKind.LibreTranslate }, null, Config(), NoNetwork).Should().BeOfType<LibreTranslateTestCaseTranslator>();
        TestCaseTranslators.Create(defaults, "k", Config(), NoNetwork).Availability.Provider.Should().Be("Azure AI Translator");
    }

    [Fact]
    public void Server_configuration_applies_until_settings_are_saved()
    {
        TestCaseTranslators.FromConfiguration(Config()).Should().BeNull();
        var configured = TestCaseTranslators.FromConfiguration(Config(("ManualTesting:Translation:Provider", "LibreTranslate"), ("ManualTesting:Translation:LibreTranslate:Url", "http://127.0.0.1:6000")))!;
        configured.Provider.Should().Be(TranslationProviderKind.LibreTranslate);
        configured.LibreTranslateManaged.Should().BeFalse();
        configured.EffectiveLibreTranslateUrl.Should().Be("http://127.0.0.1:6000");
        configured.SourceLanguage.Should().Be("en");
        configured.TargetLanguages.Should().Equal("nb");
    }

    // ── Azure AI Translator ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Azure_receives_the_language_pair_html_with_identifiers_marked_notranslate_and_the_key_and_region_headers()
    {
        var handler = new Handler((request, body) =>
        {
            var texts = JsonDocument.Parse(body).RootElement.EnumerateArray().Select(e => e.GetProperty("Text").GetString()!).ToList();
            var to = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["to"];
            return Json(texts.Select(t => new { translations = new[] { new { text = t.Replace("Check", "Kontroller").Replace("the report", "rapporten"), to } } }));
        });
        var azure = (AzureTranslatorTestCaseTranslator)AzureTranslatorTestCaseTranslator.Create("secret-key-0123456789", "norwayeast", null, new Factory(handler));
        var (masked, tokens) = ScenarioPlanning.Protect("Check the report FR-027 in SkoleService/specs/001/spec.md & <done>");

        var result = await azure.TranslateAsync([masked, null, ""], "en", "nb", default);

        var call = handler.Calls.Single();
        call.Request.RequestUri!.ToString().Should().Be("https://api.cognitive.microsofttranslator.com/translate?api-version=3.0&textType=html&to=nb&from=en");
        call.Request.Headers.GetValues("Ocp-Apim-Subscription-Key").Single().Should().Be("secret-key-0123456789");
        call.Request.Headers.GetValues("Ocp-Apim-Subscription-Region").Single().Should().Be("norwayeast");
        var sentText = JsonDocument.Parse(call.Body).RootElement[0].GetProperty("Text").GetString();
        sentText.Should().Contain("<span class=\"notranslate\">⟦0⟧</span>").And.Contain("&amp;").And.Contain("&lt;done&gt;").And.NotContain("FR-027", "identifiers are replaced before sending");
        JsonDocument.Parse(call.Body).RootElement.GetArrayLength().Should().Be(1, "empty texts are not sent");
        result.Provider.Should().Be("Azure AI Translator (norwayeast)");
        result.Texts[1].Should().BeNull();
        ScenarioPlanning.Restore(result.Texts[0]!, tokens).Should().Be("Kontroller rapporten FR-027 in SkoleService/specs/001/spec.md & <done>");
        azure.Address("auto", "sv").ToString().Should().EndWith("&to=sv", "auto-detect sends no from");
    }

    [Fact]
    public void A_custom_domain_endpoint_uses_the_resource_path_and_a_plain_http_endpoint_is_refused()
    {
        var azure = (AzureTranslatorTestCaseTranslator)AzureTranslatorTestCaseTranslator.Create("k", null, "https://bufdir-translator.cognitiveservices.azure.com/", NoNetwork);
        azure.Address("en", "nb").ToString().Should().Be("https://bufdir-translator.cognitiveservices.azure.com/translator/text/v3.0/translate?api-version=3.0&textType=html&to=nb&from=en");
        AzureTranslatorTestCaseTranslator.Create("k", null, "http://plain.example", NoNetwork).Availability.Reason.Should().Be(AzureTranslatorTestCaseTranslator.BadEndpoint);
        AzureTranslatorTestCaseTranslator.ToHtml("a\nb ⟦3⟧").Should().Be("a<br>b <span class=\"notranslate\">⟦3⟧</span>");
        AzureTranslatorTestCaseTranslator.FromHtml("a<br>b <span class=\"notranslate\">⟦3⟧</span> &amp; ø").Should().Be("a\nb ⟦3⟧ & ø");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "refused the key")]
    [InlineData(HttpStatusCode.BadRequest, "language pair")]
    [InlineData(HttpStatusCode.TooManyRequests, "rate limited")]
    [InlineData(HttpStatusCode.InternalServerError, "answered 500")]
    public async Task An_Azure_error_is_reported_and_nothing_is_returned(HttpStatusCode status, string message)
    {
        var azure = AzureTranslatorTestCaseTranslator.Create("k", null, null, new Factory(new Handler((_, _) => new HttpResponseMessage(status))));
        var act = () => azure.TranslateAsync(["Check the report"], "en", "nb", default);
        (await act.Should().ThrowAsync<TestCaseTranslationException>()).Which.Message.Should().Contain(message).And.Contain("Nothing was changed");
    }

    // ── LibreTranslate (offline) ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LibreTranslate_receives_only_the_plain_language_pieces_with_the_language_pair()
    {
        var handler = new Handler((_, body) =>
        {
            var q = JsonDocument.Parse(body).RootElement.GetProperty("q").EnumerateArray().Select(e => e.GetString()!).ToList();
            return Json(new { translatedText = q.Select(t => t.Replace("Check the report", "Kontroller rapporten").Replace("and the field", "og feltet")).ToList() });
        });
        var libre = LibreTranslateTestCaseTranslator.Create("http://localhost:5050", null, new Factory(handler));
        var (masked, tokens) = ScenarioPlanning.Protect("Check the report FR-027 and the field `childId`");

        var result = await libre.TranslateAsync([masked, "Check the report", null], "en", "nb", default);

        var call = handler.Calls.Single();
        call.Request.RequestUri!.ToString().Should().Be("http://localhost:5050/translate");
        var sent = JsonDocument.Parse(call.Body).RootElement;
        sent.GetProperty("source").GetString().Should().Be("en");
        sent.GetProperty("target").GetString().Should().Be("nb");
        sent.GetProperty("format").GetString().Should().Be("text");
        sent.GetProperty("q").EnumerateArray().Select(e => e.GetString()).Should().Equal("Check the report", "and the field");
        call.Body.Should().NotContain("⟦").And.NotContain("FR-027").And.NotContain("childId");
        result.Provider.Should().Be("LibreTranslate (local, offline)");
        ScenarioPlanning.Restore(result.Texts[0]!, tokens).Should().Be("Kontroller rapporten FR-027 og feltet `childId`");
        result.Texts[1].Should().Be("Kontroller rapporten");
        result.Texts[2].Should().BeNull();
    }

    [Fact]
    public async Task A_LibreTranslate_error_names_the_missing_language_model_and_a_stopped_server()
    {
        var refusing = LibreTranslateTestCaseTranslator.Create("http://localhost:5050", null, new Factory(new Handler((_, _) => Json(new { error = "nb is not supported" }, HttpStatusCode.BadRequest))));
        (await refusing.Invoking(l => l.TranslateAsync(["Check"], "en", "nb", default)).Should().ThrowAsync<TestCaseTranslationException>()).Which.Message
            .Should().Contain("nb is not supported").And.Contain("en→nb language model installed");
        var stopped = LibreTranslateTestCaseTranslator.Create("http://localhost:5050", null, new Factory(new Handler((_, _) => throw new HttpRequestException("refused"))));
        (await stopped.Invoking(l => l.TranslateAsync(["Check"], "en", "nb", default)).Should().ThrowAsync<TestCaseTranslationException>()).Which.Message.Should().Contain("Is it running?");
        LibreTranslateTestCaseTranslator.Create("", null, NoNetwork).Availability.Reason.Should().Be(LibreTranslateTestCaseTranslator.NoUrl);
    }

    [Fact]
    public async Task A_text_with_only_identifiers_needs_no_call()
    {
        var libre = LibreTranslateTestCaseTranslator.Create("http://localhost:5050", null, NoNetwork);
        var (masked, tokens) = ScenarioPlanning.Protect("FR-027 202");
        var result = await libre.TranslateAsync([masked], "en", "nb", default);
        ScenarioPlanning.Restore(result.Texts[0]!, tokens).Should().Be("FR-027 202");
    }

    // ── Offline fallback ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Without_internet_Azure_falls_back_to_the_local_server_and_the_result_names_it()
    {
        var handler = new Handler((request, body) => request.RequestUri!.Host.Contains("microsofttranslator", StringComparison.Ordinal)
            ? throw new HttpRequestException("offline") : LibreAnswer(body));
        var translator = TestCaseTranslators.Create(new TranslationSettings(), "secret-key-0123456789", Config(), new Factory(handler));

        var result = await translator.TranslateAsync(["Check the report"], "en", "nb", default);

        result.Provider.Should().Be("LibreTranslate (local, offline)");
        result.Texts.Single().Should().Be("[nb] Check the report");
        handler.Calls.Select(c => c.Request.RequestUri!.Host).Should().Equal("api.cognitive.microsofttranslator.com", "127.0.0.1");
    }

    [Fact]
    public async Task When_no_choice_can_translate_one_message_names_both_and_nothing_is_returned()
    {
        var translator = TestCaseTranslators.Create(new TranslationSettings(), azureKey: null, Config(), new Factory(new Handler((_, _) => throw new HttpRequestException("refused"))));
        var error = (await translator.Invoking(t => t.TranslateAsync(["Check"], "en", "nb", default)).Should().ThrowAsync<TestCaseTranslationException>()).Which.Message;
        error.Should().Contain("Azure AI Translator has no key").And.Contain("The offline fallback failed too").And.Contain("127.0.0.1:5050").And.Contain("Is it running?");
    }
}
