using BirkNext.TestPlanning;
using BirkNext.Web.Components;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Export to Azure DevOps (Plan &amp; prepare): Excel is the default, every case shows why it is or is not in the file, destination values are shown
/// as "confirm in Azure DevOps" (never verified offline), blocking problems disable the download, the download goes through the browser with the
/// file the server built, update mode asks for ids and an explicit confirmation, settings are saved only with write permission.
/// </summary>
public sealed class AdoExportWizardTests : BunitContext
{
    private static readonly Guid PlanId = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private readonly Mock<IAdoExportApiService> _api = new();
    private readonly List<AdoExportRequest> _previews = [];
    private AdoExportRequest? _file;

    private static TestPlanDraft Draft() => new()
    {
        Id = PlanId, GuideKey = "project:library", ProjectKey = "library", PlanName = "Library plan", Revision = 4,
        Cases =
        [
            new() { Source = new ScenarioReference { Kind = ScenarioSourceKind.Manual, Owner = "library", ScenarioId = "c1" }, Review = TestCaseReviewState.Accepted, Generated = new() { Title = "Borrow" } },
            new() { Source = new ScenarioReference { Kind = ScenarioSourceKind.Manual, Owner = "library", ScenarioId = "c2" }, Generated = new() { Title = "Return" } },
        ],
    };

    private static AdoExportPreview Preview(bool canDownload, AdoExportRequest r) => new()
    {
        ProjectKey = "library", DraftId = PlanId, PlanName = "Library plan", PlanRevision = 4, TotalCases = 2, SelectedCases = 1, ReadyCases = canDownload ? 1 : 0,
        CasesNeedingAttention = canDownload ? 0 : 1, CanDownload = canDownload, Headers = [.. AdoExport.Headers],
        Cases =
        [
            new() { CaseId = "c1", CaseKey = $"{PlanId:N}/c1", Title = "Borrow", Review = "Approved", Included = true, Steps = 2,
                Issues = canDownload ? [] : [new(AdoIssueLevel.Blocking, "1 step needs an expected result.", "c1")] },
            new() { CaseId = "c2", CaseKey = $"{PlanId:N}/c2", Title = "Return", Review = "Prepared", Included = false, ExcludedReason = "Not approved yet — choose \"Include cases that are not approved\" to export it", Steps = 1 },
        ],
        Rows = canDownload ? [new("", "Test Case", "Borrow", 1, "Open", "Shown", "Library\\Loans", "", "Design", null), new("", "Test Case", "Borrow", 2, "Lend", "Lent", "Library\\Loans", "", "Design", null)] : [],
        Fields =
        [
            new("Area Path", r.Profile.AreaPath, string.IsNullOrEmpty(r.Profile.AreaPath) ? AdoFieldCheck.Missing : AdoFieldCheck.NeedsConfirmationInAzureDevOps, "x"),
            new("State", "Design", AdoFieldCheck.NeedsConfirmationInAzureDevOps, "Proposed category"),
        ],
        Issues = canDownload ? [new(AdoIssueLevel.Info, "Good to know.")] : [new(AdoIssueLevel.Blocking, "1 step needs an expected result.", "c1")],
    };

    private IRenderedComponent<AdoExportWizard> Open(bool canDownload = true, bool canWrite = true, bool dirty = false, AdoExportProfile? profile = null)
    {
        _api.Setup(x => x.ProfileAsync("library", It.IsAny<CancellationToken>())).ReturnsAsync((profile ?? new AdoExportProfile { AreaPath = "Library\\Loans" }, (string?)null));
        _api.Setup(x => x.HistoryAsync("library", It.IsAny<CancellationToken>())).ReturnsAsync(([], (string?)null));
        _api.Setup(x => x.PreviewAsync(It.IsAny<AdoExportRequest>(), It.IsAny<CancellationToken>()))
            .Callback<AdoExportRequest, CancellationToken>((r, _) => _previews.Add(r))
            .ReturnsAsync((AdoExportRequest r, CancellationToken _) => (Preview(canDownload, r), (string?)null));
        _api.Setup(x => x.FileAsync(It.IsAny<AdoExportRequest>(), It.IsAny<CancellationToken>()))
            .Callback<AdoExportRequest, CancellationToken>((r, _) => _file = r)
            .ReturnsAsync((AdoExportRequest r, CancellationToken _) => (new AdoExportFile($"library-plan-azure-test-plans-r4.{(r.Profile.Format == AdoExportFormat.Xlsx ? "xlsx" : "csv")}", "x", "QUJD", 2, 1), (string?)null));
        _api.Setup(x => x.SaveProfileAsync("library", It.IsAny<AdoExportProfile>(), It.IsAny<CancellationToken>())).ReturnsAsync((string _, AdoExportProfile p, CancellationToken _) => (p, (string?)null));
        Services.AddSingleton(_api.Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
        var cut = Render<AdoExportWizard>(p => p.Add(x => x.Draft, Draft()).Add(x => x.ProjectKey, "library").Add(x => x.CanWrite, canWrite).Add(x => x.Dirty, dirty));
        cut.WaitForAssertion(() => cut.Find("[data-testid=adx-counts]"));
        return cut;
    }

    [Fact]
    public void Excel_is_the_default_every_case_says_whether_it_is_in_the_file_and_the_saved_revision_is_requested()
    {
        var cut = Open();

        cut.Find("[data-testid=adx-format-xlsx]").HasAttribute("checked").Should().BeTrue();
        _previews.First().Should().Match<AdoExportRequest>(r => r.ProjectKey == "library" && r.DraftId == PlanId && r.PlanRevision == 4 && r.Profile.Format == AdoExportFormat.Xlsx
            && r.Mode == AdoExportMode.CreateNew && r.CaseIds.Count == 0);
        cut.FindAll("[data-testid=adx-case]").Select(c => (c.GetAttribute("data-case"), c.GetAttribute("data-state"))).Should().Equal(("c1", "ready"), ("c2", "excluded"));
        cut.Find("[data-case=c2] [data-testid=adx-case-why]").TextContent.Should().StartWith("Not approved yet");
        cut.Find("[data-testid=adx-ready]").TextContent.Should().Be("1 ready");
        cut.Find("[data-field='Area Path']").TextContent.Should().Contain("Confirm in Azure DevOps", "an offline export never claims the path exists");
        cut.Find("[data-testid=adx-boundary]").TextContent.Should().Contain("Downloading is not importing");
        cut.Find("[data-testid=adx-howto]").TextContent.Should().Contain("Import test cases from CSV/XLSX");
    }

    [Fact]
    public void A_blocking_problem_disables_the_download_and_links_to_the_case()
    {
        var cut = Open(canDownload: false);

        cut.Find("[data-testid=adx-download-xlsx]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=adx-download-csv]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=adx-issues-blocking]").TextContent.Should().Contain("Borrow: 1 step needs an expected result.");
        cut.Find("[data-testid=adx-case-open]").GetAttribute("href").Should().Be($"test-plan-suggestions?draft={PlanId}&area=prepare&case=c1");
        cut.Find("[data-testid=adx-attention]").TextContent.Should().Be("1 need attention");
    }

    [Fact]
    public void Downloading_asks_the_server_for_the_file_in_the_chosen_format_and_hands_it_to_the_browser()
    {
        var cut = Open();
        var download = JSInterop.SetupVoid("downloadBase64File", _ => true); download.SetVoidResult();

        cut.Find("[data-testid=adx-download-csv]").Click();

        cut.WaitForAssertion(() => _file.Should().NotBeNull());
        _file!.Profile.Format.Should().Be(AdoExportFormat.Csv);
        cut.WaitForAssertion(() => cut.Find("[data-testid=adx-downloaded]").TextContent.Should().Contain("1 test case, 2 rows").And.Contain("Import it in Azure DevOps"));
        download.Invocations.Single().Arguments.Should().Equal("library-plan-azure-test-plans-r4.csv", "QUJD", "x");
    }

    [Fact]
    public void Destination_and_selection_changes_are_checked_again_and_choosing_an_unapproved_case_includes_it()
    {
        var cut = Open();
        cut.Find("[data-testid=adx-area]").Change("Library\\Returns");
        _previews.Last().Profile.AreaPath.Should().Be("Library\\Returns");

        cut.Find("[data-case=c2] [data-testid=adx-case-select]").Change(true);
        _previews.Last().Should().Match<AdoExportRequest>(r => r.IncludeNotApproved && r.CaseIds.OrderBy(x => x).SequenceEqual(new[] { "c1", "c2" }));
        cut.Find("[data-case=c1] [data-testid=adx-case-select]").Change(false);
        _previews.Last().CaseIds.Should().Equal("c2");
        cut.Find("[data-testid=adx-select-none]").Click();
        _previews.Last().CaseIds.Should().ContainSingle().Which.Should().NotBe("c1").And.NotBe("c2");
    }

    [Fact]
    public void Update_mode_asks_for_azure_devops_ids_and_an_explicit_confirmation()
    {
        var cut = Open();
        cut.FindAll("[data-testid=adx-update]").Should().BeEmpty("creating new test cases is the default");

        cut.Find("[data-testid=adx-mode-update]").Change(true);
        cut.Find("[data-testid=adx-update]").TextContent.Should().Contain("replaces ALL steps").And.Contain("never guesses");
        cut.Find("[data-testid=adx-existing-id]").Change("4711");
        cut.Find("[data-testid=adx-confirm-ids]").Change(true);
        _previews.Last().Should().Match<AdoExportRequest>(r => r.Mode == AdoExportMode.UpdateExisting && r.ExistingIdsConfirmed && r.ExistingIds["c1"] == "4711");
    }

    [Fact]
    public void Settings_are_saved_for_the_project_with_write_permission()
    {
        var cut = Open(canWrite: true);
        cut.Find("[data-testid=adx-state]").Change("Ready");
        cut.Find("[data-testid=adx-save-profile]").Click();
        _api.Verify(x => x.SaveProfileAsync("library", It.Is<AdoExportProfile>(p => p.State == "Ready" && p.AreaPath == "Library\\Loans"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Without_write_permission_settings_cannot_be_saved_and_unsaved_plan_edits_are_flagged()
    {
        var readOnly = Open(canWrite: false, dirty: true);
        readOnly.FindAll("[data-testid=adx-save-profile]").Should().BeEmpty();
        readOnly.Find("[data-testid=adx-dirty]").TextContent.Should().Contain("uses the saved plan");
    }

    [Fact]
    public void A_click_right_after_editing_a_field_is_not_lost_while_the_export_is_checked_again()
    {
        var cut = Open();
        var pending = new TaskCompletionSource<(AdoExportPreview?, string?)>();
        _api.Setup(x => x.PreviewAsync(It.IsAny<AdoExportRequest>(), It.IsAny<CancellationToken>())).Returns(pending.Task);

        // In a browser the field's change fires on the click that leaves it: the re-check starts in the same moment as the click.
        cut.Find("[data-testid=adx-area]").Change("Library\\Returns");
        cut.Find("[data-testid=adx-save-profile]").HasAttribute("disabled").Should().BeFalse("a re-check never disables Save, so the click is not lost");
        cut.Find("[data-testid=adx-download-xlsx]").HasAttribute("disabled").Should().BeFalse();
        cut.Find("[data-testid=adx-save-profile]").Click();
        _api.Verify(x => x.SaveProfileAsync(It.IsAny<string>(), It.IsAny<AdoExportProfile>(), It.IsAny<CancellationToken>()), Times.Never, "saving waits for the running re-check");

        pending.SetResult((Preview(true, new AdoExportRequest { Profile = new AdoExportProfile { AreaPath = "Library\\Returns" } }), null));
        cut.WaitForAssertion(() => _api.Verify(x => x.SaveProfileAsync("library", It.Is<AdoExportProfile>(p => p.AreaPath == "Library\\Returns"), It.IsAny<CancellationToken>()), Times.Once));
        cut.Find("[data-testid=adx-downloaded]").TextContent.Should().Contain("settings saved");
    }

    [Fact]
    public void A_failed_check_shows_the_error_with_a_retry_and_labels_are_accessible()
    {
        _api.Setup(x => x.ProfileAsync("library", It.IsAny<CancellationToken>())).ReturnsAsync((new AdoExportProfile(), (string?)null));
        _api.Setup(x => x.HistoryAsync("library", It.IsAny<CancellationToken>())).ReturnsAsync(([], (string?)null));
        _api.Setup(x => x.PreviewAsync(It.IsAny<AdoExportRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(((AdoExportPreview?)null, "The plan was saved again (revision 5)."));
        Services.AddSingleton(_api.Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
        var cut = Render<AdoExportWizard>(p => p.Add(x => x.Draft, Draft()).Add(x => x.ProjectKey, "library"));

        cut.WaitForAssertion(() => cut.Find("[data-testid=adx-error]").TextContent.Should().Contain("revision 5"));
        cut.Find("[data-testid=adx-retry]");
        cut.FindAll("fieldset.adx-step > legend").Select(l => l.TextContent.Trim()).Should().Equal("1 Select test cases", "2 Configure Azure DevOps", "3 Check", "4 Preview and download");
        cut.FindAll("[data-testid=adx-configure] input:not([type=radio]):not([type=checkbox])").Should().OnlyContain(i => i.Closest("label") != null, "every field has a visible label");
    }
}
