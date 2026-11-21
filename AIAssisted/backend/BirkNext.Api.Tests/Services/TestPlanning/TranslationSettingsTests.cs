using System.Net;
using System.Text;
using BirkNext.Api.Services.PerformanceTests;
using BirkNext.Api.Services.TestPlanning;
using BirkNext.TestPlanning;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BirkNext.Api.Tests.Services.TestPlanning;

/// <summary>
/// System Settings → Translation: the defaults (Azure AI Translator, English → Norwegian bokmål, offline fallback), the server configuration
/// until settings are saved, validation, the Azure key stored encrypted (never in the settings file, removable), a change applying without a
/// restart, and the local LibreTranslate server BirkNext runs with Podman (exact arguments: 127.0.0.1 only, model volume, only the needed
/// languages; status fact by fact; no Podman in the Tester Package). A fake process runner and HTTP handler: nothing is started or called.
/// </summary>
public sealed class TranslationSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "trs-" + Guid.NewGuid().ToString("N")[..8]);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request));
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Runner(Func<IReadOnlyList<string>, ProcessOutcome> answer) : IProcessRunner
    {
        public List<string> Commands { get; } = [];
        public Task<ProcessOutcome> RunAsync(ProcessSpec spec, TimeSpan timeout, int maxOutputBytes, CancellationToken ct)
        {
            Commands.Add(string.Join(' ', spec.Arguments));
            return Task.FromResult(answer(spec.Arguments));
        }
    }

    private static ProcessOutcome Ok(string output = "") => new(0, output, "", false, false, null);
    private static ProcessOutcome No => new(1, "", "no such object", false, false, null);

    [Fact]
    public void Defaults_then_server_configuration_then_saved_settings_apply_and_a_change_needs_no_restart()
    {
        var empty = new TranslationSettingsStore(_dir, Config());
        empty.Effective().Should().Match<(TranslationSettings Settings, string Origin, string?, DateTimeOffset?)>(e =>
            e.Origin == "default" && e.Settings.Provider == TranslationProviderKind.LibreTranslate && e.Settings.SourceLanguage == "en" && e.Settings.TargetLanguages.Single() == "nb");

        var configured = new TranslationSettingsStore(Path.Combine(_dir, "c"), Config(("ManualTesting:Translation:Provider", "LibreTranslate"), ("ManualTesting:Translation:LibreTranslate:Url", "http://127.0.0.1:6000")));
        configured.Effective().Origin.Should().Be("configuration");

        var resolver = new TestCaseTranslatorResolver(configured, Config(), new Factory(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK))));
        resolver.Current().Should().BeOfType<LibreTranslateTestCaseTranslator>();
        configured.Save(new SaveTranslationSettingsRequest(new TranslationSettings { Provider = TranslationProviderKind.Off }), "local-session:loopback", DateTimeOffset.UnixEpoch).Should().BeNull();
        configured.Effective().Should().Match<(TranslationSettings Settings, string Origin, string? By, DateTimeOffset?)>(e => e.Origin == "saved" && e.By == "local-session:loopback");
        resolver.Current().Availability.Reason.Should().Be(TestCaseTranslators.NotEnabled, "the next request uses the saved settings");
    }

    [Fact]
    public void The_Azure_key_is_stored_encrypted_never_in_the_settings_file_and_can_be_removed()
    {
        var store = new TranslationSettingsStore(_dir, Config());
        const string key = "0123456789abcdef0123456789abcdef";
        store.Save(new SaveTranslationSettingsRequest(new TranslationSettings { AzureRegion = "norwayeast" }, key), "a", DateTimeOffset.UnixEpoch).Should().BeNull();

        store.AzureKeySet.Should().BeTrue();
        store.AzureKey().Should().Be(key);
        foreach (var file in Directory.GetFiles(_dir, "*", SearchOption.AllDirectories)) File.ReadAllText(file).Should().NotContain(key, $"{Path.GetFileName(file)} never holds the key in clear text");
        new TranslationSettingsStore(_dir, Config()).AzureKey().Should().Be(key, "another instance (a restart) reads it back");

        store.Save(new SaveTranslationSettingsRequest(new TranslationSettings(), AzureKey: null), "a", DateTimeOffset.UnixEpoch).Should().BeNull();
        store.AzureKeySet.Should().BeTrue("null keeps the stored key");
        store.Save(new SaveTranslationSettingsRequest(new TranslationSettings(), AzureKey: ""), "a", DateTimeOffset.UnixEpoch).Should().BeNull();
        store.AzureKeySet.Should().BeFalse("an empty key removes it");
        store.Save(new SaveTranslationSettingsRequest(new TranslationSettings(), "short"), "a", DateTimeOffset.UnixEpoch).Should().Contain("does not look like");
    }

    [Theory]
    [InlineData("xx yy", "nb", null, 5050, "not a language code")]
    [InlineData("en", "en", null, 5050, "same as the \"From\"")]
    [InlineData("en", "", null, 5050, "at least one")]
    [InlineData("en", "nb", "west europe", 5050, "region")]
    [InlineData("en", "nb", null, 5000, "not 5000 or 5173")]
    public void Settings_are_validated(string source, string target, string? region, int port, string problem)
    {
        var settings = new TranslationSettings { SourceLanguage = source, TargetLanguages = target.Length == 0 ? [] : [target], AzureRegion = region, LibreTranslatePort = port };
        TranslationSettingsStore.Validate(settings).Problem.Should().Contain(problem);
        TranslationSettingsStore.Validate(new TranslationSettings { LibreTranslateImage = "docker.io/libretranslate/libretranslate:latest" }).Problem.Should().Contain("pinned");
        TranslationSettingsStore.Validate(new TranslationSettings { LibreTranslateManaged = false, LibreTranslateUrl = "" }).Problem.Should().Contain("address");
        TranslationSettingsStore.Validate(new TranslationSettings { SourceLanguage = "auto", TargetLanguages = ["nb", "nn", "nb"] }).Settings!.TargetLanguages.Should().Equal("nb", "nn");
    }

    // ── Local LibreTranslate server ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_local_server_listens_on_127_0_0_1_only_keeps_its_models_and_loads_only_the_needed_languages()
    {
        var args = LibreTranslateServiceManager.RunArguments(new TranslationSettings { TargetLanguages = ["nb", "nn"], LibreTranslatePort = 5055 });
        string.Join(' ', args).Should().Be("run --detach --name birknext-libretranslate --pull=never --label birknext.managed=true --label birknext.component=translation "
            + "--publish 127.0.0.1:5055:5000 --volume birknext-libretranslate-models:/home/libretranslate/.local --env LT_LOAD_ONLY=en,nb,nn docker.io/libretranslate/libretranslate:v1.6.0");
        string.Join(' ', LibreTranslateServiceManager.RunArguments(new TranslationSettings { SourceLanguage = "auto" })).Should().Contain("LT_LOAD_ONLY=en,nb");
    }

    [Fact]
    public async Task Status_says_fact_by_fact_what_is_missing_and_never_starts_anything()
    {
        var runner = new Runner(a => a[0] switch { "info" => Ok("5.4.2"), "image" => No, _ => No });
        var manager = new LibreTranslateServiceManager(runner, new PerformanceTestOptions(), new Factory(new Handler(_ => throw new HttpRequestException("refused"))), NullLogger<LibreTranslateServiceManager>.Instance);

        var status = await manager.StatusAsync(new TranslationSettings(), default);

        status.Should().Match<LibreTranslateServiceStatus>(s => s.PodmanAvailable && !s.ImagePresent && !s.Running && !s.Answering && s.Url == "http://127.0.0.1:5050");
        status.Detail.Should().Contain("Download image");
        runner.Commands.Should().NotContain(c => c.StartsWith("run", StringComparison.Ordinal) || c.StartsWith("pull", StringComparison.Ordinal), "status never downloads or starts");
    }

    [Fact]
    public async Task A_running_server_that_offers_the_pair_is_ready_and_one_without_it_says_so()
    {
        var runner = new Runner(a => a[0] switch { "info" => Ok("5.4.2"), "image" => Ok(), "container" => Ok("true"), _ => No });
        HttpResponseMessage Languages(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        var ready = new LibreTranslateServiceManager(runner, new PerformanceTestOptions(),
            new Factory(new Handler(_ => Languages("""[{"code":"en","name":"English","targets":["en","nb"]},{"code":"nb","name":"Norwegian","targets":["en","nb"]}]"""))), NullLogger<LibreTranslateServiceManager>.Instance);
        var status = await ready.StatusAsync(new TranslationSettings(), default);
        status.Should().Match<LibreTranslateServiceStatus>(s => s.Running && s.Answering && s.PairAvailable);
        status.Detail.Should().Contain("translates English → Norwegian (bokmål) offline");

        var noNorwegian = new LibreTranslateServiceManager(runner, new PerformanceTestOptions(),
            new Factory(new Handler(_ => Languages("""[{"code":"en","name":"English","targets":["en","de"]}]"""))), NullLogger<LibreTranslateServiceManager>.Instance);
        (await noNorwegian.StatusAsync(new TranslationSettings(), default)).Should().Match<LibreTranslateServiceStatus>(s => s.Answering && !s.PairAvailable);
    }

    [Fact]
    public async Task Without_Podman_in_the_Tester_Package_the_local_server_is_not_offered()
    {
        var runner = new Runner(_ => throw new InvalidOperationException("no process expected"));
        var manager = new LibreTranslateServiceManager(runner, new PerformanceTestOptions { Runner = PerformanceTestOptions.Runners.Native },
            new Factory(new Handler(_ => throw new HttpRequestException("refused"))), NullLogger<LibreTranslateServiceManager>.Instance);
        manager.CanManage.Should().BeFalse();
        (await manager.StartAsync(new TranslationSettings())).Should().Be(LibreTranslateServiceManager.NativeRunner);
        (await manager.StatusAsync(new TranslationSettings(), default)).Detail.Should().Be(LibreTranslateServiceManager.NativeRunner);
    }

    [Fact]
    public async Task Start_replaces_BirkNexts_own_container_and_needs_the_downloaded_image()
    {
        var runner = new Runner(a => a[0] switch { "image" => Ok(), _ => Ok() });
        var manager = new LibreTranslateServiceManager(runner, new PerformanceTestOptions(), new Factory(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK))), NullLogger<LibreTranslateServiceManager>.Instance);
        (await manager.StartAsync(new TranslationSettings())).Should().StartWith("Started on http://127.0.0.1:5050");
        runner.Commands.Should().Equal("image exists docker.io/libretranslate/libretranslate:v1.6.0", "rm --force --ignore birknext-libretranslate",
            string.Join(' ', LibreTranslateServiceManager.RunArguments(new TranslationSettings())));

        var missing = new LibreTranslateServiceManager(new Runner(_ => No), new PerformanceTestOptions(), new Factory(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK))), NullLogger<LibreTranslateServiceManager>.Instance);
        (await missing.StartAsync(new TranslationSettings())).Should().Be("Download the image first.");
    }
}
