using BirkNext.Api.Models.Admin;
namespace BirkNext.Api.Services;
public interface IFeatureVisibilityPageService { Task<List<SettingsSection>> GetSectionsAsync(); Task<StatusSummary> GetStatusSummaryAsync(); }
public sealed class FeatureVisibilityPageService : IFeatureVisibilityPageService
{
    private readonly ISystemSettingsStatusEngine _statusEngine;
    public FeatureVisibilityPageService(ISystemSettingsStatusEngine statusEngine, ILogger<FeatureVisibilityPageService> logger) { _statusEngine = statusEngine; }
    public async Task<List<SettingsSection>> GetSectionsAsync() => await Task.FromResult(new List<SettingsSection> { new() { Title = "Features", Description = "Feature visibility configuration", Status = SystemSettingsStatus.Pass, Items = new() { new SettingsItem { Name = "Feature Flags", Value = "Configured", Status = SystemSettingsStatus.Pass, Description = "Feature flags are properly configured", Recommendation = "", IsRequired = false } }, IsRequired = false } });
    public async Task<StatusSummary> GetStatusSummaryAsync() { var sections = await GetSectionsAsync(); var summary = new StatusSummary(); foreach (var item in sections.SelectMany(s => s.Items)) summary.AddStatus(item.Status); return summary; }
}
