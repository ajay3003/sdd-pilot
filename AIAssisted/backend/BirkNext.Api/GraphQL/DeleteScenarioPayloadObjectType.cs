using HotChocolate.Types;

namespace BirkNext.Api.GraphQL;

public class DeleteScenarioPayloadObjectType : ObjectType<DeleteScenarioPayload>
{
    protected override void Configure(IObjectTypeDescriptor<DeleteScenarioPayload> descriptor)
    {
        descriptor.Description("Payload returned by the deleteScenario mutation.");
        descriptor.Field(p => p.DeletedId)
            .Description("The ID of the deleted scenario on success; null on failure.")
            .Type<IdType>();
        descriptor.Field(p => p.Success)
            .Description("True when the scenario was successfully deleted.")
            .Type<NonNullType<BooleanType>>();
        descriptor.Field(p => p.Errors)
            .Description("Business errors that prevented deletion.");
        descriptor.Field(p => p.CorrelationId)
            .Description("Correlation ID for tracing this request in logs.")
            .Type<NonNullType<StringType>>();
    }
}
