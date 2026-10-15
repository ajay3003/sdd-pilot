using BirkNext.SourceDomains;

namespace BirkNext.Api.Services.ContractEvolution;

/// <summary>
/// Conservative structural compatibility of one change for one party, by direction. A party's paths are the paths it writes (producer) or
/// reads (consumer); null means they are not known, which yields <see cref="ContractCompatibility.NeedsReview"/>, never Compatible. Business
/// meaning is not judged here: that is a domain overlay's job.
/// </summary>
public static class ContractCompatibilityRules
{
    public static (ContractCompatibility Compatibility, string Reason) Structural(ContractChange change, ContractPerspective perspective,
        IReadOnlyCollection<string>? paths, ContractUnknownMemberPolicy unknownMembers = ContractUnknownMemberPolicy.Unknown)
    {
        bool? Uses() => paths is null ? null : change.Path.Length > 0 && paths.Any(path => Covers(path, change.Path));
        var uses = Uses();
        (ContractCompatibility, string) ByUse(ContractCompatibility used, string usedReason, ContractCompatibility unused = ContractCompatibility.Compatible) => uses switch
        {
            true => (used, usedReason),
            false => (unused, $"{change.Path} is not {(perspective == ContractPerspective.Producer ? "written" : "read")} by this party."),
            null => (ContractCompatibility.NeedsReview, $"Which paths this party {(perspective == ContractPerspective.Producer ? "writes" : "reads")} is not known."),
        };

        // A member the contract now requires: the producer must write it.
        (ContractCompatibility, string) MustWrite(string what) => uses switch
        {
            true => (ContractCompatibility.Compatible, $"{change.Path} {what}; the producer already writes it."),
            false => (ContractCompatibility.Breaking, $"{change.Path} {what} and the producer does not write it."),
            null => (ContractCompatibility.NeedsReview, $"{change.Path} {what}; which paths the producer writes is not known."),
        };

        if (perspective == ContractPerspective.Producer)
            return change.Change switch
            {
                ContractChangeClass.FieldAddedOptional => (ContractCompatibility.Compatible, "An optional addition does not require the producer to write it."),
                ContractChangeClass.FieldAddedRequired => MustWrite("was added as required"),
                ContractChangeClass.RequirednessChanged when change.NewValue == "required" => MustWrite("became required"),
                ContractChangeClass.RequirednessChanged => (ContractCompatibility.Compatible, "Relaxing a requirement does not affect what the producer writes."),
                ContractChangeClass.FieldRemoved => ByUse(ContractCompatibility.Breaking, $"The producer still writes removed {change.Path}."),
                ContractChangeClass.TypeChanged => ByUse(ContractCompatibility.Breaking, $"The producer writes {change.Path} as {change.OldValue ?? "the old type"}; the contract now says {change.NewValue ?? "another type"}."),
                ContractChangeClass.CardinalityChanged or ContractChangeClass.OrderChanged or ContractChangeClass.EnumValueRemoved =>
                    ByUse(ContractCompatibility.NeedsReview, $"The producer writes {change.Path}; whether its output still conforms needs review."),
                ContractChangeClass.EnumValueAdded => (ContractCompatibility.Compatible, "A new allowed value does not change what the producer writes."),
                ContractChangeClass.NamespaceChanged => (ContractCompatibility.NeedsReview, "Whether the producer writes the new namespace needs review."),
                ContractChangeClass.VersionChanged => (ContractCompatibility.NeedsReview, "The declared version changed; whether the producer targets it needs review."),
                _ => (ContractCompatibility.NeedsReview, "Unclassified change: needs review."),
            };

        return change.Change switch
        {
            ContractChangeClass.FieldAddedOptional or ContractChangeClass.FieldAddedRequired => unknownMembers switch
            {
                ContractUnknownMemberPolicy.Ignore => (ContractCompatibility.Compatible, $"The consumer ignores members it does not know, so {change.Path} is ignored (structurally)."),
                ContractUnknownMemberPolicy.Reject => (ContractCompatibility.Breaking, $"The consumer rejects members it does not know, and {change.Path} is new."),
                _ => (ContractCompatibility.NeedsReview, "How the consumer treats unknown members is not known."),
            },
            ContractChangeClass.FieldRemoved => ByUse(ContractCompatibility.Breaking, $"The consumer reads removed {change.Path}."),
            ContractChangeClass.TypeChanged => ByUse(ContractCompatibility.Breaking, $"The consumer reads {change.Path} as {change.OldValue ?? "the old type"}; it is now {change.NewValue ?? "another type"}."),
            ContractChangeClass.RequirednessChanged when change.NewValue == "optional" => ByUse(ContractCompatibility.NeedsReview, $"The consumer reads {change.Path}, which may now be absent."),
            ContractChangeClass.RequirednessChanged => (ContractCompatibility.Compatible, $"{change.Path} is now always present."),
            ContractChangeClass.CardinalityChanged => ByUse(ContractCompatibility.NeedsReview, $"The consumer reads {change.Path}; its occurrence changed."),
            ContractChangeClass.EnumValueAdded => ByUse(ContractCompatibility.NeedsReview, $"The consumer reads {change.Path}, which may now carry \"{change.NewValue}\"."),
            ContractChangeClass.EnumValueRemoved => (ContractCompatibility.Compatible, "A value the consumer may receive was removed; what it already accepts still covers the rest."),
            ContractChangeClass.OrderChanged => unknownMembers == ContractUnknownMemberPolicy.Reject
                ? (ContractCompatibility.Breaking, $"The consumer validates strictly and the member order of {change.Path} changed.")
                : (ContractCompatibility.NeedsReview, $"The member order of {change.Path} changed."),
            ContractChangeClass.NamespaceChanged => (ContractCompatibility.NeedsReview, "Members in another namespace are not assumed to be the same members."),
            ContractChangeClass.VersionChanged => (ContractCompatibility.NeedsReview, "The declared version changed."),
            _ => (ContractCompatibility.NeedsReview, "Unclassified change: needs review."),
        };
    }

    /// <summary>Whether a declared party path covers a contract path (equal, or an ancestor of it).</summary>
    public static bool Covers(string declared, string path)
    {
        declared = ContractSnapshots.NormalizePath(declared);
        return string.Equals(declared, path, StringComparison.Ordinal) || path.StartsWith(declared + "/", StringComparison.Ordinal);
    }

    public static ContractCompatibility Worst(IEnumerable<ContractCompatibility> values) => values.DefaultIfEmpty(ContractCompatibility.Compatible).Max();
}
