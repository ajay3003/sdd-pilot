using BirkNext.Api.Services.TestPlanning;
using BirkNext.TestPlanning;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace BirkNext.Api.Tests.Services.TestPlanning;

/// <summary>
/// Opt-in check against a real, local LibreTranslate server with the English and Norwegian bokmål models (for example the image BirkNext's
/// System Settings → Translation starts: <c>libretranslate/libretranslate:v1.6.0</c> with <c>LT_LOAD_ONLY=en,nb</c>). Set
/// <c>BIRKNEXT_REAL_LIBRETRANSLATE_URL</c> (e.g. http://127.0.0.1:5050); without it the test does nothing. Only synthetic text is sent, and only
/// to that address. Checks the same path Plan &amp; prepare uses: identifiers protected, the pieces translated, identifiers restored unchanged.
/// </summary>
public sealed class RealLibreTranslateTests(ITestOutputHelper output)
{
    public const string Variable = "BIRKNEXT_REAL_LIBRETRANSLATE_URL";

    private sealed class Factory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    [Fact]
    public async Task A_real_local_LibreTranslate_translates_a_case_into_Norwegian_and_keeps_identifiers()
    {
        if (Environment.GetEnvironmentVariable(Variable) is not { Length: > 0 } url)
        {
            output.WriteLine($"{Variable} not set: skipped (no LibreTranslate server named).");
            return;
        }
        var translator = LibreTranslateTestCaseTranslator.Create(url, null, new Factory());
        translator.Availability.Available.Should().BeTrue();

        string?[] fields =
        [
            "Child past compulsory school age when the period locks",
            "Prepare the starting situation in the test environment: a child past compulsory school age",
            "Check FR-027 in spec.md: the child is not expected",
            null,
        ];
        var masked = fields.Select(f => f is null ? (null, new List<string>()) : ScenarioPlanning.Protect(f)).ToList();
        var answer = await translator.TranslateAsync([.. masked.Select(m => m.Item1)], "en", "nb", default);

        answer.Provider.Should().StartWith("LibreTranslate");
        answer.Texts.Should().HaveCount(fields.Length);
        answer.Texts[3].Should().BeNull("an empty field stays empty");
        var restored = answer.Texts.Select((t, i) => t is null ? null : ScenarioPlanning.Restore(t, masked[i].Item2)).ToList();
        for (var i = 0; i < 3; i++)
        {
            output.WriteLine($"{fields[i]}\n  → {restored[i]}");
            restored[i].Should().NotBeNullOrWhiteSpace().And.NotBe(fields[i], "the text is translated");
        }
        restored[2].Should().Contain("FR-027").And.Contain("spec.md", "identifiers come back exactly as written");
        restored[0]!.ToLowerInvariant().Should().ContainAny("barn", "skole", "periode");
    }
}
