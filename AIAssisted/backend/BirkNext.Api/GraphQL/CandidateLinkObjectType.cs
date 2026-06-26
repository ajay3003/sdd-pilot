using BirkNext.Api.Models;
using HotChocolate.Types;

namespace BirkNext.Api.GraphQL;

public sealed class CandidateLinkObjectType : ObjectType<CandidateLink>
{
    protected override void Configure(IObjectTypeDescriptor<CandidateLink> descriptor)
    {
        descriptor.Field(l => l.Id).Type<NonNullType<IdType>>();
        descriptor.Field(l => l.ProjectId).Type<NonNullType<StringType>>();
        descriptor.Field(l => l.SessionId).Type<NonNullType<StringType>>();
        descriptor.Field(l => l.SourceCandidateRef).Type<NonNullType<StringType>>();
        descriptor.Field(l => l.TargetCandidateRef).Type<NonNullType<StringType>>();
        descriptor.Field(l => l.LinkType).Type<NonNullType<EnumType<CandidateLinkType>>>();
        descriptor.Field(l => l.CreatedAt).Type<NonNullType<DateTimeType>>();
    }
}
