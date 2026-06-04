using BirkNext.Api.Models;
using BirkNext.Api.Services;
using HotChocolate.Types;

namespace BirkNext.Api.GraphQL;

public sealed class CodeFileObjectType : ObjectType<CodeFile>
{
    protected override void Configure(IObjectTypeDescriptor<CodeFile> descriptor)
    {
        descriptor.Description("A source-code file registered in the code traceability system.");
        descriptor.Field(f => f.Id).Type<NonNullType<IdType>>();
        descriptor.Field(f => f.ProjectId).Type<NonNullType<StringType>>();
        descriptor.Field(f => f.FilePath).Type<NonNullType<StringType>>();
        descriptor.Field(f => f.FileName).Type<NonNullType<StringType>>();
        descriptor.Field(f => f.Description).Type<StringType>();
        descriptor.Field(f => f.CreatedAt).Type<NonNullType<DateTimeType>>();
    }
}

public sealed class CodeLinkObjectType : ObjectType<CodeLink>
{
    protected override void Configure(IObjectTypeDescriptor<CodeLink> descriptor)
    {
        descriptor.Description("A link between a code file and a QA scenario (requirement or test).");
        descriptor.Field(l => l.Id).Type<NonNullType<IdType>>();
        descriptor.Field(l => l.ProjectId).Type<NonNullType<StringType>>();
        descriptor.Field(l => l.CodeFileId).Type<NonNullType<IdType>>();
        descriptor.Field(l => l.ScenarioId).Type<NonNullType<IdType>>();
        descriptor.Field(l => l.ScenarioKind).Type<NonNullType<StringType>>();
        descriptor.Field(l => l.CreatedAt).Type<NonNullType<DateTimeType>>();
    }
}

public sealed class CodeLinkWithScenarioObjectType : ObjectType<CodeLinkWithScenario>
{
    protected override void Configure(IObjectTypeDescriptor<CodeLinkWithScenario> descriptor)
    {
        descriptor.Description("A code link paired with its resolved scenario.");
        descriptor.Field(x => x.Link).Type<NonNullType<ObjectType<CodeLink>>>();
        descriptor.Field(x => x.Scenario).Type<NonNullType<ObjectType<Scenario>>>();
    }
}

public sealed class CodeImpactObjectType : ObjectType<CodeImpact>
{
    protected override void Configure(IObjectTypeDescriptor<CodeImpact> descriptor)
    {
        descriptor.Description("Full code impact for a file: all linked requirements and tests.");
        descriptor.Field(i => i.File).Type<NonNullType<ObjectType<CodeFile>>>();
        descriptor.Field(i => i.LinkedRequirements).Type<NonNullType<ListType<NonNullType<ObjectType<CodeLinkWithScenario>>>>>();
        descriptor.Field(i => i.LinkedTests).Type<NonNullType<ListType<NonNullType<ObjectType<CodeLinkWithScenario>>>>>();
    }
}

public sealed class CodeSummaryObjectType : ObjectType<CodeSummary>
{
    protected override void Configure(IObjectTypeDescriptor<CodeSummary> descriptor)
    {
        descriptor.Description("Project-wide code traceability summary.");
        descriptor.Field(s => s.TotalFiles).Type<NonNullType<IntType>>();
        descriptor.Field(s => s.LinkedRequirements).Type<NonNullType<IntType>>();
        descriptor.Field(s => s.LinkedTests).Type<NonNullType<IntType>>();
        descriptor.Field(s => s.UnlinkedFiles).Type<NonNullType<IntType>>();
    }
}
