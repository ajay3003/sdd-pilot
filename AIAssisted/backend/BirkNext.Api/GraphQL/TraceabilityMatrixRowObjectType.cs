using BirkNext.Api.Models;
using BirkNext.Api.Services;
using HotChocolate.Types;

namespace BirkNext.Api.GraphQL;

/// <summary>GraphQL object type for <see cref="TraceLinkWithTest"/>.</summary>
public sealed class TraceLinkWithTestObjectType : ObjectType<TraceLinkWithTest>
{
    protected override void Configure(IObjectTypeDescriptor<TraceLinkWithTest> descriptor)
    {
        descriptor.Description("A test scenario paired with the trace link that connects it to a requirement.");

        descriptor.Field(t => t.Link).Type<NonNullType<ObjectType<TraceLink>>>();
        descriptor.Field(t => t.Test).Type<NonNullType<ObjectType<Scenario>>>();
    }
}

/// <summary>GraphQL object type for <see cref="TraceabilityMatrixRow"/>.</summary>
public sealed class TraceabilityMatrixRowObjectType : ObjectType<TraceabilityMatrixRow>
{
    protected override void Configure(IObjectTypeDescriptor<TraceabilityMatrixRow> descriptor)
    {
        descriptor.Description("A single row in the traceability matrix: one requirement and all tests that cover it.");

        descriptor.Field(r => r.Requirement).Type<NonNullType<ObjectType<Scenario>>>();
        descriptor.Field(r => r.LinkedTests).Type<NonNullType<ListType<NonNullType<ObjectType<TraceLinkWithTest>>>>>();
        descriptor.Field(r => r.CoverageStatus).Type<NonNullType<EnumType<CoverageStatus>>>();
    }
}
