using System.Text.Json;
using BirkNext.Api.Services.TestData;
using BirkNext.TestData;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.TestData;

/// <summary>
/// Test Data sources and provenance: per-source status (available / configured but empty / not configured / unavailable, scoped), source
/// counts that reconcile with the assets, approval detail kept apart from validation detail, provenance per asset, and Schema Studio fixtures
/// that become Test Data only when explicitly registered — with intent, approval and schema reference preserved and no value leaked.
/// </summary>
public sealed class TestDataSourcesTests
{
    private sealed class Provider(string id, IReadOnlyList<TestDataAsset> assets, bool configured = false, Exception? failure = null) : ITestDataProvider
    {
        public string Id => id;
        public string Label => id;
        public string Description => "In-test source.";
        public string? Detail => "Provider-specific detail.";
        public bool Configured => configured || assets.Count > 0;
        public IReadOnlyList<TestDataAsset> Assets() => failure is null ? assets : throw failure;
    }

    private static TestDataAsset Asset(string alias, TestDataApproval approval = TestDataApproval.Approved, string? approvalDetail = null) => new()
    {
        Id = $"x:{alias}", Alias = alias, Kind = TestDataKind.Scalar, ValueType = TestDataValueType.Integer, Approval = approval, ApprovalDetail = approvalDetail,
        Sensitivity = TestDataSensitivity.Internal, Source = "X", Value = new TestDataValue("abc"), Provenance = [new("Configuration", "TestData:Assets")],
    };

    [Fact]
    public void SourceStatus_DistinguishesAvailableEmptyNotConfiguredAndUnavailable_AndCountsReconcile()
    {
        var view = new TestDataCatalog([
            new Provider("available", [Asset("one"), Asset("two")]),
            new Provider("empty", [], configured: true),
            new Provider("unset", []),
            new Provider("broken", [Asset("never")], failure: new IOException("disk")),
        ]).View();

        view.Sources.Select(s => (s.Id, s.Status, s.AssetCount)).Should().Equal(
            ("available", TestDataSourceStatus.Available, 2), ("empty", TestDataSourceStatus.Empty, 0),
            ("unset", TestDataSourceStatus.NotConfigured, 0), ("broken", TestDataSourceStatus.Unavailable, 0));
        view.Sources.Single(s => s.Id == "broken").Problem.Should().Contain("IOException").And.NotContain("disk", "the exception message is not echoed");
        view.Sources.Sum(s => s.AssetCount).Should().Be(view.Assets.Count);
        view.Assets.Select(a => a.SourceId).Should().AllBe("available");
        view.Sources.Should().OnlyContain(s => s.Detail == "Provider-specific detail.");
    }

    [Fact]
    public void ApprovalDetail_IsSeparateFromValidationDetail_AndProvenanceIsCarried()
    {
        var summary = new TestDataCatalog([new Provider("p", [Asset("blocked", TestDataApproval.Unapproved, "Configuration problem: alias pattern.")])]).View().Assets.Single();
        summary.ApprovalDetail.Should().Be("Configuration problem: alias pattern.");
        summary.ValidationDetail.Should().NotContain("Configuration problem", "an approval reason never replaces the validation detail");
        summary.Provenance.Should().ContainSingle().Which.Should().Be(new TestDataFact("Configuration", "TestData:Assets"));
    }

    // ── Schema Studio registered fixtures ──────────────────────────────────────────────────────────────────────────────

    private static readonly Guid Snapshot = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static string Document(params object[] items) => JsonSerializer.Serialize(new { version = 1, items }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static object Saved(string xml, object? testData, string scope = "import:a") => new
    {
        id = Guid.NewGuid(), packageFingerprint = new string('c', 64), fileName = "contracts/report.xsd", profileName = "Generic valid", xml, savedAt = DateTimeOffset.UtcNow,
        projectScope = scope, provenance = new { sourceSnapshotId = Snapshot, sourceSnapshotFingerprint = new string('a', 64), selectedContractPath = "contracts/report.xsd", contractFingerprint = new string('b', 64) },
        testData,
    };

    private static object Registration(string alias, string intent, bool approved) => new { alias, intent, approved, registeredAt = DateTimeOffset.UtcNow };

    [Fact]
    public void SchemaStudio_OnlyRegisteredSavedXmlBecomesTestData_WithIntentApprovalSchemaAndProvenance()
    {
        var provider = new SchemaStudioTestDataProvider(() =>
        [
            Document(Saved("<Report>unregistered-secret</Report>", null),
                     Saved("<Report>negative-secret</Report>", Registration("missing-startdato", SchemaStudioTestDataProvider.NegativeTest, true)),
                     Saved("<Report>draft-secret</Report>", Registration("draft-report", "positive", false))),
            "{ not json",
        ]);
        var assets = provider.Assets();
        assets.Select(a => a.Alias).Should().BeEquivalentTo(["missing-startdato", "draft-report"], "saved-but-unregistered XML is never Test Data");

        var negative = assets.Single(a => a.Alias == "missing-startdato");
        negative.Approval.Should().Be(TestDataApproval.Approved, "the user's explicit approval, independent of validity");
        negative.Capabilities.Should().Contain(SchemaStudioTestDataProvider.NegativeTest);
        negative.Sensitivity.Should().Be(TestDataSensitivity.Sensitive);
        negative.MediaType.Should().Be("application/xml");
        negative.Schema.Should().Be(new TestDataSchemaReference("contracts/report.xsd", Snapshot, new string('b', 64), null, "contracts/report.xsd"));
        negative.Provenance.Select(f => f.Label).Should().Contain(["Saved in", "Schema file", "Source snapshot", "Schema fingerprint", "Generation profile", "Project", "Registered"]);

        var draft = assets.Single(a => a.Alias == "draft-report");
        draft.Approval.Should().Be(TestDataApproval.Unapproved);
        draft.ApprovalDetail.Should().Contain("approve it in Test Data", "Schema Studio never approves; Test Data does");
        draft.ReviewRequested.Should().BeTrue();
        draft.Capabilities.Should().NotContain(SchemaStudioTestDataProvider.NegativeTest);

        var view = new TestDataCatalog([provider]).View();
        var json = JsonSerializer.Serialize(view);
        json.Should().NotContain("secret", "registered XML is sensitive: no value or preview leaves the backend");
        view.Sources.Single().Should().Match<TestDataSourceSummary>(s => s.Status == TestDataSourceStatus.Available && s.AssetCount == 2);
        new TestDataCatalog([provider]).Detail("schema-studio:missing-startdato")!.Preview.Should().BeNull();
    }

    [Fact]
    public void SchemaStudio_WithNothingRegistered_IsNotConfigured_NotAFailure()
    {
        var provider = new SchemaStudioTestDataProvider(() => [Document(Saved("<Report />", null))]);
        provider.Assets().Should().BeEmpty();
        new TestDataCatalog([provider]).View().Sources.Single().Status.Should().Be(TestDataSourceStatus.NotConfigured);
    }

    [Fact]
    public void SchemaStudio_RegistrationFacts_BecomeProvenance_ScopeAndNeedsReview()
    {
        var facts = new
        {
            expectedResult = "Invalid", validationStatus = "Invalid", mutation = "below minLength", targetRule = "Length boundaries: below minLength", path = "/Report/Code",
            profileId = Guid.NewGuid().ToString("D"), profileVersion = "built-in", generatorVersion = "schema-generator/2", rootElement = "Report", environmentId = "env-qa",
        };
        var registration = new { alias = "code-too-short", intent = SchemaStudioTestDataProvider.NegativeTest, approved = false, registeredAt = DateTimeOffset.UtcNow, facts };
        var provider = new SchemaStudioTestDataProvider(() => [Document(Saved("<Report>x</Report>", registration))]);
        var asset = provider.Assets().Single();

        asset.Environment.Should().Be("env-qa", "the scope the user chose at registration");
        asset.Provenance.Should().Contain(new TestDataFact("Expected schema result", "Invalid")).And.Contain(new TestDataFact("Mutation", "below minLength"))
            .And.Contain(new TestDataFact("Schema rule targeted", "Length boundaries: below minLength")).And.Contain(new TestDataFact("Generator", "schema-generator/2"));
        var summary = new TestDataCatalog([provider]).View().Assets.Single();
        summary.Status.Should().Be(TestDataAssetStatus.NeedsReview, "registered from Schema Studio, approved only in Test Data");
        summary.DataType.Should().Be(TestDataDataType.XmlPayload);
    }

    [Fact]
    public void SchemaStudio_InvalidAliasIsNeverApproved()
    {
        var asset = new SchemaStudioTestDataProvider(() => [Document(Saved("<Report />", Registration("Bad Alias", "positive", true)))]).Assets().Single();
        asset.Approval.Should().Be(TestDataApproval.Unapproved);
        asset.ApprovalDetail.Should().Contain("alias");
    }
}
