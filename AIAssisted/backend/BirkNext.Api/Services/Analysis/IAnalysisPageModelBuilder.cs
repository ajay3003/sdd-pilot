namespace BirkNext.Api.Services.Analysis;

/// <summary>
/// Builds structured page models for Analysis pages.
/// Each analysis page (Spec Drift, Impact Analysis, etc.) has a specialized builder.
/// </summary>
public interface IAnalysisPageModelBuilder
{
    /// <summary>Build the page model asynchronously.</summary>
    Task<AnalysisPageModel> BuildPageModelAsync();
}

/// <summary>
/// Specific builders for each analysis page.
/// </summary>

public interface ISpecDriftPageModelBuilder : IAnalysisPageModelBuilder { }

public interface IImpactAnalysisPageModelBuilder : IAnalysisPageModelBuilder { }

public interface IRequirementsTraceabilityPageModelBuilder : IAnalysisPageModelBuilder { }

public interface IImplementationReviewPageModelBuilder : IAnalysisPageModelBuilder { }

public interface IImplementationTraceabilityPageModelBuilder : IAnalysisPageModelBuilder { }
