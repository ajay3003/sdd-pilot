using BirkNext.Integrations;
using BirkNext.TestData;

namespace BirkNext.Web.Services;

/// <summary>One Test Data role (Reporter, Child) for the tester view: the identity Test Data resolves for it and whether it is usable.</summary>
public sealed record TestDataRoleStatus(string Role, string? Label, bool Ready, string Status, string? Note);

/// <summary>
/// Test Data readiness for one test context, as testers read it: Reporter, Child, XML fixture, contract validation and an overall state with
/// one reason. Test Data readiness only — never runtime or delivery readiness. Built on <see cref="TestDataXmlReadiness"/>, the rule Setup
/// Library preflight also uses.
/// </summary>
public sealed record TestDataTesterStatus(IReadOnlyList<TestDataRoleStatus> Roles, TestDataXmlReadinessState Xml, string XmlText, TestDataAssetSummary? Fixture,
    string ContractValidation, string Overall, string? Reason)
{
    public bool Ready => Overall == "Ready";
}

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
    public static IntegrationTemplateSchemaCandidate? Preselected(IntegrationTemplateView context) => XmlContractResolution.Resolve(context).Contract;

    /// <summary>Test Data assets that can fill a role (by capability).</summary>
    public static IReadOnlyList<TestDataAssetSummary> Candidates(IEnumerable<TestDataAssetSummary> assets, string capability) =>
        [.. assets.Where(a => a.Capabilities.Contains(capability, StringComparer.OrdinalIgnoreCase) && a.Kind == TestDataKind.EntityReference)];

    /// <summary>
    /// A Test Data asset as a selector option: friendly name, canonical asset id (alias) and status/scope, so several reporters or children are
    /// told apart ("Default child — child-default · Approved · Shared"). Never a value.
    /// </summary>
    public static string IdentityOption(TestDataAssetSummary asset) =>
        $"{asset.Label ?? asset.Alias} — {asset.Alias} · {(asset.Approval == TestDataApproval.Approved ? "Approved" : "Not approved")} · {(string.IsNullOrWhiteSpace(asset.Environment) ? "Shared" : asset.Environment)}{(asset.IsDefault ? " · default" : "")}";

    /// <summary>An explicit choice wins; otherwise the single default; otherwise the only candidate; otherwise none (never the first or newest).</summary>
    public static TestDataAssetSummary? Chosen(IReadOnlyList<TestDataAssetSummary> candidates, string? explicitAlias) =>
        explicitAlias is { Length: > 0 } && candidates.FirstOrDefault(c => c.Alias == explicitAlias) is { } chosen ? chosen
        : candidates.Where(c => c.IsDefault).ToList() is { Count: 1 } defaults ? defaults[0]
        : candidates.Count == 1 ? candidates[0] : null;

    /// <summary>Saved XML fixtures for the contract (positive fixtures only) — the shared rule's own selection.</summary>
    public static IReadOnlyList<TestDataAssetSummary> Fixtures(IEnumerable<TestDataAssetSummary> assets, string contract) =>
        [.. assets.Where(a => TestDataXmlReadiness.IsFixtureFor(a.MediaType, a.Schema, a.Capabilities, contract))];

    /// <summary>The shared readiness rule over these fixtures, with tester wording and the fixture it is about.</summary>
    public static (TestDataXmlReadinessState State, string Text, TestDataAssetSummary? Fixture) Readiness(IReadOnlyList<TestDataAssetSummary> fixtures, Guid? currentSnapshot)
    {
        var (state, facts) = TestDataXmlReadiness.Evaluate(
            [.. fixtures.Select(f => new TestDataXmlFixtureFacts(f.Alias, f.Approval == TestDataApproval.Approved, f.SchemaCheck?.Status, f.SchemaCheck?.SourceSnapshotId ?? f.Schema?.SourceSnapshotId))],
            currentSnapshot);
        return (state, ReadinessText(state), facts is null ? null : fixtures.First(f => f.Alias == facts.Alias));
    }

    public static string ReadinessText(TestDataXmlReadinessState state) => state switch
    {
        TestDataXmlReadinessState.Missing => "Missing",
        TestDataXmlReadinessState.NotApproved => "Saved, not approved",
        TestDataXmlReadinessState.Ready => "Ready for test use",
        TestDataXmlReadinessState.Outdated => "Validation outdated",
        TestDataXmlReadinessState.Invalid => "Invalid",
        TestDataXmlReadinessState.SchemaUnavailable => "Schema unavailable",
        _ => "Needs validation",
    };

    /// <summary>Schema validation of one fixture, apart from its approval ("Approved" and "Valid" are different facts).</summary>
    public static string SchemaValidation(TestDataAssetSummary fixture) => fixture.SchemaCheck?.Status switch
    {
        TestDataSchemaStatus.Validated => "Valid",
        TestDataSchemaStatus.Invalid => "Invalid",
        TestDataSchemaStatus.SchemaUnavailable or TestDataSchemaStatus.DependencyUnresolved => "Schema unavailable",
        _ => "Needs validation",
    };

    /// <summary>
    /// Test Data readiness for the context: each role's chosen identity (explicit choice, else the single default, else the only one), the XML
    /// fixture by the shared rule, and the fixture's schema validation. Overall Ready only when everything is; Needs attention for a missing
    /// identity or an invalid fixture; Partial otherwise — always with one reason.
    /// </summary>
    public static TestDataTesterStatus Tester(IReadOnlyList<TestDataAssetSummary> assets, IntegrationTemplateView context, IReadOnlyDictionary<string, string?>? explicitAliases = null)
    {
        var roles = context.FixtureBindings.Select(b =>
        {
            var chosen = Chosen(Candidates(assets, b.Capability), explicitAliases?.GetValueOrDefault(b.Role));
            var ready = chosen is { Approval: TestDataApproval.Approved };
            return new TestDataRoleStatus(b.Role, chosen?.Label ?? chosen?.Alias, ready, chosen is null ? "Missing" : ready ? "Ready" : "Not approved",
                b.ElementPath is null ? "Used to submit the report; not part of the XML payload." : null);
        }).ToList();
        var (xml, xmlText, fixture) = Readiness(Fixtures(assets, context.SchemaName ?? ""), Preselected(context)?.SnapshotId);
        var contract = fixture is null ? "Missing" : xml == TestDataXmlReadinessState.Outdated ? "Needs validation" : SchemaValidation(fixture);
        var missingRole = roles.FirstOrDefault(r => !r.Ready);
        var (overall, reason) =
            missingRole is null && xml == TestDataXmlReadinessState.Ready ? ("Ready", (string?)null)
            : missingRole is { Status: "Missing" } ? ("Needs attention", $"{missingRole.Role} missing")
            : xml == TestDataXmlReadinessState.Invalid ? ("Needs attention", "XML fixture invalid")
            : missingRole is not null ? ("Partial", $"{missingRole.Role} not approved")
            : ("Partial", xml switch
            {
                TestDataXmlReadinessState.Missing => "XML fixture missing",
                TestDataXmlReadinessState.NotApproved => "XML fixture not approved",
                TestDataXmlReadinessState.Outdated => "XML fixture validation outdated",
                TestDataXmlReadinessState.SchemaUnavailable => "Contract schema unavailable",
                _ => "XML fixture needs validation",
            });
        return new(roles, xml, xmlText, fixture, contract, overall, reason);
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
