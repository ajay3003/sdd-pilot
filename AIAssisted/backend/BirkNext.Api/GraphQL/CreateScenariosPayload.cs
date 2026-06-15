using BirkNext.Api.Models;
using HotChocolate.Types;

namespace BirkNext.Api.GraphQL;

/// <summary>Marker interface for the CreateScenarioResult GraphQL union type.</summary>
public interface ICreateScenarioResult { }

/// <summary>Indicates that a single batch item was successfully created.</summary>
public sealed class CreateScenarioSuccess : ICreateScenarioResult
{
    public Scenario Scenario { get; init; } = null!;
}

/// <summary>Indicates that creation of a single batch item failed.</summary>
public sealed class CreateScenarioError : ICreateScenarioResult
{
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string? Field { get; init; }
}

/// <summary>HotChocolate union type registration for CreateScenarioResult.</summary>
public sealed class CreateScenarioResultType : UnionType<ICreateScenarioResult>
{
    protected override void Configure(IUnionTypeDescriptor descriptor)
    {
        descriptor.Name("CreateScenarioResult");
        descriptor.Description("A discriminated result for a single item in a batch scenario creation.");
        descriptor.Type<ObjectType<CreateScenarioSuccess>>();
        descriptor.Type<ObjectType<CreateScenarioError>>();
    }
}

/// <summary>Payload returned after a batch scenario creation attempt.</summary>
public sealed class CreateScenariosPayload
{
    public IReadOnlyList<ICreateScenarioResult> Results { get; init; } = [];
    public int SuccessCount { get; init; }
    public int FailureCount { get; init; }
    public string CorrelationId { get; init; } = string.Empty;

    /// <summary>Traceability suggestions generated automatically after save. Null when no suggestions were attempted.</summary>
    public SuggestionGenerationResult? SuggestionResult { get; init; }
}
