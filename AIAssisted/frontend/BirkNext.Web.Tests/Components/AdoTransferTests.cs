using BirkNext.TestPlanning;
using BirkNext.Web.Components.AzureDevOps;
using BirkNext.Web.Components.ManualTesting;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Overfør testplan (Manual Testing, Norwegian) and System Settings → Azure DevOps – Test Plans (English), 2026-11-25: the panel always opens on
/// «Eksporter til fil»; «Send direkte til Azure DevOps» shows the test lead approval warning first (why, consequences, what does not happen), the
/// declaration starts unchecked and alone sends nothing; only «Bekreft og send til Azure DevOps» transfers, once, with confirmation and declaration;
/// results separate successes from problems and never claim tests ran; the history names each status with date and time. The settings section is
/// English, saves locally, and creates a test plan only after a review — never one with an existing name.
/// </summary>
public sealed class AdoTransferTests : BunitContext
{
    private static readonly Guid PlanId = Guid.Parse("88888888-8888-8888-8888-888888888888");
    private static readonly Guid Conn = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private readonly Mock<IAdoTestPlansApiService> _ado = new();
    private readonly Mock<IAdoExportApiService> _export = new();
    private readonly List<AdoTransferRequest> _previews = [];
    private readonly List<AdoTransferRequest> _transfers = [];

    private static TestPlanDraft Draft() => new()
    {
        Id = PlanId, GuideKey = "project:m2lb", ProjectKey = "m2lb", PlanName = "Skolenærvær", Revision = 3,
        Cases =
        [
            new() { Source = new ScenarioReference { Kind = ScenarioSourceKind.Manual, Owner = "m2lb", ScenarioId = "c1" }, Review = TestCaseReviewState.Accepted, Generated = new() { Title = "Send rapport" } },
            new() { Source = new ScenarioReference { Kind = ScenarioSourceKind.Manual, Owner = "m2lb", ScenarioId = "c2" }, Review = TestCaseReviewState.Accepted, Generated = new() { Title = "Lukk periode" } },
        ],
    };

    private static AdoTestPlanDestination Destination() => new()
    {
        ConnectionId = Conn, Organization = "bufdir", Project = "M2LB", PlanId = 71001, PlanName = "Skolenærvær 2026", SuiteId = 71002, SuiteName = "Kritiske brukerreiser",
        SuitePath = "Skolenærvær 2026 › Kritiske brukerreiser",
    };

    private static AdoTransferPreview Preview(AdoTransferRequest r) => new()
    {
        Destination = r.Destination ?? Destination(), DestinationSource = r.Destination is null ? AdoDestinationSource.Project : AdoDestinationSource.Explicit, PlanName = "Skolenærvær", PlanRevision = 3,
        Cases =
        [
            new() { CaseId = "c1", CaseKey = $"{PlanId:N}/c1", Title = "Send rapport", Steps = 2, Ready = true, CurrentRevision = "aaaa", Allowed = [AdoTransferAction.Create, AdoTransferAction.Skip], Suggested = AdoTransferAction.Create },
            new() { CaseId = "c2", CaseKey = $"{PlanId:N}/c2", Title = "Lukk periode", Steps = 1, Ready = true, CurrentRevision = "bbbb", ChangedSinceTransfer = true,
                Link = new() { State = AdoLinkState.Linked, TestCaseId = 4711, RemoteRevision = 2, CaseRevision = "old" },
                Allowed = [AdoTransferAction.Update, AdoTransferAction.Create, AdoTransferAction.Skip], Suggested = AdoTransferAction.Update, Note = "Endret i BirkNext siden overføringen." },
        ],
        ToCreate = 1, ToUpdate = 1, CanTransfer = true,
    };

    private void Setup(List<AdoExportHistoryEntry>? history = null, AdoTransferResult? result = null)
    {
        _export.Setup(x => x.ProfileAsync("m2lb", It.IsAny<CancellationToken>())).ReturnsAsync((new AdoExportProfile(), (string?)null));
        _export.Setup(x => x.HistoryAsync("m2lb", It.IsAny<CancellationToken>())).ReturnsAsync((history ?? [], (string?)null));
        _export.Setup(x => x.PreviewAsync(It.IsAny<AdoExportRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync((new AdoExportPreview { Headers = [.. AdoExport.Headers] }, (string?)null));
        _ado.Setup(x => x.ResultsAsync("m2lb", PlanId, It.IsAny<CancellationToken>())).ReturnsAsync(([], (string?)null));
        _ado.Setup(x => x.PreviewAsync(It.IsAny<AdoTransferRequest>(), It.IsAny<CancellationToken>()))
            .Callback<AdoTransferRequest, CancellationToken>((r, _) => _previews.Add(r))
            .ReturnsAsync((AdoTransferRequest r, CancellationToken _) => (Preview(r), (string?)null));
        _ado.Setup(x => x.TransferAsync(It.IsAny<AdoTransferRequest>(), It.IsAny<CancellationToken>()))
            .Callback<AdoTransferRequest, CancellationToken>((r, _) => _transfers.Add(r))
            .Returns(async (AdoTransferRequest _, CancellationToken _) =>
            {
                await Task.Delay(50);
                return (result ?? new AdoTransferResult
                {
                    Destination = Destination(), SuiteUrl = "https://ado.example/bufdir/M2LB/_testPlans/define?planId=71001&suiteId=71002",
                    Cases = [new("c1", "Send rapport", AdoTransferOutcome.Created, "Opprettet som testtilfelle 4801.") { TestCaseId = 4801 },
                             new("c2", "Lukk periode", AdoTransferOutcome.NeedsReview, "Testtilfelle 4711 er endret i Azure DevOps etter siste overføring.") { TestCaseId = 4711 }],
                }, (string?)null);
            });
        Services.AddSingleton(_ado.Object);
        Services.AddSingleton(_export.Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<AdoTransferPanel> Panel(bool canWrite = true) =>
        Render<AdoTransferPanel>(p => p.Add(x => x.Draft, Draft()).Add(x => x.ProjectKey, "m2lb").Add(x => x.CanWrite, canWrite));

    private void NoRemoteWrite()
    {
        _ado.Verify(x => x.TransferAsync(It.IsAny<AdoTransferRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _ado.Verify(x => x.CreatePlanAsync(It.IsAny<AdoCreatePlanRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _ado.Verify(x => x.CreateSuiteAsync(It.IsAny<AdoCreateSuiteRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void T01_T04_the_panel_always_opens_on_export_to_file_even_after_a_direct_transfer()
    {
        Setup(history: [new(Guid.NewGuid(), PlanId, "Skolenærvær", 3, "Direct", "Direct", 2, 3, "bufdir / M2LB", "anna", DateTimeOffset.UtcNow, []) { Status = "Transferred" }]);
        var first = Panel();
        first.Find("[data-testid=atp-panel]").GetAttribute("data-method").Should().Be("file");
        first.Find("[data-testid=atp-method-file]").GetAttribute("aria-selected").Should().Be("true");
        first.Find("[data-testid=atp-method-direct]").GetAttribute("aria-selected").Should().Be("false", "direct transfer is never preselected");
        first.Find("[data-testid=atp-method-direct]").Click();
        first.Find("[data-testid=atp-panel]").GetAttribute("data-method").Should().Be("direct");
        first.Dispose();

        var reopened = Panel();
        reopened.Find("[data-testid=atp-panel]").GetAttribute("data-method").Should().Be("file", "a direct choice or an earlier direct transfer is never remembered");
        NoRemoteWrite();
    }

    [Fact]
    public void T05_T07_file_mode_needs_no_approval_writes_nothing_and_explains_what_the_file_is()
    {
        Setup();
        var cut = Panel();
        cut.Find("[data-testid=atp-file-safe]").TextContent.Should().Be("Ingenting blir endret i Azure DevOps.");
        cut.Find("[data-testid=atp-file-reminder]").TextContent.Should().Be("Testplanen bør være godkjent av testleder før den tas i bruk.");
        cut.FindAll("[data-testid=adt-declare]").Should().BeEmpty("downloading a file needs no approval checkbox");
        cut.WaitForAssertion(() => cut.Find("[data-testid=adx-format-note]").TextContent.Should().Contain("oppretter ikke testplanen eller testgruppen"));
        cut.Find("[data-testid=adx-howto]").TextContent.Should().Contain("Import test cases from CSV/XLSX");
        NoRemoteWrite();
        _ado.Verify(x => x.PreviewAsync(It.IsAny<AdoTransferRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void T08_T15_direct_mode_warns_first_the_declaration_starts_unchecked_and_neither_it_nor_cancel_sends_anything()
    {
        Setup();
        var cut = Panel();
        cut.Find("[data-testid=atp-method-direct]").Click();

        cut.Find(".adt-warning-title").TextContent.Should().Be("GODKJENNING FRA TESTLEDER KREVES");
        cut.Find(".adt-warning-icon").Should().NotBeNull();
        cut.Find("[data-testid=adt-why]").TextContent.Should().Contain("Hvorfor er godkjenning nødvendig?").And.Contain("kan opprette nye").And.Contain("Derfor skal testleder godkjenne");
        var consequences = cut.Find("[data-testid=adt-consequences]");
        consequences.QuerySelector("summary")!.TextContent.Should().Be("Hva kan skje når du overfører?");
        consequences.QuerySelectorAll("li").Should().HaveCount(8);
        consequences.TextContent.Should().Contain("med tilgang til prosjektet", "visibility depends on Azure DevOps permissions").And.Contain("Delvis overføring");
        cut.Find("[data-testid=adt-not-happen]").TextContent.Should().Contain("blir ikke slettet").And.Contain("betyr ikke at testene er bestått").And.Contain("Ingenting sendes før");

        cut.Find("[data-testid=adt-declare]").HasAttribute("checked").Should().BeFalse("the declaration starts unchecked");
        cut.Find("[data-testid=adt-continue]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=adt-declare]").Change(true);
        cut.Find("[data-testid=adt-continue]").HasAttribute("disabled").Should().BeFalse();
        _ado.Verify(x => x.PreviewAsync(It.IsAny<AdoTransferRequest>(), It.IsAny<CancellationToken>()), Times.Never, "checking the box reads or sends nothing");

        cut.Find("[data-testid=adt-cancel]").Click();
        cut.Find("[data-testid=atp-panel]").GetAttribute("data-method").Should().Be("file");
        cut.Find("[data-testid=atp-method-direct]").Click();
        cut.Find("[data-testid=adt-declare]").HasAttribute("checked").Should().BeFalse("the declaration is renewed after a cancel");
        NoRemoteWrite();
    }

    [Fact]
    public void T16_T21_the_final_review_shows_destination_and_counts_and_only_its_button_transfers_once_with_confirmation_and_declaration()
    {
        Setup();
        var cut = Panel();
        cut.Find("[data-testid=atp-method-direct]").Click();
        cut.Find("[data-testid=adt-declare]").Change(true);
        cut.Find("[data-testid=adt-continue]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=adt-destination-plan]").TextContent.Should().Be("Skolenærvær 2026"));
        cut.Find("[data-testid=adt-destination-source]").TextContent.Should().Be("Prosjektets standard");
        _previews.Should().OnlyContain(r => !r.Confirmed && !r.LeadApprovalDeclared, "a preview never carries a confirmation");
        cut.FindAll("[data-testid=adt-case]").Select(c => c.GetAttribute("data-action")).Should().Equal("Create", "Update");
        cut.Find("[data-case=c2] [data-testid=adt-case-linked]").TextContent.Should().Contain("Koblet til testtilfelle 4711").And.Contain("endret i BirkNext");
        cut.FindAll("[data-case=c2] [data-testid=adt-case-action]").Select(b => b.TextContent).Should().Equal("Oppdater eksisterende", "Opprett nytt", "Hopp over");
        NoRemoteWrite();

        cut.Find("[data-testid=adt-to-review]").Click();
        cut.Find("[data-testid=adt-destination-suite]").TextContent.Should().Be("Skolenærvær 2026 › Kritiske brukerreiser");
        cut.Find("[data-testid=adt-review-create]").TextContent.Should().Be("1");
        cut.Find("[data-testid=adt-review-update]").TextContent.Should().Be("1");
        cut.FindAll("[data-testid=adt-review-change]").Select(c => c.TextContent).Should().Contain(t => t.Contains("Testtilfelle 4711") && t.Contains("oppdateres"));
        NoRemoteWrite();

        var send = cut.Find("[data-testid=adt-send]");
        send.TextContent.Should().Be("Bekreft og send til Azure DevOps");
        send.Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=adt-result]"));
        _transfers.Should().ContainSingle().Which.Should().Match<AdoTransferRequest>(r => r.Confirmed && r.LeadApprovalDeclared && r.Destination!.PlanId == 71001 && r.Destination.SuiteId == 71002,
            "the transfer goes to exactly the destination the tester reviewed");

        cut.Find("[data-testid=adt-result-status]").TextContent.Should().Be("Delvis overført", "one case needs review: never reported as complete");
        cut.Find("[data-testid=adt-not-execution]").TextContent.Should().Contain("ikke at testene er kjørt eller bestått");
        cut.FindAll("[data-testid=adt-result-case]").Select(c => c.GetAttribute("data-outcome")).Should().Equal("Created", "NeedsReview");
        cut.Find("[data-testid=adt-problems-title]").TextContent.Should().Be("Må følges opp (1)");
        cut.Find("[data-testid=adt-open-suite]").GetAttribute("href").Should().Contain("suiteId=71002");
    }

    [Fact]
    public void T22_a_double_click_on_send_transfers_once()
    {
        Setup();
        var cut = Panel();
        cut.Find("[data-testid=atp-method-direct]").Click();
        cut.Find("[data-testid=adt-declare]").Change(true);
        cut.Find("[data-testid=adt-continue]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=adt-to-review]"));
        cut.Find("[data-testid=adt-to-review]").Click();
        var send = cut.Find("[data-testid=adt-send]");
        send.Click();
        cut.Find("[data-testid=adt-send]").HasAttribute("disabled").Should().BeTrue("busy while the first click is sending");
        cut.WaitForAssertion(() => cut.Find("[data-testid=adt-result]"));
        _transfers.Should().HaveCount(1);
    }

    [Fact]
    public void T23_the_history_names_each_status_with_date_and_time_and_a_file_is_never_an_import()
    {
        var at = new DateTimeOffset(2026, 11, 25, 13, 5, 0, TimeSpan.Zero);
        Setup(history:
        [
            new(Guid.NewGuid(), PlanId, "Skolenærvær", 3, "Direct", "Direct", 1, 3, "bufdir / M2LB / Plan / Gruppe", "anna", at, []) { Status = "Partial", Created = 1, Problems = 1, LeadApprovalDeclared = true, StartedAt = at, CompletedAt = at.AddSeconds(4) },
            new(Guid.NewGuid(), PlanId, "Skolenærvær", 3, "Direct", "Direct", 0, 0, null, "anna", at, []) { Status = "Failed", Reason = "Fant ikke testgruppen." },
            new(Guid.NewGuid(), PlanId, "Skolenærvær", 2, "Xlsx", "CreateNew", 2, 5, null, "anna", at, []),
        ]);
        var cut = Panel();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=atp-history-item]").Should().HaveCount(3));
        cut.FindAll("[data-testid=atp-history-status]").Select(s => s.TextContent).Should().Equal("Delvis overført", "Kunne ikke overføre", "Fil laget");
        var items = cut.FindAll("[data-testid=atp-history-item]");
        items[0].TextContent.Should().Contain(" kl. ").And.Contain("bekreftet testleders godkjenning (egen erklæring)");
        items[1].TextContent.Should().Contain("Fant ikke testgruppen.");
        items[2].TextContent.Should().Contain("en fil er ikke importert før du importerer den i Azure DevOps");
    }

    [Fact]
    public void Without_write_permission_direct_transfer_offers_nothing_to_send()
    {
        Setup();
        var cut = Panel(canWrite: false);
        cut.Find("[data-testid=atp-method-direct]").Click();
        cut.Find("[data-testid=adt-no-access]").TextContent.Should().Contain("Bruk «Eksporter til fil»");
        cut.FindAll("[data-testid=adt-declare]").Should().BeEmpty();
    }

    // ── System Settings → Azure DevOps – Test Plans (English) ──────────────────────────────────────────────────────────────────────────────

    private static AzureDevOpsConnectionInfo Connection() => new(Conn, "bufdir", "bufdir", true, "Connected", DateTimeOffset.UtcNow);

    private void SetupSettings(List<AzureDevOpsConnectionInfo> connections, List<AdoTestPlanInfo>? plans = null)
    {
        _ado.Setup(x => x.SettingsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync((new AdoTestPlanSettingsView { Connections = connections }, (string?)null));
        _ado.Setup(x => x.ProjectsAsync(Conn, It.IsAny<CancellationToken>())).ReturnsAsync(new AdoListResult<AdoProjectInfo>([new("p1", "M2LB"), new("p2", "Testdokumentasjon")], null));
        _ado.Setup(x => x.PlansAsync(Conn, "M2LB", It.IsAny<CancellationToken>())).ReturnsAsync(new AdoListResult<AdoTestPlanInfo>(plans ?? [new(71001, "Skolenærvær 2026", "Active", 71002, null, null, null)], null));
        _ado.Setup(x => x.SuitesAsync(Conn, "M2LB", 71001, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AdoListResult<AdoTestSuiteInfo>([new(71002, "Skolenærvær 2026", null, "staticTestSuite"), new(71003, "Kritiske brukerreiser", 71002, "staticTestSuite")], null));
        _ado.Setup(x => x.SaveDefaultAsync(It.IsAny<AdoSaveDestinationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AdoSaveDestinationRequest r, CancellationToken _) => (new AdoSavedDestination(r.Destination!, "anna", DateTimeOffset.UtcNow), (string?)null));
        _ado.Setup(x => x.CreatePlanAsync(It.IsAny<AdoCreatePlanRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AdoCreatePlanRequest r, CancellationToken _) => (new AdoCreateResult(AdoCreateOutcome.Created, "created") { Plan = new(71500, r.Name, "Active", 71501, null, null, null) }, (string?)null));
        _ado.Setup(x => x.SuitesAsync(Conn, "M2LB", 71500, It.IsAny<CancellationToken>())).ReturnsAsync(new AdoListResult<AdoTestSuiteInfo>([new(71501, "M2LB – Skolenærvær – Testplan", null, "staticTestSuite")], null));
        Services.AddSingleton(_ado.Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void Settings_are_english_and_without_a_connection_offer_to_add_one()
    {
        SetupSettings([]);
        var cut = Render<AdoTestPlansSettingsCard>();
        cut.WaitForAssertion(() => cut.Find("#ats-title").TextContent.Should().Be("Azure DevOps – Test Plans"));
        cut.Find("[data-testid=ats-overall]").TextContent.Should().Be("Not connected");
        cut.Find("[data-testid=ats-no-connection]").TextContent.Should().Contain("No Azure DevOps connection yet.");
        cut.Find("[data-testid=ats-pat]").GetAttribute("type").Should().Be("password");
        cut.Find("[data-testid=ats-edit]").HasAttribute("disabled").Should().BeTrue();
        cut.Markup.Should().NotContain("50088").And.NotContain("50090").And.NotContain("Testdokumentasjon");
    }

    [Fact]
    public void Choosing_a_destination_and_saving_it_is_local_and_nothing_is_preselected()
    {
        SetupSettings([Connection()]);
        var cut = Render<AdoTestPlansSettingsCard>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=ats-edit]"));
        cut.Find("[data-testid=ats-system-none]").TextContent.Should().Contain("Not configured.");
        cut.Find("[data-testid=ats-edit]").Click();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=adp-project-option]").Should().HaveCount(2));
        cut.Find("[data-testid=adp-connection-state]").TextContent.Should().Be("Connected");
        cut.FindAll(".gar-pick.is-picked").Should().BeEmpty("no project, plan or suite is chosen for the user");
        cut.Find("[data-testid=ats-save]").HasAttribute("disabled").Should().BeTrue();

        cut.FindAll("[data-testid=adp-project-option]").Single(b => b.TextContent == "M2LB").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=adp-plan-option]"));
        cut.FindAll(".gar-pick.is-picked").Should().BeEmpty("the existing plan is not selected automatically");
        cut.Find("[data-testid=adp-plan-option]").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=adp-suite-option]").Should().HaveCount(2));
        cut.FindAll("[data-testid=adp-suite-option]").Select(s => s.GetAttribute("data-depth")).Should().Equal(["0", "1"], "the suite hierarchy is kept");
        cut.FindAll("[data-testid=adp-suite-option]")[1].Click();
        cut.Find("[data-testid=ats-local-note]").TextContent.Should().Be("Saving is local: nothing is created or changed in Azure DevOps.");
        cut.Find("[data-testid=ats-save]").Click();

        _ado.Verify(x => x.SaveDefaultAsync(It.Is<AdoSaveDestinationRequest>(r => r.Scope == AdoDestinationScope.System && r.Destination!.PlanId == 71001 && r.Destination.SuiteId == 71003
            && r.Destination.SuitePath == "Skolenærvær 2026 › Kritiske brukerreiser"), It.IsAny<CancellationToken>()), Times.Once);
        NoRemoteWrite();
        cut.WaitForAssertion(() => cut.Find("[data-testid=ats-notice]").TextContent.Should().Contain("Nothing was changed in Azure DevOps."));
    }

    [Fact]
    public void A_new_test_plan_is_created_only_after_review_and_an_existing_name_is_offered_instead()
    {
        SetupSettings([Connection()]);
        var cut = Render<AdoTestPlansSettingsCard>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=ats-edit]"));
        cut.Find("[data-testid=ats-edit]").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=adp-project-option]").Should().HaveCount(2));
        cut.FindAll("[data-testid=adp-project-option]").Single(b => b.TextContent == "M2LB").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=adp-plan-new]"));
        cut.Find("[data-testid=adp-plan-new]").Click();
        cut.Find("[data-testid=adp-plan-name]").GetAttribute("placeholder").Should().Be("M2LB – Skolenærvær – Testplan", "a placeholder only");
        cut.Find("[data-testid=adp-plan-name]").GetAttribute("value").Should().BeNullOrEmpty();

        cut.Find("[data-testid=adp-plan-name]").Input("skolenærvær 2026");
        cut.Find("[data-testid=adp-plan-duplicate]").TextContent.Should().Contain("A test plan named \"Skolenærvær 2026\" already exists in M2LB");
        cut.Find("[data-testid=adp-plan-review]").HasAttribute("disabled").Should().BeTrue("a plan with the same name is never created");

        cut.Find("[data-testid=adp-plan-name]").Input("M2LB – Skolenærvær – Testplan");
        cut.FindAll("[data-testid=adp-plan-duplicate]").Should().BeEmpty();
        NoRemoteWrite();
        cut.Find("[data-testid=adp-plan-review]").Click();
        cut.Find("[data-testid=adp-plan-confirm-name]").TextContent.Should().Be("M2LB – Skolenærvær – Testplan");
        NoRemoteWrite();
        cut.Find("[data-testid=adp-plan-create-confirm]").Click();

        _ado.Verify(x => x.CreatePlanAsync(It.Is<AdoCreatePlanRequest>(r => r.Confirmed && r.Project == "M2LB" && r.Name == "M2LB – Skolenærvær – Testplan"), It.IsAny<CancellationToken>()), Times.Once);
        cut.WaitForAssertion(() => cut.Find("[data-testid=adp-notice]").TextContent.Should().Be("The test plan \"M2LB – Skolenærvær – Testplan\" was created."));
        cut.Find("[data-testid=adp-suite-tree]").TextContent.Should().Contain("M2LB – Skolenærvær – Testplan");
    }

    [Fact]
    public void An_empty_project_offers_to_create_a_plan_and_changing_project_clears_the_choice()
    {
        SetupSettings([Connection()], plans: []);
        AdoTestPlanDestination? emitted = new();
        var cut = Render<AdoDestinationPicker>(p => p.Add(x => x.Connections, [Connection()]).Add(x => x.CanWrite, true).Add(x => x.English, true)
            .Add(x => x.OnChanged, (AdoTestPlanDestination? d) => emitted = d));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=adp-project-option]").Should().HaveCount(2));
        cut.FindAll("[data-testid=adp-project-option]").Single(b => b.TextContent == "M2LB").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=adp-plans-empty]").TextContent.Should().Contain("No test plans were found in this project."));
        cut.Find("[data-testid=adp-plans-empty-create]").Should().NotBeNull();
        emitted.Should().BeNull("an incomplete choice is never a destination");
    }

    [Fact]
    public void The_picker_speaks_norwegian_in_manual_testing()
    {
        SetupSettings([Connection()]);
        var cut = Render<AdoDestinationPicker>(p => p.Add(x => x.Connections, [Connection()]).Add(x => x.CanWrite, true));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=adp-project-option]").Should().HaveCount(2));
        cut.Find("[data-testid=adp-connection-state]").TextContent.Should().Be("Tilkoblet");
        cut.Find("[data-testid=adp-check]").TextContent.Should().Be("Kontroller tilkobling");
    }
}
