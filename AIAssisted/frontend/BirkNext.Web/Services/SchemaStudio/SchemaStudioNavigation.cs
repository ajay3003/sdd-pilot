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
        "Overview" => "The package in plain language and where to start.",
        "Forretning" => "The message hierarchy as a tester reads it, with the schema's own documentation — optionally with types and origin.",
        "Felt og regler" => "Every field: whether it is required, its type, its rules (allowed values, patterns, lengths) and documentation.",
        "Schema Tree" => "Every reusable definition, grouped by file, with its meaning in plain language and technical details on request.",
        "Types" => "Reusable types: what kind of value each one is, its base and its rules.",
        "Constraints" => "Every schema rule: required, optional or repeated fields, lengths, patterns, allowed values and numeric bounds.",
        "Namespaces" => "XML namespaces, the files that declare them and which files import them.",
        "Schema dependencies" => "Imports and includes between XSD files, and whether they resolve.",
        "References" => "Where one XSD file uses a definition declared in another.",
        "Effective Schema" => "The complete resolved message structure, including inherited fields, with where each field is declared.",
        "Raw XSD" => "The imported XSD exactly as received (read-only, for technical inspection).",
        "Designer" => "Edit a working draft and save it as a new version; the imported source is never changed.",
        "Compare" => "What changed between this package and another imported version.",
        "Generated Tests" => "Valid and invalid cases derived from the schema rules, run locally. Schema rules only — not integration tests.",
        "Generation Profile" => "How synthetic XML values are generated: profile, per-field rules and XML generation.",
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
}
