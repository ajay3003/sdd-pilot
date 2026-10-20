using BirkNext.Schemas;

namespace BirkNext.Web.Services.SchemaStudio;

/// <summary>What a schema tree view narrows to. Filters only hide; they never change what the schema says.</summary>
public enum SchemaTreeFilterKind { All, Mandatory, Optional, WithDocumentation, WithConstraints }

/// <summary>
/// Progressive disclosure for large effective-schema trees (Forretning, Effective Schema): which nodes are open by default, and which nodes a
/// filter or search keeps. A node is kept when it matches or any descendant matches, so a match is always shown with its path. Pure.
/// </summary>
public static class SchemaTreeFilter
{
    /// <summary>Levels below the root that are open by default: the root's own fields are visible, deeper groups start collapsed.</summary>
    public const int DefaultOpenDepth = 1;

    /// <summary>Mandatory = at least one occurrence required and not one alternative of an xs:choice (a choice member is required only when chosen).</summary>
    public static bool IsMandatory(EffectiveMember member) => member.MinOccurs != "0" && !member.InChoice;

    public static bool IsOptional(EffectiveMember member) => member.MinOccurs == "0";

    public static bool Matches(EffectiveMember member, SchemaTreeFilterKind filter, string? search)
    {
        var term = search?.Trim() ?? "";
        var kindMatch = filter switch
        {
            SchemaTreeFilterKind.Mandatory => IsMandatory(member),
            SchemaTreeFilterKind.Optional => IsOptional(member),
            SchemaTreeFilterKind.WithDocumentation => !string.IsNullOrWhiteSpace(member.Documentation) || !string.IsNullOrWhiteSpace(member.TypeDocumentation),
            SchemaTreeFilterKind.WithConstraints => member.Facets.Count > 0 || (member.Attributes ?? []).Any(attribute => attribute.Facets.Count > 0),
            _ => true,
        };
        var textMatch = term.Length == 0 || member.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
            || (member.Type ?? "").Contains(term, StringComparison.OrdinalIgnoreCase)
            || (member.Documentation ?? "").Contains(term, StringComparison.OrdinalIgnoreCase);
        return kindMatch && textMatch;
    }

    /// <summary>Whether the node or any descendant matches (bounded depth, like the views that render it).</summary>
    public static bool Keep(EffectiveMember member, SchemaTreeFilterKind filter, string? search, int depth = 0) =>
        Matches(member, filter, search) || (depth < 40 && member.Children.Any(child => Keep(child, filter, search, depth + 1)));

    public static bool IsFiltering(SchemaTreeFilterKind filter, string? search) => filter != SchemaTreeFilterKind.All || !string.IsNullOrWhiteSpace(search);

    /// <summary>Open state of a node: forced by Expand/Collapse all; while filtering, open so matches are visible; otherwise the default depth.</summary>
    public static bool IsOpen(int depth, bool? expandAll, bool filtering) => expandAll ?? (filtering || depth < DefaultOpenDepth);

    public static string Label(SchemaTreeFilterKind filter) => filter switch
    {
        SchemaTreeFilterKind.Mandatory => "Mandatory only",
        SchemaTreeFilterKind.Optional => "Optional only",
        SchemaTreeFilterKind.WithDocumentation => "With documentation",
        SchemaTreeFilterKind.WithConstraints => "With constraints",
        _ => "All fields",
    };

    /// <summary>Constraint categories for the Constraints view filter (by facet kind, as the XSD names them).</summary>
    public static string ConstraintCategory(string facetKind) => facetKind switch
    {
        "enumeration" => "Enumeration",
        "pattern" => "Pattern",
        "length" or "minLength" or "maxLength" => "Length",
        "minInclusive" or "maxInclusive" or "minExclusive" or "maxExclusive" or "totalDigits" or "fractionDigits" => "Numeric bounds",
        "Occurrence" => "Occurrence",
        _ => "Other",
    };
}
