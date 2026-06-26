using BirkNext.Api.Models;
using HotChocolate.Types;

namespace BirkNext.Api.GraphQL;

/// <summary>GraphQL object type configuration for <see cref="TraceLink"/>.</summary>
public sealed class TraceLinkObjectType : ObjectType<TraceLink>
{
    protected override void Configure(IObjectTypeDescriptor<TraceLink> descriptor)
    {
        descriptor.Description("A directional link between two artifacts in BirkNext.");

        descriptor.Field(t => t.Id).Type<NonNullType<IdType>>();
        descriptor.Field(t => t.ProjectId).Type<NonNullType<StringType>>();
        descriptor.Field(t => t.SourceId).Type<NonNullType<IdType>>();
        descriptor.Field(t => t.SourceKind).Type<NonNullType<StringType>>();
        descriptor.Field(t => t.TargetId).Type<NonNullType<IdType>>();
        descriptor.Field(t => t.TargetKind).Type<NonNullType<StringType>>();
        descriptor.Field(t => t.LinkType).Type<NonNullType<EnumType<TraceLinkType>>>();
        descriptor.Field(t => t.CreatedAt).Type<NonNullType<DateTimeType>>();
        descriptor.Field(t => t.CreatedBy).Type<StringType>();
        descriptor.Field(t => t.Notes).Type<StringType>();
    }
}
