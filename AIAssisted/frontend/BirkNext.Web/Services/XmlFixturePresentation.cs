using BirkNext.Integrations;
using BirkNext.TestData;

namespace BirkNext.Web.Services;

/// <summary>How ready a contract's XML fixture is in Test Data. Outdated: validated against another snapshot of the schema than the current one.</summary>
public enum XmlFixtureReadiness { Missing, Ready, Outdated, Invalid, NotApproved, NotValidated }

/// <summary>
/// Test Data → XML fixtures: which test context, identities and XSD a fixture is created from, and how ready the saved fixtures are. Pure over
/// what the backend already resolves — the Test Pack template (contract, deterministic XSD candidates, role bindings) and the Test Data catalog
/// (assets with their schema check). Nothing is chosen by recency: one candidate is preselected, several need a choice, none is never guessed.
/// </summary>
public static class XmlFixturePresentation
{
    /// <summary>Test contexts that can produce a fixture: a template naming a contract and declaring the Test Data roles of its message.</summary>
    public static IReadOnlyList<IntegrationTemplateView> Contexts(IReadOnlyList<IntegrationTemplateView> templates) =>
        [.. templates.Where(t => t.SchemaName is { Length: > 0 } && t.FixtureBindings.Count > 0)];

    /// <summary>The XSD files the template's evidence resolves for its contract, once each.</summary>
    public static IReadOnlyList<IntegrationTemplateSchemaCandidate> Xsds(IntegrationTemplateView context) =>
        [.. context.SchemaCandidates.DistinctBy(c => (c.SnapshotId, c.File))];

    /// <summary>The single XSD when exactly one resolves; null when there is none or a choice is needed.</summary>
    public static IntegrationTemplateSchemaCandidate? Preselected(IntegrationTemplateView context) => Xsds(context) is { Count: 1 } one ? one[0] : null;

    /// <summary>Test Data assets that can fill a role (by capability).</summary>
    public static IReadOnlyList<TestDataAssetSummary> Candidates(IEnumerable<TestDataAssetSummary> assets, string capability) =>
        [.. assets.Where(a => a.Capabilities.Contains(capability, StringComparer.OrdinalIgnoreCase) && a.Kind == TestDataKind.EntityReference)];

    /// <summary>An explicit choice wins; otherwise the single default; otherwise the only candidate; otherwise none (never the first or newest).</summary>
    public static TestDataAssetSummary? Chosen(IReadOnlyList<TestDataAssetSummary> candidates, string? explicitAlias) =>
        explicitAlias is { Length: > 0 } && candidates.FirstOrDefault(c => c.Alias == explicitAlias) is { } chosen ? chosen
        : candidates.Where(c => c.IsDefault).ToList() is { Count: 1 } defaults ? defaults[0]
        : candidates.Count == 1 ? candidates[0] : null;

    /// <summary>Saved XML fixtures for the contract: XML assets whose schema file is the contract (positive fixtures only).</summary>
    public static IReadOnlyList<TestDataAssetSummary> Fixtures(IEnumerable<TestDataAssetSummary> assets, string contract) =>
        [.. assets.Where(a => string.Equals(a.MediaType, "application/xml", StringComparison.OrdinalIgnoreCase) && a.Schema is { } schema
            && string.Equals(Path.GetFileNameWithoutExtension(schema.SchemaPath.Replace('\\', '/').Split('/').Last()), contract, StringComparison.OrdinalIgnoreCase)
            && !a.Capabilities.Contains("negative-test", StringComparer.OrdinalIgnoreCase))];

    /// <summary>Ready only for an approved fixture that validates against the current schema snapshot; anything else names what is missing.</summary>
    public static (XmlFixtureReadiness State, string Text) Readiness(IReadOnlyList<TestDataAssetSummary> fixtures, Guid? currentSnapshot)
    {
        if (fixtures.Count == 0) return (XmlFixtureReadiness.Missing, "Missing");
        var validated = fixtures.Where(f => f.Approval == TestDataApproval.Approved && f.SchemaCheck?.Status == TestDataSchemaStatus.Validated).ToList();
        if (validated.Any(f => currentSnapshot is null || f.Schema?.SourceSnapshotId == currentSnapshot)) return (XmlFixtureReadiness.Ready, "Ready");
        if (validated.Count > 0) return (XmlFixtureReadiness.Outdated, "Validated against another schema snapshot — create it again from the current XSD");
        if (fixtures.Any(f => f.SchemaCheck?.Status == TestDataSchemaStatus.Invalid)) return (XmlFixtureReadiness.Invalid, "Invalid against its XSD");
        if (fixtures.Any(f => f.Approval != TestDataApproval.Approved && f.SchemaCheck?.Status == TestDataSchemaStatus.Validated)) return (XmlFixtureReadiness.NotApproved, "Saved, not approved");
        return (XmlFixtureReadiness.NotValidated, "Not validated against the contract");
    }

    /// <summary>"Child: skolenaervaer-child-default; Reporter (Altinn actor): skolenaervaer-reporter-default" — fixture metadata, aliases only.</summary>
    public static string BindingsText(IntegrationTemplateView context, IReadOnlyDictionary<string, string?> aliases) =>
        string.Join("; ", context.FixtureBindings.Select(b => $"{b.Role}{(b.ElementPath is null ? " (not in the XML)" : "")}: {aliases.GetValueOrDefault(b.Role) ?? "—"}"));

    /// <summary>A Test Data alias from a name ("Skolenærvær valid report" → "skolenaervaer-valid-report").</summary>
    public static string SuggestedAlias(string name)
    {
        var folded = name.ToLowerInvariant().Replace("æ", "ae").Replace("ø", "o").Replace("å", "a");
        var alias = new string([.. folded.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]);
        while (alias.Contains("--", StringComparison.Ordinal)) alias = alias.Replace("--", "-", StringComparison.Ordinal);
        return alias.Trim('-') is { Length: > 0 } trimmed ? trimmed[..Math.Min(64, trimmed.Length)] : "xml-fixture";
    }
}
