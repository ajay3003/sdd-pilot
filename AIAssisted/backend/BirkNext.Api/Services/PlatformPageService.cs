using BirkNext.Api.Models.Admin;

namespace BirkNext.Api.Services;

public interface IPlatformPageService { Task<List<SettingsSection>> GetSectionsAsync(); Task<StatusSummary> GetStatusSummaryAsync(); }
public sealed class PlatformPageService : IPlatformPageService
{
    private readonly ISystemSettingsStatusEngine _statusEngine;
    public PlatformPageService(ISystemSettingsStatusEngine statusEngine, ILogger<PlatformPageService> logger) { _statusEngine = statusEngine; }
    public async Task<List<SettingsSection>> GetSectionsAsync() => await Task.FromResult(new List<SettingsSection> { new() { Title = "Platform", Description = "Platform configuration and services", Status = SystemSettingsStatus.Pass, Items = new() { new SettingsItem { Name = "Infrastructure", Value = "Ready", Status = SystemSettingsStatus.Pass, Description = "Platform infrastructure is operational", Recommendation = "", IsRequired = true } }, IsRequired = true } });
    public async Task<StatusSummary> GetStatusSummaryAsync() { var sections = await GetSectionsAsync(); var summary = new StatusSummary(); foreach (var item in sections.SelectMany(s => s.Items)) summary.AddStatus(item.Status); return summary; }
}
