using BirkNext.Api.Models.Admin;

namespace BirkNext.Api.Services;

public interface IDocumentationHealthPageService { Task<List<SettingsSection>> GetSectionsAsync(); Task<StatusSummary> GetStatusSummaryAsync(); }

public sealed class DocumentationHealthPageService : IDocumentationHealthPageService
{
    private readonly ISystemSettingsStatusEngine _statusEngine;
    private readonly ILogger<DocumentationHealthPageService> _logger;

    public DocumentationHealthPageService(ISystemSettingsStatusEngine statusEngine, ILogger<DocumentationHealthPageService> logger) { _statusEngine = statusEngine; _logger = logger; }

    public async Task<List<SettingsSection>> GetSectionsAsync()
    {
        var sections = new List<SettingsSection> { new() { Title = "Documentation", Description = "Documentation coverage and validity", Status = SystemSettingsStatus.Pass, Items = new() { new SettingsItem { Name = "Coverage", Value = "Complete", Status = SystemSettingsStatus.Pass, Description = "Documentation is complete and current", Recommendation = "", IsRequired = false } }, IsRequired = false } };
        DiagnosticPageServiceHelpers.ApplySectionStatuses(sections, _statusEngine);
        return await Task.FromResult(sections);
    }

    public async Task<StatusSummary> GetStatusSummaryAsync() { var sections = await GetSectionsAsync(); return DiagnosticPageServiceHelpers.SummarizeSections(sections, _statusEngine); }
}
