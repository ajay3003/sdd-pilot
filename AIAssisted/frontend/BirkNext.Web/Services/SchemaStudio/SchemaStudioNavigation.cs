namespace BirkNext.Web.Services.SchemaStudio;

/// <summary>One primary area of Schema Studio and the views it groups (view names are the existing view identifiers, unchanged).</summary>
public sealed record SchemaStudioGroup(string Name, string Purpose, IReadOnlyList<string> Views);

/// <summary>
/// Two-level Schema Studio navigation: five primary areas, each with its own named views. Structure is reachable from Understand (Business
/// structure, Schema tree, Effective structure). Views keep their internal identifiers (search jumps, programmatic switches); <see cref="Label"/>
/// is what a person reads, and <c>?view=</c> accepts both the label's slug and every former identifier, so saved links keep working.
/// Presentation only — no schema semantics live here.
/// </summary>
public static class SchemaStudioNavigation
{
    public const string DefaultView = "Overview";

    /// <summary>The former "Struktur" view: the business structure with types and origin (a mode of Business structure, not its own view).</summary>
    public const string TechnicalStructureAlias = "Struktur";

    public static readonly IReadOnlyList<SchemaStudioGroup> Groups =
    [
        new("Understand", "What the schema looks like: summary, structure as a tester reads it, declarations by file and the resolved structure.",
            ["Overview", "Forretning", "Schema Tree", "Effective Schema"]),
        new("Schema", "Fields, types, constraints and namespaces as the XSD defines them.", ["Felt og regler", "Types", "Constraints", "Namespaces"]),
        new("Inspect", "How files relate and the source XSD as imported.", ["References", "Schema dependencies", "Raw XSD"]),
        new("Design & Compare", "Draft changes in a working copy and compare package versions.", ["Designer", "Compare"]),
        new("Validate & Test", "Schema-derived test cases and synthetic XML generation.", ["Generated Tests", "Generation Profile"]),
    ];

    /// <summary>Every view, in navigation order.</summary>
    public static IReadOnlyList<string> AllViews { get; } = [.. Groups.SelectMany(group => group.Views)];

    public static SchemaStudioGroup GroupOf(string? view) =>
        Groups.FirstOrDefault(group => group.Views.Contains(view ?? "", StringComparer.Ordinal)) ?? Groups[0];

    /// <summary>The name a person reads for a view.</summary>
    public static string Label(string view) => view switch
    {
        "Forretning" => "Business structure",
        "Schema Tree" => "Schema tree",
        "Effective Schema" => "Effective structure",
        "Felt og regler" => "Fields and rules",
        "Schema dependencies" => "Dependencies",
        "Generated Tests" => "Generated tests",
        "Generation Profile" => "Generation profile",
        _ => view,
    };

    /// <summary>One line saying what a view shows, so views that look alike (business structure vs schema tree, dependencies vs references) are told apart.</summary>
    public static string Purpose(string view) => view switch
    {
        "Overview" => "Package summary and where to start.",
        "Forretning" => "The message hierarchy as the schema defines it, with the schema's own documentation — optionally with types and origin.",
        "Felt og regler" => "Every field with occurrence, type, XSD rules (allowed values, patterns, lengths) and documentation.",
        "Schema Tree" => "Schema declarations grouped by file: global elements and types as written in each XSD.",
        "Types" => "Global simple and complex types with their base, facets and how often they are used.",
        "Constraints" => "Every XSD constraint by path: enumerations, patterns, lengths, numeric bounds and occurrence.",
        "Namespaces" => "Target namespaces, the files that declare them and which files import them.",
        "Schema dependencies" => "File-level dependencies: imports and includes between XSD files and their resolution.",
        "References" => "Cross-file declaration references: type use, inheritance and element references between files.",
        "Effective Schema" => "The resolved structure with inherited and referenced members, keeping where each was declared.",
        "Raw XSD" => "The imported XSD exactly as received (read-only source evidence).",
        "Designer" => "Edit a working draft; saving creates a new immutable version, the imported source is never changed.",
        "Compare" => "Declaration-level differences between this package and another imported version.",
        "Generated Tests" => "Valid and invalid cases derived from XSD declarations, run locally against this package. Schema-derived only — not integration tests.",
        "Generation Profile" => "Rules for generating synthetic XML: profile, per-element rules and XML generation.",
        _ => "",
    };

    /// <summary>URL form of a view (<c>?view=generated-tests</c>, <c>?view=business-structure</c>).</summary>
    public static string Slug(string view) => SlugOf(Label(view));

    private static string SlugOf(string text) =>
        new string(text.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Replace("--", "-").Trim('-');

    /// <summary>The view a slug, label or former identifier names; null for anything else (never guessed). "Struktur" opens Business structure.</summary>
    public static string? FromQuery(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (IsTechnicalStructure(text)) return "Forretning";
        return AllViews.FirstOrDefault(view => string.Equals(view, text, StringComparison.OrdinalIgnoreCase) || string.Equals(Label(view), text, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Slug(view), text, StringComparison.OrdinalIgnoreCase) || string.Equals(SlugOf(view), text, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True for the former "Struktur" view's name or slug: Business structure opens with types and origin shown.</summary>
    public static bool IsTechnicalStructure(string? value) =>
        string.Equals(value?.Trim(), TechnicalStructureAlias, StringComparison.OrdinalIgnoreCase);

    /// <summary>Views whose content is wide (tables, editors, graphs) and get the full content width; text views keep a readable width.</summary>
    public static bool IsWide(string view) => view is "Felt og regler" or "Types" or "Constraints" or "References" or "Schema dependencies"
        or "Raw XSD" or "Designer" or "Generated Tests" or "Effective Schema" or "Generation Profile";
}
