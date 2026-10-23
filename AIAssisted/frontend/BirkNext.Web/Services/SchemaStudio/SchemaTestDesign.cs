using System.Security.Cryptography;
using System.Text;
using BirkNext.Schemas;

namespace BirkNext.Web.Services.SchemaStudio;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// Validate & Test over the one generated-case model and the one local validator: canonical rule families (from the structured rule key each
// case is created with, never from rendered text), the discovered rule inventory (from the effective schema — no fabricated denominators),
// deterministic representative sampling, result classification (generator defect vs mutation defect vs unsupported) and the self-validated
// minimum example. Schema-valid is structure only: never business-valid, never approved, never runtime-verified.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

public enum SchemaRuleFamily
{
    Namespace, WellFormedness, RequiredOptional, ElementOrder, NestedStructure, ChoiceGroup, Occurrence, Attributes, Enumeration,
    LengthBoundaries, NumericBoundaries, Pattern, Datatype, Other,
}

public enum SchemaRunMode { Representative, All, Families, Failed }

/// <summary>How a run result is read: a pass, a failure attributed to the case generator (the schema is the oracle), or not evaluated.</summary>
public enum SchemaResultClass { Pass, PassWithAdditionalViolations, GeneratorDefect, MutationDefect, NotEvaluated }

/// <summary>One rule of the schema as discovered from the effective schema, and whether any generated case targets it.</summary>
public sealed record SchemaDiscoveredRule(SchemaRuleFamily Family, string Path, string Description, bool Covered, string? NotCoveredReason);

/// <summary>Coverage of one rule family: discovered → generated → executed → expected behaviour confirmed. Each count is its own fact.</summary>
public sealed record SchemaRuleCoverage(SchemaRuleFamily Family, int Discovered, int Covered, int Unsupported, int Generated, int Executed, int Passed, int Failed,
    string? UnsupportedReason);

public static class SchemaRuleFamilies
{
    public static readonly IReadOnlyList<SchemaRuleFamily> Ordered = Enum.GetValues<SchemaRuleFamily>();

    public static string Label(SchemaRuleFamily family) => family switch
    {
        SchemaRuleFamily.Namespace => "Namespace",
        SchemaRuleFamily.WellFormedness => "Well-formedness",
        SchemaRuleFamily.RequiredOptional => "Required / optional",
        SchemaRuleFamily.ElementOrder => "Element order",
        SchemaRuleFamily.NestedStructure => "Nested structures",
        SchemaRuleFamily.ChoiceGroup => "Choice / group behavior",
        SchemaRuleFamily.Occurrence => "Occurrence / cardinality",
        SchemaRuleFamily.Attributes => "Attributes",
        SchemaRuleFamily.Enumeration => "Enumeration",
        SchemaRuleFamily.LengthBoundaries => "Length boundaries",
        SchemaRuleFamily.NumericBoundaries => "Numeric / date boundaries",
        SchemaRuleFamily.Pattern => "Pattern",
        SchemaRuleFamily.Datatype => "Datatype",
        _ => "Other",
    };

    /// <summary>The canonical family of a case, from the structured rule key the generator created it with (a closed set of keys).</summary>
    public static SchemaRuleFamily Of(SchemaTestCase test) => test.Rule switch
    {
        "wrong namespace" or "wrong namespace prefix binding" or "missing namespace" or "wrong element namespace" => SchemaRuleFamily.Namespace,
        "malformed XML" => SchemaRuleFamily.WellFormedness,
        "missing required element" or "optional element omitted" or "empty required value" => SchemaRuleFamily.RequiredOptional,
        "wrong element order" => SchemaRuleFamily.ElementOrder,
        "missing parent element" => SchemaRuleFamily.NestedStructure,
        "choice branch" or "choice with multiple branches" => SchemaRuleFamily.ChoiceGroup,
        "minOccurs - 1" or "maxOccurs + 1" => SchemaRuleFamily.Occurrence,
        "missing required attribute" => SchemaRuleFamily.Attributes,
        "wrong datatype" when test.Path.Contains("/@", StringComparison.Ordinal) => SchemaRuleFamily.Attributes,
        "wrong datatype" => SchemaRuleFamily.Datatype,
        "known enumeration" or "unknown enumeration" => SchemaRuleFamily.Enumeration,
        "length boundary" or "length outside boundary" or "minLength boundary" or "below minLength" or "maxLength boundary" or "above maxLength" => SchemaRuleFamily.LengthBoundaries,
        "pattern match" or "pattern non-match" => SchemaRuleFamily.Pattern,
        var rule when rule.StartsWith("minInclusive", StringComparison.Ordinal) || rule.StartsWith("maxInclusive", StringComparison.Ordinal)
            || rule.StartsWith("minExclusive", StringComparison.Ordinal) || rule.StartsWith("maxExclusive", StringComparison.Ordinal) => SchemaRuleFamily.NumericBoundaries,
        _ => SchemaRuleFamily.Other,
    };

    /// <summary>The intended mutation of a negative case (or the shape of a positive one), for display and provenance.</summary>
    public static string Mutation(SchemaTestCase test) => test.ExpectedValid ? $"none ({test.Rule})" : test.Rule;
}

public static class SchemaTestDesign
{
    /// <summary>Bumped whenever generation or sampling changes, so a stored run says which generator produced it.</summary>
    public const string GeneratorVersion = "schema-generator/2";
    public const int RepresentativeSize = 100;
    public const int MaxRunCases = 1000;
    public const string SchemaOnlyNotice = "Schema validity does not prove business validity or runtime acceptance.";

    /// <summary>
    /// The rules the effective schema declares, per family, and whether a generated case targets each one. A rule without a case is a
    /// test-generation gap ("Detected · generation unsupported"), never a schema failure.
    /// </summary>
    public static IReadOnlyList<SchemaDiscoveredRule> Inventory(SchemaPackage package)
    {
        var rules = new List<SchemaDiscoveredRule>();
        var cases = package.GeneratedTests.GroupBy(t => (SchemaRuleFamilies.Of(t), t.Path)).ToDictionary(g => g.Key, g => g.ToList());
        bool Has(SchemaRuleFamily family, string path, Func<SchemaTestCase, bool>? also = null) =>
            cases.TryGetValue((family, path), out var list) && (also is null || list.Any(also));
        void Add(SchemaRuleFamily family, string path, string description, bool covered, string? reason = null) =>
            rules.Add(new(family, path, description, covered, covered ? null : reason ?? "No generator exists for this rule shape."));

        foreach (var root in package.EffectiveSchema)
        {
            var rootPath = "/" + root.Name;
            Add(SchemaRuleFamily.Namespace, rootPath, $"Message namespace {(root.NamespaceUri.Length > 0 ? root.NamespaceUri : "(none)")}", Has(SchemaRuleFamily.Namespace, rootPath));
            Add(SchemaRuleFamily.WellFormedness, rootPath, "Well-formed XML", Has(SchemaRuleFamily.WellFormedness, rootPath));
            foreach (var attribute in root.Attributes ?? []) Attribute(attribute, rootPath);
            Visit(root, rootPath);
        }
        return package.GeneratedTestsTruncated
            ? [.. rules.Select(r => r.Covered ? r : r with { NotCoveredReason = "Case generation stopped at the 5,000-case limit." })]
            : rules;

        void Attribute(EffectiveAttribute attribute, string owner)
        {
            var path = owner + "/@" + attribute.Name;
            if (attribute.Use == "required") Add(SchemaRuleFamily.Attributes, path, "Required attribute", Has(SchemaRuleFamily.Attributes, path, t => t.Rule == "missing required attribute"));
            if (Lexical(attribute.BaseType ?? attribute.Type)) Add(SchemaRuleFamily.Attributes, path, $"Attribute datatype {LocalName(attribute.Type)}", Has(SchemaRuleFamily.Attributes, path, t => t.Rule == "wrong datatype"));
        }

        void Visit(EffectiveMember member, string path)
        {
            foreach (var sequence in member.Children.Where(c => c.Compositor == "sequence").GroupBy(c => c.CompositorGroup).Where(g => g.Count() > 1))
                Add(SchemaRuleFamily.ElementOrder, path, $"Sequence order ({sequence.Count()} elements)", Has(SchemaRuleFamily.ElementOrder, path),
                    "Fewer than two required elements: the order cannot be broken without optional content.");
            foreach (var choice in member.Children.Where(c => c.Compositor == "choice").GroupBy(c => c.CompositorGroup).Where(g => g.Count() > 1))
                Add(SchemaRuleFamily.ChoiceGroup, path, $"Choice of {string.Join(" | ", choice.Select(c => c.Name))}",
                    choice.Any(branch => Has(SchemaRuleFamily.ChoiceGroup, path + "/" + branch.Name)) || Has(SchemaRuleFamily.ChoiceGroup, path));
            foreach (var child in member.Children)
            {
                var childPath = path + "/" + child.Name;
                Add(SchemaRuleFamily.RequiredOptional, childPath, child.MinOccurs == "0" ? "Optional element" : "Required element", Has(SchemaRuleFamily.RequiredOptional, childPath));
                if (child.MinOccurs != "0" && child.Children.Count > 0)
                    Add(SchemaRuleFamily.NestedStructure, childPath, "Required nested structure", Has(SchemaRuleFamily.NestedStructure, childPath),
                        "Only the first branch of a choice is materialized in the valid baseline.");
                if (int.TryParse(child.MinOccurs, out var min) && min > 1) Add(SchemaRuleFamily.Occurrence, childPath, $"minOccurs {min}", Has(SchemaRuleFamily.Occurrence, childPath, t => t.Rule == "minOccurs - 1"));
                if (child.MaxOccurs is not ("1" or "unbounded") && int.TryParse(child.MaxOccurs, out _))
                    Add(SchemaRuleFamily.Occurrence, childPath, $"maxOccurs {child.MaxOccurs}", Has(SchemaRuleFamily.Occurrence, childPath, t => t.Rule == "maxOccurs + 1"));
                if (child.Facets.Any(f => f.Kind == "enumeration"))
                    Add(SchemaRuleFamily.Enumeration, childPath, $"Enumeration ({child.Facets.Count(f => f.Kind == "enumeration")} values)", Has(SchemaRuleFamily.Enumeration, childPath));
                foreach (var facet in child.Facets)
                {
                    if (facet.Kind is "length" or "minLength" or "maxLength")
                        Add(SchemaRuleFamily.LengthBoundaries, childPath, $"{facet.Kind} {facet.Value}", Has(SchemaRuleFamily.LengthBoundaries, childPath, t => t.Rule.Contains(facet.Kind == "length" ? "length" : facet.Kind, StringComparison.Ordinal)),
                            $"The boundary exceeds the {SchemaStudioService.MaxGeneratedBoundaryCharacters:N0}-character generation limit.");
                    if (facet.Kind is "minInclusive" or "minExclusive" or "maxInclusive" or "maxExclusive")
                        Add(SchemaRuleFamily.NumericBoundaries, childPath, $"{facet.Kind} {facet.Value}", Has(SchemaRuleFamily.NumericBoundaries, childPath, t => t.Rule.StartsWith(facet.Kind, StringComparison.Ordinal)));
                    if (facet.Kind == "pattern")
                        Add(SchemaRuleFamily.Pattern, childPath, $"Pattern {facet.Value}", Has(SchemaRuleFamily.Pattern, childPath, t => t.Rule == "pattern match"),
                            "Complex regex generation unsupported: no matching value could be constructed safely.");
                }
                if (Lexical(child.BaseType ?? child.Type)) Add(SchemaRuleFamily.Datatype, childPath, $"Datatype {LocalName(child.Type)}", Has(SchemaRuleFamily.Datatype, childPath));
                foreach (var attribute in child.Attributes ?? []) Attribute(attribute, childPath);
                Visit(child, childPath);
            }
        }
    }

    /// <summary>Coverage per family: discovered and covered rules from the inventory; generated cases; executed and passed from the results.</summary>
    public static IReadOnlyList<SchemaRuleCoverage> Coverage(SchemaPackage package, IReadOnlyCollection<SchemaTestResult> results)
    {
        var inventory = Inventory(package);
        var byId = package.GeneratedTests.ToDictionary(t => t.Id, StringComparer.Ordinal);
        var executed = results.Where(r => r.Outcome != "Not evaluated" && byId.ContainsKey(r.TestId)).ToList();
        return [.. SchemaRuleFamilies.Ordered.Select(family =>
        {
            var discovered = inventory.Where(r => r.Family == family).ToList();
            var generated = package.GeneratedTests.Count(t => SchemaRuleFamilies.Of(t) == family);
            var ran = executed.Where(r => SchemaRuleFamilies.Of(byId[r.TestId]) == family).ToList();
            var unsupported = discovered.Where(r => !r.Covered).ToList();
            return new SchemaRuleCoverage(family, discovered.Count, discovered.Count - unsupported.Count, unsupported.Count, generated, ran.Count,
                ran.Count(r => r.Outcome == "Pass"), ran.Count(r => r.Outcome == "Fail"), unsupported.Select(r => r.NotCoveredReason).FirstOrDefault());
        }).Where(c => c.Discovered + c.Generated > 0)];
    }

    /// <summary>
    /// A deterministic, stratified sample: round-robin over rule families (each family's own cases interleaved valid/invalid and spread across
    /// paths first), so no family takes the slots just because it is listed first. The order depends only on <paramref name="key"/> (schema
    /// fingerprint, profile, size, generator version) and the case ids — never on randomness or list position.
    /// </summary>
    public static IReadOnlyList<SchemaTestCase> Representative(IReadOnlyList<SchemaTestCase> cases, int size, string key)
    {
        if (cases.Count <= size) return cases;
        var queues = cases.GroupBy(SchemaRuleFamilies.Of).OrderBy(g => g.Key).Select(group =>
        {
            // One case per path first, then the second case per path, … — breadth across paths before depth.
            List<SchemaTestCase> Spread(IEnumerable<SchemaTestCase> items) => [.. items
                .GroupBy(t => t.Path).SelectMany(g => g.OrderBy(t => Rank(key, t.Id)).Select((t, i) => (Case: t, Round: i)))
                .OrderBy(x => x.Round).ThenBy(x => Rank(key, x.Case.Id)).Select(x => x.Case)];
            var valid = Spread(group.Where(t => t.ExpectedValid));
            var invalid = Spread(group.Where(t => !t.ExpectedValid));
            var merged = new List<SchemaTestCase>();
            for (var i = 0; i < Math.Max(valid.Count, invalid.Count); i++)
            {
                if (i < invalid.Count) merged.Add(invalid[i]);
                if (i < valid.Count) merged.Add(valid[i]);
            }
            return new Queue<SchemaTestCase>(merged);
        }).ToList();
        var sample = new List<SchemaTestCase>(size);
        while (sample.Count < size && queues.Any(q => q.Count > 0))
            foreach (var queue in queues.Where(q => q.Count > 0))
            {
                if (sample.Count >= size) break;
                sample.Add(queue.Dequeue());
            }
        return sample;
    }

    public static string SampleKey(SchemaPackage package, SchemaGenerationProfile? profile, int size) =>
        $"{package.Fingerprint}|{profile?.Id.ToString("N") ?? "generic"}|{size}|{GeneratorVersion}";

    /// <summary>The cases a run mode selects. Every mode uses the same generated-case model and the same local validator.</summary>
    public static IReadOnlyList<SchemaTestCase> Select(SchemaPackage package, SchemaRunMode mode, string key, IReadOnlyCollection<SchemaRuleFamily>? families = null,
        IReadOnlyCollection<SchemaTestResult>? previous = null) => mode switch
    {
        SchemaRunMode.Representative => Representative(package.GeneratedTests, RepresentativeSize, key),
        SchemaRunMode.Families => [.. package.GeneratedTests.Where(t => families?.Contains(SchemaRuleFamilies.Of(t)) == true).Take(MaxRunCases)],
        SchemaRunMode.Failed => [.. package.GeneratedTests.Where(t => previous?.Any(r => r.TestId == t.Id && r.Outcome is "Fail" or "Not evaluated") == true).Take(MaxRunCases)],
        _ => [.. package.GeneratedTests.Take(MaxRunCases)],
    };

    /// <summary>"100 of 297 cases selected across 9 rule categories · 32 valid · 68 invalid · 41 paths · 1 source schema".</summary>
    public static string SampleSummary(IReadOnlyCollection<SchemaTestCase> selected, int total)
    {
        var families = selected.Select(SchemaRuleFamilies.Of).Distinct().Count();
        var schemas = selected.Select(t => t.SourceFile).Distinct(StringComparer.Ordinal).Count();
        return $"{selected.Count} of {total} cases selected across {families} rule {(families == 1 ? "category" : "categories")} · {selected.Count(t => t.ExpectedValid)} valid · "
            + $"{selected.Count(t => !t.ExpectedValid)} invalid · {selected.Select(t => t.Path).Distinct(StringComparer.Ordinal).Count()} paths · {schemas} source {(schemas == 1 ? "schema" : "schemas")}";
    }

    /// <summary>
    /// The schema is the oracle for this local generator test: a positive case the validator rejects is a generator defect candidate (not an
    /// application or schema defect); a negative case the validator accepts is a mutation defect (the mutation did not break the rule) and
    /// never counts as a successful negative case; a negative case rejected for more than its intended rule passes, but says so.
    /// </summary>
    public static SchemaResultClass Classify(SchemaTestResult result) => result.Outcome switch
    {
        "Pass" when !result.ExpectedValid && result.IssueCount > 1 => SchemaResultClass.PassWithAdditionalViolations,
        "Pass" => SchemaResultClass.Pass,
        "Fail" when result.ExpectedValid => SchemaResultClass.GeneratorDefect,
        "Fail" => SchemaResultClass.MutationDefect,
        _ => SchemaResultClass.NotEvaluated,
    };

    public static string Label(SchemaResultClass value) => value switch
    {
        SchemaResultClass.PassWithAdditionalViolations => "Pass · mutation caused additional violations",
        SchemaResultClass.GeneratorDefect => "Generator defect candidate",
        SchemaResultClass.MutationDefect => "Mutation generator defect",
        SchemaResultClass.NotEvaluated => "Not evaluated / unsupported",
        _ => "Pass",
    };

    /// <summary>"Accepted" / "Rejected" — what the local validator did, independent of what was expected.</summary>
    public static string Actual(SchemaTestResult result) => result.Actual switch { "Valid" => "Accepted", "Invalid" => "Rejected", _ => "Not evaluated" };

    private static double Rank(string key, string id) => BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes(key + "|" + id)), 0);

    private static bool Lexical(string? type) => LocalName(type) is "decimal" or "float" or "double" or "integer" or "int" or "long" or "short" or "byte"
        or "positiveInteger" or "nonNegativeInteger" or "negativeInteger" or "nonPositiveInteger" or "unsignedInt" or "unsignedLong" or "unsignedShort"
        or "unsignedByte" or "date" or "dateTime" or "time" or "boolean";

    private static string? LocalName(string? value) => XmlSchemaPackageAnalyzer.LocalName(value);
}

public enum SchemaGenerationState { SchemaValid, FailedValidation, Blocked }

/// <summary>Exact identity of a generated document: package, snapshot, source XSD + fingerprint, root, profile, generator and the case.</summary>
public sealed record SchemaGenerationProvenance(string PackageId, string PackageFingerprint, Guid? SourceSnapshotId, string? SourceSnapshotFingerprint,
    string SourceXsd, string? SchemaFingerprint, string RootElement, string ProfileId, string ProfileName, string ProfileVersion, string GeneratorVersion,
    string? Mutation, bool ExpectedValid, DateTimeOffset GeneratedAt, string Validation)
{
    public bool IsEntrySchema(SchemaPackage package) => package.Provenance?.SelectedContractPath is not { } entry || string.Equals(entry, SourceXsd, StringComparison.OrdinalIgnoreCase);
}

/// <summary>A generated example after self-validation with the same local validator that Validate XML uses. Only SchemaValid may be presented as valid.</summary>
public sealed record SchemaGenerationResult(SchemaGenerationState State, string Xml, IReadOnlyList<string> Reasons, IReadOnlyList<string> Choices, SchemaGenerationProvenance Provenance)
{
    public bool IsSchemaValid => State == SchemaGenerationState.SchemaValid;

    public string Heading => State switch
    {
        SchemaGenerationState.SchemaValid => "Schema-valid",
        SchemaGenerationState.Blocked => "Generation blocked",
        _ => "Could not generate a valid example",
    };

    /// <summary>What the XML (if any) may be called: never "minimum valid" unless it validated.</summary>
    public string XmlLabel => State == SchemaGenerationState.SchemaValid ? "Minimum schema-valid example" : "Partial generated XML · not schema-valid";
}
