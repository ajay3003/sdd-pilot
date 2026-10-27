using System.Net;
using System.Net.Http.Json;
using System.Text;
using BirkNext.Integrations;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Xunit;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Never-saved built-in Target Environments are stored (exactly as seeded) before an operation that declares it attaches backend-owned
/// state to one of them, so External Integrations, IQR data and Altinn settings (keyed by environment id alone) survive a reload. Reads,
/// previews and undeclared POSTs store nothing; seed ids stay random per browser, never fixed or shared.
/// </summary>
public sealed class TargetEnvironmentMaterializationTests
{
    /// <summary>Browser storage for one browser profile/workspace: survives "reloads" (new service instances).</summary>
    private sealed class FakeStorage : IJSRuntime
    {
        public string? Item;
        public int Writes;
        public bool FailWrites;

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            switch (identifier)
            {
                case "birkNextStorage.getItem": return ValueTask.FromResult((TValue)(object?)Item!);
                case "birkNextStorage.setItem":
                    if (FailWrites) throw new JSException("storage refused");
                    Item = (string?)args![1]; Writes++;
                    return ValueTask.FromResult(default(TValue)!);
                default: return ValueTask.FromResult(default(TValue)!);
            }
        }
    }

    /// <summary>A backend that keys every write by its environmentId query value, like the real environment-keyed tables.</summary>
    private sealed class FakeBackend : HttpMessageHandler
    {
        public readonly List<(string Environment, string Path)> Records = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
            var environment = query["environmentId"] ?? "";
            var path = request.RequestUri.AbsolutePath;
            if (request.Method != HttpMethod.Get && !Records.Contains((environment, path))) Records.Add((environment, path));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = request.Method == HttpMethod.Get
                    ? JsonContent.Create(Records.Where(r => r.Environment == environment).Select(r => r.Path).ToList())
                    : new StringContent("{}", Encoding.UTF8, "application/json"),
            });
        }
    }

    private static async Task<FrontendAnalysisSettingsService> LoadAsync(FakeStorage storage)
    {
        var service = new FrontendAnalysisSettingsService();
        await service.LoadAsync(storage);
        return service;
    }

    private static HttpClient Client(IFrontendAnalysisSettingsService settings, IJSRuntime js, FakeBackend backend) =>
        new(new TargetEnvironmentMaterializationHandler(settings, js) { InnerHandler = backend }) { BaseAddress = new Uri("http://backend.test/") };

    private static string Id(IFrontendAnalysisSettingsService settings, string name) => settings.Settings.Profiles.Single(p => p.Name == name).Id;

    private static ExternalIntegrationRequest Integration() => new("Altinn", ExternalProviderType.Altinn, "TT02", true, null);

    [Fact]
    public async Task Unsaved_seed_profiles_are_not_stored_until_configuration_is_attached()
    {
        var storage = new FakeStorage();
        var first = await LoadAsync(storage);
        first.IsMaterialized.Should().BeFalse();

        var reload = await LoadAsync(storage);

        storage.Writes.Should().Be(0, "opening the defaults alone must not save them");
        Id(reload, "Development").Should().NotBe(Id(first, "Development"), "nothing was attached, so nothing can be orphaned");
    }

    [Fact]
    public async Task External_integration_IQR_and_Altinn_data_attached_to_an_unsaved_default_survive_a_reload()
    {
        var storage = new FakeStorage();
        var backend = new FakeBackend();
        var settings = await LoadAsync(storage);
        var development = Id(settings, "Development");
        var http = Client(settings, storage, backend);

        (await new ExternalIntegrationsApiService(http).SaveAsync(development, null, Integration())).Error.Should().BeNull();
        settings.IsMaterialized.Should().BeTrue();
        (await new IntegrationCatalogApiService(Client(settings, storage, backend)).SetEvidenceBindingAsync(development, "altinn", Guid.NewGuid())).Error.Should().BeNull();
        (await new AltinnEnvironmentApiService(http).SaveAsync(development, new AltinnEnvironmentSettingsUpdate())).Error.Should().BeNull();

        var reload = await LoadAsync(storage);
        var reopened = Id(reload, "Development");

        reopened.Should().Be(development);
        reload.Settings.Profiles.Select(p => p.Id).Should().Equal(settings.Settings.Profiles.Select(p => p.Id), "every default keeps its id, none is re-created");
        var attached = await Client(reload, storage, backend).GetFromJsonAsync<List<string>>($"api/anything?environmentId={reopened}");
        attached.Should().BeEquivalentTo([
            "/api/target-environments/external-integrations", "/api/integration-review/evidence-bindings", "/api/target-environments/altinn/settings"]);
        backend.Records.Should().OnlyContain(r => r.Environment == development, "no record points at an id that no longer exists");
        storage.Writes.Should().Be(1, "the defaults are stored once, on the first attach");
    }

    [Fact]
    public async Task Previews_detection_and_undeclared_posts_never_store_the_defaults()
    {
        var storage = new FakeStorage();
        var settings = await LoadAsync(storage);
        var development = Id(settings, "Development");
        var http = Client(settings, storage, new FakeBackend());
        var externalIntegrations = new ExternalIntegrationsApiService(http);

        await externalIntegrations.PreflightAsync(development, new SetupPreflightRequest("{}"));
        await externalIntegrations.DetectAsync(development);
        // A read-like POST (such as a GraphQL query) that names the environment but declares nothing.
        await http.PostAsJsonAsync("graphql", new { query = "{ x }", variables = new { environmentId = development } });

        settings.IsMaterialized.Should().BeFalse();
        storage.Writes.Should().Be(0);
    }

    [Fact]
    public async Task Only_the_canonical_defaults_are_stored_never_an_unsaved_new_profile_or_unsaved_edit()
    {
        var storage = new FakeStorage();
        var settings = await LoadAsync(storage);
        var development = settings.Settings.Profiles.Single(p => p.Name == "Development");
        var unsavedNew = settings.CreateProfile("Unsaved new", FrontendEnvironmentType.Test);
        development.TargetUrl = "https://unsaved-edit.example.test";
        var http = Client(settings, storage, new FakeBackend());

        await new ExternalIntegrationsApiService(http).SaveAsync(unsavedNew.Id, null, Integration());
        settings.IsMaterialized.Should().BeFalse("an unsaved user profile is not a built-in default");

        await new ExternalIntegrationsApiService(http).SaveAsync(development.Id, null, Integration());

        var reload = await LoadAsync(storage);
        reload.Settings.Profiles.Select(p => p.Name).Should().Equal("Local", "Development", "QA", "Production");
        reload.Settings.Profiles.Single(p => p.Id == development.Id).TargetUrl.Should().NotBe("https://unsaved-edit.example.test");
        settings.Settings.Profiles.Should().Contain(unsavedNew, "the in-memory draft is left as it was");
    }

    [Fact]
    public async Task Different_workspaces_never_share_default_ids()
    {
        var one = await LoadAsync(new FakeStorage());
        var other = await LoadAsync(new FakeStorage());

        one.Settings.Profiles.Select(p => p.Id).Should().NotIntersectWith(other.Settings.Profiles.Select(p => p.Id));
    }

    [Fact]
    public async Task A_saved_environment_keeps_its_id_and_is_not_rewritten()
    {
        var storage = new FakeStorage { Item = """{"activeProfileId":"saved-dev","profiles":[{"id":"saved-dev","name":"Development","environmentType":"Development"}]}""" };
        var settings = await LoadAsync(storage);
        settings.IsMaterialized.Should().BeTrue();

        (await new ExternalIntegrationsApiService(Client(settings, storage, new FakeBackend())).SaveAsync("saved-dev", null, Integration())).Error.Should().BeNull();

        storage.Writes.Should().Be(0, "an already saved environment is never migrated or re-saved");
        Id(await LoadAsync(storage), "Development").Should().Be("saved-dev");
    }

    [Fact]
    public async Task Duplicate_gets_a_new_id_and_rename_keeps_the_id()
    {
        var storage = new FakeStorage();
        var settings = await LoadAsync(storage);
        var development = settings.Settings.Profiles.Single(p => p.Name == "Development");
        var id = development.Id;

        var copy = settings.DuplicateProfile(id);
        development.Name = "Dev (renamed)";
        settings.UpdateProfile(development);
        await settings.SaveAsync(storage);

        var reload = await LoadAsync(storage);
        copy.Id.Should().NotBe(id);
        Id(reload, "Dev (renamed)").Should().Be(id);
        Id(reload, "Development (Copy)").Should().Be(copy.Id);
    }

    [Fact]
    public async Task Typed_backend_clients_get_the_handler_from_the_client_defaults()
    {
        var storage = new FakeStorage();
        var settings = await LoadAsync(storage);
        var backend = new FakeBackend();
        var services = new ServiceCollection();
        services.AddSingleton<IJSRuntime>(storage);
        services.AddSingleton<IFrontendAnalysisSettingsService>(settings);
        services.AddTransient<TargetEnvironmentMaterializationHandler>();
        services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(() => backend).AddHttpMessageHandler<TargetEnvironmentMaterializationHandler>());
        services.AddHttpClient<IExternalIntegrationsApiService, ExternalIntegrationsApiService>(client => client.BaseAddress = new Uri("http://backend.test/"));
        using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<IExternalIntegrationsApiService>().SaveAsync(Id(settings, "Development"), null, Integration());

        settings.IsMaterialized.Should().BeTrue();
    }

    [Fact]
    public async Task A_failed_store_blocks_the_write_so_nothing_is_orphaned()
    {
        var storage = new FakeStorage { FailWrites = true };
        var backend = new FakeBackend();
        var settings = await LoadAsync(storage);

        var result = await new ExternalIntegrationsApiService(Client(settings, storage, backend)).SaveAsync(Id(settings, "Development"), null, Integration());

        result.Error.Should().StartWith("The Target Environment could not be saved");
        backend.Records.Should().BeEmpty();
        settings.IsMaterialized.Should().BeFalse();
    }
}
