namespace BirkNext.Api.Configuration;

public sealed class AzureDevOpsOptions
{
    public const string SectionName = "AzureDevOps";

    public bool Enabled { get; init; }
    public string OrganizationUrl { get; init; } = string.Empty;
    public string Project { get; init; } = string.Empty;
    public string RepositoryId { get; init; } = string.Empty;
    public string Pat { get; set; } = string.Empty;
    public string DefaultBranch { get; init; } = "main";

    public bool IsConfigured =>
        Enabled &&
        !string.IsNullOrWhiteSpace(OrganizationUrl) &&
        !string.IsNullOrWhiteSpace(Project) &&
        !string.IsNullOrWhiteSpace(Pat);
}
