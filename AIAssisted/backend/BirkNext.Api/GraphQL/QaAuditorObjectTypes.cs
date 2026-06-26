using BirkNext.Api.Models;
using HotChocolate.Types;

namespace BirkNext.Api.GraphQL;

public sealed class QaScoreDeductionObjectType : ObjectType<QaScoreDeduction>
{
    protected override void Configure(IObjectTypeDescriptor<QaScoreDeduction> descriptor)
    {
        descriptor.Field(f => f.Category).Type<NonNullType<StringType>>();
        descriptor.Field(f => f.Reason).Type<NonNullType<StringType>>();
        descriptor.Field(f => f.Points).Type<NonNullType<IntType>>();
    }
}

public sealed class QaAuditReportObjectType : ObjectType<QaAuditReport>
{
    protected override void Configure(IObjectTypeDescriptor<QaAuditReport> descriptor)
    {
        descriptor.Field(f => f.QualityScore).Type<NonNullType<IntType>>();
        descriptor.Field(f => f.ReadinessStatus).Type<NonNullType<EnumType<QaReadinessStatus>>>();
        descriptor.Field(f => f.AiExecutiveSummary).Type<StringType>();
        descriptor.Field(f => f.AiConcerns).Type<NonNullType<ListType<NonNullType<StringType>>>>();
        descriptor.Field(f => f.AiRecommendedActions).Type<NonNullType<ListType<NonNullType<StringType>>>>();
        descriptor.Field(f => f.CoveragePercent).Type<NonNullType<FloatType>>();
        descriptor.Field(f => f.TotalRequirements).Type<NonNullType<IntType>>();
        descriptor.Field(f => f.RequirementsAtRisk).Type<NonNullType<IntType>>();
        descriptor.Field(f => f.HighRiskRequirements).Type<NonNullType<IntType>>();
        descriptor.Field(f => f.DriftFindingsCount).Type<NonNullType<IntType>>();
        descriptor.Field(f => f.HighRiskDriftFindings).Type<NonNullType<IntType>>();
        descriptor.Field(f => f.OrphanTestCount).Type<NonNullType<IntType>>();
        descriptor.Field(f => f.TotalCodeFiles).Type<NonNullType<IntType>>();
        descriptor.Field(f => f.UnlinkedCodeFiles).Type<NonNullType<IntType>>();
        descriptor.Field(f => f.DriftFindings).Type<NonNullType<ListType<NonNullType<DriftFindingObjectType>>>>();
        descriptor.Field(f => f.TopRisks).Type<NonNullType<ListType<NonNullType<DriftRequirementObjectType>>>>();
        descriptor.Field(f => f.RecommendedActions).Type<NonNullType<ListType<NonNullType<StringType>>>>();
        descriptor.Field(f => f.ScoreDeductions).Type<NonNullType<ListType<NonNullType<QaScoreDeductionObjectType>>>>();
    }
}
