namespace BirkNext.Web.Services.SchemaStudio;

/// <summary>One primary area of Schema Studio and the views it groups (view names are the existing view identifiers, unchanged).</summary>
public sealed record SchemaStudioGroup(string Name, string Purpose, IReadOnlyList<string> Views);

/// <summary>
/// Two-level Schema Studio navigation: five primary areas, each with its own views. Every existing view keeps its identifier, so search
/// jumps, programmatic switches and <c>?view=</c> deep links select the right area. Presentation only — no schema semantics live here.
/// </summary>
public static class SchemaStudioNavigation
{
    public const string DefaultView = "Overview";

    public static readonly IReadOnlyList<SchemaStudioGroup> Groups =
    [
        new("Understand", "What the message looks like for a tester: structure, mandatory fields, rules and documentation.", ["Overview", "Forretning", "Felt og regler"]),
        new("Schema", "Technical schema structure, declarations, constraints and how files relate.",
            ["Struktur", "Schema Tree", "Types", "Constraints", "Namespaces", "Schema dependencies", "References"]),
        new("Inspect", "The effective (resolved) schema and the source XSD as imported.", ["Effective Schema", "Raw XSD"]),
        new("Design & Compare", "Draft changes in a working copy and compare package versions.", ["Designer", "Compare"]),
        new("Validate & Test", "Schema-derived test cases and synthetic XML generation.", ["Generated Tests", "Generation Profile"]),
    ];

    /// <summary>Every view, in navigation order (the former flat tab list).</summary>
    public static IReadOnlyList<string> AllViews { get; } = [.. Groups.SelectMany(group => group.Views)];

    public static SchemaStudioGroup GroupOf(string? view) =>
        Groups.FirstOrDefault(group => group.Views.Contains(view ?? "", StringComparer.Ordinal)) ?? Groups[0];

    /// <summary>One line saying what a view shows, so views that look alike (Struktur vs Schema Tree, dependencies vs references) are told apart.</summary>
    public static string Purpose(string view) => view switch
    {
        "Overview" => "Package summary and where to start.",
        "Forretning" => "The message as a tester reads it: fields in order, mandatory or optional, with the schema's own documentation.",
        "Felt og regler" => "Every field with occurrence, type, XSD rules (allowed values, patterns, lengths) and documentation.",
        "Struktur" => "Effective message structure: the instance hierarchy with types, occurrence and where each part is declared.",
        "Schema Tree" => "Schema declarations grouped by file: global elements and types as written in each XSD.",
        "Types" => "Global simple and complex types with their base, facets and how often they are used.",
        "Constraints" => "Every XSD constraint by path: enumerations, patterns, lengths, numeric bounds and occurrence.",
        "Namespaces" => "Target namespaces, the files that declare them and which files import them.",
        "Schema dependencies" => "File-level dependencies: imports and includes between XSD files and their resolution.",
        "References" => "Cross-file declaration references: type use, inheritance and element references between files.",
        "Effective Schema" => "The resolved schema with inherited and referenced members, keeping where each was declared.",
        "Raw XSD" => "The imported XSD exactly as received (read-only source evidence).",
        "Designer" => "Edit a working draft; saving creates a new immutable version, the imported source is never changed.",
        "Compare" => "Declaration-level differences between this package and another imported version.",
        "Generated Tests" => "Valid and invalid cases derived from XSD declarations, run locally against this package.",
        "Generation Profile" => "Rules for generating synthetic XML: profile, per-element rules and XML generation.",
        _ => "",
    };

    /// <summary>URL form of a view (<c>?view=generated-tests</c>).</summary>
    public static string Slug(string view) =>
        new string(view.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Replace("--", "-").Trim('-');

    /// <summary>The view a slug or a view name identifies; null for anything else (never guessed).</summary>
    public static string? FromQuery(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null
        : AllViews.FirstOrDefault(view => string.Equals(view, value, StringComparison.OrdinalIgnoreCase) || string.Equals(Slug(view), value.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Views whose content is wide (tables, editors, graphs) and get the full content width; text views keep a readable width.</summary>
    public static bool IsWide(string view) => view is "Felt og regler" or "Types" or "Constraints" or "References" or "Schema dependencies"
        or "Raw XSD" or "Designer" or "Generated Tests" or "Effective Schema" or "Generation Profile";
}
