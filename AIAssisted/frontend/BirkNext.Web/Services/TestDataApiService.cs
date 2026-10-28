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
    /// <summary>Structural candidates of one exact snapshot (GET api/test-data/candidates); never approved by listing.</summary>
    Task<IReadOnlyList<TestDataCandidate>?> CandidatesAsync(Guid snapshotId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TestDataCandidate>?>(null);

    // Catalog actions Test Data owns. Each returns an error text or null; none sends or receives a value.
    Task<(TestDataConsumerView? View, string? Error)> ConsumersAsync(CancellationToken ct = default) => Task.FromResult<(TestDataConsumerView?, string?)>((null, Unavailable));
    Task<(string? AssetId, string? Error)> RegisterAsync(TestDataRegistrationRequest request, CancellationToken ct = default) => Task.FromResult<(string?, string?)>((null, Unavailable));
    Task<string?> ApproveAsync(string assetId, CancellationToken ct = default) => Task.FromResult<string?>(Unavailable);
    Task<string?> DeprecateAsync(string assetId, CancellationToken ct = default) => Task.FromResult<string?>(Unavailable);
    Task<(ConsumerTestDataRequirement? Requirement, string? Error)> AddRequirementAsync(ConsumerTestDataRequirement requirement, CancellationToken ct = default) =>
        Task.FromResult<(ConsumerTestDataRequirement?, string?)>((null, Unavailable));
    Task<(List<TestDataMatch>? Matches, string? Error)> MatchesAsync(ConsumerTestDataRequirement requirement, CancellationToken ct = default) => Task.FromResult<(List<TestDataMatch>?, string?)>((null, Unavailable));
    Task<(TestDataUsageReference? Usage, string? Error)> BindAsync(TestDataUsageReference usage, CancellationToken ct = default) => Task.FromResult<(TestDataUsageReference?, string?)>((null, Unavailable));

    /// <summary>
    /// The one call that sends a value: a Live Discovery value, in the POST body only, for a backend-only comparison with approved Test Data. The
    /// answer is an alias or nothing; nothing is stored, approved or created (POST api/test-data/observed-value/match).
    /// </summary>
    Task<(ObservedValueMatchResult? Result, string? Error)> MatchObservedValueAsync(ObservedValueMatchRequest request, CancellationToken ct = default) =>
        Task.FromResult<(ObservedValueMatchResult?, string?)>((null, Unavailable));

    /// <summary>Fills generated XML with Test Data values by alias (POST api/test-data/xml-fixtures/bind); the values are resolved by the backend.</summary>
    Task<(string? Xml, IReadOnlyList<string> Problems)> BindXmlFixtureAsync(TestDataXmlBindRequest request, CancellationToken ct = default) =>
        Task.FromResult<(string?, IReadOnlyList<string>)>((null, [Unavailable]));

    const string Unavailable = "This Test Data action is not available in this host.";
}

public sealed class TestDataApiService(HttpClient http) : ITestDataApiService
{
    public const string Title = "Test Data";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<TestDataCandidate>?> CandidatesAsync(Guid snapshotId, CancellationToken ct = default)
    {
        try { return await http.GetFromJsonAsync<List<TestDataCandidate>>($"api/test-data/candidates?snapshotId={snapshotId}", Json, ct); }
        catch (HttpRequestException) { return null; }
    }

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

    public async Task<(TestDataConsumerView? View, string? Error)> ConsumersAsync(CancellationToken ct = default)
    {
        try { return (await http.GetFromJsonAsync<TestDataConsumerView>("api/test-data/consumers", Json, ct), null); }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e))
        { return (null, BackendRequestClassifier.FromException(Title, e, "GET api/test-data/consumers", ct).UserMessage); }
    }

    public async Task<(string? AssetId, string? Error)> RegisterAsync(TestDataRegistrationRequest request, CancellationToken ct = default)
    {
        var (body, error) = await PostAsync("api/test-data/registrations", request, ct);
        return error is not null ? (null, error) : (body is { } b && b.TryGetProperty("assetId", out var id) ? id.GetString() : null, null);
    }

    public async Task<(string? Xml, IReadOnlyList<string> Problems)> BindXmlFixtureAsync(TestDataXmlBindRequest request, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.PostAsJsonAsync("api/test-data/xml-fixtures/bind", request, Json, ct);
            var result = await response.Content.ReadFromJsonAsync<TestDataXmlBindResult>(Json, ct);
            return result is null ? (null, [$"The request failed ({(int)response.StatusCode})."]) : (result.Xml, result.Problems);
        }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e))
        { return (null, [BackendRequestClassifier.FromException(Title, e, "POST api/test-data/xml-fixtures/bind", ct).UserMessage]); }
    }

    public async Task<string?> ApproveAsync(string assetId, CancellationToken ct = default) =>
        (await PostAsync($"api/test-data/assets/{Uri.EscapeDataString(assetId)}/approve", new { }, ct)).Error;

    public async Task<string?> DeprecateAsync(string assetId, CancellationToken ct = default) =>
        (await PostAsync($"api/test-data/assets/{Uri.EscapeDataString(assetId)}/deprecate", new { }, ct)).Error;

    public async Task<(ConsumerTestDataRequirement? Requirement, string? Error)> AddRequirementAsync(ConsumerTestDataRequirement requirement, CancellationToken ct = default)
    {
        var (body, error) = await PostAsync("api/test-data/requirements", requirement, ct);
        return (body?.Deserialize<ConsumerTestDataRequirement>(Json), error);
    }

    public async Task<(List<TestDataMatch>? Matches, string? Error)> MatchesAsync(ConsumerTestDataRequirement requirement, CancellationToken ct = default)
    {
        var (body, error) = await PostAsync("api/test-data/matches", requirement, ct);
        return (body?.Deserialize<List<TestDataMatch>>(Json), error);
    }

    public async Task<(TestDataUsageReference? Usage, string? Error)> BindAsync(TestDataUsageReference usage, CancellationToken ct = default)
    {
        var (body, error) = await PostAsync("api/test-data/usages", usage, ct);
        return (body?.Deserialize<TestDataUsageReference>(Json), error);
    }

    public async Task<(ObservedValueMatchResult? Result, string? Error)> MatchObservedValueAsync(ObservedValueMatchRequest request, CancellationToken ct = default)
    {
        var (body, error) = await PostAsync("api/test-data/observed-value/match", request, ct);
        return (body?.Deserialize<ObservedValueMatchResult>(Json), error);
    }

    /// <summary>POST with the backend's own refusal text (400/409 "message") surfaced as the error.</summary>
    private async Task<(JsonElement? Body, string? Error)> PostAsync(string path, object payload, CancellationToken ct)
    {
        try
        {
            using var response = await http.PostAsJsonAsync(path, payload, Json, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            JsonElement? body = text.Length == 0 ? null : JsonSerializer.Deserialize<JsonElement>(text, Json);
            if (response.IsSuccessStatusCode) return (body, null);
            return (null, body is { ValueKind: JsonValueKind.Object } b && b.TryGetProperty("message", out var m) ? m.GetString() : $"The request failed ({(int)response.StatusCode}).");
        }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e))
        { return (null, BackendRequestClassifier.FromException(Title, e, $"POST {path}", ct).UserMessage); }
    }

    public async Task<(TestDataAssetDetail? Detail, string? Error)> AssetAsync(string id, CancellationToken ct = default)
    {
        try { return (await http.GetFromJsonAsync<TestDataAssetDetail>($"api/test-data/assets/{Uri.EscapeDataString(id)}", Json, ct), null); }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e))
        { return (null, BackendRequestClassifier.FromException(Title, e, "GET api/test-data/assets", ct).UserMessage); }
    }
}
