using System.Net.Http.Json;
using System.Text.Json;
using BirkNext.TestData;
using BirkNext.Web.Configuration;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

namespace BirkNext.Web.Services;

/// <summary>Test Data catalog calls (read-only): safe summaries and bounded previews of non-sensitive values. No value is ever sent here.</summary>
public interface ITestDataApiService
{
    Task<(TestDataCatalogView? Catalog, string? Error)> CatalogAsync(CancellationToken ct = default);
    Task<(TestDataAssetDetail? Detail, string? Error)> AssetAsync(string id, CancellationToken ct = default);
}

public sealed class TestDataApiService(HttpClient http) : ITestDataApiService
{
    public const string Title = "Test Data";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// A client for the one configured backend address (the same <c>BackendUrl</c> rule every API client uses), for a page that is not wired
    /// through Program.cs. A registered <see cref="ITestDataApiService"/> always wins.
    /// </summary>
    public static ITestDataApiService Resolve(IServiceProvider services)
    {
        if (services.GetService(typeof(ITestDataApiService)) is ITestDataApiService registered) return registered;
        var configuration = (IConfiguration)services.GetService(typeof(IConfiguration))!;
        var environment = (IWebAssemblyHostEnvironment)services.GetService(typeof(IWebAssemblyHostEnvironment))!;
        var factory = (IHttpClientFactory)services.GetService(typeof(IHttpClientFactory))!;
        var backendUrl = configuration["BackendUrl"] ?? "https://localhost:5000";
        BackendUrlValidator.Validate(backendUrl, environment.Environment);
        var client = factory.CreateClient(nameof(TestDataApiService));
        client.BaseAddress = BackendUrlValidator.BaseAddress(backendUrl);
        return new TestDataApiService(client);
    }

    public async Task<(TestDataCatalogView? Catalog, string? Error)> CatalogAsync(CancellationToken ct = default)
    {
        try { return (await http.GetFromJsonAsync<TestDataCatalogView>("api/test-data", Json, ct), null); }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e))
        { return (null, BackendRequestClassifier.FromException(Title, e, "GET api/test-data", ct).UserMessage); }
    }

    public async Task<(TestDataAssetDetail? Detail, string? Error)> AssetAsync(string id, CancellationToken ct = default)
    {
        try { return (await http.GetFromJsonAsync<TestDataAssetDetail>($"api/test-data/assets/{Uri.EscapeDataString(id)}", Json, ct), null); }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e))
        { return (null, BackendRequestClassifier.FromException(Title, e, "GET api/test-data/assets", ct).UserMessage); }
    }
}
