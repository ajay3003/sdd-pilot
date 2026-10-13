using System.Net.Http.Json;
using System.Text.Json;
using BirkNext.Integrations;

namespace BirkNext.Web.Services;

/// <summary>
/// Asks the backend whether this browser session may execute active tests (an authenticated principal with ActiveEventExecute, or the
/// backend's local trusted execution). It only enables or explains Run; the backend re-checks every execution request. A failed check is
/// "cannot execute", never assumed permission.
/// </summary>
public interface IExecutionAccessApiService
{
    Task<ExecutionAccessView> GetAsync(CancellationToken ct = default);
}

public sealed class ExecutionAccessApiService(HttpClient http) : IExecutionAccessApiService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private Task<ExecutionAccessView>? _pending;

    public Task<ExecutionAccessView> GetAsync(CancellationToken ct = default) => _pending ??= LoadAsync();

    private async Task<ExecutionAccessView> LoadAsync()
    {
        try
        {
            return await http.GetFromJsonAsync<ExecutionAccessView>("api/execution-access", Json)
                ?? new ExecutionAccessView(ExecutionAccessMode.None, false, "The backend did not answer the execution-access check.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException
            or Microsoft.AspNetCore.Components.WebAssembly.Authentication.AccessTokenNotAvailableException)
        {
            _pending = null;
            return new ExecutionAccessView(ExecutionAccessMode.None, false, "Execution access could not be checked with the backend.");
        }
    }
}

/// <summary>How a panel decides whether to offer Run: the deployment's sign-in (Entra configured) or the backend's execution-access answer.</summary>
public static class ExecutionGate
{
    public static bool CanExecute(ActiveEventAuthenticationState authentication, ExecutionAccessView? access) =>
        authentication.Configured || access?.CanExecute == true;

    public static string Detail(ActiveEventAuthenticationState authentication, ExecutionAccessView? access) =>
        access is { CanExecute: true } ? access.Detail
        : authentication.Configured ? authentication.Detail
        : access?.Detail ?? authentication.Detail;
}
