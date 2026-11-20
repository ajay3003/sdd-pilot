using System.Security.Cryptography;
using System.Text.Json;
using BirkNext.Api.Services.TestPlanning;
using BirkNext.Api.Services.TestPlanning.OfflineTranslation;
using BirkNext.TestPlanning;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using T = BirkNext.Api.Services.TestPlanning.OfflineTranslation.EmbeddedOfflineTestCaseTranslator;

namespace BirkNext.Api.Tests.Services.TestPlanning;

/// <summary>
/// The embedded offline translator (2026-11-20): identifiers and glossary terms travel as &lt;xN&gt; markers (the model keeps those; it splits
/// ⟦n⟧), Norwegian text is left alone, and a bundle is used only when every file matches its manifest. Pure tests run everywhere; the real model is
/// exercised by <see cref="RealModelTests"/> only when BIRKNEXT_OFFLINE_TRANSLATION_DIR names a built bundle.
/// </summary>
public sealed class EmbeddedOfflineTranslationTests
{
    [Fact]
    public void Identifiers_and_glossary_terms_become_markers_and_come_back_unchanged()
    {
        var (masked, tokens) = ScenarioPlanning.Protect("A correction to FR-017 replaces the report in spec.md for Skolenærvær.");
        var m = T.Mask(masked, TranslationGlossary.DefaultEnNb);
        m.Text.Should().NotContain("correction").And.NotContain("Skolenærvær").And.NotContain("⟦");
        m.Values.Should().Contain("korrigering").And.Contain("Skolenærvær");

        // The model answers in Norwegian with the markers in place (order may change).
        var answer = m.Text.Replace("A ", "En ").Replace(" replaces the report in ", " erstatter rapporten i ").Replace(" for ", " for ");
        var restored = T.Unmask(answer, m);
        restored.Should().NotBeNull();
        ScenarioPlanning.Restore(restored!, tokens).Should().Contain("FR-017").And.Contain("spec.md").And.Contain("korrigering").And.Contain("Skolenærvær");
    }

    [Fact]
    public void A_lost_repeated_or_unknown_marker_is_never_accepted()
    {
        var m = T.Mask("Send ⟦0⟧ twice and check ⟦1⟧.", []);
        T.Unmask("Send <x0> to ganger.", m).Should().BeNull("<x1> is lost");
        T.Unmask("Send <x0> <x0> og sjekk <x1>.", m).Should().BeNull("<x0> is repeated");
        T.Unmask("Send <x0> og sjekk <x1> <x7>.", m).Should().BeNull("<x7> is unknown");
        T.Unmask("Send < x0 > to ganger og sjekk <X1>.", m).Should().Be("Send ⟦0⟧ to ganger og sjekk ⟦1⟧.", "spacing and case inside a marker are tolerated");
    }

    [Fact]
    public void A_glossary_term_matches_whole_words_in_any_case_and_starts_a_sentence_with_a_capital()
    {
        var m = T.Mask("Withdrawal of a report. Corrections are not withdrawals.", TranslationGlossary.DefaultEnNb);
        m.Text.Should().StartWith("<x0> of a report.").And.Contain("Corrections").And.Contain("withdrawals", "only whole words are glossary terms");
        T.Unmask("<x0> av en rapport.", m).Should().Be("Tilbaketrekking av en rapport.");
        T.Mask("correction, Korrigering and korrigering", TranslationGlossary.DefaultEnNb).Values.Should().Equal(new[] { "Korrigering", "korrigering", "korrigering" }, "a kept term keeps its own case");
    }

    [Theory]
    [InlineData("Samme meldingId gir samme utfall.", true)]
    [InlineData("Identisk svar, ingen ny effekt.", true)]
    [InlineData("Periodelås", true)]
    [InlineData("Send the same message ID twice and compare the responses.", false)]
    [InlineData("The child is not expected in the period.", false)]
    [InlineData("Check that <x0> is met.", false)]
    public void Norwegian_text_is_recognised_and_left_alone(string text, bool norwegian) => T.LooksNorwegian(text).Should().Be(norwegian);

    // ── Bundle verification ─────────────────────────────────────────────────────────────────────────────

    private static string Bundle(int protocol = 1, Action<string>? tamper = null, string? extraPath = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "bn-offline-" + Guid.NewGuid().ToString("N"));
        var files = new Dictionary<string, string>
        {
            ["python/python.exe"] = "synthetic", ["helper/birknext_translate.py"] = "print()", ["model/model.bin"] = "weights", ["model/sentencepiece.model"] = "spm",
        };
        foreach (var (rel, content) in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(dir, rel))!);
            File.WriteAllText(Path.Combine(dir, rel), content);
        }
        var entries = files.Keys.Select(rel => new { path = rel, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(dir, rel)))).ToLowerInvariant(), size = new FileInfo(Path.Combine(dir, rel)).Length }).ToList();
        if (extraPath is not null) entries.Add(new { path = extraPath, sha256 = new string('0', 64), size = 0L });
        File.WriteAllText(Path.Combine(dir, OfflineTranslationBundle.ManifestFile), JsonSerializer.Serialize(new
        {
            format = 1, protocol, engine = new { name = "CTranslate2", version = "4.9.0", tokenizer = "SentencePiece 0.2.3", python = "3.12.10" },
            model = new { id = "HPLT/translate-en-nb-v2.0-hplt", revision = "24a48dba7fa622d090bf5bfb7d2ebc08d5a58932", source = "en", target = "nb", license = "CC-BY-4.0", attribution = "HPLT" },
            python = "python/python.exe", helper = "helper/birknext_translate.py", modelDirectory = "model", sentencePiece = "model/sentencepiece.model", files = entries,
        }));
        tamper?.Invoke(dir);
        return dir;
    }

    [Fact]
    public void A_complete_unchanged_bundle_is_accepted()
    {
        var dir = Bundle();
        try
        {
            var (bundle, problem) = OfflineTranslationBundle.Verify(dir);
            problem.Should().BeNull();
            bundle!.Manifest.Model.License.Should().Be("CC-BY-4.0");
            bundle.PythonExe.Should().EndWith(Path.Combine("python", "python.exe"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_missing_changed_foreign_or_incompatible_bundle_is_refused_with_the_reason()
    {
        var cases = new (string Dir, string Expected)[]
        {
            (Bundle(tamper: d => File.Delete(Path.Combine(d, "model/model.bin"))), "model/model.bin is missing"),
            (Bundle(tamper: d => File.WriteAllText(Path.Combine(d, "model/model.bin"), "weightz")), "model/model.bin has changed"),
            (Bundle(protocol: 2), "protocol 2"),
            (Bundle(extraPath: "../outside.txt"), "outside the package"),
            (Bundle(tamper: d => File.Delete(Path.Combine(d, OfflineTranslationBundle.ManifestFile))), "not installed"),
        };
        try
        {
            foreach (var (dir, expected) in cases)
            {
                var (bundle, problem) = OfflineTranslationBundle.Verify(dir);
                bundle.Should().BeNull();
                problem.Should().Contain(expected);
            }
        }
        finally { foreach (var (dir, _) in cases) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Without_a_bundle_the_offline_translator_is_unavailable_and_nothing_is_started()
    {
        var empty = Path.Combine(Path.GetTempPath(), "bn-offline-none-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [OfflineTranslationBundle.DirectoryKey] = empty }).Build();
            await using var engine = new OfflineTranslationEngine(config, NullLogger<OfflineTranslationEngine>.Instance);
            if (engine.Bundle().Bundle is not null) return; // a developer bundle in %LOCALAPPDATA% is a valid fallback location; nothing to assert then
            var status = await engine.StatusAsync(probe: true, default);
            status.State.Should().Be("NotInstalled");
            status.Ready.Should().BeFalse();
            engine.RunningProcessId.Should().BeNull();
            var translator = new EmbeddedOfflineTestCaseTranslator(engine, []);
            translator.Availability.Available.Should().BeFalse();
            await FluentActions.Awaiting(() => translator.TranslateAsync(["Hello"], "en", "nb", default)).Should().ThrowAsync<TestCaseTranslationException>();
        }
        finally { Directory.Delete(empty, true); }
    }

    [Fact]
    public void New_installations_default_to_the_embedded_offline_translator_without_internet_fallback_to_it()
    {
        var s = new TranslationSettings();
        s.Provider.Should().Be(TranslationProviderKind.EmbeddedOffline);
        (s.SourceLanguage, s.DefaultTarget).Should().Be(("en", "nb"));
        s.Glossary.Should().NotBeEmpty();
        // A saved file from before keeps its own choice (System.Text.Json reads the saved enum name).
        JsonSerializer.Deserialize<TranslationSettings>("{\"Provider\":\"AzureTranslator\"}", new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })!
            .Provider.Should().Be(TranslationProviderKind.AzureTranslator);
    }
}

/// <summary>
/// The real packaged model (opt-in: BIRKNEXT_OFFLINE_TRANSLATION_DIR = a folder built by scripts/build-offline-translation.ps1, its "translation"
/// subfolder). Starts the helper, translates M2LB-style test text and checks identifiers, glossary terms and that the helper stops with the engine.
/// </summary>
public sealed class RealModelTests(Xunit.Abstractions.ITestOutputHelper output)
{
    /// <summary>M2LB terminology (the nine terms of the 2026-11-20 brief): English test text, and the Norwegian term a reviewer expects in it.</summary>
    public static readonly (string English, string ExpectedTerm, string Reference)[] Terminology =
    [
        ("The child lives in a foster home.", "fosterhjem", "Barnet bor i et fosterhjem."),
        ("The school reports school attendance every month.", "skolenærvær", "Skolen rapporterer skolenærvær hver måned."),
        ("Register the child's educational relationship with the school.", "utdanningsforhold", "Registrer barnets utdanningsforhold til skolen."),
        ("Check that the period lock prevents new reports.", "periodelås", "Kontroller at periodelåsen hindrer nye rapporter."),
        ("The municipality has a reporting obligation for the child.", "rapporteringsplikt", "Kommunen har rapporteringsplikt for barnet."),
        ("The child is not part of expected reporting.", "forventet rapportering", "Barnet er ikke med i forventet rapportering."),
        ("Children with code 6/7 must not be shown.", "kode 6/7", "Barn med kode 6/7 skal ikke vises."),
        ("A correction replaces the previous report.", "korrigering", "En korrigering erstatter forrige rapport."),
        ("A withdrawal removes the report from the statistics.", "tilbaketrekking", "En tilbaketrekking fjerner rapporten fra statistikken."),
    ];

    [Fact]
    public async Task M2LB_terminology_uses_the_reviewed_Norwegian_terms()
    {
        if (Dir is not { } dir) return;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [OfflineTranslationBundle.DirectoryKey] = dir }).Build();
        await using var engine = new OfflineTranslationEngine(config, NullLogger<OfflineTranslationEngine>.Instance);
        var translator = new EmbeddedOfflineTestCaseTranslator(engine, TranslationGlossary.DefaultEnNb);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await translator.TranslateAsync([.. Terminology.Select(t => (string?)t.English)], "en", "nb", default);
        output.WriteLine($"{Terminology.Length} texts in {watch.Elapsed.TotalSeconds:0.00} s (including start)");
        for (var i = 0; i < Terminology.Length; i++)
        {
            output.WriteLine($"| {Terminology[i].English} | {result.Texts[i]} | {Terminology[i].Reference} |");
            result.Texts[i]!.Should().ContainEquivalentOf(Terminology[i].ExpectedTerm);
        }
        // Without the glossary the same model misses most of these terms: the glossary is what makes the terminology reliable.
        var plain = await new EmbeddedOfflineTestCaseTranslator(engine, []).TranslateAsync([.. Terminology.Select(t => (string?)t.English)], "en", "nb", default);
        for (var i = 0; i < Terminology.Length; i++) output.WriteLine($"no glossary | {plain.Texts[i]}");
    }

    private static string? Dir => Environment.GetEnvironmentVariable("BIRKNEXT_OFFLINE_TRANSLATION_DIR") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    [Fact]
    public async Task The_packaged_model_translates_test_text_keeping_identifiers_and_terms()
    {
        if (Dir is not { } dir) return;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [OfflineTranslationBundle.DirectoryKey] = dir }).Build();
        var engine = new OfflineTranslationEngine(config, NullLogger<OfflineTranslationEngine>.Instance);
        int? pid;
        await using (engine)
        {
            var status = await engine.StatusAsync(probe: true, default);
            status.State.Should().Be("Ready", status.Detail);
            status.ProbeResult.Should().Contain("Lagre");
            pid = engine.RunningProcessId;
            pid.Should().NotBeNull();

            var translator = new EmbeddedOfflineTestCaseTranslator(engine, TranslationGlossary.DefaultEnNb);
            var texts = new[]
            {
                "Prepare a child in foster care with an educational relationship to the school (FR-027).",
                "Send the same MessageId twice and check that ReportIntakeIdempotencyTests passes.",
                "A correction replaces the previous report; a withdrawal removes it.",
                "Samme meldingId gir samme utfall.",
                "1. Open the page.\n2. Fill in all required fields and select Save.",
            };
            var protectedTexts = texts.Select(ScenarioPlanning.Protect).ToList();
            var result = await translator.TranslateAsync([.. protectedTexts.Select(p => (string?)p.Masked)], "en", "nb", default);
            var restored = result.Texts.Select((t, i) => ScenarioPlanning.Restore(t!, protectedTexts[i].Tokens)).ToList();
            restored.Should().NotContainNulls("every identifier comes back");
            restored[0].Should().Contain("FR-027").And.Contain("fosterhjem").And.Contain("utdanningsforhold");
            restored[1].Should().Contain("MessageId").And.Contain("ReportIntakeIdempotencyTests");
            restored[2].Should().Contain("orrigering").And.Contain("tilbaketrekking");
            restored[3].Should().Be(texts[3], "Norwegian text is left unchanged");
            restored[4].Should().StartWith("1. ").And.Contain("\n2. ");
            result.Provider.Should().StartWith(EmbeddedOfflineTestCaseTranslator.ProviderName);
        }
        System.Diagnostics.Process.GetProcesses().Any(p => p.Id == pid && !p.HasExited && p.ProcessName.StartsWith("python", StringComparison.OrdinalIgnoreCase))
            .Should().BeFalse("the helper stops with the engine");
    }
}
