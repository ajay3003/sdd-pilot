using System.Net.Http.Json;
using BirkNext.CriticalE2E;

namespace BirkNext.Web.Services;

public interface ICriticalE2EApiService
{
    Task<CriticalE2EOverview> OverviewAsync(CriticalE2EOverviewRequest request, CancellationToken cancellationToken = default);
    /// <summary>Full definitions, which the overview deliberately does not carry: a summary is for reading, a definition is for editing.</summary>
    Task<List<CriticalE2EFlowDefinition>> FlowsAsync(string environmentId, CancellationToken cancellationToken = default);
    Task<CriticalE2EFlowDefinition> SaveFlowAsync(CriticalE2EFlowDefinition flow, CancellationToken cancellationToken = default);
    Task DeleteFlowAsync(string flowId, CancellationToken cancellationToken = default);
    Task<CriticalE2ERunBatchResult> RunAsync(CriticalE2ERunFlowRequest request, CancellationToken cancellationToken = default);
    /// <summary>Authoring: waits while the tester picks one element in the paired browser.</summary>
    Task<CriticalE2EElementPickResult> PickElementAsync(CriticalE2EElementPickRequest request, CancellationToken cancellationToken = default);
}

public sealed class CriticalE2EApiService(HttpClient http) : ICriticalE2EApiService
{
    public Task<CriticalE2EOverview> OverviewAsync(CriticalE2EOverviewRequest request, CancellationToken ct = default) =>
        PostAsync<CriticalE2EOverview>("api/critical-e2e/overview", request, ct);

    public async Task<List<CriticalE2EFlowDefinition>> FlowsAsync(string environmentId, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<CriticalE2EFlowDefinition>>($"api/critical-e2e/flows?environmentId={Uri.EscapeDataString(environmentId)}", ct) ?? [];

    public Task<CriticalE2EFlowDefinition> SaveFlowAsync(CriticalE2EFlowDefinition flow, CancellationToken ct = default) =>
        PostAsync<CriticalE2EFlowDefinition>("api/critical-e2e/flows", flow, ct);

    public async Task DeleteFlowAsync(string flowId, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"api/critical-e2e/flows/{Uri.EscapeDataString(flowId)}", ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// A run waits for the browser, and the browser waits for a person, so this call is long by nature. The caller's
    /// cancellation token is the stop button.
    /// </summary>
    public Task<CriticalE2ERunBatchResult> RunAsync(CriticalE2ERunFlowRequest request, CancellationToken ct = default) =>
        PostAsync<CriticalE2ERunBatchResult>("api/critical-e2e/run", request, ct);

    public Task<CriticalE2EElementPickResult> PickElementAsync(CriticalE2EElementPickRequest request, CancellationToken ct = default) =>
        PostAsync<CriticalE2EElementPickResult>("api/critical-e2e/pick-element", request, ct);

    private async Task<T> PostAsync<T>(string path, object body, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(path, body, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct))!;
    }
}
