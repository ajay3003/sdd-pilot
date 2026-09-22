using System.Net.Http.Json;
using BirkNext.Web.Models;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Services;

public static class BrowserAutomationDiagnosticServiceCollectionExtensions
{
    public static IServiceCollection AddBrowserAutomationDiagnosticApi(this IServiceCollection services, Uri baseAddress)
    {
        services.AddHttpClient<IBrowserAutomationDiagnosticApiService, BrowserAutomationDiagnosticApiService>(client =>
        {
            client.BaseAddress = baseAddress;
            // The run starts a real browser and navigates twice; the backend bounds every stage, and this is the
            // outer bound so a hung request cannot outlive the diagnostic it is waiting for.
            client.Timeout = TimeSpan.FromMinutes(3);
        });
        return services;
    }
}

public interface IBrowserAutomationDiagnosticApiService
{
    Task<BrowserAutomationDiagnosticReport?> RunAsync(BrowserAutomationDiagnosticRequest request, CancellationToken ct = default);
}

public sealed class BrowserAutomationDiagnosticApiService(
    HttpClient http, ILogger<BrowserAutomationDiagnosticApiService> logger) : IBrowserAutomationDiagnosticApiService
{
    public async Task<BrowserAutomationDiagnosticReport?> RunAsync(
        BrowserAutomationDiagnosticRequest request, CancellationToken ct = default)
    {
        try
        {
            var response = await http.PostAsJsonAsync("api/browser-automation-diagnostic/run", request, ct);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<BrowserAutomationDiagnosticReport>(cancellationToken: ct);
        }
        catch (OperationCanceledException)
        {
            // The user cancelled; the backend's own cleanup runs regardless. Not an error to report.
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Browser automation diagnostic request failed");
            return null;
        }
    }
}
