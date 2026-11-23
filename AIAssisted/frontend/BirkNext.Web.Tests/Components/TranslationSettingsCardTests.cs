using BirkNext.TestPlanning;
using BirkNext.Web.Components.Admin;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// System Settings → Translation (2026-11-20): the default is the offline translator packaged with BirkNext (English → Norwegian bokmål, no
/// internet fallback); "Ready" appears only after the packaged model translated the probe sentence; online services, LibreTranslate/Podman, extra
/// languages and the glossary live under Advanced; a save sends the choices and a key only when typed (never shown again); Test translation names
/// the translator that answered; without permission nothing is editable.
/// </summary>
public sealed class TranslationSettingsCardTests : BunitContext
{
    private readonly Mock<ITranslationSettingsApiService> _api = new();
    private SaveTranslationSettingsRequest? _saved;
    private readonly List<string> _actions = [];
    private int _probes;

    private static readonly OfflineTranslationStatus Installed = new()
    {
        State = "Installed", Summary = "Offline translation — installed, not started yet", Model = "HPLT/translate-en-nb-v2.0-hplt (revision 24a48dba7fa6)",
        ModelLicense = "CC-BY-4.0", Engine = "CTranslate2 4.9.0, SentencePiece 0.2.3, CPU", Languages = "English → Norwegian (bokmål)", Attribution = "HPLT MT release v2.0",
    };

    private static readonly OfflineTranslationStatus Ready = Installed with
    {
        State = "Ready", Summary = "Offline translation — Ready", ProbeSource = "Open the page, fill in all required fields and select Save.",
        ProbeResult = "Åpne siden, fyll ut alle obligatoriske felt og velg Lagre.", ProbeSeconds = 1.4,
    };

    private static TranslationSettingsView View(TranslationSettings? settings = null, bool keySet = false, bool canWrite = true, OfflineTranslationStatus? offline = null) => new()
    {
        Settings = settings ?? new TranslationSettings { Provider = TranslationProviderKind.EmbeddedOffline }, AzureKeySet = keySet, CanWrite = canWrite, CanManageLibreTranslate = true, Origin = "default",
        Availability = new(true, "BirkNext offline translator (HPLT/translate-en-nb-v2.0-hplt)", "Offline translation on this PC by the model packaged with BirkNext; nothing leaves this PC."),
        Offline = offline ?? Installed,
    };

    private IRenderedComponent<TranslationSettingsCard> Render(TranslationSettingsView view, LibreTranslateServiceStatus? libre = null, OfflineTranslationStatus? probe = null)
    {
        _api.Setup(a => a.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync((view, (string?)null));
        _api.Setup(a => a.SaveAsync(It.IsAny<SaveTranslationSettingsRequest>(), It.IsAny<CancellationToken>()))
            .Callback<SaveTranslationSettingsRequest, CancellationToken>((r, _) => _saved = r)
            .ReturnsAsync((SaveTranslationSettingsRequest r, CancellationToken _) => (view with { Settings = r.Settings, Origin = "saved", AzureKeySet = r.AzureKey is { Length: > 0 } || view.AzureKeySet }, (string?)null));
        _api.Setup(a => a.OfflineAsync(It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).Callback(() => _probes++).ReturnsAsync((probe ?? Ready, (string?)null));
        _api.Setup(a => a.LibreTranslateAsync(It.IsAny<CancellationToken>())).ReturnsAsync((libre ?? new LibreTranslateServiceStatus { PodmanAvailable = true, PodmanDetail = "Podman 5.4.2",
            Url = "http://127.0.0.1:5050", Detail = "The image is not downloaded yet: choose Download image (needs internet once)." }, (string?)null));
        _api.Setup(a => a.LibreTranslateActionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Callback<string, CancellationToken>((a, _) => _actions.Add(a)).ReturnsAsync("Done.");
        _api.Setup(a => a.TestAsync(It.IsAny<CancellationToken>())).ReturnsAsync((new TranslationTestResult(true, "BirkNext offline translator", "Prepare a child … (FR-027).",
            "Klargjør et barn … (FR-027).", "Translated by BirkNext offline translator (English → Norwegian (bokmål))."), (string?)null));
        Services.AddSingleton(_api.Object);
        var cut = Render<TranslationSettingsCard>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=trx-settings]"));
        return cut;
    }

    /// <summary>The selected translator card's value.</summary>
    private static string? Chosen(IRenderedComponent<TranslationSettingsCard> cut) => cut.Find("[data-testid=trx-provider] input:checked").GetAttribute("value");

    [Fact]
    public void LibreTranslate_is_shown_in_the_normal_view_with_the_offline_translator_as_fallback()
    {
        // LibreTranslate's server controls are in the normal view, not under Advanced. Since 2026-11-23 the offline translator is the default.
        var cut = Render(View(new TranslationSettings { Provider = TranslationProviderKind.LibreTranslate }));
        Chosen(cut).Should().Be("LibreTranslate");
        cut.Find("[data-testid=trx-provider-option]").GetAttribute("data-value").Should().Be("EmbeddedOffline", "the default is listed first");
        cut.Find("[data-testid=trx-libre]").Should().NotBeNull();
        cut.Find("[data-testid=trx-advanced]").QuerySelector("[data-testid=trx-libre]").Should().BeNull("not hidden under Advanced");
        cut.Find("[data-testid=trx-advanced]").HasAttribute("open").Should().BeFalse("LibreTranslate is a standard choice");
        cut.Find("[data-testid=trx-fallback]").HasAttribute("checked").Should().BeTrue();
        cut.Find("[data-testid=trx-internet]").TextContent.Should().Contain("offline translator packaged with BirkNext is used");
        cut.FindAll("[data-testid=trx-provider-option][data-online]").Should().BeEmpty();
        _probes.Should().Be(0, "the offline model is not started while LibreTranslate is chosen");
    }

    [Fact]
    public void The_header_says_Ready_for_LibreTranslate_only_when_the_server_answers_and_an_unsaved_choice_is_not_shown_as_the_saved_one()
    {
        var notInstalled = Installed with { State = "NotInstalled", Summary = "Offline translation — not available" };
        var cut = Render(View(new TranslationSettings { Provider = TranslationProviderKind.LibreTranslate }, offline: notInstalled),
            libre: new LibreTranslateServiceStatus { PodmanAvailable = true, ImagePresent = true, Url = "http://127.0.0.1:5050" });
        cut.WaitForAssertion(() => cut.Find("[data-testid=trx-overall]").TextContent.Should().Be("LibreTranslate not running — starts when a translation needs it"));
        cut.Find("[data-testid=trx-internet]").TextContent.Should().Contain("offline fallback is not installed");
        cut.Find($"[data-testid=trx-provider-option][data-value={TranslationProviderKind.EmbeddedOffline}]").TextContent.Should().Contain("Not installed here");

        cut.Find($"[data-testid=trx-provider-option][data-value={TranslationProviderKind.EmbeddedOffline}] input").Change(true);
        cut.Find("[data-testid=trx-overall]").TextContent.Should().Be("Not saved — Save to use this translator", "the header never mixes the saved translator with an unsaved choice");
    }

    [Fact]
    public void The_header_says_Ready_for_LibreTranslate_when_the_server_answers()
    {
        var cut = Render(View(new TranslationSettings { Provider = TranslationProviderKind.LibreTranslate }), libre: new LibreTranslateServiceStatus { PodmanAvailable = true, ImagePresent = true, Running = true, Answering = true });
        cut.WaitForAssertion(() => cut.Find("[data-testid=trx-overall]").TextContent.Should().Be("Ready: LibreTranslate (local server answering)"));
    }

    [Fact]
    public void The_offline_translator_is_the_default_and_how_long_it_stays_loaded_can_be_chosen_including_never()
    {
        var cut = Render(View(new TranslationSettings()));
        Chosen(cut).Should().Be("EmbeddedOffline");
        cut.Find($"[data-testid=trx-provider-option][data-value={TranslationProviderKind.EmbeddedOffline}]").TextContent.Should().Contain("Default");
        var idle = cut.Find("[data-testid=trx-idle-stop]");
        idle.QuerySelectorAll("input").Select(o => o.GetAttribute("value")).Should().Equal("10", "30", "60", "240", "480", "0");
        idle.QuerySelector("input[value='0']")!.GetAttribute("aria-label").Should().Contain("never stop");
        idle.QuerySelector("input[value='60']")!.GetAttribute("aria-label").Should().Contain("(default)");
        idle.QuerySelector("input[value='60']")!.HasAttribute("checked").Should().BeTrue();
        cut.Find("[data-testid=trx-idle-stop] input[value='0']").Change(true);
        cut.Find("[data-testid=trx-idle-stop] input[value='0']").HasAttribute("checked").Should().BeTrue();
        cut.Find("[data-testid=trx-save]").HasAttribute("disabled").Should().BeFalse("a changed choice can be saved");
    }

    [Fact]
    public void The_offline_translator_is_English_to_Norwegian_and_Ready_only_after_the_probe_translated()
    {
        var cut = Render(View());
        Chosen(cut).Should().Be("EmbeddedOffline");
        cut.Find("[data-testid=trx-source] option[selected]").GetAttribute("value").Should().Be("en");
        cut.Find("[data-testid=trx-to] option[selected]").GetAttribute("value").Should().Be("nb");
        cut.Find("[data-testid=trx-internet]").TextContent.Should().Contain("never");
        cut.WaitForAssertion(() => cut.Find("[data-testid=trx-offline]").GetAttribute("data-state").Should().Be("Ready"));
        _probes.Should().Be(1, "opening the page checks the packaged model once, for real");
        cut.Find("[data-testid=trx-overall]").TextContent.Should().Be("Offline translation — Ready");
        cut.Find("[data-testid=trx-offline-probe]").TextContent.Should().Contain("velg Lagre");
        cut.Find("[data-testid=trx-offline-model]").TextContent.Should().Contain("HPLT").And.Contain("CC-BY-4.0");
        cut.FindAll("[data-testid=trx-azure-key]").Should().BeEmpty("online services are optional, under Advanced");
        cut.FindAll("[data-testid=trx-libre]").Should().BeEmpty("no Podman controls in the normal view");
        cut.FindAll("[data-testid=trx-provider-option][data-online]").Should().BeEmpty();
        cut.Find("[data-testid=trx-origin]").TextContent.Should().Be("Default settings (not saved yet)");
        cut.Find("[data-testid=trx-save]").HasAttribute("disabled").Should().BeTrue("nothing changed yet");
    }

    [Fact]
    public void A_failing_probe_is_never_shown_as_Ready()
    {
        var failed = Installed with { State = "Failed", Summary = "Offline translation — not working", Detail = "The offline translation model could not be loaded: …" };
        var cut = Render(View(), probe: failed);
        cut.WaitForAssertion(() => cut.Find("[data-testid=trx-offline]").GetAttribute("data-state").Should().Be("Failed"));
        cut.Find("[data-testid=trx-overall]").TextContent.Should().Be("Offline translation — not available");
        cut.Find("[data-testid=trx-offline-detail]").TextContent.Should().Contain("could not be loaded");
    }

    [Fact]
    public void A_missing_model_is_not_started_and_says_so()
    {
        var missing = new OfflineTranslationStatus { State = "NotInstalled", Summary = "Offline translation — not available", Detail = "The offline translation model is not installed with this BirkNext." };
        var none = Render(View(offline: missing));
        _probes.Should().Be(0, "a missing package is not started");
        none.Find("[data-testid=trx-offline-state]").TextContent.Should().Be("Offline translation — not available");
    }

    [Fact]
    public void Advanced_is_opened_and_closed_by_its_summary_only_so_it_never_flickers()
    {
        // Regression: an @ontoggle handler flipped the state each time Blazor set "open", so with Azure saved the section opened and closed forever.
        var cut = Render(View(new TranslationSettings { Provider = TranslationProviderKind.AzureTranslator }));
        var details = () => cut.Find("[data-testid=trx-advanced]");
        details().HasAttribute("open").Should().BeTrue();
        details().GetAttribute("blazor:ontoggle").Should().BeNull("the browser's toggle event must not drive the state");
        cut.Find("[data-testid=trx-advanced-summary]").Click();
        details().HasAttribute("open").Should().BeFalse();
        cut.Find("[data-testid=trx-advanced-summary]").Click();
        details().HasAttribute("open").Should().BeTrue();
    }

    [Fact]
    public void Azure_under_Advanced_saves_the_choices_and_the_key_only_when_typed_and_the_key_is_never_shown_again()
    {
        var cut = Render(View(new TranslationSettings { Provider = TranslationProviderKind.AzureTranslator }));
        cut.Find("[data-testid=trx-advanced]").HasAttribute("open").Should().BeTrue("a saved online choice opens Advanced");
        cut.Find("[data-testid=trx-azure-key]").Input("0123456789abcdef0123456789abcdef");
        cut.Find("[data-testid=trx-azure-region]").Input("norwayeast");
        cut.Find("[data-testid=trx-target][data-language=sv]").Change(true);
        cut.Find("[data-testid=trx-save]").Click();

        cut.WaitForAssertion(() => _saved.Should().NotBeNull());
        _saved!.AzureKey.Should().Be("0123456789abcdef0123456789abcdef");
        _saved.Settings.Should().Match<TranslationSettings>(s => s.Provider == TranslationProviderKind.AzureTranslator && s.AzureRegion == "norwayeast" && s.SourceLanguage == "en");
        _saved.Settings.TargetLanguages.Should().Equal("nb", "sv");
        cut.WaitForAssertion(() => cut.Find("[data-testid=trx-message]").TextContent.Should().Contain("no restart needed"));
        cut.Find("[data-testid=trx-azure-key]").GetAttribute("value").Should().BeNullOrEmpty("the key is never shown again");
        cut.Find("[data-testid=trx-azure-key]").GetAttribute("placeholder").Should().Be("••••••••");

        _saved = null;
        cut.Find("[data-testid=trx-azure-region]").Input("westeurope");
        cut.Find("[data-testid=trx-save]").Click();
        cut.WaitForAssertion(() => _saved.Should().NotBeNull());
        _saved!.AzureKey.Should().BeNull("an untouched key field keeps the stored key");
    }

    [Fact]
    public void Glossary_terms_can_be_added_changed_and_removed_and_are_saved_with_the_settings()
    {
        var cut = Render(View());
        var before = cut.FindAll("[data-testid=trx-gloss-row]").Count;
        before.Should().Be(TranslationGlossary.DefaultEnNb.Count);
        cut.Find("[data-testid=trx-gloss-add]").Click();
        cut.FindAll("[data-testid=trx-gloss-source]")[^1].Change("municipality");
        cut.FindAll("[data-testid=trx-gloss-target]")[^1].Change("kommune");
        cut.FindAll("[data-testid=trx-gloss-remove]")[0].Click();
        cut.Find("[data-testid=trx-save]").Click();

        cut.WaitForAssertion(() => _saved.Should().NotBeNull());
        _saved!.Settings.Glossary.Should().HaveCount(before).And.Contain(new TranslationGlossaryEntry("municipality", "kommune"))
            .And.NotContain(TranslationGlossary.DefaultEnNb[0]);
    }

    [Fact]
    public void Without_an_image_the_local_server_offers_only_the_download()
    {
        var cut = Render(View(new TranslationSettings { Provider = TranslationProviderKind.LibreTranslate }));
        cut.Find("[data-testid=trx-libre-image-state]").TextContent.Should().Be("Not downloaded");
        cut.FindAll("[data-testid=trx-libre-start]").Should().BeEmpty("there is no image to start yet");
        cut.Find("[data-testid=trx-libre-pull]").Click();
        cut.WaitForAssertion(() => _actions.Should().Equal("pull"));
    }

    [Fact]
    public void A_running_server_offering_the_pair_says_so_and_a_changed_port_waits_for_a_save_before_stop_or_start()
    {
        var running = Render(View(new TranslationSettings { Provider = TranslationProviderKind.LibreTranslate }), new LibreTranslateServiceStatus
        {
            PodmanAvailable = true, ImagePresent = true, Running = true, Answering = true, PairAvailable = true, Languages = ["en", "nb"], Url = "http://127.0.0.1:5050",
            Detail = "Running on http://127.0.0.1:5050 — translates English → Norwegian (bokmål) offline.",
        });
        running.Find("[data-testid=trx-libre-pair]").TextContent.Should().Be("The From → To pair is offered");
        running.Find("[data-testid=trx-libre-port]").Change("5060");
        running.FindAll("[data-testid=trx-libre-stop]").Should().BeEmpty("start/stop use the saved settings: save first");
    }

    [Fact]
    public void Test_translation_names_the_translator_that_answered()
    {
        var cut = Render(View());
        cut.Find("[data-testid=trx-test]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=trx-test-result]").TextContent.Should().Contain("Translated by BirkNext offline translator"));
        cut.Find("[data-testid=trx-test-text]").TextContent.Should().Contain("(FR-027)", "identifiers are kept");
    }

    [Fact]
    public void Without_permission_the_settings_are_read_only()
    {
        var cut = Render(View(canWrite: false));
        cut.Find("[data-testid=trx-settings]").HasAttribute("disabled").Should().BeTrue();
        cut.FindAll("[data-testid=trx-save]").Should().BeEmpty();
        cut.Find("[data-testid=trx-read-only]").TextContent.Should().Contain("system-configuration permission");
    }
}
