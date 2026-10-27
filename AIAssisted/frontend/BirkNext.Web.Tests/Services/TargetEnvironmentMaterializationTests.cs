using System.Net;
using System.Net.Http.Json;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Xunit;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Never-saved built-in Target Environments are stored the first time backend configuration is attached to them, so External Integrations,
/// IQR data and Altinn settings (keyed by environment id alone) survive a reload. Seed ids stay random per browser, never fixed or shared.
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

    /// <summary>A backend that keys every record by the environment id in the URL, like the real environment-keyed tables.</summary>
    private sealed class FakeBackend : HttpMessageHandler
    {
        public readonly List<(string Environment, string Kind)> Records = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var segments = request.RequestUri!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var environment = segments.Length > 3 ? segments[2] : request.RequestUri.Query;
            var kind = segments.Length > 3 ? segments[3] : segments[^1];
            if (request.Method != HttpMethod.Get && !Records.Contains((environment, kind))) Records.Add((environment, kind));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(Records.Where(r => r.Environment == environment).Select(r => r.Kind).ToList()),
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

        (await http.PostAsJsonAsync($"api/target-environments/{development}/external-integrations", new { provider = "Generic" })).EnsureSuccessStatusCode();
        settings.IsMaterialized.Should().BeTrue();
        (await http.PutAsJsonAsync($"api/target-environments/{development}/message-flows", new { review = "iqr" })).EnsureSuccessStatusCode();
        (await http.PutAsJsonAsync($"api/target-environments/{development}/altinn", new { environment = "tt02" })).EnsureSuccessStatusCode();

        var reload = await LoadAsync(storage);
        var reopened = Id(reload, "Development");

        reopened.Should().Be(development);
        reload.Settings.Profiles.Select(p => p.Id).Should().Equal(settings.Settings.Profiles.Select(p => p.Id), "every default keeps its id, none is re-created");
        var attached = await Client(reload, storage, backend).GetFromJsonAsync<List<string>>($"api/target-environments/{reopened}/external-integrations");
        attached.Should().BeEquivalentTo(["external-integrations", "message-flows", "altinn"]);
        backend.Records.Should().OnlyContain(r => r.Environment == development, "no record points at an id that no longer exists");
        storage.Writes.Should().Be(1, "the defaults are stored once, on the first attach");
    }

    [Fact]
    public async Task A_write_that_names_the_environment_only_in_its_JSON_body_also_materializes()
    {
        var storage = new FakeStorage();
        var settings = await LoadAsync(storage);
        var qa = Id(settings, "QA");

        (await Client(settings, storage, new FakeBackend()).PostAsJsonAsync("api/integration-review/x/bindings", new { environmentId = qa })).EnsureSuccessStatusCode();

        Id(await LoadAsync(storage), "QA").Should().Be(qa);
    }

    [Fact]
    public async Task Reads_and_writes_that_do_not_reference_a_default_do_not_store_anything()
    {
        var storage = new FakeStorage();
        var settings = await LoadAsync(storage);
        var http = Client(settings, storage, new FakeBackend());

        await http.GetAsync($"api/target-environments/{Id(settings, "Development")}/external-integrations");
        await http.PostAsJsonAsync("api/target-environments/someone-else/external-integrations", new { provider = "Generic" });

        settings.IsMaterialized.Should().BeFalse();
        storage.Writes.Should().Be(0);
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

        (await Client(settings, storage, new FakeBackend()).PostAsJsonAsync("api/target-environments/saved-dev/external-integrations", new { })).EnsureSuccessStatusCode();

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

        var api = provider.GetRequiredService<IExternalIntegrationsApiService>();
        await api.DeleteAsync(Id(settings, "Development"), "x");

        settings.IsMaterialized.Should().BeTrue();
    }

    [Fact]
    public async Task A_failed_store_blocks_the_write_so_nothing_is_orphaned()
    {
        var storage = new FakeStorage { FailWrites = true };
        var backend = new FakeBackend();
        var settings = await LoadAsync(storage);

        var send = () => Client(settings, storage, backend).PostAsJsonAsync($"api/target-environments/{Id(settings, "Development")}/external-integrations", new { });

        await send.Should().ThrowAsync<JSException>();
        backend.Records.Should().BeEmpty();
        settings.IsMaterialized.Should().BeFalse();
    }
}
