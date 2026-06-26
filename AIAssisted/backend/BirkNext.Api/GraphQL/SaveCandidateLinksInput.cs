using BirkNext.Api.Models;

namespace BirkNext.Api.GraphQL;

public record SaveCandidateLinkItemInput(
    string SourceCandidateRef,
    string TargetCandidateRef,
    CandidateLinkType LinkType);

public record SaveCandidateLinksInput(
    string ProjectId,
    string SessionId,
    IReadOnlyList<SaveCandidateLinkItemInput> Links);
