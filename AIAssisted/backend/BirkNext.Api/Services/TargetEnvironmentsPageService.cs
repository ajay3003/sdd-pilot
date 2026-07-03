using BirkNext.Api.Models.Admin;
namespace BirkNext.Api.Services;
public interface ITargetEnvironmentsPageService { Task<List<SettingsSection>> GetSectionsAsync(); Task<StatusSummary> GetStatusSummaryAsync(); }
public sealed class TargetEnvironmentsPageService : ITargetEnvironmentsPageService
{
    private readonly ISystemSettingsStatusEngine _statusEngine;
    public TargetEnvironmentsPageService(ISystemSettingsStatusEngine statusEngine, ILogger<TargetEnvironmentsPageService> logger) { _statusEngine = statusEngine; }
    public async Task<List<SettingsSection>> GetSectionsAsync() => await Task.FromResult(new List<SettingsSection> { new() { Title = "Target Environments", Description = "Target analysis environments", Status = SystemSettingsStatus.Pass, Items = new() { new SettingsItem { Name = "Environment Targets", Value = "Configured", Status = SystemSettingsStatus.Pass, Description = "Target environments are configured", Recommendation = "", IsRequired = false } }, IsRequired = false } });
    public async Task<StatusSummary> GetStatusSummaryAsync() { var sections = await GetSectionsAsync(); var summary = new StatusSummary(); foreach (var item in sections.SelectMany(s => s.Items)) summary.AddStatus(item.Status); return summary; }
}
