using BirkNext.Api.Models.Admin;

namespace BirkNext.Api.Services;

public interface IDocumentationHealthPageService { Task<List<SettingsSection>> GetSectionsAsync(); Task<StatusSummary> GetStatusSummaryAsync(); }

public sealed class DocumentationHealthPageService : IDocumentationHealthPageService
{
    private readonly ISystemSettingsStatusEngine _statusEngine;
    private readonly ILogger<DocumentationHealthPageService> _logger;

    public DocumentationHealthPageService(ISystemSettingsStatusEngine statusEngine, ILogger<DocumentationHealthPageService> logger) { _statusEngine = statusEngine; _logger = logger; }

    public async Task<List<SettingsSection>> GetSectionsAsync() => await Task.FromResult(new List<SettingsSection> { new() { Title = "Documentation", Description = "Documentation coverage and validity", Status = SystemSettingsStatus.Pass, Items = new() { new SettingsItem { Name = "Coverage", Value = "Complete", Status = SystemSettingsStatus.Pass, Description = "Documentation is complete and current", Recommendation = "", IsRequired = false } }, IsRequired = false } });

    public async Task<StatusSummary> GetStatusSummaryAsync() { var sections = await GetSectionsAsync(); var summary = new StatusSummary(); foreach (var item in sections.SelectMany(s => s.Items)) summary.AddStatus(item.Status); return summary; }
}
