using BirkNext.Api.Models;
using HotChocolate.Types;

namespace BirkNext.Api.GraphQL;

public class QaDeltaReviewObjectType : ObjectType<QaDeltaReview>
{
    protected override void Configure(IObjectTypeDescriptor<QaDeltaReview> descriptor)
    {
        descriptor.Description("Represents a saved QA delta review from a specification comparison.");
        descriptor.Field(r => r.Id).Type<NonNullType<IdType>>();
        descriptor.Field(r => r.Title).Type<NonNullType<StringType>>();
        descriptor.Field(r => r.ProjectId).Type<NonNullType<StringType>>();
        descriptor.Field(r => r.CreatedAt).Type<NonNullType<DateTimeType>>();
        descriptor.Field(r => r.OldSpecFileName).Type<StringType>();
        descriptor.Field(r => r.NewSpecFileName).Type<StringType>();
        descriptor.Field(r => r.OldSpecHash).Type<StringType>();
        descriptor.Field(r => r.NewSpecHash).Type<StringType>();
        descriptor.Field(r => r.OldSpecSize).Type<IntType>();
        descriptor.Field(r => r.NewSpecSize).Type<IntType>();
        descriptor.Field(r => r.AnalysisProfile).Type<NonNullType<StringType>>();
        descriptor.Field(r => r.SummaryJson).Type<NonNullType<StringType>>();
        descriptor.Field(r => r.DeltaItemsJson).Type<NonNullType<StringType>>();
    }
}
