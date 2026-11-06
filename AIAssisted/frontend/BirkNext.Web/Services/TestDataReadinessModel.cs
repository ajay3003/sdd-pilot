using BirkNext.Integrations;
using BirkNext.TestData;

namespace BirkNext.Web.Services;

public enum TestDataDimensionState { Ready, NeedsAttention, Blocked, NotAssessed }

/// <summary>One of the eight Test Data readiness dimensions for a test context: its state, what it is based on and what to do.</summary>
public sealed record TestDataReadinessDimension(string Key, string Title, TestDataDimensionState State, string Detail, string? Action = null)
{
    /// <summary>The evidence facts this dimension is derived from (source available, XSD resolved, …) — each with its own state, never a second readiness.</summary>
    public IReadOnlyList<TestDataReadinessEvidence> Evidence { get; init; } = [];
}

/// <summary>One evidence fact behind a dimension. No evidence is Not assessed, never Ready.</summary>
public sealed record TestDataReadinessEvidence(string Key, string Label, TestDataDimensionState State, string Detail);

/// <summary>
/// The eight readiness dimensions of a test context's Test Data, each from its own evidence: test identities, contract (XSD), mapping, the fixture
/// persisted in Test Data, XSD validation, consumer binding, environment compatibility and runtime readiness. Preview ≠ saved, saved ≠ validated,
/// XSD-valid ≠ runtime-ready: runtime is never assessed by Test Data (consumers verify it), so it is Not assessed here, never Ready.
/// </summary>
public static class TestDataReadinessModel
{
    public static IReadOnlyList<TestDataReadinessDimension> Dimensions(TestDataTesterStatus status, IntegrationTemplateView context, IReadOnlyList<TestDataAssetSummary> assets,
        string? environmentName, string? environmentType)
    {
        var inXml = context.FixtureBindings.Where(b => b.ElementPath is not null).Select(b => b.Role).ToHashSet(StringComparer.Ordinal);
        var roles = status.Roles;
        var fixture = status.Fixture;
        var xsds = XmlFixturePresentation.Xsds(context);
        var chosen = roles.Where(r => r.Label is not null).Select(r => r.Label!).ToHashSet(StringComparer.Ordinal);

        var identities = roles.All(r => r.Ready)
            ? new TestDataReadinessDimension("identities", "Test identities", TestDataDimensionState.Ready, string.Join(" · ", roles.Select(r => $"{r.Role}: {r.Label}")))
            : new("identities", "Test identities", roles.Any(r => r.Status == "Missing") ? TestDataDimensionState.Blocked : TestDataDimensionState.NeedsAttention,
                string.Join(" · ", roles.Where(r => !r.Ready).Select(r => $"{r.Role}: {r.Status.ToLowerInvariant()}")), "Choose an approved identity under Session test identities.");

        var contract = xsds.Count switch
        {
            0 => new TestDataReadinessDimension("contract", "Contract (XSD)", TestDataDimensionState.Blocked, $"No imported source declares an XSD for {context.SchemaName}.", "Import the source package that contains the XSD."),
            1 => new("contract", "Contract (XSD)", TestDataDimensionState.Ready, $"{System.IO.Path.GetFileName(xsds[0].File)} · snapshot {xsds[0].SnapshotId.ToString()[..8]}"),
            var n => new("contract", "Contract (XSD)", TestDataDimensionState.NeedsAttention, $"{n} candidate XSDs — nothing is chosen by date or name.", "Choose the XSD in the XML fixture builder."),
        };

        var unmappedInXml = roles.Where(r => inXml.Contains(r.Role) && !r.Ready).Select(r => r.Role).ToList();
        // A mapped asset updated or removed after the fixture was generated: the recorded mapping is stale, never still "Ready".
        IReadOnlyList<(string Alias, bool Missing)> changed = fixture is null ? [] : TestDataValueVersions.Changed(fixture, assets);
        var mapping = changed.Count > 0
            ? new TestDataReadinessDimension("mapping", "Mapping", changed.Any(c => c.Missing) ? TestDataDimensionState.Blocked : TestDataDimensionState.NeedsAttention,
                $"Mapped Test Data changed after the fixture was generated: {string.Join(", ", changed.Select(c => c.Missing ? $"{c.Alias} (no longer in Test Data)" : c.Alias))}.",
                "Regenerate the XML fixture as a new draft.")
            : fixture?.Provenance.FirstOrDefault(p => p.Label == "Value mappings")?.Value is { Length: > 0 } recorded
            ? new TestDataReadinessDimension("mapping", "Mapping", unmappedInXml.Count == 0 ? TestDataDimensionState.Ready : TestDataDimensionState.NeedsAttention,
                $"Recorded in the fixture: {recorded}")
            : unmappedInXml.Count > 0
                ? new("mapping", "Mapping", TestDataDimensionState.Blocked, $"{string.Join(", ", unmappedInXml)} has no approved identity to map into the XML.", "Choose the identity first.")
                : fixture is null
                    ? new("mapping", "Mapping", TestDataDimensionState.NotAssessed, "Suggested from the test pack's role bindings; confirmed when the fixture is generated.")
                    : new("mapping", "Mapping", TestDataDimensionState.Ready, "Role bindings mapped into the XML (no further field mappings recorded).");

        var persisted = fixture is null
            ? new TestDataReadinessDimension("fixture", "Fixture saved in Test Data", TestDataDimensionState.Blocked, "No XML fixture is stored for this contract — a preview is not a fixture.", "Generate, validate and save the XML fixture.")
            : new("fixture", "Fixture saved in Test Data", fixture.Approval == TestDataApproval.Approved ? TestDataDimensionState.Ready : TestDataDimensionState.NeedsAttention,
                $"{fixture.Alias}{(fixture.Version is { } v ? $" · v{v}" : "")} · {(fixture.Approval == TestDataApproval.Approved ? "approved" : "not approved")}",
                fixture.Approval == TestDataApproval.Approved ? null : "Approve the fixture to use it in runs.");

        var validation = status.ContractValidation switch
        {
            "Valid" => new TestDataReadinessDimension("xsd", "XSD validation", TestDataDimensionState.Ready, "Valid against the exact XSD. Business rules are not checked by the XSD."),
            "Missing" => new("xsd", "XSD validation", TestDataDimensionState.NotAssessed, "Waiting for a saved fixture."),
            var other => new("xsd", "XSD validation", status.Xml == TestDataXmlReadinessState.Invalid ? TestDataDimensionState.Blocked : TestDataDimensionState.NeedsAttention,
                other, "Validate the fixture again against the current XSD."),
        };

        var consumers = fixture is null
            ? new TestDataReadinessDimension("consumers", "Consumer binding", TestDataDimensionState.NotAssessed, "Nothing to bind until a fixture is saved.")
            : fixture.Consumers > 0
                ? new("consumers", "Consumer binding", TestDataDimensionState.Ready, $"Used by {fixture.Consumers} consumer{(fixture.Consumers == 1 ? "" : "s")} by reference.")
                : new("consumers", "Consumer binding", TestDataDimensionState.NeedsAttention, "Not bound by a consumer yet.", $"Open {context.PackName} to bind the fixture.");

        var used = assets.Where(a => chosen.Contains(a.Label ?? "") || chosen.Contains(a.Alias) || a.Id == fixture?.Id).ToList();
        var scoped = used.Where(a => !string.IsNullOrWhiteSpace(a.Environment)).ToList();
        bool Fits(string env) => string.Equals(env, environmentName, StringComparison.OrdinalIgnoreCase) || string.Equals(env, environmentType, StringComparison.OrdinalIgnoreCase);
        var environment = environmentName is null && environmentType is null
            ? new TestDataReadinessDimension("environment", "Environment compatibility", TestDataDimensionState.NotAssessed, "No active Target Environment.")
            : scoped.Count == 0
                ? new("environment", "Environment compatibility", TestDataDimensionState.Ready, "Shared assets — usable in any environment.")
                : scoped.All(a => Fits(a.Environment!))
                    ? new("environment", "Environment compatibility", TestDataDimensionState.Ready, $"Scoped to {string.Join(", ", scoped.Select(a => a.Environment).Distinct())}, the active environment.")
                    : new("environment", "Environment compatibility", TestDataDimensionState.Blocked,
                        $"{string.Join(", ", scoped.Where(a => !Fits(a.Environment!)).Select(a => $"{a.Alias} is scoped to {a.Environment}"))} — not {environmentName ?? environmentType}.",
                        "Choose Test Data for this environment.");

        var runtime = new TestDataReadinessDimension("runtime", "Runtime readiness", TestDataDimensionState.NotAssessed,
            $"Not assessed by Test Data — {context.PackName} verifies it when a test runs. XSD-valid is not runtime-ready.");

        var preselected = XmlFixturePresentation.Preselected(context);
        identities = identities with { Evidence =
        [
            roles.Count == 0 ? new("approved-test-data", "Approved Test Data available", TestDataDimensionState.NotAssessed, "The test pack declares no Test Data roles.")
            : roles.All(r => r.Ready) ? new("approved-test-data", "Approved Test Data available", TestDataDimensionState.Ready, $"{roles.Count} of {roles.Count} roles have an approved identity.")
            : new("approved-test-data", "Approved Test Data available", identities.State, $"{roles.Count(r => r.Ready)} of {roles.Count} roles have an approved identity."),
        ] };
        contract = contract with { Evidence =
        [
            xsds.Count == 0 ? new("source", "Source available", TestDataDimensionState.NotAssessed, "No imported source declares this contract.")
                : new("source", "Source available", TestDataDimensionState.Ready, $"{xsds.Select(x => x.SnapshotId).Distinct().Count()} source snapshot(s) declare {context.SchemaName}."),
            preselected is not null ? new("xsd-resolved", "XSD resolved", TestDataDimensionState.Ready, System.IO.Path.GetFileName(preselected.File))
                : xsds.Count > 1 ? new("xsd-resolved", "XSD resolved", TestDataDimensionState.NeedsAttention, "Several candidates — choose one.")
                : new("xsd-resolved", "XSD resolved", TestDataDimensionState.NotAssessed, "No XSD to resolve."),
        ] };
        mapping = mapping with { Evidence =
        [
            new("required-mappings", "Required mappings complete", mapping.State == TestDataDimensionState.NotAssessed ? TestDataDimensionState.NotAssessed : mapping.State,
                changed.Count > 0 ? "Stale since generation." : unmappedInXml.Count > 0 ? $"Missing: {string.Join(", ", unmappedInXml)}." : fixture is null ? "Confirmed when the fixture is generated." : "Every role inside the XML is mapped."),
        ] };
        persisted = persisted with { Evidence =
        [
            fixture is null ? new("xml-generated", "XML generated", TestDataDimensionState.NotAssessed, "No generated XML is stored — an editor preview is not evidence.")
                : new("xml-generated", "XML generated", TestDataDimensionState.Ready, $"{fixture.Alias}{(fixture.Version is { } gv ? $" v{gv}" : "")}{(fixture.DraftVersion is { } dv ? $" · draft v{dv}" : "")}"),
            fixture is null ? new("fixture-saved", "Fixture saved", TestDataDimensionState.Blocked, "Test Data lists no fixture for this contract.")
                : new("fixture-saved", "Fixture saved", TestDataDimensionState.Ready, "Listed by the Test Data catalog."),
            fixture is null ? new("fixture-approved", "Fixture approved", TestDataDimensionState.NotAssessed, "Nothing saved to approve.")
                : fixture.Approval == TestDataApproval.Approved
                    ? new("fixture-approved", "Fixture approved", TestDataDimensionState.Ready, fixture.DraftVersion is { } pending ? $"Approved version in use; draft v{pending} is not approved." : "Approved for test use.")
                    : new("fixture-approved", "Fixture approved", TestDataDimensionState.NeedsAttention, "Saved as a draft — a draft is never treated as approved."),
        ] };
        validation = validation with { Evidence =
        [
            new("schema-valid", "XML schema-valid", validation.State, status.ContractValidation == "Missing" ? "No saved fixture to validate." : status.ContractValidation),
        ] };

        return [identities, contract, mapping, persisted, validation, consumers, environment, runtime];
    }

    public static string Label(TestDataDimensionState state) => state switch
    {
        TestDataDimensionState.Ready => "Ready",
        TestDataDimensionState.NeedsAttention => "Needs attention",
        TestDataDimensionState.Blocked => "Blocked",
        _ => "Not assessed",
    };
}
