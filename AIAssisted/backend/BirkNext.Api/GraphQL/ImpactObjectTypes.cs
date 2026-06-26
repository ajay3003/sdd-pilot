using BirkNext.Api.Models;
using BirkNext.Api.Services;
using HotChocolate.Types;

namespace BirkNext.Api.GraphQL;

/// <summary>GraphQL object type for <see cref="ImpactedTest"/>.</summary>
public sealed class ImpactedTestObjectType : ObjectType<ImpactedTest>
{
    protected override void Configure(IObjectTypeDescriptor<ImpactedTest> descriptor)
    {
        descriptor.Description("A test scenario linked to a requirement via a Covers trace link.");
        descriptor.Field(t => t.Test).Type<NonNullType<ObjectType<Scenario>>>();
        descriptor.Field(t => t.Link).Type<NonNullType<ObjectType<TraceLink>>>();
    }
}

/// <summary>GraphQL object type for <see cref="RegressionItem"/>.</summary>
public sealed class RegressionItemObjectType : ObjectType<RegressionItem>
{
    protected override void Configure(IObjectTypeDescriptor<RegressionItem> descriptor)
    {
        descriptor.Description("A test recommended for regression, with the reason it is included.");
        descriptor.Field(r => r.Test).Type<NonNullType<ObjectType<Scenario>>>();
        descriptor.Field(r => r.Reason).Type<NonNullType<StringType>>();
    }
}

/// <summary>GraphQL object type for <see cref="RequirementImpactSummary"/>.</summary>
public sealed class RequirementImpactSummaryObjectType : ObjectType<RequirementImpactSummary>
{
    protected override void Configure(IObjectTypeDescriptor<RequirementImpactSummary> descriptor)
    {
        descriptor.Description("Aggregate impact metrics for a single requirement.");
        descriptor.Field(s => s.TotalLinkedTests).Type<NonNullType<IntType>>();
        descriptor.Field(s => s.AcceptedTests).Type<NonNullType<IntType>>();
        descriptor.Field(s => s.MissingCoverage).Type<NonNullType<IntType>>();
        descriptor.Field(s => s.RiskLevel).Type<NonNullType<EnumType<RiskLevel>>>();
    }
}

/// <summary>GraphQL object type for <see cref="RequirementImpact"/>.</summary>
public sealed class RequirementImpactObjectType : ObjectType<RequirementImpact>
{
    protected override void Configure(IObjectTypeDescriptor<RequirementImpact> descriptor)
    {
        descriptor.Description("Full impact analysis for a single requirement.");
        descriptor.Field(r => r.Requirement).Type<NonNullType<ObjectType<Scenario>>>();
        descriptor.Field(r => r.LinkedTests).Type<NonNullType<ListType<NonNullType<ObjectType<ImpactedTest>>>>>();
        descriptor.Field(r => r.RegressionRecommendation).Type<NonNullType<ListType<NonNullType<ObjectType<RegressionItem>>>>>();
        descriptor.Field(r => r.Summary).Type<NonNullType<ObjectType<RequirementImpactSummary>>>();
    }
}

/// <summary>GraphQL object type for <see cref="RequirementRiskItem"/>.</summary>
public sealed class RequirementRiskItemObjectType : ObjectType<RequirementRiskItem>
{
    protected override void Configure(IObjectTypeDescriptor<RequirementRiskItem> descriptor)
    {
        descriptor.Description("A requirement with its computed risk level.");
        descriptor.Field(r => r.Requirement).Type<NonNullType<ObjectType<Scenario>>>();
        descriptor.Field(r => r.RiskLevel).Type<NonNullType<EnumType<RiskLevel>>>();
        descriptor.Field(r => r.LinkedTestCount).Type<NonNullType<IntType>>();
    }
}

/// <summary>GraphQL object type for <see cref="ImpactSummary"/>.</summary>
public sealed class ImpactSummaryObjectType : ObjectType<ImpactSummary>
{
    protected override void Configure(IObjectTypeDescriptor<ImpactSummary> descriptor)
    {
        descriptor.Description("Project-wide impact summary: all requirements ranked by risk.");
        descriptor.Field(s => s.TotalRequirements).Type<NonNullType<IntType>>();
        descriptor.Field(s => s.HighRiskCount).Type<NonNullType<IntType>>();
        descriptor.Field(s => s.MediumRiskCount).Type<NonNullType<IntType>>();
        descriptor.Field(s => s.LowRiskCount).Type<NonNullType<IntType>>();
        descriptor.Field(s => s.Requirements).Type<NonNullType<ListType<NonNullType<ObjectType<RequirementRiskItem>>>>>();
    }
}
