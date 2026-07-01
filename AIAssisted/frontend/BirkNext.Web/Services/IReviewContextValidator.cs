using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Validates ReviewContext consistency against downstream result models.
/// Developer-only diagnostic service.
/// </summary>
public interface IReviewContextValidator
{
    /// <summary>
    /// Generate validation report comparing ReviewContext canonical metrics to downstream sources.
    /// </summary>
    ReviewContextValidationReport Validate(
        ConstitutionDocument? constitution,
        SpecTree? spec,
        PlanDocument? plan,
        TaskTree? tasks,
        string projectName = "Current Project");

    /// <summary>
    /// Generate validation report from ReviewContext and downstream sources.
    /// </summary>
    ReviewContextValidationReport ValidateContext(
        ReviewContext reviewContext,
        ArtifactTraceabilityReport? traceabilityReport,
        string projectName = "Current Project");
}
