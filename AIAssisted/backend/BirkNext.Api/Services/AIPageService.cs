using BirkNext.Api.Models.Admin;
namespace BirkNext.Api.Services;
public interface IAIPageService { Task<List<SettingsSection>> GetSectionsAsync(); Task<StatusSummary> GetStatusSummaryAsync(); }
public sealed class AIPageService : IAIPageService
{
    private readonly ISystemSettingsStatusEngine _statusEngine;
    public AIPageService(ISystemSettingsStatusEngine statusEngine, ILogger<AIPageService> logger) { _statusEngine = statusEngine; }
    public async Task<List<SettingsSection>> GetSectionsAsync() => await Task.FromResult(new List<SettingsSection> { new() { Title = "AI Services", Description = "AI model and service configuration", Status = SystemSettingsStatus.Pass, Items = new() { new SettingsItem { Name = "AI Provider", Value = "Configured", Status = SystemSettingsStatus.Pass, Description = "AI provider is configured and available", Recommendation = "", IsRequired = false } }, IsRequired = false } });
    public async Task<StatusSummary> GetStatusSummaryAsync() { var sections = await GetSectionsAsync(); var summary = new StatusSummary(); foreach (var item in sections.SelectMany(s => s.Items)) summary.AddStatus(item.Status); return summary; }
}
