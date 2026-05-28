using BirkNext.Api.Models;
using HotChocolate.Types;

namespace BirkNext.Api.GraphQL;

public class ScenarioObjectType : ObjectType<Scenario>
{
    protected override void Configure(IObjectTypeDescriptor<Scenario> descriptor)
    {
        descriptor.Description("Represents a scenario used for specification and QA.");
        descriptor.Field(s => s.Id).Type<NonNullType<IdType>>();
        descriptor.Field(s => s.Title).Type<NonNullType<StringType>>();
        descriptor.Field(s => s.Description).Type<StringType>();
        descriptor.Field(s => s.Kind).Type<NonNullType<EnumType<ScenarioKind>>>();
        descriptor.Field(s => s.ProjectId).Type<NonNullType<StringType>>();
        descriptor.Field(s => s.CreatedAt).Type<NonNullType<DateTimeType>>();
        descriptor.Field(s => s.DisplayOrder).Type<NonNullType<IntType>>();
    }
}
