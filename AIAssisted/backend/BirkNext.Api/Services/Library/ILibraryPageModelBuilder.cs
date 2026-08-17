namespace BirkNext.Api.Services.Library;

/// <summary>
/// Builds structured page models for Library pages.
/// Each library page (QA Artifact Library, Create Test Scenario, Sample Projects) has a specialized builder.
/// </summary>
public interface ILibraryPageModelBuilder
{
    /// <summary>Build the page model asynchronously.</summary>
    Task<LibraryPageModel> BuildPageModelAsync();
}

/// <summary>
/// Specific builders for each library page.
/// </summary>

public interface IQAArtifactLibraryPageModelBuilder : ILibraryPageModelBuilder { }

public interface ISampleProjectsPageModelBuilder : ILibraryPageModelBuilder { }
