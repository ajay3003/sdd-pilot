using BirkNext.TestPlanning;
using BirkNext.Web.Components.Admin;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// System Settings → Translation: the default (Azure AI Translator, English → Norwegian bokmål, offline fallback to the local LibreTranslate
/// server) is shown as such; a save sends the choices and a key only when typed (the key is never shown); the local server's actions follow its
/// status (download → start → stop, never before saving); Test translation names the service that answered; without permission nothing is editable.
/// </summary>
public sealed class TranslationSettingsCardTests : BunitContext
{
    private readonly Mock<ITranslationSettingsApiService> _api = new();
    private SaveTranslationSettingsRequest? _saved;
    private readonly List<string> _actions = [];

    private static TranslationSettingsView View(TranslationSettings? settings = null, bool keySet = false, bool canWrite = true, bool available = false) => new()
    {
        Settings = settings ?? new TranslationSettings(), AzureKeySet = keySet, CanWrite = canWrite, CanManageLibreTranslate = true, Origin = "default",
        Availability = available ? new(true, "Azure AI Translator (norwayeast)", "Translation by Azure AI Translator.")
            : new(true, "LibreTranslate (local, offline)", "Azure AI Translator has no key yet (System Settings → Translation). The local LibreTranslate server is used instead (offline), if it is running."),
    };

    private IRenderedComponent<TranslationSettingsCard> Render(TranslationSettingsView view, LibreTranslateServiceStatus? libre = null)
    {
        _api.Setup(a => a.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync((view, (string?)null));
        _api.Setup(a => a.SaveAsync(It.IsAny<SaveTranslationSettingsRequest>(), It.IsAny<CancellationToken>()))
            .Callback<SaveTranslationSettingsRequest, CancellationToken>((r, _) => _saved = r)
            .ReturnsAsync((SaveTranslationSettingsRequest r, CancellationToken _) => (view with { Settings = r.Settings, Origin = "saved", AzureKeySet = r.AzureKey is { Length: > 0 } || view.AzureKeySet }, (string?)null));
        _api.Setup(a => a.LibreTranslateAsync(It.IsAny<CancellationToken>())).ReturnsAsync((libre ?? new LibreTranslateServiceStatus { PodmanAvailable = true, PodmanDetail = "Podman 5.4.2",
            Url = "http://127.0.0.1:5050", Detail = "The image is not downloaded yet: choose Download image (needs internet once)." }, (string?)null));
        _api.Setup(a => a.LibreTranslateActionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Callback<string, CancellationToken>((a, _) => _actions.Add(a)).ReturnsAsync("Done.");
        _api.Setup(a => a.TestAsync(It.IsAny<CancellationToken>())).ReturnsAsync((new TranslationTestResult(true, "LibreTranslate (local, offline)", "Prepare a child … (FR-027).",
            "Klargjør et barn … (FR-027).", "Translated by LibreTranslate (local, offline) (English → Norwegian (bokmål))."), (string?)null));
        Services.AddSingleton(_api.Object);
        var cut = Render<TranslationSettingsCard>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=trx-settings]"));
        return cut;
    }

    [Fact]
    public void The_default_is_Azure_English_to_Norwegian_bokmal_with_the_offline_fallback()
    {
        var cut = Render(View());
        cut.Find("[data-testid=trx-provider] option[selected]").GetAttribute("value").Should().Be("AzureTranslator");
        cut.Find("[data-testid=trx-source] option[selected]").GetAttribute("value").Should().Be("en");
        cut.FindAll("[data-testid=trx-target]").Where(t => t.HasAttribute("checked")).Select(t => t.GetAttribute("data-language")).Should().Equal("nb");
        cut.Find("[data-testid=trx-fallback]").HasAttribute("checked").Should().BeTrue();
        cut.Find("[data-testid=trx-availability]").TextContent.Should().Contain("has no key yet").And.Contain("local LibreTranslate server is used instead");
        cut.Find("[data-testid=trx-azure-key]").GetAttribute("type").Should().Be("password");
        cut.Find("[data-testid=trx-origin]").TextContent.Should().Be("Default settings (not saved yet)");
        cut.Find("[data-testid=trx-save]").HasAttribute("disabled").Should().BeTrue("nothing changed yet");
    }

    [Fact]
    public void A_save_sends_the_choices_and_the_key_only_when_typed_and_the_key_is_never_shown_again()
    {
        var cut = Render(View());
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
    public void Test_translation_names_the_service_that_answered()
    {
        var cut = Render(View());
        cut.Find("[data-testid=trx-test]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=trx-test-result]").TextContent.Should().Contain("Translated by LibreTranslate (local, offline)"));
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
