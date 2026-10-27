using BirkNext.Schemas;

namespace BirkNext.Web.Services.SchemaStudio;

/// <summary>A small labelled chip. Tone is a CSS modifier; the label always carries the meaning (never colour alone).</summary>
public sealed record SchemaChip(string Label, string Tone);

/// <summary>Where the schema evidence on screen comes from: imported/observed analysis, a saved workspace copy, or a session-only import.</summary>
public sealed record SchemaEvidenceOrigin(string Label, string Detail, string Tone);

/// <summary>
/// Visual vocabulary of Schema Studio: chips for definitions and occurrence, evidence-origin wording, Save semantics and intentional empty
/// states. Presentation only — it reads the shared schema model and never changes what the schema says.
/// </summary>
public static class SchemaStudioVisuals
{
    public const string SaveLabel = "Save to workspace";

    private static readonly string[] TextBases = ["string", "normalizedString", "token", "language", "Name", "NCName"];

    /// <summary>Chips for one reusable definition: its kind, what its rules are and how often the package uses it.</summary>
    public static IReadOnlyList<SchemaChip> DeclarationChips(SchemaDeclaration declaration, int usage)
    {
        var chips = new List<SchemaChip>();
        switch (declaration.Kind)
        {
            case "complexType":
                chips.Add(new("Complex type", "complex"));
                chips.Add(new("Reusable object", "object"));
                break;
            case "simpleType":
                chips.Add(new("Simple type", "simple"));
                if (TextBases.Contains(XmlSchemaPackageAnalyzer.LocalName(declaration.BaseType ?? declaration.Type) ?? "", StringComparer.Ordinal))
                    chips.Add(new("Text type", "text"));
                break;
            case "element":
                chips.Add(new("Element", "element"));
                break;
            default:
                chips.Add(new(SchemaStudioPresentation.KindLabel(declaration.Kind), "neutral"));
                break;
        }
        chips.AddRange(FacetChips(declaration.Facets));
        chips.Add(usage == 0 ? new("Not used in this package", "muted") : new($"Used {usage}×", "usage"));
        return chips;
    }

    /// <summary>Rule chips for a set of facets: Enumeration, Pattern, Length rule, Numeric bounds.</summary>
    public static IReadOnlyList<SchemaChip> FacetChips(IReadOnlyList<SchemaFacet> facets)
    {
        var chips = new List<SchemaChip>();
        if (facets.Any(f => f.Kind == "enumeration")) chips.Add(new($"Enumeration · {facets.Count(f => f.Kind == "enumeration")}", "enum"));
        if (facets.Any(f => f.Kind == "pattern")) chips.Add(new("Pattern", "pattern"));
        if (facets.Any(f => f.Kind is "length" or "minLength" or "maxLength")) chips.Add(new("Length rule", "length"));
        if (facets.Any(f => f.Kind is "minInclusive" or "maxInclusive" or "minExclusive" or "maxExclusive" or "totalDigits" or "fractionDigits")) chips.Add(new("Numeric bounds", "numeric"));
        return chips;
    }

    /// <summary>Required / Optional, plus Repeating when more than one occurrence is allowed, plus "One of" inside an xs:choice.</summary>
    public static IReadOnlyList<SchemaChip> OccurrenceChips(string min, string max, bool inChoice = false)
    {
        var chips = new List<SchemaChip> { min == "0" ? new("Optional", "optional") : new("Required", "required") };
        if (max == "unbounded" || int.TryParse(max, out var upper) && upper > 1) chips.Add(new(max == "unbounded" ? "Repeating" : $"Repeating · up to {max}", "repeating"));
        if (inChoice) chips.Add(new("One of alternatives", "choice"));
        return chips;
    }

    public static IReadOnlyList<SchemaChip> OccurrenceChips(EffectiveMember member) => OccurrenceChips(member.MinOccurs, member.MaxOccurs, member.InChoice);

    /// <summary>Key XSD rules of one field in plain language (allowed-value count, lengths, patterns, bounds). Facts only.</summary>
    public static IReadOnlyList<string> PlainRules(EffectiveMember member)
    {
        var rules = SchemaReadablePresentation.Rules(member.Facets).Select(r => r.Text).ToList();
        var values = SchemaReadablePresentation.AllowedValues(member.Facets).Count;
        if (values > 0) rules.Insert(0, SchemaStudioPresentation.Count(values, "allowed value", "allowed values"));
        return rules;
    }

    /// <summary>Where the current package's evidence comes from. A Source Analysis package is usable as it is; saving never makes it "more" evidence.</summary>
    public static SchemaEvidenceOrigin Origin(SchemaPackage package, bool savedInWorkspace) => package.Provenance is { SourceSnapshotId: not null }
        ? new("Imported analysis", "From an immutable Source Analysis snapshot. Already usable here and by other reviews that read that snapshot — no Save needed."
            + (savedInWorkspace ? " Also saved to this workspace." : ""), "imported")
        : savedInWorkspace
            ? new("Saved workspace analysis", "XSD files imported into this session and saved to this workspace.", "saved")
            : new("Session import", "XSD files imported into this session only. Save to workspace to reopen them later.", "session");

    /// <summary>What Save to workspace does — and what it does not.</summary>
    public static string SaveHelp(SchemaPackage package) => package.Provenance is { SourceSnapshotId: not null }
        ? "Save to workspace only adds this package to your saved list so you can reopen it with the same snapshot and contract selection. The analysis is already available without saving. XSD files are never modified."
        : "Save to workspace keeps these imported XSD files in this BirkNext workspace so you can reopen them later. XSD files are never modified; drafts and generation profiles are saved separately.";

    /// <summary>One sentence for a package with a single namespace; null when there are several.</summary>
    public static string? NamespaceNote(SchemaPackage package)
    {
        var namespaces = package.Files.Select(f => f.TargetNamespace ?? "").Distinct(StringComparer.Ordinal).Count();
        var links = package.Files.Sum(f => f.Links.Count);
        if (namespaces != 1) return null;
        return links == 0
            ? "This package is single-namespace and has no imports or includes."
            : $"This package is single-namespace; its files are linked by {SchemaStudioPresentation.Count(links, "include", "includes/imports")}.";
    }

    /// <summary>The References empty state, in words that say why it is empty.</summary>
    public static (string Headline, string Detail) ReferencesEmpty(SchemaPackage package) => package.Files.Count == 1
        ? ("No cross-file references", "This package contains one XSD file, so there are no cross-file references.")
        : package.Files.Sum(f => f.Links.Count) == 0
            ? ("No cross-file references", "No imports/includes were detected in this package, so no definition is used across files.")
            : ("No cross-file references", "The files are linked, but no definition declared in one file is used in another.");

    /// <summary>The Dependencies empty state for a package without imports or includes; null when there are links.</summary>
    public static (string Headline, string Detail)? DependenciesEmpty(SchemaPackage package) => package.Files.Sum(f => f.Links.Count) > 0 ? null
        : package.Files.Count == 1
            ? ("No schema dependencies", "This package contains one XSD file and no imports/includes were detected, so there is no dependency graph to show.")
            : ("No schema dependencies", "No imports/includes were detected in this package; its files are independent of each other.");
}
