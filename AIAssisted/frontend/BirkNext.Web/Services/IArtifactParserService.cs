using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Shared artifact parsing service. Converts raw markdown text strings into
/// typed domain models, handling all four artifact types in one call.
///
/// Replaces the duplicated parsing pattern that previously appeared in every page:
/// <code>
///     constitution = ConstitutionService.Parse(text);
///     spec         = SpecExplorerService.Parse(text);
///     plan         = PlanService.Parse(text);
///     tasks        = TaskExplorerService.Parse(text);
/// </code>
///
/// Pages that need parsed domain models inject this service and call
/// <see cref="Parse"/> once. Null-safety is preserved: if a text string is null
/// or whitespace the corresponding model property is null.
/// </summary>
public interface IArtifactParserService
{
    /// <summary>
    /// Parse any combination of artifact texts into their domain models.
    /// Null or whitespace inputs produce null in the corresponding output property.
    /// </summary>
    ParsedArtifactSet Parse(
        string? constitutionText,
        string? specText,
        string? planText,
        string? taskText);
}
