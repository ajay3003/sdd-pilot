namespace BirkNext.Api.Services.Review;

/// <summary>
/// Builder for Dashboard ReviewPageModel.
/// </summary>
public interface IDashboardPageModelBuilder
{
    Task<ReviewPageModel> BuildAsync();
}

/// <summary>
/// Builder for Constitution Explorer ReviewPageModel.
/// </summary>
public interface IConstitutionExplorerPageModelBuilder
{
    Task<ReviewPageModel> BuildAsync();
}

/// <summary>
/// Builder for Data Model Explorer ReviewPageModel.
/// </summary>
public interface IDataModelExplorerPageModelBuilder
{
    Task<ReviewPageModel> BuildAsync();
}

/// <summary>
/// Builder for Plan Explorer ReviewPageModel.
/// </summary>
public interface IPlanExplorerPageModelBuilder
{
    Task<ReviewPageModel> BuildAsync();
}

/// <summary>
/// Builder for Task Explorer ReviewPageModel.
/// </summary>
public interface ITaskExplorerPageModelBuilder
{
    Task<ReviewPageModel> BuildAsync();
}

/// <summary>
/// Builder for Specification Review/Explorer ReviewPageModel.
/// </summary>
public interface ISpecificationReviewPageModelBuilder
{
    Task<ReviewPageModel> BuildAsync();
}
