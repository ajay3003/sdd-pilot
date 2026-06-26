using HotChocolate.Types.Relay;

namespace BirkNext.Api.GraphQL;

/// <summary>Input for the deleteTraceLink mutation.</summary>
public record DeleteTraceLinkInput([ID] string Id, string ProjectId);
