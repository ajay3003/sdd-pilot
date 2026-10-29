using System.Xml.Linq;
using BirkNext.Integrations;
using BirkNext.Schemas;
using BirkNext.TestData;
using BirkNext.Web.Services.SchemaStudio;

namespace BirkNext.Web.Services;

/// <summary>Where a mapped XML element's value comes from. <see cref="SchemaDefault"/> leaves the generated/default value in place.</summary>
public enum XmlValueSourceKind { TestDataAsset, Fixed, Generated, SchemaDefault }

/// <summary>Safe generated values: technical identifiers and timestamps only — never a business identifier that should come from Test Data.</summary>
public enum XmlGeneratedValue { Uuid, Timestamp, Date }

/// <summary>
/// One XSD element a value can be mapped to. <see cref="Path"/> is the dotted path under the root ("Klient.Identifikator.Fodselsnummer") the
/// generator and the Test Data binder use; <see cref="Display"/> and <see cref="Technical"/> are the full human and technical paths (two
/// elements named alike in different parents stay distinguishable).
/// </summary>
public sealed record XmlParameter(string Path, string Display, string Technical, string? Type, string? Documentation);

/// <summary>One row of "XML value mappings". A Test Data mapping stores the asset alias (a reference), never the asset's value.</summary>
public sealed record XmlValueMapping(string Path, XmlValueSourceKind Kind, string? AssetAlias = null, string? FixedValue = null,
    XmlGeneratedValue? Generated = null, string? SuggestedFrom = null, string? NeedsReview = null)
{
    public bool IsSuggested => SuggestedFrom is not null;
}

/// <summary>
/// The mapping layer before XML generation: XSD structure/defaults → explicit field mappings → generated values → XML generation → XSD
/// validation. Field-level mappings take scalar/simple Test Data only; payload assets (XML, JSON, bodies, files) are payload-level and never
/// offered for a single element.
/// </summary>
public static class XmlValueMappings
{
    private const int MaxParameters = 500, MaxDepth = 12;

    /// <summary>Every leaf element of the message root (bounded), in schema order.</summary>
    public static IReadOnlyList<XmlParameter> Parameters(SchemaPackage package)
    {
        var root = package.EffectiveSchema.FirstOrDefault();
        if (root is null) return [];
        var list = new List<XmlParameter>();
        void Walk(EffectiveMember member, List<string> trail, HashSet<string> types)
        {
            if (list.Count >= MaxParameters || trail.Count > MaxDepth) return;
            if (member.Children.Count == 0)
            {
                list.Add(new(string.Join('.', trail), string.Join(" → ", trail), $"/{root.Name}/{string.Join('/', trail)}", member.Type, member.Documentation));
                return;
            }
            // A recursive type (a member containing its own type) is walked once.
            if (member.Type is { } type && !types.Add(type)) return;
            foreach (var child in member.Children) Walk(child, [.. trail, child.Name], new HashSet<string>(types, StringComparer.Ordinal));
        }
        foreach (var child in root.Children) Walk(child, [child.Name], []);
        return list;
    }

    /// <summary>Payload-level assets (XML/JSON bodies, files, structured or message assets) — never a value for one element.</summary>
    public static bool IsPayload(TestDataAssetSummary asset) =>
        asset.Kind is TestDataKind.Structured or TestDataKind.Message or TestDataKind.File
        || asset.MediaType is { } media && (media.Contains("xml", StringComparison.OrdinalIgnoreCase) || media.Contains("json", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Assets a field-level mapping may use: approved, scalar/simple (never a payload). When a Test Data role is declared for the element (e.g.
    /// the child for the child identifier), only that role's assets are compatible; otherwise any approved scalar asset is.
    /// </summary>
    public static IReadOnlyList<TestDataAssetSummary> CompatibleAssets(IEnumerable<TestDataAssetSummary> assets, string path, IReadOnlyList<TestDataFixtureBinding> bindings)
    {
        var role = bindings.FirstOrDefault(b => string.Equals(b.ElementPath, path, StringComparison.Ordinal));
        return [.. assets.Where(a => !IsPayload(a) && a.Approval == TestDataApproval.Approved
            && a.Kind is TestDataKind.Scalar or TestDataKind.EntityReference or TestDataKind.ReferenceData
            && (role is null || a.Capabilities.Contains(role.Capability, StringComparer.OrdinalIgnoreCase)))
            .OrderBy(a => a.Label ?? a.Alias, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The roles' deterministic mappings (each role bound to an element path), suggested with the chosen asset. A role without a path
    /// (e.g. the submitting actor) is never mapped into the XML.</summary>
    public static IReadOnlyList<XmlValueMapping> Suggested(IntegrationTemplateView context, Func<TestDataFixtureBinding, string?> chosenAlias) =>
        [.. context.FixtureBindings.Where(b => b.ElementPath is not null)
            .Select(b => new XmlValueMapping(b.ElementPath!, XmlValueSourceKind.TestDataAsset, chosenAlias(b), SuggestedFrom: $"{context.PackName} test context ({b.Role})"))];

    /// <summary>"Klient → Identifikator → Fodselsnummer" for a path, from the parameter list (or the path itself when the XSD is not loaded).</summary>
    public static string Display(IReadOnlyList<XmlParameter> parameters, string path) =>
        parameters.FirstOrDefault(p => p.Path == path)?.Display ?? path.Replace(".", " → ", StringComparison.Ordinal);

    public static string GeneratedValue(XmlGeneratedValue kind, DateTimeOffset now) => kind switch
    {
        XmlGeneratedValue.Uuid => Guid.NewGuid().ToString(),
        XmlGeneratedValue.Timestamp => now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
        _ => now.UtcDateTime.ToString("yyyy-MM-dd"),
    };

    /// <summary>
    /// Applies fixed and generated mappings to the generated XML (the elements exist: the generator materialized every mapped path). An
    /// explicit mapping replaces the synthetic/default value. Returns the problems (an element the XML does not contain).
    /// </summary>
    public static (string Xml, IReadOnlyList<string> Problems) Apply(string xml, IReadOnlyList<XmlValueMapping> mappings, DateTimeOffset now)
    {
        var document = XDocument.Parse(xml);
        var problems = new List<string>();
        foreach (var mapping in mappings.Where(m => m.Kind is XmlValueSourceKind.Fixed or XmlValueSourceKind.Generated))
        {
            var element = document.Root;
            foreach (var part in mapping.Path.Split('.', StringSplitOptions.RemoveEmptyEntries))
                element = element?.Elements().FirstOrDefault(e => e.Name.LocalName == part);
            if (element is null) { problems.Add($"{mapping.Path}: not present in the generated XML."); continue; }
            element.Value = mapping.Kind == XmlValueSourceKind.Fixed ? mapping.FixedValue ?? "" : GeneratedValue(mapping.Generated ?? XmlGeneratedValue.Uuid, now);
        }
        return (document.Declaration is null ? document.ToString(SaveOptions.DisableFormatting) : document.Declaration + document.ToString(SaveOptions.DisableFormatting), problems);
    }

    /// <summary>Fixture provenance of the mappings: "Klient/Identifikator/Fodselsnummer ← Test Data skolenaervaer-child-default; Meldingshode/Id ←
    /// Generated UUID". Aliases and rules only; a Test Data value never appears.</summary>
    public static string ProvenanceText(IEnumerable<XmlValueMapping> mappings) => string.Join("; ", mappings.Where(m => m.Kind != XmlValueSourceKind.SchemaDefault).Select(m =>
        $"{m.Path.Replace('.', '/')} ← {m.Kind switch
        {
            XmlValueSourceKind.TestDataAsset => $"Test Data {m.AssetAlias ?? "—"}",
            XmlValueSourceKind.Fixed => $"Fixed value \"{Truncate(m.FixedValue ?? "", 40)}\"",
            _ => $"Generated {m.Generated switch { XmlGeneratedValue.Timestamp => "timestamp", XmlGeneratedValue.Date => "date", _ => "UUID" }}",
        }}"));

    /// <summary>The asset alias the saved fixture used for a role's element, read from its provenance ("…/Fodselsnummer ← Test Data alias", or the
    /// older "Child: alias" roles text). Null when the fixture does not record it.</summary>
    public static string? RecordedAlias(TestDataAssetSummary fixture, TestDataFixtureBinding binding)
    {
        if (binding.ElementPath is { } path && fixture.Provenance.FirstOrDefault(p => p.Label == "Value mappings")?.Value is { } mappings)
            foreach (var part in mappings.Split("; "))
                if (part.StartsWith(path.Replace('.', '/') + " ← Test Data ", StringComparison.Ordinal)) return part[(path.Length + " ← Test Data ".Length)..];
        if (fixture.Provenance.FirstOrDefault(p => p.Label == "Test Data roles")?.Value is { } roles)
            foreach (var part in roles.Split("; "))
                if (part.StartsWith(binding.Role + ": ", StringComparison.Ordinal)) return part[(binding.Role.Length + 2)..];
        return null;
    }

    /// <summary>An alias not used by any existing fixture: the base, else base-2, base-3 … (a regenerated fixture never replaces the approved one).</summary>
    public static string FreeAlias(string alias, IEnumerable<TestDataAssetSummary> existing)
    {
        var taken = existing.Select(a => a.Alias).ToHashSet(StringComparer.Ordinal);
        if (!taken.Contains(alias)) return alias;
        for (var n = 2; ; n++) if (!taken.Contains($"{alias}-{n}")) return $"{alias}-{n}";
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";
}
