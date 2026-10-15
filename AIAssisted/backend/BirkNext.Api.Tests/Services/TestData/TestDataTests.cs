using System.Text.Json;
using System.Text.RegularExpressions;
using BirkNext.Api.Services.TestData;
using BirkNext.TestData;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.TestData;

/// <summary>
/// The generic typed Test Data capability on neutral data: typed assets of every kind, the catalog's safe summaries, type-aware validation
/// separate from approval, the deterministic resolver (no guessing, no generation), constraints, data sets and file safety.
/// </summary>
public sealed class TestDataTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    private const string SecretLike = "12345678901";

    private sealed class Provider(params TestDataAsset[] assets) : ITestDataProvider
    {
        public List<TestDataSet> DataSets { get; } = [];
        public string Id => "fixture";
        public string Label => "Fixture";
        public string Description => "In-test assets.";
        public IReadOnlyList<TestDataAsset> Assets() => assets;
        public IReadOnlyList<TestDataSet> Sets() => DataSets;
    }

    private static TestDataAsset Asset(string alias, TestDataKind kind, string? value, TestDataValueType type = TestDataValueType.None, string? media = null,
        string[]? capabilities = null, string? environment = null, TestDataApproval approval = TestDataApproval.Approved, bool isDefault = false,
        TestDataSensitivity sensitivity = TestDataSensitivity.Internal, string? entityType = null, DateTimeOffset? expires = null, TestDataValidation? validation = null) => new()
    {
        Id = $"fixture:{alias}", Alias = alias, Kind = kind, ValueType = type, MediaType = media, Capabilities = [.. capabilities ?? []], Environment = environment,
        Approval = approval, IsDefault = isDefault, Sensitivity = sensitivity, EntityType = entityType, ExpiresAt = expires, Validation = validation, Source = "Fixture",
        Value = value is null ? null : new TestDataValue(value),
    };

    private static TestDataCatalog Catalog(params TestDataAsset[] assets) => new([new Provider(assets)], new FixedClock(Now));

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }

    private static TestDataRoleResolution One(TestDataCatalog catalog, TestDataRequirement requirement, IReadOnlyDictionary<string, string>? selections = null) =>
        catalog.Resolve([requirement], selections: selections).Roles.Single();

    // ── Typed assets resolve by kind, type, media type and capability ─────────────────────────────────────────────

    [Fact]
    public void ScalarStringAndInteger_JsonXmlFileEntityAndExpectedResult_EachResolveByTheirOwnMetadata()
    {
        var catalog = Catalog(
            Asset("expected-status-accepted", TestDataKind.Scalar, "Accepted", TestDataValueType.String),
            Asset("period-lock-day", TestDataKind.Scalar, "10", TestDataValueType.Integer),
            Asset("order-request", TestDataKind.Structured, """{"id":1}""", media: "application/json", capabilities: ["order"]),
            Asset("valid-report", TestDataKind.Structured, "<report/>", media: "application/xml; charset=utf-8", capabilities: ["report", "valid-report"]),
            Asset("import-fixture-01", TestDataKind.File, "a,b\n1,2", media: "text/csv"),
            Asset("person-a", TestDataKind.EntityReference, SecretLike, entityType: "person", capabilities: ["person", "subject"], sensitivity: TestDataSensitivity.Sensitive),
            Asset("expected-record", TestDataKind.ExpectedResult, """{"status":"Stored"}""", media: "application/json", capabilities: ["record-expected"]));

        One(catalog, new() { Role = "Status", Kind = TestDataKind.Scalar, ValueType = TestDataValueType.String }).AssetAlias.Should().Be("expected-status-accepted");
        One(catalog, new() { Role = "LockDay", Kind = TestDataKind.Scalar, ValueType = TestDataValueType.Integer }).AssetAlias.Should().Be("period-lock-day");
        One(catalog, new() { Role = "Request", Kind = TestDataKind.Structured, MediaType = "application/json" }).AssetAlias.Should().Be("order-request");
        One(catalog, new() { Role = "Payload", Kind = TestDataKind.Structured, MediaType = "application/xml", Capabilities = ["valid-report"] }).AssetAlias.Should().Be("valid-report");
        One(catalog, new() { Role = "Import", Kind = TestDataKind.File, MediaType = "text/csv" }).AssetAlias.Should().Be("import-fixture-01");
        One(catalog, new() { Role = "Subject", Kind = TestDataKind.EntityReference, EntityType = "person", Capabilities = ["subject"] }).AssetAlias.Should().Be("person-a");
        One(catalog, new() { Role = "Expected", Kind = TestDataKind.ExpectedResult, MediaType = "application/json" }).AssetAlias.Should().Be("expected-record");
    }

    [Fact]
    public void ExactlyOne_AutoSelects_OneDefault_IsChosen_SeveralWithoutDefault_AreAmbiguous_None_IsUnresolved_NothingGenerated()
    {
        var requirement = new TestDataRequirement { Role = "Subject", Kind = TestDataKind.EntityReference, Capabilities = ["subject"] };
        One(Catalog(Asset("a", TestDataKind.EntityReference, "1", capabilities: ["subject"])), requirement)
            .Should().Match<TestDataRoleResolution>(item => item.State == TestDataResolutionState.Resolved && item.AssetAlias == "a");
        One(Catalog(Asset("a", TestDataKind.EntityReference, "1", capabilities: ["subject"]), Asset("b", TestDataKind.EntityReference, "2", capabilities: ["subject"], isDefault: true)), requirement)
            .AssetAlias.Should().Be("b");
        One(Catalog(Asset("a", TestDataKind.EntityReference, "1", capabilities: ["subject"]), Asset("b", TestDataKind.EntityReference, "2", capabilities: ["subject"])), requirement)
            .Should().Match<TestDataRoleResolution>(item => item.State == TestDataResolutionState.Ambiguous && item.AssetAlias == null && item.Candidates.SequenceEqual(new[] { "a", "b" }));
        One(Catalog(), requirement).Should().Match<TestDataRoleResolution>(item => item.State == TestDataResolutionState.Unresolved && item.Reason.Contains("Nothing is generated"));
    }

    [Fact]
    public void Exclusions_Environment_Approval_Lifecycle_Capability_MediaType_ValueType()
    {
        TestDataRoleResolution Only(TestDataAsset asset, TestDataRequirement requirement) => One(Catalog(asset), requirement);
        var subject = new TestDataRequirement { Role = "S", Kind = TestDataKind.EntityReference, Capabilities = ["subject"], Environment = "TT02", AllowEnvironmentAgnostic = false };

        Only(Asset("qa", TestDataKind.EntityReference, "1", capabilities: ["subject"], environment: "QA"), subject).State.Should().Be(TestDataResolutionState.Unresolved);
        Only(Asset("any", TestDataKind.EntityReference, "1", capabilities: ["subject"]), subject).State.Should().Be(TestDataResolutionState.Unresolved, "agnostic not allowed here");
        Only(Asset("any", TestDataKind.EntityReference, "1", capabilities: ["subject"]), subject with { AllowEnvironmentAgnostic = true }).AssetAlias.Should().Be("any");
        Only(Asset("u", TestDataKind.EntityReference, "1", capabilities: ["subject"], environment: "TT02", approval: TestDataApproval.Unapproved), subject).State.Should().Be(TestDataResolutionState.Unresolved);
        Only(Asset("d", TestDataKind.EntityReference, "1", capabilities: ["subject"], environment: "TT02", approval: TestDataApproval.Disabled), subject).State.Should().Be(TestDataResolutionState.Unresolved);
        Only(Asset("x", TestDataKind.EntityReference, "1", capabilities: ["subject"], environment: "TT02", expires: Now.AddMinutes(-1)), subject)
            .Should().Match<TestDataRoleResolution>(item => item.State == TestDataResolutionState.Unresolved && item.Reason.Contains("Expired"));
        Only(Asset("c", TestDataKind.EntityReference, "1", capabilities: ["other"], environment: "TT02"), subject).State.Should().Be(TestDataResolutionState.Unresolved);
        Only(Asset("j", TestDataKind.Structured, "{}", media: "application/json"), new() { Role = "P", Kind = TestDataKind.Structured, MediaType = "application/xml" }).State.Should().Be(TestDataResolutionState.Unresolved);
        Only(Asset("s", TestDataKind.Scalar, "x", TestDataValueType.String), new() { Role = "N", Kind = TestDataKind.Scalar, ValueType = TestDataValueType.Integer }).State.Should().Be(TestDataResolutionState.Unresolved);
    }

    [Fact]
    public void AnExplicitChoice_IsRespected_AndNeverReplacedByADefault()
    {
        var catalog = Catalog(Asset("a", TestDataKind.Scalar, "1", TestDataValueType.Integer), Asset("b", TestDataKind.Scalar, "2", TestDataValueType.Integer, isDefault: true));
        var requirement = new TestDataRequirement { Role = "N", Kind = TestDataKind.Scalar, ValueType = TestDataValueType.Integer };
        One(catalog, requirement, new Dictionary<string, string> { ["N"] = "a" }).Should().Match<TestDataRoleResolution>(item => item.State == TestDataResolutionState.Selected && item.AssetAlias == "a");
        One(catalog, requirement, new Dictionary<string, string> { ["N"] = "missing" }).Should().Match<TestDataRoleResolution>(item => item.State == TestDataResolutionState.Unresolved && item.AssetAlias == null);
    }

    [Fact]
    public void DistinctConstraint_IsCheckedOnValuesInTheBackend_AndReportsOnlyAliases()
    {
        var catalog = Catalog(Asset("left", TestDataKind.EntityReference, SecretLike, capabilities: ["a"], sensitivity: TestDataSensitivity.Sensitive),
            Asset("right", TestDataKind.EntityReference, SecretLike, capabilities: ["b"], sensitivity: TestDataSensitivity.Sensitive));
        var result = catalog.Resolve([new() { Role = "A", Kind = TestDataKind.EntityReference, Capabilities = ["a"] }, new() { Role = "B", Kind = TestDataKind.EntityReference, Capabilities = ["b"] }],
            [new(TestDataConstraintKind.Distinct, ["A", "B"])]);
        result.Satisfied.Should().BeFalse();
        result.ConstraintProblems.Should().ContainSingle().Which.Should().Contain("left").And.Contain("right").And.NotContain(SecretLike);
    }

    // ── Validation is type-aware and separate from approval ───────────────────────────────────────────────────────

    [Fact]
    public void ValidationIsTypeAware_AndAnApprovedInvalidFixture_StaysApprovedForNegativeTests()
    {
        TestDataValidation Of(TestDataAsset asset) => Catalog(asset).View().Assets.Single().Validation;
        Of(Asset("n", TestDataKind.Scalar, "10", TestDataValueType.Integer)).Should().Be(TestDataValidation.Validated);
        Of(Asset("n", TestDataKind.Scalar, "ten", TestDataValueType.Integer)).Should().Be(TestDataValidation.Invalid);
        Of(Asset("d", TestDataKind.Scalar, "2026-09-31", TestDataValueType.Date)).Should().Be(TestDataValidation.Invalid);
        Of(Asset("j", TestDataKind.Structured, "{", media: "application/json")).Should().Be(TestDataValidation.Invalid);

        var negative = Asset("invalid-report-missing-required-field", TestDataKind.Structured, "<report><unclosed></report>", media: "application/xml", capabilities: ["report", "invalid"]);
        var summary = Catalog(negative).View().Assets.Single();
        (summary.Approval, summary.Validation).Should().Be((TestDataApproval.Approved, TestDataValidation.Invalid), "approved for test use is not a valid payload");
        One(Catalog(negative), new() { Role = "Negative", Kind = TestDataKind.Structured, MediaType = "application/xml", Capabilities = ["invalid"] }).AssetAlias
            .Should().Be("invalid-report-missing-required-field", "a negative fixture is usable unless a consumer requires validated data");
    }

    [Fact]
    public void XmlValidation_RefusesDtdsAndExternalEntities()
    {
        var xxe = Asset("xxe", TestDataKind.Structured, """<?xml version="1.0"?><!DOCTYPE r [<!ENTITY e SYSTEM "file:///c:/windows/win.ini">]><r>&e;</r>""", media: "application/xml");
        Catalog(xxe).View().Assets.Single().Should().Match<TestDataAssetSummary>(item => item.Validation == TestDataValidation.Invalid && item.ValidationDetail!.Contains("DTD"));
    }

    [Fact]
    public void ExpectedAbsence_IsRepresentedWithoutAFakeDocument()
    {
        var absent = Asset("expected-no-record", TestDataKind.ExpectedResult, null, capabilities: ["record-expected"]) with { ExpectsAbsence = true };
        var summary = Catalog(absent).View().Assets.Single();
        (summary.ExpectsAbsence, summary.ValueAvailable, summary.Validation).Should().Be((true, false, TestDataValidation.Validated));
        One(Catalog(absent), new() { Role = "Expected", Kind = TestDataKind.ExpectedResult, Capabilities = ["record-expected"] }).AssetAlias.Should().Be("expected-no-record");
    }

    // ── Sensitivity and serialization ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SensitiveValuesNeverLeave_SafeScalarsMayShowTheirValue_PayloadsGiveAFingerprint()
    {
        var catalog = Catalog(Asset("person-a", TestDataKind.EntityReference, SecretLike, sensitivity: TestDataSensitivity.Sensitive),
            Asset("lock-day", TestDataKind.Scalar, "10", TestDataValueType.Integer),
            Asset("secret-scalar", TestDataKind.Scalar, "s3cr3t", TestDataValueType.String, sensitivity: TestDataSensitivity.Sensitive),
            Asset("payload", TestDataKind.Structured, "<r>body</r>", media: "application/xml"));
        var json = JsonSerializer.Serialize(catalog.View());
        json.Should().NotContain(SecretLike).And.NotContain("s3cr3t").And.NotContain("<r>body</r>");
        var view = catalog.View().Assets.ToDictionary(item => item.Alias);
        view["lock-day"].DisplayValue.Should().Be("10");
        view["person-a"].Should().Match<TestDataAssetSummary>(item => item.DisplayValue == null && item.ValueAvailable);
        view["payload"].Fingerprint.Should().MatchRegex("^[0-9a-f]{64}$");
        catalog.Detail("fixture:person-a")!.Preview.Should().BeNull();
        catalog.Detail("fixture:payload")!.Preview.Should().Be("<r>body</r>");
        new TestDataValue(SecretLike).ToString().Should().NotContain(SecretLike, "a value never prints itself");
    }

    [Fact]
    public void ADuplicatedAlias_IsDisabled_NeverPickedArbitrarily()
    {
        var catalog = Catalog(Asset("dup", TestDataKind.Scalar, "1", TestDataValueType.Integer), Asset("dup", TestDataKind.Scalar, "2", TestDataValueType.Integer));
        catalog.View().Assets.Should().OnlyContain(item => item.Approval == TestDataApproval.Disabled);
        One(catalog, new() { Role = "N", Kind = TestDataKind.Scalar }).State.Should().Be(TestDataResolutionState.Unresolved);
    }

    // ── Data sets ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AHeterogeneousDataSet_ResolvesEveryRole_ToleratesExtraRoles_AndRejectsAWrongEnvironment()
    {
        var provider = new Provider(
            Asset("subject-a", TestDataKind.EntityReference, SecretLike, capabilities: ["subject"], sensitivity: TestDataSensitivity.Sensitive),
            Asset("normal-report", TestDataKind.Structured, "<r/>", media: "application/xml", capabilities: ["report"]),
            Asset("expected-normal", TestDataKind.ExpectedResult, "{}", media: "application/json", capabilities: ["expected"]),
            Asset("other-report", TestDataKind.Structured, "<r/>", media: "application/xml", capabilities: ["report"]));
        provider.DataSets.Add(new TestDataSet
        {
            Id = "fixture:set:normal", Alias = "normal-case", Approval = TestDataApproval.Approved, Environment = "TT02", Capabilities = ["normal"],
            Bindings = new(StringComparer.Ordinal) { ["Subject"] = "subject-a", ["Payload"] = "normal-report", ["Expected"] = "expected-normal", ["Extra"] = "other-report" },
        });
        var catalog = new TestDataCatalog([provider], new FixedClock(Now));
        TestDataRequirement[] requirements =
        [
            new() { Role = "Subject", Kind = TestDataKind.EntityReference, Capabilities = ["subject"], Environment = "TT02" },
            new() { Role = "Payload", Kind = TestDataKind.Structured, MediaType = "application/xml", Capabilities = ["report"], Environment = "TT02" },
            new() { Role = "Expected", Kind = TestDataKind.ExpectedResult, MediaType = "application/json", Environment = "TT02" },
        ];

        var result = catalog.Resolve(requirements, dataSetCapabilities: ["normal"]);
        result.DataSetAlias.Should().Be("normal-case");
        result.Roles.Select(role => (role.Role, role.AssetAlias)).Should().Equal(("Subject", "subject-a"), ("Payload", "normal-report"), ("Expected", "expected-normal"));
        JsonSerializer.Serialize(catalog.View()).Should().NotContain(SecretLike);

        catalog.Resolve(requirements.Append(new() { Role = "Missing", Kind = TestDataKind.Scalar }).ToList(), dataSetCapabilities: ["normal"]).DataSetAlias
            .Should().BeNull("a set that lacks a required role does not resolve the scenario");
        catalog.Resolve([.. requirements.Select(item => item with { Environment = "QA" })], dataSetCapabilities: ["normal"]).DataSetAlias.Should().BeNull();
    }

    // ── Configured provider and file safety ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void ConfiguredFiles_AreReadOnlyFromInsideTheRoot_AndNothingIsApprovedSilently()
    {
        var root = Directory.CreateTempSubdirectory("birknext-testdata-");
        try
        {
            Directory.CreateDirectory(Path.Combine(root.FullName, "TestData", "reports"));
            File.WriteAllText(Path.Combine(root.FullName, "TestData", "reports", "normal.xml"), "<report/>");
            File.WriteAllText(Path.Combine(root.FullName, "outside.txt"), "outside");
            var provider = new ConfiguredTestDataProvider(new TestDataOptions
            {
                Assets =
                [
                    new() { Alias = "normal-report", Kind = TestDataKind.Structured, MediaType = "application/xml", File = "reports/normal.xml", Approval = TestDataApproval.Approved, Sensitivity = TestDataSensitivity.Internal },
                    new() { Alias = "escape", Kind = TestDataKind.File, File = "../outside.txt", Approval = TestDataApproval.Approved },
                    new() { Alias = "absolute", Kind = TestDataKind.File, File = Path.Combine(root.FullName, "outside.txt"), Approval = TestDataApproval.Approved },
                    new() { Alias = "not-approved", Kind = TestDataKind.Scalar, ValueType = TestDataValueType.Integer, Value = "3" },
                ],
            }, root.FullName);
            var view = new TestDataCatalog([provider]).View().Assets.ToDictionary(item => item.Alias);
            view["normal-report"].Should().Match<TestDataAssetSummary>(item => item.ValueAvailable && item.Validation == TestDataValidation.Validated);
            view["escape"].Should().Match<TestDataAssetSummary>(item => !item.ValueAvailable && item.Validation == TestDataValidation.Unavailable);
            view["absolute"].ValueAvailable.Should().BeFalse();
            JsonSerializer.Serialize(view).Should().NotContain(root.FullName.Replace("\\", "\\\\")).And.NotContain("outside\"");
            view["not-approved"].Approval.Should().Be(TestDataApproval.Unapproved, "nothing is approved unless configured so");
        }
        finally { root.Delete(true); }
    }

    // ── Architecture ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GenericTestData_KnowsNoDomainProjectOrPersonCentricModel()
    {
        var root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "BirkNext.Api"))) root = Path.GetDirectoryName(root)!;
        var files = Directory.EnumerateFiles(Path.Combine(root, "BirkNext.Api", "Services", "TestData"), "*.cs")
            .Append(Path.GetFullPath(Path.Combine(root, "..", "shared", "TestDataContracts.cs"))).Append(Path.Combine(root, "BirkNext.Api", "Controllers", "TestDataController.cs")).ToList();
        files.Should().HaveCountGreaterThan(5);
        var forbidden = new Regex(@"(?i:Skole|skolenaervaer|Altinn|Bufdata|reporter-a|synthetic-child|Code6|Kode6|M2LB|buf/|TestPerson|TestIdentity|CriticalE2E)|BiRK", RegexOptions.CultureInvariant);
        files.Where(file => forbidden.IsMatch(File.ReadAllText(file))).Select(Path.GetFileName).Should().BeEmpty();
    }
}
