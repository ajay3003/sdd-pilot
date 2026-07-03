using BirkNext.Api.Models.Admin;
namespace BirkNext.Api.Services;
public interface ISystemDiagnosticsPageService { Task<List<SettingsSection>> GetSectionsAsync(); Task<StatusSummary> GetStatusSummaryAsync(); }
public sealed class SystemDiagnosticsPageService : ISystemDiagnosticsPageService
{
    private readonly ISystemSettingsStatusEngine _statusEngine;
    public SystemDiagnosticsPageService(ISystemSettingsStatusEngine statusEngine, ILogger<SystemDiagnosticsPageService> logger) { _statusEngine = statusEngine; }
    public async Task<List<SettingsSection>> GetSectionsAsync()
    {
        var sections = new List<SettingsSection> { new() { Title = "System", Description = "System health and diagnostics", Status = SystemSettingsStatus.Pass, Items = new() { new SettingsItem { Name = "Health Status", Value = "Healthy", Status = SystemSettingsStatus.Pass, Description = "System is healthy and operational", Recommendation = "", IsRequired = true } }, IsRequired = true } };
        DiagnosticPageServiceHelpers.ApplySectionStatuses(sections, _statusEngine);
        return await Task.FromResult(sections);
    }
    public async Task<StatusSummary> GetStatusSummaryAsync() { var sections = await GetSectionsAsync(); return DiagnosticPageServiceHelpers.SummarizeSections(sections, _statusEngine); }
}
