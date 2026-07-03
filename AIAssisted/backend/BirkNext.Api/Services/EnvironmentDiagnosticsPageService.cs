using BirkNext.Api.Models.Admin;

namespace BirkNext.Api.Services;

public interface IEnvironmentDiagnosticsPageService
{
    Task<List<SettingsSection>> GetSectionsAsync();
    Task<StatusSummary> GetStatusSummaryAsync();
}

public sealed class EnvironmentDiagnosticsPageService : IEnvironmentDiagnosticsPageService
{
    private readonly IEnvironmentDiagnosticsService _diagnosticsService;

    public EnvironmentDiagnosticsPageService(
        IEnvironmentDiagnosticsService diagnosticsService,
        ILogger<EnvironmentDiagnosticsPageService> logger)
    {
        _diagnosticsService = diagnosticsService;
    }

    public async Task<List<SettingsSection>> GetSectionsAsync()
    {
        var report = await _diagnosticsService.RunDiagnosticsAsync();
        return report.Sections;
    }

    public async Task<StatusSummary> GetStatusSummaryAsync()
    {
        var report = await _diagnosticsService.RunDiagnosticsAsync();
        return report.Summary ?? new StatusSummary();
    }
}
