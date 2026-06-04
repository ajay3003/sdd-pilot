using BirkNext.Api.Models;
using HotChocolate.Types.Relay;

namespace BirkNext.Api.GraphQL;

/// <summary>Input for the createTraceLink mutation.</summary>
public record CreateTraceLinkInput(
    string ProjectId,
    [ID] string SourceId,
    string SourceKind,
    [ID] string TargetId,
    string TargetKind,
    TraceLinkType LinkType,
    string? CreatedBy,
    string? Notes);
