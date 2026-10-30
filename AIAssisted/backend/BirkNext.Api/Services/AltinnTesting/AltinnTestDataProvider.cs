using System.Text.RegularExpressions;
using BirkNext.Api.Services.IntegrationJourneys.Packs.SkoleNaervaer;
using BirkNext.Api.Services.TestData;
using BirkNext.TestData;

namespace BirkNext.Api.Services.AltinnTesting;

/// <summary>
/// The local Altinn testing configuration as test data: approved synthetic TestID reporters (<c>AltinnTesting:UnattendedIdPortenTest:Reporters</c>)
/// and approved synthetic persons (<c>AltinnTesting:SyntheticChildren</c>) become sensitive entity references. Nothing is re-entered or migrated;
/// identifiers stay in the backend. A reporter is approved when its configuration is usable and validated when its Altinn TT02 profile is
/// confirmed; a synthetic person is approved when its identifier is synthetic and it is not any reporter's person. Nothing is generated.
/// </summary>
public sealed partial class AltinnTestDataProvider(AltinnTestingOptions options) : ITestDataProvider
{
    public const string SourceLabel = "Approved local test data";
    public const string Reporter = "reporter";
    public const string Child = "child";
    public const string KnownToPerson = "known-to-person";
    public const string UnknownToPerson = "unknown-to-person";

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex AliasPattern();

    public string Id => "altinn-local";

    /// <summary>Canonical contexts of this source's identities (ids, not display text).</summary>
    public static readonly IReadOnlyList<string> ReporterContexts = [TestDataContexts.AltinnIntake, TestDataContexts.Skolenaervaer];
    public static readonly IReadOnlyList<string> ChildContexts = [TestDataContexts.AltinnIntake, TestDataContexts.SkolenaervaerReport];
    public string? StatusLabel(TestDataSourceStatus status) => status == TestDataSourceStatus.Available ? "Approved local references" : "No approved assets";
    public string Label => SourceLabel;
    public string Description => "Approved local test references supplied by environment or test configuration. Identifiers are never shown.";
    public string? Detail => "Approved synthetic TestID reporters and synthetic children: entered in Test Data for this backend session (memory only), or supplied by the backend's own configuration.";
    public bool Configured => options.UnattendedIdPortenTest.Reporters.Count > 0 || options.SyntheticChildren.Count > 0;

    private static TestDataFact Origin(bool builtIn, DateTimeOffset? sessionExpiresAt) =>
        builtIn ? new("Built-in", "Skolenærvær default test identity (Test Data built-in)")
        : sessionExpiresAt is null ? new("Configuration", "Backend configuration") : new("Session", "Entered in Test Data for this backend session");

    /// <summary>Built-in (system-seeded) identities are protected defaults; session identities are local data; the rest is explicit configuration.</summary>
    public static TestDataOwnership Ownership(bool builtIn, DateTimeOffset? sessionExpiresAt) =>
        builtIn ? TestDataOwnership.ProtectedDefault : sessionExpiresAt is null ? TestDataOwnership.Configuration : TestDataOwnership.SessionEntered;

    public IReadOnlyList<TestDataAsset> Assets()
    {
        var assets = new List<TestDataAsset>();
        foreach (var reporter in options.UnattendedIdPortenTest.Reporters.Where(item => !string.IsNullOrWhiteSpace(item.Alias)))
        {
            var problem = AltinnTestingPolicy.ReporterConfigurationProblem(reporter);
            assets.Add(new TestDataAsset
            {
                Id = $"{Id}:reporter:{reporter.Alias}", Alias = reporter.Alias, Label = reporter.Label, Kind = TestDataKind.EntityReference, EntityType = "person",
                Environment = reporter.AllowedEnvironment, Capabilities = ["person", "synthetic", Reporter, $"altinn-role-{reporter.AltinnRole.ToLowerInvariant()}"],
                Approval = problem is null ? TestDataApproval.Approved : TestDataApproval.Unapproved, ApprovalDetail = problem,
                Validation = reporter.AltinnProfileConfirmed ? TestDataValidation.Validated : TestDataValidation.NeedsValidation,
                ValidationDetail = reporter.AltinnProfileConfirmed ? "Altinn TT02 profile confirmed (configured confirmation; not an authentication)." : "Altinn TT02 profile not confirmed.",
                Sensitivity = TestDataSensitivity.Sensitive, Source = SourceLabel, IsDefault = reporter.Default,
                Value = string.IsNullOrWhiteSpace(reporter.TestIdPersonIdentifier) ? null : new TestDataValue(reporter.TestIdPersonIdentifier.Trim()),
                Provenance = [Origin(reporter.BuiltIn, reporter.SessionExpiresAt)], Ownership = Ownership(reporter.BuiltIn, reporter.SessionExpiresAt),
                ValueVersion = reporter.ValueVersion, ValueUpdatedAt = reporter.ValueUpdatedAt, ValueModified = reporter.ValueModified, ValueEditable = reporter.BuiltIn || reporter.SessionExpiresAt is not null,
                // Declared by this source: a TestID reporter is the login/submission identity of Altinn intake (Skolenærvær).
                SemanticRole = TestDataSemanticRoles.Reporter, AppliesTo = [.. ReporterContexts], MetadataFixed = true,
            });
        }
        foreach (var child in options.SyntheticChildren.Where(item => !string.IsNullOrWhiteSpace(item.Alias)))
        {
            var problem = ChildProblem(child);
            assets.Add(new TestDataAsset
            {
                Id = $"{Id}:child:{child.Alias}", Alias = child.Alias, Label = child.Label, Kind = TestDataKind.EntityReference, EntityType = "person",
                Capabilities = ["person", "synthetic", Child, child.KnownToPerson ? KnownToPerson : UnknownToPerson],
                Approval = problem is null ? TestDataApproval.Approved : TestDataApproval.Unapproved, ApprovalDetail = problem,
                Validation = TestDataValidation.Unknown,
                ValidationDetail = child.KnownToPerson ? "Declared known to Person; not verified by BirkNext." : "Declared unknown to Person; not verified by BirkNext.",
                Sensitivity = TestDataSensitivity.Sensitive, Source = SourceLabel, IsDefault = child.Default,
                Value = string.IsNullOrWhiteSpace(child.TestPersonIdentifier) ? null : new TestDataValue(child.TestPersonIdentifier.Trim()),
                Provenance = [Origin(child.BuiltIn, child.SessionExpiresAt)], Ownership = Ownership(child.BuiltIn, child.SessionExpiresAt),
                ValueVersion = child.ValueVersion, ValueUpdatedAt = child.ValueUpdatedAt, ValueModified = child.ValueModified, ValueEditable = child.BuiltIn || child.SessionExpiresAt is not null,
                // A synthetic child is the business identity inside the report payload — never an authentication identity.
                SemanticRole = TestDataSemanticRoles.Child, AppliesTo = [.. ChildContexts], MetadataFixed = true,
            });
        }
        return assets;
    }

    /// <summary>Why an approved synthetic person cannot be used, or null. Never contains an identifier.</summary>
    public string? ChildProblem(SyntheticChildOptions child)
    {
        if (!AliasPattern().IsMatch(child.Alias)) return "The synthetic child alias must be lowercase letters, digits and hyphens.";
        if (options.SyntheticChildren.Count(item => string.Equals(item.Alias, child.Alias, StringComparison.Ordinal)) > 1) return $"Synthetic child {child.Alias}: the alias is configured more than once.";
        var identifier = child.TestPersonIdentifier?.Trim();
        // Domain-correct child rule (date part and control digits); the reporter's TestID rule (month + 80) does not apply to a child.
        if (!SyntheticPersonIdentifier.IsTestPerson(identifier))
            return $"Synthetic child {child.Alias}: the identifier is missing or not a valid 11-digit person identifier (date part and control digits).";
        if (options.UnattendedIdPortenTest.Reporters.FirstOrDefault(reporter => string.Equals(reporter.TestIdPersonIdentifier?.Trim(), identifier, StringComparison.Ordinal)) is { } same)
            return $"Synthetic child {child.Alias} is the same person as reporter {same.Alias}; the reporter and the child must be different persons.";
        return null;
    }
}
