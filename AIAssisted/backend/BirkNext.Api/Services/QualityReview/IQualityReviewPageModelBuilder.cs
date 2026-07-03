namespace BirkNext.Api.Services.QualityReview;

/// <summary>
/// Interface for building structured QualityReviewPageModel from page-specific data.
/// Each Quality Review page implementation must build a consistent model structure.
/// </summary>
public interface IQualityReviewPageModelBuilder
{
    /// <summary>Builds the complete page model with readiness status, packs, and checks.</summary>
    Task<QualityReviewPageModel> BuildPageModelAsync();
}

/// <summary>Builds model for Quality Review page (workspace artifacts + QA packs)</summary>
public interface IQualityReviewPageModelBuilder_QualityReview : IQualityReviewPageModelBuilder { }

/// <summary>Builds model for Frontend Quality Review page (target URL + frontend checks)</summary>
public interface IQualityReviewPageModelBuilder_FrontendQuality : IQualityReviewPageModelBuilder { }

/// <summary>Builds model for API Quality Review page (endpoints + API checks)</summary>
public interface IQualityReviewPageModelBuilder_ApiQuality : IQualityReviewPageModelBuilder { }

/// <summary>Builds model for Integration Quality Review page (integrations + configuration checks)</summary>
public interface IQualityReviewPageModelBuilder_IntegrationQuality : IQualityReviewPageModelBuilder { }
