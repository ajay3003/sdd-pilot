using BirkNext.Api.Models.Admin;
namespace BirkNext.Api.Services;
public interface IMaintenancePageService { Task<List<SettingsSection>> GetSectionsAsync(); Task<StatusSummary> GetStatusSummaryAsync(); }
public sealed class MaintenancePageService : IMaintenancePageService
{
    private readonly ISystemSettingsStatusEngine _statusEngine;
    public MaintenancePageService(ISystemSettingsStatusEngine statusEngine, ILogger<MaintenancePageService> logger) { _statusEngine = statusEngine; }
    public async Task<List<SettingsSection>> GetSectionsAsync() => await Task.FromResult(new List<SettingsSection> { new() { Title = "Maintenance", Description = "System maintenance and cleanup tasks", Status = SystemSettingsStatus.Pass, Items = new() { new SettingsItem { Name = "Cleanup Tasks", Value = "Scheduled", Status = SystemSettingsStatus.Pass, Description = "Cleanup and maintenance tasks are scheduled", Recommendation = "", IsRequired = false } }, IsRequired = false } });
    public async Task<StatusSummary> GetStatusSummaryAsync() { var sections = await GetSectionsAsync(); var summary = new StatusSummary(); foreach (var item in sections.SelectMany(s => s.Items)) summary.AddStatus(item.Status); return summary; }
}
