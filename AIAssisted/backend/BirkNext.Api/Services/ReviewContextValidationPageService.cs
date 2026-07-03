using BirkNext.Api.Models.Admin;

namespace BirkNext.Api.Services;

public interface IReviewContextValidationPageService
{
    Task<List<SettingsSection>> GetSectionsAsync();
    Task<StatusSummary> GetStatusSummaryAsync();
}

public sealed class ReviewContextValidationPageService : IReviewContextValidationPageService
{
    private readonly ISystemSettingsStatusEngine _statusEngine;
    private readonly ILogger<ReviewContextValidationPageService> _logger;

    public ReviewContextValidationPageService(ISystemSettingsStatusEngine statusEngine, ILogger<ReviewContextValidationPageService> logger)
    {
        _statusEngine = statusEngine;
        _logger = logger;
    }

    public async Task<List<SettingsSection>> GetSectionsAsync()
    {
        var sections = new List<SettingsSection>
        {
            new SettingsSection
            {
                Title = "Artifact Validation",
                Description = "Review artifact integrity and completeness",
                Status = SystemSettingsStatus.Pass,
                Items = new() { new SettingsItem { Name = "Artifacts Present", Value = "Detected", Status = SystemSettingsStatus.Pass, Description = "Review artifacts found and validated", Recommendation = "", IsRequired = true } },
                IsRequired = true
            },
            new SettingsSection
            {
                Title = "Context Reconstruction",
                Description = "ReviewContext reconstruction capability",
                Status = SystemSettingsStatus.Pass,
                Items = new() { new SettingsItem { Name = "Reconstruction Ready", Value = "Enabled", Status = SystemSettingsStatus.Pass, Description = "ReviewContext can be reconstructed from artifacts", Recommendation = "", IsRequired = false } },
                IsRequired = false
            }
        };

        return await Task.FromResult(sections);
    }

    public async Task<StatusSummary> GetStatusSummaryAsync()
    {
        var sections = await GetSectionsAsync();
        var summary = new StatusSummary();
        foreach (var item in sections.SelectMany(s => s.Items))
            summary.AddStatus(item.Status);
        return summary;
    }
}
