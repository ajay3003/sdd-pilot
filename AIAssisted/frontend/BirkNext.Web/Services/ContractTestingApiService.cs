using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BirkNext.ContractTesting;

namespace BirkNext.Web.Services;

/// <summary>Contract-Driven Testing API: the contract registry, contract tests, the Test Data picker and runs. Only <see cref="RunAsync"/> sends requests
/// to an API under test (through Execution Evidence); everything else reads or edits definitions.</summary>
public interface IContractTestingApiService
{
    Task<(IReadOnlyList<ApiContractSummary>? Contracts, string? Error)> ContractsAsync(CancellationToken ct = default);
    Task<(ApiContractDetail? Detail, string? Error)> ContractAsync(string key, int? version, CancellationToken ct = default);
    Task<(ContractImportResult? Result, string? Error)> ImportAsync(ContractImportRequest request, CancellationToken ct = default);
    Task<(ContractVersionDiff? Diff, string? Error)> DiffAsync(string key, int from, int to, CancellationToken ct = default);
    Task<(int Created, IReadOnlyList<string> Skipped, string? Error)> GenerateAsync(string key, int? version, IReadOnlyList<string>? operations, CancellationToken ct = default);
    Task<(IReadOnlyList<ContractTestView>? Tests, string? Error)> TestsAsync(string key, ContractTestEnvironment? environment, CancellationToken ct = default);
    Task<(IReadOnlyList<ContractDatasetOption>? Options, string? Error)> DatasetsAsync(Guid testId, string? environmentId, CancellationToken ct = default);
    Task<string?> BindAsync(Guid testId, ContractTestBindRequest request, CancellationToken ct = default);
    Task<string?> RegenerateAsync(Guid testId, CancellationToken ct = default);
    Task<string?> DeleteAsync(Guid testId, CancellationToken ct = default);
    Task<(ContractTestRunResult? Result, string? Error)> RunAsync(ContractTestRunRequest request, CancellationToken ct = default);
}

public sealed class ContractTestingApiService(HttpClient http) : IContractTestingApiService
{
    public const string Title = "Contract Testing";
    private const string Base = "api/contract-testing";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public Task<(IReadOnlyList<ApiContractSummary>? Contracts, string? Error)> ContractsAsync(CancellationToken ct = default) =>
        SendAsync<IReadOnlyList<ApiContractSummary>>(HttpMethod.Get, $"{Base}/contracts", null, ct);

    public Task<(ApiContractDetail? Detail, string? Error)> ContractAsync(string key, int? version, CancellationToken ct = default) =>
        SendAsync<ApiContractDetail>(HttpMethod.Get, $"{Base}/contracts/{Uri.EscapeDataString(key)}{(version is { } v ? $"?version={v}" : "")}", null, ct);

    public Task<(ContractImportResult? Result, string? Error)> ImportAsync(ContractImportRequest request, CancellationToken ct = default) =>
        SendAsync<ContractImportResult>(HttpMethod.Post, $"{Base}/contracts", request, ct, readErrorBody: true);

    public Task<(ContractVersionDiff? Diff, string? Error)> DiffAsync(string key, int from, int to, CancellationToken ct = default) =>
        SendAsync<ContractVersionDiff>(HttpMethod.Get, $"{Base}/contracts/{Uri.EscapeDataString(key)}/diff?from={from}&to={to}", null, ct);

    private sealed record GenerateResponse(List<ContractTestDefinition> Created, List<string> Skipped);

    public async Task<(int Created, IReadOnlyList<string> Skipped, string? Error)> GenerateAsync(string key, int? version, IReadOnlyList<string>? operations, CancellationToken ct = default)
    {
        var (body, error) = await SendAsync<GenerateResponse>(HttpMethod.Post, $"{Base}/contracts/{Uri.EscapeDataString(key)}/tests", new { version, operations }, ct);
        return (body?.Created.Count ?? 0, body?.Skipped ?? [], error);
    }

    public Task<(IReadOnlyList<ContractTestView>? Tests, string? Error)> TestsAsync(string key, ContractTestEnvironment? environment, CancellationToken ct = default) =>
        SendAsync<IReadOnlyList<ContractTestView>>(HttpMethod.Post, $"{Base}/contracts/{Uri.EscapeDataString(key)}/tests/query", environment ?? new ContractTestEnvironment(), ct);

    public Task<(IReadOnlyList<ContractDatasetOption>? Options, string? Error)> DatasetsAsync(Guid testId, string? environmentId, CancellationToken ct = default) =>
        SendAsync<IReadOnlyList<ContractDatasetOption>>(HttpMethod.Get, $"{Base}/tests/{testId}/datasets{(environmentId is { Length: > 0 } e ? $"?environmentId={Uri.EscapeDataString(e)}" : "")}", null, ct);

    public async Task<string?> BindAsync(Guid testId, ContractTestBindRequest request, CancellationToken ct = default) =>
        (await SendAsync<JsonElement?>(HttpMethod.Put, $"{Base}/tests/{testId}/binding", request, ct)).Error;

    public async Task<string?> RegenerateAsync(Guid testId, CancellationToken ct = default) =>
        (await SendAsync<JsonElement?>(HttpMethod.Post, $"{Base}/tests/{testId}/regenerate", new { }, ct)).Error;

    public async Task<string?> DeleteAsync(Guid testId, CancellationToken ct = default) =>
        (await SendAsync<JsonElement?>(HttpMethod.Delete, $"{Base}/tests/{testId}", null, ct)).Error;

    public Task<(ContractTestRunResult? Result, string? Error)> RunAsync(ContractTestRunRequest request, CancellationToken ct = default) =>
        SendAsync<ContractTestRunResult>(HttpMethod.Post, $"{Base}/runs", request, ct, readErrorBody: true);

    /// <summary>One request. A refusal's message (<c>{ "message": … }</c>) is shown as is; with <paramref name="readErrorBody"/> the typed body of a 422 is returned too.</summary>
    private async Task<(T? Body, string? Error)> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct, bool readErrorBody = false)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: Json);
            using var response = await http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
                return response.StatusCode == System.Net.HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0 ? (default, null) : (await response.Content.ReadFromJsonAsync<T>(Json, ct), null);
            var text = await response.Content.ReadAsStringAsync(ct);
            string? message = null;
            T? typed = default;
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) message = m.GetString();
                if (readErrorBody) typed = JsonSerializer.Deserialize<T>(text, Json);
            }
            catch (JsonException) { }
            return (typed, message ?? $"{Title}: the request was refused (HTTP {(int)response.StatusCode}).");
        }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e))
        { return (default, BackendRequestClassifier.FromException(Title, e, $"{method} {path}", ct).UserMessage); }
    }
}
