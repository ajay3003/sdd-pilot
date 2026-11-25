using BirkNext.Api.Services;
using HotChocolate.Types;

namespace BirkNext.Api.GraphQL;

/// <summary>GraphQL object type for <see cref="CoverageSummary"/>.</summary>
public sealed class CoverageSummaryObjectType : ObjectType<CoverageSummary>
{
    protected override void Configure(IObjectTypeDescriptor<CoverageSummary> descriptor)
    {
        descriptor.Description("Aggregate TEST DESIGN LINKAGE statistics for a project: a requirement counts as covered when at least one test scenario is " +
            "linked to it (Covers). Not evidence of prepared, approved, executed or passed tests (evidenceBasis / executionEvidence).");
        descriptor.Field(s => s.EvidenceBasis).Type<NonNullType<StringType>>().Description("Always \"TestDesignLinkage\".");
        descriptor.Field(s => s.ExecutionEvidence).Type<NonNullType<StringType>>().Description("Always \"NotEvaluated\": approval, execution, result and freshness are not part of this summary.");

        descriptor.Field(s => s.TotalRequirements).Type<NonNullType<IntType>>();
        descriptor.Field(s => s.CoveredRequirements).Type<NonNullType<IntType>>().Description("Requirements with at least one linked test scenario (design linkage only).");
        descriptor.Field(s => s.NotCoveredRequirements).Type<NonNullType<IntType>>();
        descriptor.Field(s => s.CoveragePercent).Type<NonNullType<FloatType>>().Description("Share of requirements with a linked test scenario (design linkage), not executed-test coverage.");
        descriptor.Field(s => s.OrphanTests).Type<NonNullType<IntType>>();
        descriptor.Field(s => s.SuggestedCoverageRequirements).Type<NonNullType<IntType>>();
        descriptor.Field(s => s.PendingSuggestionCount).Type<NonNullType<IntType>>();
        descriptor.Field(s => s.HighConfidenceSuggestionCount).Type<NonNullType<IntType>>();
    }
}
