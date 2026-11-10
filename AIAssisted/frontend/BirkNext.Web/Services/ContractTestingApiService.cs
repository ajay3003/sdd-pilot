using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BirkNext.ContractTesting;
using BirkNext.ExecutionEvidence;

namespace BirkNext.Web.Services;

/// <summary>Contract-Driven Testing API: the contract registry, contract tests, the Test Data picker and runs. Only <see cref="RunAsync"/> sends requests
/// to an API under test (through Execution Evidence); everything else reads or edits definitions.</summary>
public interface IContractTestingApiService
{
    Task<(IReadOnlyList<ApiContractSummary>? Contracts, string? Error)> ContractsAsync(CancellationToken ct = default);
    Task<(ApiContractDetail? Detail, string? Error)> ContractAsync(string key, int? version, CancellationToken ct = default);
    Task<(ContractImportResult? Result, string? Error)> ImportAsync(ContractImportRequest request, CancellationToken ct = default);
    Task<(IReadOnlyList<SourceContractCandidate>? Candidates, string? Error)> SourceContractsAsync(CancellationToken ct = default);
    Task<(ContractImportResult? Result, string? Error)> ImportFromSourceAsync(SourceContractImportRequest request, CancellationToken ct = default);
    /// <summary>Imports several source contracts (each continues its earlier import), then generates their tests and test data when asked; sends nothing.</summary>
    Task<(SourceContractBulkImportResult? Result, string? Error)> ImportFromSourceBulkAsync(SourceContractBulkImportRequest request, CancellationToken ct = default) =>
        Task.FromResult<(SourceContractBulkImportResult?, string?)>((null, "Bulk import is not available in this host."));
    Task<(ContractVersionDiff? Diff, string? Error)> DiffAsync(string key, int from, int to, CancellationToken ct = default);
    Task<(int Created, IReadOnlyList<string> Skipped, string? Error)> GenerateAsync(string key, int? version, IReadOnlyList<string>? operations, CancellationToken ct = default);
    Task<(int Created, IReadOnlyList<string> Skipped, string? Error)> ProposeNegativesAsync(string key, string operation, CancellationToken ct = default);
    /// <summary>Typed synthetic test data for the contract's tests: validated, kept in Test Data, approved and pinned only when certain.</summary>
    Task<(ContractTestDataReport? Report, string? Error)> GenerateTestDataAsync(string key, int? version, IReadOnlyList<string>? operations, CancellationToken ct = default) =>
        Task.FromResult<(ContractTestDataReport?, string?)>((null, "Automatic test data is not available in this host."));
    Task<(IReadOnlyList<ContractTestView>? Tests, string? Error)> TestsAsync(string key, ContractTestEnvironment? environment, CancellationToken ct = default);
    Task<(IReadOnlyList<ContractDatasetOption>? Options, string? Error)> DatasetsAsync(Guid testId, string? environmentId, CancellationToken ct = default);
    Task<string?> BindAsync(Guid testId, ContractTestBindRequest request, CancellationToken ct = default);
    Task<string?> RegenerateAsync(Guid testId, CancellationToken ct = default);
    Task<string?> DeleteAsync(Guid testId, CancellationToken ct = default);
    Task<(ContractTestRunResult? Result, string? Error)> RunAsync(ContractTestRunRequest request, CancellationToken ct = default);
    Task<(ContractBindingResponse? Binding, string? Error)> BindingAsync(string key, string environmentId, CancellationToken ct = default);
    Task<string?> SaveBindingAsync(string key, ContractEndpointBindingSave save, CancellationToken ct = default);
    Task<(ApiServiceCredentialView? Credential, string? Error)> CredentialAsync(string environmentId, CancellationToken ct = default);
    Task<string?> SaveCredentialAsync(ApiServiceCredentialSave save, CancellationToken ct = default);
    Task<string?> DeleteCredentialAsync(string environmentId, CancellationToken ct = default);
    Task<(ContractTestSaveResult? Result, string? Error)> UpdateTestAsync(Guid testId, ContractTestEdit edit, CancellationToken ct = default);
    Task<(ContractTestDefinition? Test, string? Error)> DuplicateTestAsync(Guid testId, CancellationToken ct = default);
    Task<(IReadOnlyList<ContractTestRevisionInfo>? Revisions, string? Error)> RevisionsAsync(Guid testId, CancellationToken ct = default);
    Task<(ContractTestRevisionDiff? Diff, string? Error)> CompareRevisionsAsync(Guid testId, int from, int to, CancellationToken ct = default);
    Task<(ContractTestSaveResult? Result, string? Error)> RevertTestAsync(Guid testId, int revision, int expectedRevision, CancellationToken ct = default);
    Task<(ContractTestMigrationProposal? Proposal, string? Error)> MigrationAsync(Guid testId, CancellationToken ct = default);
    Task<(ContractTestSaveResult? Result, string? Error)> ApplyMigrationAsync(Guid testId, int expectedRevision, CancellationToken ct = default);
    Task<(ContractCoverageReport? Report, string? Error)> CoverageAsync(string key, int? version, CancellationToken ct = default);
    Task<(ContractRetention? Retention, string? Error)> RetentionAsync(string key, CancellationToken ct = default);
    Task<string?> SetLifecycleAsync(string key, ContractLifecycle lifecycle, CancellationToken ct = default);
    Task<string?> SetHiddenAsync(string key, int version, bool hidden, CancellationToken ct = default);
    Task<string?> DeleteVersionAsync(string key, int version, string confirm, CancellationToken ct = default);
    Task<string?> DeleteContractAsync(string key, string confirm, CancellationToken ct = default);
    Task<(IReadOnlyList<ContractTestChain>? Chains, string? Error)> ChainsAsync(CancellationToken ct = default);
    /// <summary>Creates (null id) or saves the next revision of a chain; the result lists every problem when it is refused.</summary>
    Task<(ContractChainSaveResult? Result, string? Error)> SaveChainAsync(Guid? id, ContractChainSave save, CancellationToken ct = default);
    Task<string?> DeleteChainAsync(Guid id, CancellationToken ct = default);
    Task<(ContractTestRunResult? Result, string? Error)> RunChainAsync(Guid id, ContractChainRunRequest request, CancellationToken ct = default);
}

public sealed record ContractBindingResponse(ContractEndpointBinding? Binding, List<ContractServerProposal> Proposals);

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

    public Task<(IReadOnlyList<SourceContractCandidate>? Candidates, string? Error)> SourceContractsAsync(CancellationToken ct = default) =>
        SendAsync<IReadOnlyList<SourceContractCandidate>>(HttpMethod.Get, $"{Base}/source-contracts", null, ct);

    public Task<(ContractImportResult? Result, string? Error)> ImportFromSourceAsync(SourceContractImportRequest request, CancellationToken ct = default) =>
        SendAsync<ContractImportResult>(HttpMethod.Post, $"{Base}/contracts/from-source", request, ct, readErrorBody: true);

    public Task<(SourceContractBulkImportResult? Result, string? Error)> ImportFromSourceBulkAsync(SourceContractBulkImportRequest request, CancellationToken ct = default) =>
        SendAsync<SourceContractBulkImportResult>(HttpMethod.Post, $"{Base}/contracts/from-source/bulk", request, ct);

    public Task<(ContractVersionDiff? Diff, string? Error)> DiffAsync(string key, int from, int to, CancellationToken ct = default) =>
        SendAsync<ContractVersionDiff>(HttpMethod.Get, $"{Base}/contracts/{Uri.EscapeDataString(key)}/diff?from={from}&to={to}", null, ct);

    private sealed record GenerateResponse(List<ContractTestDefinition> Created, List<string> Skipped);

    public async Task<(int Created, IReadOnlyList<string> Skipped, string? Error)> GenerateAsync(string key, int? version, IReadOnlyList<string>? operations, CancellationToken ct = default)
    {
        var (body, error) = await SendAsync<GenerateResponse>(HttpMethod.Post, $"{Base}/contracts/{Uri.EscapeDataString(key)}/tests", new { version, operations }, ct);
        return (body?.Created.Count ?? 0, body?.Skipped ?? [], error);
    }

    public Task<(ContractTestDataReport? Report, string? Error)> GenerateTestDataAsync(string key, int? version, IReadOnlyList<string>? operations, CancellationToken ct = default) =>
        SendAsync<ContractTestDataReport>(HttpMethod.Post, $"{Base}/contracts/{Uri.EscapeDataString(key)}/test-data", new ContractTestDataRequest(version, operations?.ToList()), ct);

    public async Task<(int Created, IReadOnlyList<string> Skipped, string? Error)> ProposeNegativesAsync(string key, string operation, CancellationToken ct = default)
    {
        var (body, error) = await SendAsync<GenerateResponse>(HttpMethod.Post, $"{Base}/contracts/{Uri.EscapeDataString(key)}/tests/negatives", new { operation }, ct);
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

    public Task<(ContractBindingResponse? Binding, string? Error)> BindingAsync(string key, string environmentId, CancellationToken ct = default) =>
        SendAsync<ContractBindingResponse>(HttpMethod.Get, $"{Base}/contracts/{Uri.EscapeDataString(key)}/binding?environmentId={Uri.EscapeDataString(environmentId)}", null, ct);

    public async Task<string?> SaveBindingAsync(string key, ContractEndpointBindingSave save, CancellationToken ct = default) =>
        (await SendAsync<JsonElement?>(HttpMethod.Put, $"{Base}/contracts/{Uri.EscapeDataString(key)}/binding", save, ct)).Error;

    /// <summary>The environment's unattended credential (configuration only). A 404 means none is configured, which is not an error.</summary>
    public async Task<(ApiServiceCredentialView? Credential, string? Error)> CredentialAsync(string environmentId, CancellationToken ct = default)
    {
        var (view, error) = await SendAsync<ApiServiceCredentialView>(HttpMethod.Get, $"{Base}/credentials/{Uri.EscapeDataString(environmentId)}", null, ct);
        return error is not null && error.Contains("HTTP 404", StringComparison.Ordinal) ? (null, null) : (view, error);
    }

    public async Task<string?> SaveCredentialAsync(ApiServiceCredentialSave save, CancellationToken ct = default) =>
        (await SendAsync<JsonElement?>(HttpMethod.Put, $"{Base}/credentials", save, ct)).Error;

    public async Task<string?> DeleteCredentialAsync(string environmentId, CancellationToken ct = default) =>
        (await SendAsync<JsonElement?>(HttpMethod.Delete, $"{Base}/credentials/{Uri.EscapeDataString(environmentId)}", null, ct)).Error;

    public Task<(ContractTestSaveResult? Result, string? Error)> UpdateTestAsync(Guid testId, ContractTestEdit edit, CancellationToken ct = default) =>
        SendAsync<ContractTestSaveResult>(HttpMethod.Put, $"{Base}/tests/{testId}", edit, ct, readErrorBody: true);

    public Task<(ContractTestDefinition? Test, string? Error)> DuplicateTestAsync(Guid testId, CancellationToken ct = default) =>
        SendAsync<ContractTestDefinition>(HttpMethod.Post, $"{Base}/tests/{testId}/duplicate", new { }, ct);

    public Task<(IReadOnlyList<ContractTestRevisionInfo>? Revisions, string? Error)> RevisionsAsync(Guid testId, CancellationToken ct = default) =>
        SendAsync<IReadOnlyList<ContractTestRevisionInfo>>(HttpMethod.Get, $"{Base}/tests/{testId}/revisions", null, ct);

    public Task<(ContractTestRevisionDiff? Diff, string? Error)> CompareRevisionsAsync(Guid testId, int from, int to, CancellationToken ct = default) =>
        SendAsync<ContractTestRevisionDiff>(HttpMethod.Get, $"{Base}/tests/{testId}/compare?from={from}&to={to}", null, ct);

    public Task<(ContractTestSaveResult? Result, string? Error)> RevertTestAsync(Guid testId, int revision, int expectedRevision, CancellationToken ct = default) =>
        SendAsync<ContractTestSaveResult>(HttpMethod.Post, $"{Base}/tests/{testId}/revert", new { revision, expectedRevision }, ct, readErrorBody: true);

    public Task<(ContractTestMigrationProposal? Proposal, string? Error)> MigrationAsync(Guid testId, CancellationToken ct = default) =>
        SendAsync<ContractTestMigrationProposal>(HttpMethod.Get, $"{Base}/tests/{testId}/migration", null, ct);

    public Task<(ContractTestSaveResult? Result, string? Error)> ApplyMigrationAsync(Guid testId, int expectedRevision, CancellationToken ct = default) =>
        SendAsync<ContractTestSaveResult>(HttpMethod.Post, $"{Base}/tests/{testId}/migration", new { expectedRevision }, ct, readErrorBody: true);

    public Task<(ContractCoverageReport? Report, string? Error)> CoverageAsync(string key, int? version, CancellationToken ct = default) =>
        SendAsync<ContractCoverageReport>(HttpMethod.Get, $"{Base}/contracts/{Uri.EscapeDataString(key)}/coverage{(version is { } v ? $"?version={v}" : "")}", null, ct);

    public Task<(ContractRetention? Retention, string? Error)> RetentionAsync(string key, CancellationToken ct = default) =>
        SendAsync<ContractRetention>(HttpMethod.Get, $"{Base}/contracts/{Uri.EscapeDataString(key)}/retention", null, ct);

    public async Task<string?> SetLifecycleAsync(string key, ContractLifecycle lifecycle, CancellationToken ct = default) =>
        (await SendAsync<JsonElement?>(HttpMethod.Put, $"{Base}/contracts/{Uri.EscapeDataString(key)}/lifecycle", new { lifecycle }, ct)).Error;

    public async Task<string?> SetHiddenAsync(string key, int version, bool hidden, CancellationToken ct = default) =>
        (await SendAsync<JsonElement?>(HttpMethod.Put, $"{Base}/contracts/{Uri.EscapeDataString(key)}/versions/{version}/hidden", new { hidden }, ct)).Error;

    public async Task<string?> DeleteVersionAsync(string key, int version, string confirm, CancellationToken ct = default) =>
        (await SendAsync<JsonElement?>(HttpMethod.Delete, $"{Base}/contracts/{Uri.EscapeDataString(key)}/versions/{version}?confirm={Uri.EscapeDataString(confirm)}", null, ct)).Error;

    public async Task<string?> DeleteContractAsync(string key, string confirm, CancellationToken ct = default) =>
        (await SendAsync<JsonElement?>(HttpMethod.Delete, $"{Base}/contracts/{Uri.EscapeDataString(key)}?confirm={Uri.EscapeDataString(confirm)}", null, ct)).Error;

    public Task<(IReadOnlyList<ContractTestChain>? Chains, string? Error)> ChainsAsync(CancellationToken ct = default) =>
        SendAsync<IReadOnlyList<ContractTestChain>>(HttpMethod.Get, $"{Base}/chains", null, ct);

    public Task<(ContractChainSaveResult? Result, string? Error)> SaveChainAsync(Guid? id, ContractChainSave save, CancellationToken ct = default) =>
        SendAsync<ContractChainSaveResult>(id is null ? HttpMethod.Post : HttpMethod.Put, id is null ? $"{Base}/chains" : $"{Base}/chains/{id}", save, ct, readErrorBody: true);

    public async Task<string?> DeleteChainAsync(Guid id, CancellationToken ct = default) =>
        (await SendAsync<JsonElement?>(HttpMethod.Delete, $"{Base}/chains/{id}", null, ct)).Error;

    public Task<(ContractTestRunResult? Result, string? Error)> RunChainAsync(Guid id, ContractChainRunRequest request, CancellationToken ct = default) =>
        SendAsync<ContractTestRunResult>(HttpMethod.Post, $"{Base}/chains/{id}/runs", request, ct, readErrorBody: true);

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
            if (message is null && response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                message = path.EndsWith("/runs", StringComparison.Ordinal)
                    ? $"{Title}: you do not have permission to run tests against a target (HTTP {(int)response.StatusCode})."
                    : $"{Title}: you do not have permission to change contract configuration (HTTP {(int)response.StatusCode}).";
            return (typed, message ?? $"{Title}: the request was refused (HTTP {(int)response.StatusCode}).");
        }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e))
        { return (default, BackendRequestClassifier.FromException(Title, e, $"{method} {path}", ct).UserMessage); }
    }
}
