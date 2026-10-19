using BirkNext.Api.Services.AltinnTesting;
using BirkNext.Api.Services.TestData;
using BirkNext.TestData;

namespace BirkNext.Api.Services.IntegrationJourneys.Packs.SkoleNaervaer;

/// <summary>
/// Skolenærvær's test-data profile: the roles a report run needs (a reporter and a synthetic child), expressed as generic
/// <see cref="TestDataRequirement"/>s and resolved by the generic <see cref="TestDataResolver"/> over the shared Test Data catalog (the one
/// reusable catalog; the requirements name the only source whose assets the Altinn executor can use). This class holds
/// no matching logic and no storage: a value is resolved automatically only when the choice is deterministic (exactly one qualifying asset,
/// or exactly one default); otherwise the tester chooses. Nothing is generated. Identifiers stay in the backend; callers get aliases, except
/// the run's own form builder.
/// </summary>
public sealed class SkoleNaervaerTestData(AltinnTestingOptions options, TestDataCatalog? shared = null)
{
    /// <summary>The provenance shown for values resolved here.</summary>
    public const string Source = "Test Data";
    public const string ReporterRole = "Reporter";
    public const string ChildRole = "Child";

    private readonly AltinnTestDataProvider _provider = new(options);
    /// <summary>The shared catalog when the host provides it; a unit without the host reads the same provider directly (same candidates).</summary>
    private TestDataCatalog Catalog => shared ?? new([_provider]);
    /// <summary>The source whose assets the Altinn run can use (its identifiers stay in this backend's local configuration).</summary>
    public const string AltinnSourceId = "altinn-local";

    /// <summary>An approved synthetic TestID reporter for TT02 whose Altinn profile is confirmed.</summary>
    public static TestDataRequirement Reporter => new()
    {
        Role = ReporterRole, Kind = TestDataKind.EntityReference, EntityType = "person", Capabilities = [AltinnTestDataProvider.Reporter],
        Environment = "TT02", AllowEnvironmentAgnostic = false, RequireValidated = true, SourceIds = [AltinnSourceId],
    };

    /// <summary>An approved synthetic child, declared known (or unknown) to Person.</summary>
    public static TestDataRequirement Child(bool knownToPerson) => new()
    {
        Role = ChildRole, Kind = TestDataKind.EntityReference, EntityType = "person",
        Capabilities = ["synthetic", AltinnTestDataProvider.Child, knownToPerson ? AltinnTestDataProvider.KnownToPerson : AltinnTestDataProvider.UnknownToPerson],
        Environment = "TT02", AllowEnvironmentAgnostic = true, SourceIds = [AltinnSourceId],
    };

    /// <summary>The reporter and the child are different persons (compared in the backend).</summary>
    public static readonly TestDataConstraint ReporterIsNotChild = new(TestDataConstraintKind.Distinct, [ReporterRole, ChildRole]);

    /// <summary>A report run's test data: reporter and child, the explicit choices respected, reporter ≠ child enforced.</summary>
    public TestDataResolutionResult Resolve(bool knownToPerson, IReadOnlyDictionary<string, string>? selections = null) =>
        Catalog.Resolve([Reporter, Child(knownToPerson)], [ReporterIsNotChild], selections);

    /// <summary>Why an approved child cannot be used, or null. Never contains an identifier.</summary>
    public string? ChildProblem(SyntheticChildOptions child) => _provider.ChildProblem(child);

    /// <summary>The usable approved children for a scenario that needs a known (registered) or an unknown child.</summary>
    public IReadOnlyList<SyntheticChildOptions> Children(bool knownToPerson) =>
        [.. Catalog.Candidates(Child(knownToPerson)).Select(asset => options.SyntheticChildren.First(child => string.Equals(child.Alias, asset.Alias, StringComparison.Ordinal)))];

    /// <summary>The child to preselect, or null when none is usable or several are and no single default decides.</summary>
    public SyntheticChildOptions? DefaultChild(bool knownToPerson) =>
        Catalog.Resolve([Child(knownToPerson)]).Role(ChildRole) is { State: TestDataResolutionState.Resolved, AssetAlias: { } alias }
            ? options.SyntheticChildren.First(child => string.Equals(child.Alias, alias, StringComparison.Ordinal)) : null;

    /// <summary>The identifier behind an approved alias for the run's form, or null. Only the form builder uses it; it is never returned to a client.</summary>
    public string? ChildIdentifier(string? alias, bool knownToPerson) =>
        Catalog.Candidates(Child(knownToPerson)).FirstOrDefault(asset => string.Equals(asset.Alias, alias, StringComparison.Ordinal))?.Value?.Reveal();

    /// <summary>The first configuration problem among the approved children, or null.</summary>
    public string? FirstChildProblem => options.SyntheticChildren.Select(ChildProblem).FirstOrDefault(problem => problem is not null);

    /// <summary>The usable (configured and profile-confirmed) approved reporters.</summary>
    public IReadOnlyList<SyntheticReporterOptions> Reporters =>
        [.. Catalog.Candidates(Reporter).Select(asset => options.UnattendedIdPortenTest.Reporters.First(reporter => string.Equals(reporter.Alias, asset.Alias, StringComparison.Ordinal)))];

    /// <summary>The reporter alias to preselect: the only usable approved reporter, else null (the tester chooses).</summary>
    public string? DefaultReporter => Catalog.Resolve([Reporter]).Role(ReporterRole) is { State: TestDataResolutionState.Resolved, AssetAlias: { } alias } ? alias : null;
}
