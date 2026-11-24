namespace BirkNext.Api.Models;

/// <summary>
/// User-owned grouping and user story → requirement review decisions of one project and specification (table <c>story_groupings</c>, 2026-11-24).
/// <see cref="ContentJson"/> is a <see cref="BirkNext.TestPlanning.StoryGroupingDocument"/>. Source-derived facts (functional areas, links the
/// specification states) are never stored here. <see cref="Revision"/> is an optimistic concurrency token: a save names the revision it started from.
/// </summary>
public sealed class StoryGroupingRecord
{
    public Guid Id { get; set; }
    public string ProjectKey { get; set; } = "";
    /// <summary>The specification file (normalized path, forward slashes) the decisions belong to.</summary>
    public string SpecificationPath { get; set; } = "";
    public int Revision { get; set; }
    public string ContentJson { get; set; } = "";
    public string UpdatedBy { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
}
