using BirkNext.Api.Services;
using HotChocolate.Types;

namespace BirkNext.Api.GraphQL;

/// <summary>GraphQL object type for <see cref="CoverageSummary"/>.</summary>
public sealed class CoverageSummaryObjectType : ObjectType<CoverageSummary>
{
    protected override void Configure(IObjectTypeDescriptor<CoverageSummary> descriptor)
    {
        descriptor.Description("Aggregate coverage statistics for a project.");

        descriptor.Field(s => s.TotalRequirements).Type<NonNullType<IntType>>();
        descriptor.Field(s => s.CoveredRequirements).Type<NonNullType<IntType>>();
        descriptor.Field(s => s.NotCoveredRequirements).Type<NonNullType<IntType>>();
        descriptor.Field(s => s.CoveragePercent).Type<NonNullType<FloatType>>();
        descriptor.Field(s => s.OrphanTests).Type<NonNullType<IntType>>();
    }
}
