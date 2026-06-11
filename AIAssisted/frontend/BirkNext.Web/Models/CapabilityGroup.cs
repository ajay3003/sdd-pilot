namespace BirkNext.Web.Models;

public sealed record CapabilityGroup(
    string Key,
    string Label,
    string Subtitle,
    IReadOnlyList<ExtractionCandidate> AllCandidates);
