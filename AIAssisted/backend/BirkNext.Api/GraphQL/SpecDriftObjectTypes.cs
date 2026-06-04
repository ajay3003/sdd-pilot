using BirkNext.Api.Models;
using HotChocolate.Types;

namespace BirkNext.Api.GraphQL;

public sealed class DriftRequirementObjectType : ObjectType<DriftRequirement>
{
    protected override void Configure(IObjectTypeDescriptor<DriftRequirement> descriptor)
    {
        descriptor.Description("A requirement showing signs of spec drift — uncovered or partially covered.");
        descriptor.Field(r => r.Requirement).Type<NonNullType<ObjectType<Scenario>>>();
        descriptor.Field(r => r.DriftRisk).Type<NonNullType<EnumType<RiskLevel>>>();
        descriptor.Field(r => r.LinkedTestCount).Type<NonNullType<IntType>>();
        descriptor.Field(r => r.DriftReason).Type<NonNullType<StringType>>();
    }
}

public sealed class DriftFindingObjectType : ObjectType<DriftFinding>
{
    protected override void Configure(IObjectTypeDescriptor<DriftFinding> descriptor)
    {
        descriptor.Description("A single drift finding produced by a deterministic drift rule.");
        descriptor.Field(f => f.Category).Type<NonNullType<StringType>>();
        descriptor.Field(f => f.Description).Type<NonNullType<StringType>>();
        descriptor.Field(f => f.Severity).Type<NonNullType<EnumType<RiskLevel>>>();
    }
}

public sealed class SpecDriftReportObjectType : ObjectType<SpecDriftReport>
{
    protected override void Configure(IObjectTypeDescriptor<SpecDriftReport> descriptor)
    {
        descriptor.Description("Deterministic spec drift report. Computed on demand from live traceability data.");
        descriptor.Field(r => r.OverallDriftRisk).Type<NonNullType<EnumType<RiskLevel>>>();
        descriptor.Field(r => r.TotalRequirements).Type<NonNullType<IntType>>();
        descriptor.Field(r => r.RequirementsAtRisk).Type<NonNullType<IntType>>();
        descriptor.Field(r => r.CoverageGaps).Type<NonNullType<IntType>>();
        descriptor.Field(r => r.OrphanTestCount).Type<NonNullType<IntType>>();
        descriptor.Field(r => r.CoveragePercent).Type<NonNullType<FloatType>>();
        descriptor.Field(r => r.RequirementsAtRiskList).Type<NonNullType<ListType<NonNullType<ObjectType<DriftRequirement>>>>>();
        descriptor.Field(r => r.OrphanTests).Type<NonNullType<ListType<NonNullType<ObjectType<Scenario>>>>>();
        descriptor.Field(r => r.Findings).Type<NonNullType<ListType<NonNullType<ObjectType<DriftFinding>>>>>();
        descriptor.Field(r => r.RecommendedActions).Type<NonNullType<ListType<NonNullType<StringType>>>>();
    }
}
