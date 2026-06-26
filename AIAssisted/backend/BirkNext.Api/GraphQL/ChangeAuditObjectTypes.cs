using BirkNext.Api.Models;
using BirkNext.Api.Services;
using HotChocolate.Types;

namespace BirkNext.Api.GraphQL;

public sealed class AuditAffectedRequirementObjectType : ObjectType<AuditAffectedRequirement>
{
    protected override void Configure(IObjectTypeDescriptor<AuditAffectedRequirement> descriptor)
    {
        descriptor.Description("A requirement identified as potentially affected by the described change, enriched with formal impact data.");
        descriptor.Field(r => r.Requirement).Type<NonNullType<ObjectType<Scenario>>>();
        descriptor.Field(r => r.RiskLevel).Type<NonNullType<EnumType<RiskLevel>>>();
        descriptor.Field(r => r.LinkedTestCount).Type<NonNullType<IntType>>();
        descriptor.Field(r => r.AiRelevanceReason).Type<NonNullType<StringType>>();
    }
}

public sealed class AuditAffectedTestObjectType : ObjectType<AuditAffectedTest>
{
    protected override void Configure(IObjectTypeDescriptor<AuditAffectedTest> descriptor)
    {
        descriptor.Description("A test scenario identified as potentially affected by the described change.");
        descriptor.Field(t => t.Test).Type<NonNullType<ObjectType<Scenario>>>();
        descriptor.Field(t => t.AiRelevanceReason).Type<NonNullType<StringType>>();
    }
}

public sealed class ChangeAuditReportObjectType : ObjectType<ChangeAuditReport>
{
    protected override void Configure(IObjectTypeDescriptor<ChangeAuditReport> descriptor)
    {
        descriptor.Description("AI-generated change audit report combining Claude's semantic analysis with formal impact data.");
        descriptor.Field(r => r.ChangeDescription).Type<NonNullType<StringType>>();
        descriptor.Field(r => r.OverallRiskLevel).Type<NonNullType<EnumType<RiskLevel>>>();
        descriptor.Field(r => r.AiReasoning).Type<NonNullType<StringType>>();
        descriptor.Field(r => r.RegressionScope).Type<NonNullType<StringType>>();
        descriptor.Field(r => r.AffectedRequirements).Type<NonNullType<ListType<NonNullType<ObjectType<AuditAffectedRequirement>>>>>();
        descriptor.Field(r => r.AffectedTests).Type<NonNullType<ListType<NonNullType<ObjectType<AuditAffectedTest>>>>>();
        descriptor.Field(r => r.CoverageGaps).Type<NonNullType<ListType<NonNullType<StringType>>>>();
        descriptor.Field(r => r.RecommendedRegressionTests).Type<NonNullType<ListType<NonNullType<ObjectType<RegressionItem>>>>>();
    }
}
