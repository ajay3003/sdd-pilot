using BirkNext.Web.GraphQL;

namespace BirkNext.Web.Models;

public sealed record CandidateLinkEntry(Guid SourceId, Guid TargetId, CandidateLinkType LinkType);
