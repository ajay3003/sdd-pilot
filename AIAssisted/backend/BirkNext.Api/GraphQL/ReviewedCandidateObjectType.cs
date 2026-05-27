using BirkNext.Api.Models;
using HotChocolate.Types;

namespace BirkNext.Api.GraphQL;

public sealed class ReviewedCandidateObjectType : ObjectType<ReviewedCandidate>
{
    protected override void Configure(IObjectTypeDescriptor<ReviewedCandidate> descriptor)
    {
        descriptor.Description("A candidate extracted from a document with its QA review decision.");

        descriptor.Field(c => c.Id).Type<NonNullType<IdType>>();
        descriptor.Field(c => c.Title).Type<NonNullType<StringType>>();
        descriptor.Field(c => c.Classification).Type<NonNullType<EnumType<ScenarioKind>>>();
        descriptor.Field(c => c.ReviewStatus).Type<NonNullType<EnumType<CandidateReviewStatus>>>();
        descriptor.Field(c => c.SourceDocument).Type<StringType>();
        descriptor.Field(c => c.SourceSection).Type<StringType>();
        descriptor.Field(c => c.ProjectId).Type<NonNullType<StringType>>();
        descriptor.Field(c => c.SessionId).Type<NonNullType<StringType>>();
        descriptor.Field(c => c.ReviewedBy).Type<NonNullType<StringType>>();
        descriptor.Field(c => c.CreatedAt).Type<NonNullType<DateTimeType>>();
        descriptor.Field(c => c.ReviewedAt).Type<DateTimeType>();
    }
}
