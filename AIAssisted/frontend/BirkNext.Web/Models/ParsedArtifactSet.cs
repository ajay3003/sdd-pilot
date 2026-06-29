namespace BirkNext.Web.Models;

/// <summary>
/// The result of parsing all four artifact texts through the shared
/// <see cref="BirkNext.Web.Services.IArtifactParserService"/>.
/// Any property is null when the corresponding artifact text was not provided.
///
/// Eliminates duplicated parsing code across pages. Pages parse once via
/// <see cref="BirkNext.Web.Services.IArtifactParserService"/> and receive this model;
/// they then pass individual properties to whichever analysis service they use.
/// </summary>
public sealed class ParsedArtifactSet
{
    public ConstitutionDocument? Constitution { get; init; }
    public SpecTree?             Spec         { get; init; }
    public PlanDocument?         Plan         { get; init; }
    public TaskTree?             Tasks        { get; init; }

    /// <summary>True if at least one artifact was parsed.</summary>
    public bool HasAnyArtifact =>
        Constitution is not null || Spec is not null || Plan is not null || Tasks is not null;
}
