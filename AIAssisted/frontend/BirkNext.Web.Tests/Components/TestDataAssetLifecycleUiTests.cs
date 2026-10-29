using BirkNext.Integrations;
using BirkNext.PerformanceTests;
using BirkNext.TestData;
using BirkNext.Web.Components;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using R = BirkNext.PerformanceTests.PerformanceScenarioRuntime;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Test Data asset lifecycle in the browser: protected defaults are editable but never deletable, Edit shows id/type/role/scope read-only and the
/// value only there, a value change of a used asset asks for confirmation, Delete is usage-aware (Replace references… or Delete and mark references
/// unresolved), Reset Test Data previews before it runs, and consumers show a deleted alias as missing. The values here are placeholders, not identifiers.
/// </summary>
public sealed class TestDataAssetLifecycleUiTests : BunitContext
{
    private const string ChildId = "altinn-local:child:skolenaervaer-child-default";
    private readonly Mock<ITestDataApiService> _api = new();
    private TestDataCatalogView _catalog;

    private static TestDataAssetSummary Child(int version = 0, bool modified = false) => new()
    {
        Id = ChildId, Alias = "skolenaervaer-child-default", Label = "Skolenærvær default child", Kind = TestDataKind.EntityReference, EntityType = "person",
        Capabilities = ["child", "person"], Approval = TestDataApproval.Approved, Status = TestDataAssetStatus.Approved, Source = "Approved local test data", SourceId = "altinn-local",
        Ownership = TestDataOwnership.ProtectedDefault, ValueEditable = true, ValueVersion = version, ValueModified = modified, DataType = TestDataDataType.SimpleValues,
    };

    private static TestDataAssetSummary Observed(string alias, int consumers = 0, TestDataApproval approval = TestDataApproval.Unapproved) => new()
    {
        Id = $"registered:{alias}", Alias = alias, Kind = TestDataKind.Scalar, Approval = approval, Status = approval == TestDataApproval.Approved ? TestDataAssetStatus.Approved : TestDataAssetStatus.NeedsReview,
        Source = "Registered assets", SourceId = "registered", Ownership = TestDataOwnership.Registered, Consumers = consumers, DataType = TestDataDataType.SimpleValues, Environment = "development",
    };

    private static readonly TestDataAssetUsage ScenarioUsage = new("observed-id-development", "Performance scenario", "def-1", "Baseline check", "Step 2 (Get user) · brukerId") { EnvironmentId = "dev" };

    public TestDataAssetLifecycleUiTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        _catalog = new([Child(), Observed("observed-id-development", consumers: 1), Observed("observed-unused")], [], []);
        _api.Setup(a => a.CatalogAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => (_catalog, (string?)null));
        _api.Setup(a => a.ConsumersAsync(It.IsAny<CancellationToken>())).ReturnsAsync((new TestDataConsumerView([], [], new TestDataOverview(3, 0, 0, 0, 0, 0, 0, 0, 0)), (string?)null));
        _api.Setup(a => a.ReferencesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(((IReadOnlyList<TestDataAssetUsage>?)[ScenarioUsage], (string?)null));
        Services.AddSingleton(_api.Object);
        var journeys = new Mock<IIntegrationJourneysApiService>();
        journeys.Setup(j => j.IntegrationTemplatesAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActiveEventApiResult<IReadOnlyList<IntegrationTemplateView>>([], null));
        Services.AddSingleton(journeys.Object);
    }

    private IRenderedComponent<TestDataAssetDialog> Dialog(TestDataAssetSummary asset, TestDataAssetDialog.Mode mode, List<string?>? closed = null) =>
        Render<TestDataAssetDialog>(p => p.Add(x => x.Asset, asset).Add(x => x.Initial, mode).Add(x => x.Assets, _catalog.Assets)
            .Add(x => x.OnClose, (string? message) => closed?.Add(message)));

    [Fact]
    public void The_table_marks_protected_defaults_offers_edit_but_no_delete_and_shows_usage_counts()
    {
        var page = Render<TestDataPage>();
        page.WaitForAssertion(() => page.FindAll("[data-testid=td-asset]").Should().HaveCount(3));

        var child = page.Find("[data-testid=td-asset][data-alias=skolenaervaer-child-default]");
        child.QuerySelector("[data-testid=td-protected]")!.TextContent.Should().Be("Protected default");
        child.QuerySelector("[data-testid=td-edit]").Should().NotBeNull();
        child.QuerySelector("[data-testid=td-delete]").Should().BeNull("a protected default is never deletable");
        child.QuerySelector("[data-testid=td-not-deletable]")!.TextContent.Should().Contain("Protected").And.Contain("cannot be deleted", "the reason is text, not colour");
        child.QuerySelector("[data-testid=td-used-count]")!.TextContent.Should().Be(TestDataPresentation.UsedBy(0));

        var used = page.Find("[data-testid=td-asset][data-alias=observed-id-development]");
        used.QuerySelector("[data-testid=td-used-count]")!.TextContent.Should().Be(TestDataPresentation.UsedBy(1));
        used.QuerySelector("[data-testid=td-view-usages]").Should().NotBeNull();
        used.QuerySelector("[data-testid=td-delete]")!.ClassList.Should().Contain("btn-danger");
        page.Markup.Should().NotContain("SYNTHETIC", "no value is ever rendered in the table");
    }

    [Fact]
    public void Protected_default_modified_is_a_neutral_marker()
    {
        _catalog = new([Child(version: 1, modified: true)], [], []);
        var page = Render<TestDataPage>();
        page.WaitForAssertion(() => page.Find("[data-testid=td-protected]").TextContent.Should().Be("Protected default · Modified"));
        page.Find("[data-testid=td-protected]").ClassList.Should().Contain("td-pill-neutral");
    }

    [Fact]
    public void Edit_shows_id_type_role_scope_read_only_and_saves_a_changed_value_after_the_impact_confirmation()
    {
        var childUsage = new TestDataAssetUsage("skolenaervaer-child-default", "Performance scenario", "def-2", "Child lookup", "Step 1 · childId");
        _api.Setup(a => a.EditViewAsync(ChildId, It.IsAny<CancellationToken>())).ReturnsAsync((new TestDataEditView
        {
            Asset = Child(), Role = "Child", Value = "SYNTHETIC-OLD", ValueEditable = true, LabelEditable = true, CanRestoreDefault = true, Usages = [childUsage],
        }, (string?)null));
        _api.Setup(a => a.EditAsync(ChildId, It.IsAny<TestDataEditRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new TestDataEditResult(true, "Test Data asset updated.", true, Child(1, true), [childUsage]), (string?)null));
        var closed = new List<string?>();
        var cut = Dialog(Child(), TestDataAssetDialog.Mode.Edit, closed);

        cut.WaitForAssertion(() => cut.Find("[data-testid=tda-id]").TextContent.Should().Be(ChildId));
        cut.Find("[data-testid=tda-role]").TextContent.Should().Be("Child");
        cut.Find("[data-testid=tda-scope]").TextContent.Should().Be("Shared");
        cut.Find("[data-testid=tda-type]").TextContent.Should().Be("Simple values / parameters");
        cut.Find("[data-testid=tda-usage-summary]").TextContent.Should().Be("Used by 1");
        cut.FindAll("[data-testid=tda-id] input").Should().BeEmpty("the canonical id is read-only");
        cut.Find("label[for$='-value']").TextContent.Should().Contain("child identifier", "the value field is labelled with its role");

        cut.Find("[data-testid=tda-value]").Change("SYNTHETIC-NEW");
        cut.Find("[data-testid=tda-save]").Click();

        cut.Find("[data-testid=tda-impact]").TextContent.Should().Contain("Update Test Data asset?").And.Contain("Child lookup").And.Contain("affects future uses");
        _api.Verify(a => a.EditAsync(It.IsAny<string>(), It.IsAny<TestDataEditRequest>(), It.IsAny<CancellationToken>()), Times.Never, "nothing is saved before the confirmation");

        cut.Find("[data-testid=tda-save-confirm]").Click();

        cut.WaitForAssertion(() => closed.Should().ContainSingle().Which.Should().StartWith("Test Data asset updated."));
        _api.Verify(a => a.EditAsync(ChildId, It.Is<TestDataEditRequest>(r => r.Value == "SYNTHETIC-NEW"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void A_payload_edit_never_offers_an_in_place_value_change()
    {
        var fixture = new TestDataAssetSummary { Id = "schema-studio:report", Alias = "report", Kind = TestDataKind.Structured, MediaType = "application/xml", Ownership = TestDataOwnership.Generated };
        _api.Setup(a => a.EditViewAsync(fixture.Id, It.IsAny<CancellationToken>())).ReturnsAsync((new TestDataEditView
        {
            Asset = fixture, ValueNote = "An XML/JSON payload is never changed in place. Regenerate it as a new draft from Test Data → Create XML fixture; the approved version stays unchanged.",
        }, (string?)null));
        var cut = Dialog(fixture, TestDataAssetDialog.Mode.Edit);
        cut.WaitForAssertion(() => cut.Find("[data-testid=tda-value-note]").TextContent.Should().Contain("new draft"));
        cut.FindAll("[data-testid=tda-value]").Should().BeEmpty();
        cut.FindAll("[data-testid=tda-save]").Should().BeEmpty();
    }

    [Fact]
    public void Deleting_an_unused_asset_asks_a_simple_confirmation()
    {
        _api.Setup(a => a.DeleteAsync("registered:observed-unused", false, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new TestDataDeleteResult("registered:observed-unused", "observed-unused", true, "Deleted observed-unused.", []), (string?)null));
        var closed = new List<string?>();
        var cut = Dialog(Observed("observed-unused"), TestDataAssetDialog.Mode.Delete, closed);

        cut.WaitForAssertion(() => cut.Find("[data-testid=tda-delete-simple]").TextContent.Should().Contain("cannot be undone"));
        cut.Find("[data-testid=tda]").GetAttribute("role").Should().Be("alertdialog");
        cut.Find("[data-testid=tda-delete]").Click();

        cut.WaitForAssertion(() => closed.Should().Equal("Deleted observed-unused."));
    }

    [Fact]
    public void Deleting_a_used_asset_lists_its_usages_and_offers_replace_or_delete_and_mark_unresolved()
    {
        var cut = Dialog(Observed("observed-id-development", 1), TestDataAssetDialog.Mode.Delete);

        cut.WaitForAssertion(() => cut.Find("[data-testid=tda-usage]").TextContent.Should().Contain("Performance scenario").And.Contain("Baseline check").And.Contain("Step 2 (Get user) · brukerId"));
        cut.Find("[data-testid=tda-usage] a").GetAttribute("href").Should().Be("performance-test-review?definition=def-1");
        cut.Find("[data-testid=tda-delete-warning]").TextContent.Should().Contain("leave these references unresolved");
        cut.FindAll("[data-testid=tda-delete]").Should().BeEmpty("an in-use asset has no plain Delete");
        cut.Find("[data-testid=tda-replace]").TextContent.Should().Be("Replace references…");
        cut.Find("[data-testid=tda-delete-unresolved]").TextContent.Should().Be("Delete and mark references unresolved");
        _api.Verify(a => a.DeleteAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Replace_references_lists_only_backend_compatible_assets_then_replaces_and_deletes_the_original()
    {
        var approved = Observed("approved-user-01", approval: TestDataApproval.Approved);
        _api.Setup(a => a.ReplacementsAsync("registered:observed-id-development", It.IsAny<CancellationToken>())).ReturnsAsync(((IReadOnlyList<TestDataAssetSummary>?)[approved], (string?)null));
        _api.Setup(a => a.ReplaceAsync("registered:observed-id-development", approved.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new TestDataReplaceResult(true, "1 reference now uses approved-user-01.", 1, []), (string?)null));
        _api.Setup(a => a.DeleteAsync("registered:observed-id-development", false, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new TestDataDeleteResult("registered:observed-id-development", "observed-id-development", true, "Deleted.", []), (string?)null));
        var closed = new List<string?>();
        var cut = Dialog(Observed("observed-id-development", 1), TestDataAssetDialog.Mode.Delete, closed);
        cut.WaitForAssertion(() => cut.Find("[data-testid=tda-replace]"));

        cut.Find("[data-testid=tda-replace]").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=tda-replacement]").Should().ContainSingle());
        cut.Find("[data-testid=tda-affected]").TextContent.Should().Be("1 affected reference");
        cut.Find("[data-testid=tda-replace-delete]").HasAttribute("disabled").Should().BeTrue("nothing is chosen for the tester");
        cut.Find("[data-testid=tda-replacement]").Change(true);
        cut.Find("[data-testid=tda-replace-delete]").Click();

        cut.WaitForAssertion(() => closed.Should().ContainSingle().Which.Should().Contain("approved-user-01").And.Contain("observed-id-development was deleted"));
        _api.Verify(a => a.DeleteAsync("registered:observed-id-development", false, It.IsAny<CancellationToken>()), Times.Once, "the original is deleted only after the references moved");
    }

    [Fact]
    public void Reset_Test_Data_previews_before_running_and_summarises_actual_counts()
    {
        _api.Setup(a => a.ResetPreviewAsync(It.IsAny<CancellationToken>())).ReturnsAsync((new TestDataResetPreview
        {
            ProtectedDefaults = ["skolenaervaer-child-default", "skolenaervaer-reporter-default"], AssetsToRemove = ["observed-bruker-id-development", "observed-id-development"],
            AffectedReferences = [ScenarioUsage],
        }, (string?)null));
        _api.Setup(a => a.ResetAsync(0, It.IsAny<CancellationToken>())).ReturnsAsync((new TestDataResetResult(
            new TestDataResetSummary { ProtectedDefaultsPreserved = 2, AssetsRemoved = 2, ReferencesRequiringRemapping = 1 }, [ScenarioUsage with { Unresolved = true }]), (string?)null));
        var cut = Render<TestDataResetPanel>();

        cut.Find("[data-testid=tdr-open]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=tdr-will-preserve]").TextContent.Should().Contain("2 protected defaults").And.Contain("current values"));
        cut.Find("[data-testid=tdr-will-remove]").TextContent.Should().Contain("2 assets").And.Contain("0 XML fixtures");
        cut.Find("[data-testid=tdr-will-affect]").TextContent.Should().Contain("1 consumer reference");
        _api.Verify(a => a.ResetAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never, "the preview never resets");

        cut.Find("[data-testid=tdr-confirm]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=tdr-summary]").TextContent.Should().Contain("Test Data reset complete"));
        cut.Find("[data-testid=tdr-preserved]").TextContent.Should().Be("2 protected defaults preserved");
        cut.Find("[data-testid=tdr-removed]").TextContent.Should().Be("2 Test Data assets removed");
        cut.Find("[data-testid=tdr-remap]").TextContent.Should().Be("1 consumer reference requires remapping");
    }

    [Fact]
    public void A_scenario_mapping_to_a_deleted_alias_is_a_missing_reference_never_a_fallback()
    {
        var definition = new PerformanceTestDefinition
        {
            Scenario = new PerformanceScenario
            {
                Steps = [new HttpPerformanceStep { RelativePath = "/api/users/{brukerId}", DataMappings = [new ScenarioDataMapping("brukerId", ["observed-id-development"], TestDataDataType.SimpleValues, "path")] }],
            },
        };

        R.Rows(definition.Scenario, 0).Single().Problem.Should().BeNull("without the catalog, the mapping is not judged");
        R.Rows(definition.Scenario, 0, new HashSet<string>(["approved-user-01"])).Single().Problem.Should().Be("Test Data reference missing: observed-id-development no longer exists — remap this value");
        R.Readiness(definition, 0, new HashSet<string>(["approved-user-01"])).Status.Should().NotBe(StepStatus.Ready);
    }

    [Fact]
    public void A_fixture_built_from_an_earlier_value_is_reported_as_mapped_test_data_changed()
    {
        var fixture = new TestDataAssetSummary
        {
            Alias = "report", Ownership = TestDataOwnership.Generated,
            Provenance = [new(TestDataValueVersions.Label, "skolenaervaer-child-default@v0")],
        };
        TestDataValueVersions.Changed(fixture, [Child(version: 0)]).Should().BeEmpty();
        TestDataValueVersions.Changed(fixture, [Child(version: 1)]).Should().Equal([("skolenaervaer-child-default", false)]);
        TestDataValueVersions.Changed(fixture, []).Should().Equal([("skolenaervaer-child-default", true)]);
        TestDataValueVersions.Format([Child(version: 2), Observed("a")]).Should().Be("a@v0; skolenaervaer-child-default@v2");
    }
}
