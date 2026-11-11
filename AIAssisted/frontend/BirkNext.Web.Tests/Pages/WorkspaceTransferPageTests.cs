using System.Text.Json;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using BirkNext.WorkspaceTransfer;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using TransferPage = BirkNext.Web.Pages.WorkspaceTransfer;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Backup &amp; Transfer page: areas and deep links, export review (closure, blockers, acknowledgement), import preview (environment mapping never
/// chosen by name, decisions only where several actions are allowed), the verification report, the disabled draft profile, and the backup form.
/// The page never decides anything the backend decides; these tests check that it shows and sends exactly that.
/// </summary>
public sealed class WorkspaceTransferPageTests : BunitContext
{
    private readonly Mock<IWorkspaceTransferApiService> _api = new();
    private readonly Mock<IFrontendAnalysisSettingsService> _settings = new();
    private readonly FrontendAnalysisSettings _store = new() { Profiles = [new FrontendAnalysisProfile { Id = "qa-1", Name = "QA one", EnvironmentType = FrontendEnvironmentType.Test }] };
    private ExportRequest? _lastExport;
    private ImportApplyRequest? _lastApply;

    public WorkspaceTransferPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_api.Object);
        Services.AddSingleton(_settings.Object);
        _settings.SetupGet(s => s.IsLoaded).Returns(true);
        _settings.SetupGet(s => s.Settings).Returns(_store);
        _api.Setup(a => a.OperationsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(([new TransferOperationSummary(Guid.NewGuid(), "Export", "CREATED", "ProjectWorkspace", "Project m2lb: 9 objects, COMPLETE", "local-session:loopback",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "abc", null)], null));
        _api.Setup(a => a.ScopesAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync((new TransferScopesView(
            [new("m2lb", "M2LB", 2, 1, 1, 1, DateTimeOffset.UtcNow), new("mu", "MU", 1, 0, 0, 0, DateTimeOffset.UtcNow)],
            [new("qa-1", 2, 5, 2, 1, 1), new("orphan-env", 1, 0, 0, 0, 0)]), null));
        _api.Setup(a => a.PreviewExportAsync(It.IsAny<ExportRequest>(), It.IsAny<CancellationToken>())).Callback((ExportRequest r, CancellationToken _) => _lastExport = r)
            .ReturnsAsync((new ExportPreview
            {
                Kind = TransferKind.IntegrationWorkspace, Scope = "Integration environment qa-1", Completeness = "PARTIAL",
                Counts = new Dictionary<string, int> { ["source-snapshot"] = 3, ["contract"] = 1 },
                Dependencies = [new(new("contract", "k"), new("source-snapshot", "x"), DependencyStatus.Missing, true, "The source snapshot no longer exists in this installation.")],
                Security = [new("SENSITIVE", "integration-suite:s.definitionJson[0].person", "An 11-digit identifier-shaped value (possible national identity number).")],
                Blockers = ["1 identifier-shaped value(s) found; confirm they are synthetic to continue."],
                Stages = [new("MU → MM", "mu", 0, 0, 0, "Source included"), new("Altinn → MU", null, 0, 0, 0, "No source mapped")],
                Excluded = ["Secrets, tokens, cookies"],
            }, null));
        _api.Setup(a => a.StageAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>())).ReturnsAsync((new StagedPackageView(Guid.NewGuid(), "sha", 10), null));
        _api.Setup(a => a.PreviewImportAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<EnvironmentCandidate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, IReadOnlyList<EnvironmentCandidate> c, CancellationToken _) => (new ImportPreview
            {
                OperationId = id, PlanFingerprint = "plan-1", CanApply = true, Verdict = "READY_FOR_REVIEW",
                Manifest = new PackageManifest { Label = "Integration qa-synth", ExportType = TransferKind.IntegrationWorkspace, IncludedProjects = ["m2lb", "mu"] },
                Checks = [new("Content hashes", TransferCheckStatus.Pass, "ok")],
                Environment = new ImportEnvironmentMapping { ImportedEnvironmentId = "qa-synth", ImportedName = "QA one", Candidates = c, Note = "Names never decide." },
                Items =
                [
                    new ImportItem { Key = "contract|k", Type = "contract", LogicalId = "k", Label = "Rapportinntak", Status = ImportStatus.NewerRevision, AllowedActions = [ImportAction.ImportAsNewRevision, ImportAction.KeepExisting], DefaultAction = ImportAction.ImportAsNewRevision, Detail = "adds 1 version" },
                    new ImportItem { Key = "source-snapshot|a", Type = "source-snapshot", LogicalId = "a", Label = "M2LB.zip", Status = ImportStatus.Identical, AllowedActions = [ImportAction.ReuseIdentical], DefaultAction = ImportAction.ReuseIdentical },
                ],
            }, null));
        _api.Setup(a => a.ApplyImportAsync(It.IsAny<ImportApplyRequest>(), It.IsAny<CancellationToken>())).Callback((ImportApplyRequest r, CancellationToken _) => _lastApply = r)
            .ReturnsAsync((new ImportReport
            {
                State = "COMPLETED", DestinationEnvironmentId = "imported-1234abcd", HistoricalRecords = 2,
                Readiness = [new("SOURCE_READY", "Yes", "ok"), new("EXECUTOR_UNAVAILABLE", "Yes", "Missing: provider:X"), new("DOM_REVALIDATION_REQUIRED", "Required", "flows disabled")],
                Reconciliation = [new("Rapportinntak", ReconciliationStatus.NeedsRevalidation, "binding")],
                ProfileDraftJson = """{"id":"imported-1234abcd","name":"QA one","manualVerification":{"verified":true},"targetUrl":"https://app.example.invalid"}""",
            }, null));
        _api.Setup(a => a.BackupStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync((new BackupStatusView(true, null, "pg_dump (PostgreSQL) 16.13", "pg_restore 16.13", "ContainerExec", "x",
            ["critical-e2e/flows.json"], [new("App_Data/api-service-credentials", StorageClass.SecretStore, "DPAPI")], "1.0.0", "fe12e79ed50ee02ca076ad715be495ddf9944873", ReadyReadiness), null));
        _api.Setup(a => a.BackupsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(([new BackupSummary("b.bnbackup", Guid.NewGuid(), DateTimeOffset.UtcNow, 4 << 20, "1.0.0", "16.13 (Debian)", new Dictionary<string, string>(), "")], null));
        _api.Setup(a => a.RestoresAsync(It.IsAny<CancellationToken>())).ReturnsAsync(([], null));
        _api.Setup(a => a.PackageDownloadUrl(It.IsAny<string>())).Returns((string f) => $"http://localhost/x/{f}");
        _api.Setup(a => a.BackupDownloadUrl(It.IsAny<string>())).Returns((string f) => $"http://localhost/b/{f}");
    }

    private static readonly BackupReadiness ReadyReadiness = new()
    {
        Backup = BackupReadinessStatus.Ready, Restore = BackupReadinessStatus.Ready, RuntimeProfile = "Developer (containers)", ToolProvider = "Container exec",
        SearchLocations = ["podman container birknext-studio-local_postgres_1"], DumpToolVersion = "pg_dump (PostgreSQL) 16.13", RestoreToolVersion = "pg_restore (PostgreSQL) 16.13",
        ServerVersion = "16.13 (Debian)", CheckedAt = DateTimeOffset.UtcNow,
        Items = [new("database", "Database", BackupReadinessStatus.Ready, "Connected · PostgreSQL 16.13"), new("pg_dump", "pg_dump", BackupReadinessStatus.Ready, "pg_dump (PostgreSQL) 16.13"),
            new("pg_restore", "pg_restore", BackupReadinessStatus.Ready, "pg_restore (PostgreSQL) 16.13"), new("identity", "Container identity", BackupReadinessStatus.Ready, "same server"),
            new("compatibility", "Version compatibility", BackupReadinessStatus.Ready, "pg_dump 16 can dump PostgreSQL 16"), new("restore-destination", "Restore destination", BackupReadinessStatus.Ready, "may create")],
    };

    private IRenderedComponent<TransferPage> Open(string query)
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("workspace-transfer" + query);
        var page = Render<TransferPage>();
        page.WaitForAssertion(() => page.Find("[data-testid=wtx]").GetAttribute("aria-busy").Should().Be("false"));
        return page;
    }

    [Fact]
    public void The_overview_offers_the_five_operations_and_recent_activity()
    {
        var page = Open("");
        page.Find("h1").TextContent.Should().Be("Backup & Transfer");
        page.FindAll("[data-testid^=wtx-area-]").Should().HaveCount(4);
        page.FindAll("[data-testid=wtx-overview] a").Select(a => a.QuerySelector("strong")!.TextContent).Should().Equal(
            "Create full backup", "Export project", "Export integration", "Import package", "Restore backup");
        page.Find("[data-testid=wtx-action-export-integration]").GetAttribute("href").Should().Be("workspace-transfer?area=export&kind=integration");
        page.Find("[data-testid=wtx-history]").TextContent.Should().Contain("Project m2lb: 9 objects, COMPLETE");
    }

    [Fact]
    public void Export_review_shows_the_closure_stages_and_blockers_and_sends_the_profile_only_when_chosen()
    {
        var page = Open("?area=export&kind=integration");
        page.FindAll("[data-testid=wtx-env-choice]").Select(b => b.QuerySelector("strong")!.TextContent).Should().Equal(["QA one", "orphan-env"],
            "the profile name where this browser knows it, the id otherwise (never a guessed name)");
        page.FindAll("[data-testid=wtx-env-choice]")[0].Click();
        page.Find("[data-testid=wtx-preview-export]").Click();
        page.WaitForAssertion(() => page.Find("[data-testid=wtx-completeness]").TextContent.Should().Be("PARTIAL"));
        _lastExport!.Kind.Should().Be(TransferKind.IntegrationWorkspace);
        _lastExport.EnvironmentId.Should().Be("qa-1");
        JsonDocument.Parse(_lastExport.TargetProfileJson!).RootElement.GetProperty("id").GetString().Should().Be("qa-1");
        page.Find("[data-testid=wtx-stage-map]").TextContent.Should().Contain("MU → MM").And.Contain("No source mapped");
        page.Find("[data-testid=wtx-deps]").TextContent.Should().Contain("Missing");
        page.Find("[data-testid=wtx-blockers]").TextContent.Should().Contain("confirm they are synthetic");
        page.Find("[data-testid=wtx-security]").TextContent.Should().NotContain("0101", "findings name locations, never values");

        page.Find("[data-testid=wtx-include-profile]").Change(false);
        page.Find("[data-testid=wtx-ack]").Change(true);
        page.Find("[data-testid=wtx-preview-export]").Click();
        page.WaitForAssertion(() => _lastExport!.TargetProfileJson.Should().BeNull());
        _lastExport!.AcknowledgeSensitive.Should().BeFalse("a new review starts unacknowledged");
    }

    [Fact]
    public void Import_requires_an_explicit_environment_choice_and_sends_only_offered_candidates_and_decisions()
    {
        var page = Open("?area=import");
        page.FindComponent<Microsoft.AspNetCore.Components.Forms.InputFile>().UploadFiles(InputFileContent.CreateFromText("zip", "p.birknext"));
        page.WaitForAssertion(() => page.Find("[data-testid=wtx-import-preview]"));
        page.Find("[data-testid=wtx-env-leave]").HasAttribute("checked").Should().BeTrue("nothing environment-bound is mapped until a person chooses");
        page.FindAll("[data-testid=wtx-decision]").Should().HaveCount(2, "only the object with several allowed actions asks for a decision");

        page.Find("[data-testid=wtx-env-reuse]").Change(true);
        page.Find("[data-testid=wtx-apply-import]").HasAttribute("disabled").Should().BeTrue("reuse needs a chosen environment");
        page.Find("[data-testid=wtx-env-candidate]").Click();
        page.FindAll("[data-testid=wtx-decision]").Single(b => b.TextContent == "Keep existing").Click();
        page.Find("[data-testid=wtx-apply-import]").Click();
        page.WaitForAssertion(() => page.Find("[data-testid=wtx-import-report]"));
        _lastApply!.EnvironmentAction.Should().Be(EnvironmentMappingAction.ReuseVerified);
        _lastApply.TargetEnvironmentId.Should().Be("qa-1");
        _lastApply.PlanFingerprint.Should().Be("plan-1");
        _lastApply.Decisions.Should().Equal(new Dictionary<string, ImportAction> { ["contract|k"] = ImportAction.KeepExisting });

        var readiness = page.FindAll("[data-testid=wtx-readiness] li");
        readiness[0].QuerySelector(".wtx-pill")!.ClassList.Should().Contain("wtx-ok");
        readiness[1].QuerySelector(".wtx-pill")!.ClassList.Should().Contain("wtx-warn", "a missing executor is never shown green");
    }

    [Fact]
    public void The_imported_profile_is_created_as_a_new_unverified_draft()
    {
        _settings.Setup(s => s.SaveAsync(It.IsAny<Microsoft.JSInterop.IJSRuntime>())).Returns(Task.CompletedTask);
        var page = Open("?area=import");
        page.FindComponent<Microsoft.AspNetCore.Components.Forms.InputFile>().UploadFiles(InputFileContent.CreateFromText("zip", "p.birknext"));
        page.WaitForAssertion(() => page.Find("[data-testid=wtx-env-create]"));
        page.Find("[data-testid=wtx-env-create]").Change(true);
        page.Find("[data-testid=wtx-apply-import]").Click();
        page.WaitForAssertion(() => page.Find("[data-testid=wtx-create-profile]"));
        page.Find("[data-testid=wtx-create-profile]").Click();
        var created = _store.Profiles.Single(p => p.Id == "imported-1234abcd");
        created.Name.Should().Be("QA one (imported – revalidate)");
        created.ManualVerification.Should().BeNull("verification is never carried over");
        _store.Profiles.Should().HaveCount(2, "the existing profile is never merged or replaced");
        _settings.Verify(s => s.SaveAsync(It.IsAny<Microsoft.JSInterop.IJSRuntime>()), Times.Once);
    }

    [Fact]
    public void The_backup_area_leads_with_readiness_states_exclusions_and_requires_matching_passphrases()
    {
        var page = Open("?area=backup");
        page.Find("[data-testid=wtx-backup-ready]").TextContent.Should().Be("Backup: Ready");
        page.Find("[data-testid=wtx-ready-tools]").TextContent.Should().Contain("Ready").And.Contain("pg_dump 16.13").And.Contain("Container exec");
        page.Find("[data-testid=wtx-backup-status]").TextContent.Should().Contain("1 found");
        page.Find("[data-testid=wtx-readiness-details]").TextContent.Should().Contain("fe12e79ed50e").And.Contain("Container identity");
        page.Find("[data-testid=wtx-exclusions]").TextContent.Should().Contain("api-service-credentials");
        page.Find("[data-testid=wtx-backup]").TextContent.Should().Contain("nobody can open the backup");
        page.FindAll("[data-testid=wtx-backup-blocker]").Should().BeEmpty("nothing blocks a ready backup");
        page.Find("[data-testid=wtx-create-reason]").TextContent.Should().Contain("at least 12 characters", "the action explains why it is disabled");
        page.Find("[data-testid=wtx-passphrase]").Input("synthetic passphrase 1");
        page.Find("[data-testid=wtx-passphrase2]").Input("synthetic passphrase 2");
        page.Find("[data-testid=wtx-pass-state]").TextContent.Should().Contain("different");
        page.Find("[data-testid=wtx-create-backup]").HasAttribute("disabled").Should().BeTrue();
        page.Find("[data-testid=wtx-backup-form]").Submit();
        _api.Verify(a => a.CreateBackupAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        page.Find("[data-testid=wtx-restore-assurance]").TextContent.Should().Contain("The current BirkNext installation will not be changed");
    }

    [Fact]
    public void Missing_tools_explain_where_BirkNext_looked_and_offer_retry_never_a_silent_disabled_button()
    {
        var missing = ReadyReadiness with
        {
            Backup = BackupReadinessStatus.Unavailable, Restore = BackupReadinessStatus.Unavailable, ToolProvider = "PATH", SearchLocations = ["PATH"], DumpToolVersion = null, RestoreToolVersion = null,
            BackupBlocker = "pg_dump could not be started (PATH: PATH).",
            Items = [new("database", "Database", BackupReadinessStatus.Ready, "Connected"), new("pg_dump", "pg_dump", BackupReadinessStatus.Unavailable, "pg_dump could not be started (PATH: PATH)."),
                new("pg_restore", "pg_restore", BackupReadinessStatus.Unavailable, "x"), new("compatibility", "Version compatibility", BackupReadinessStatus.NotChecked, "Not checked")],
        };
        _api.Setup(a => a.BackupStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync((new BackupStatusView(true, null, null, null, "Host", "x", [], [], "1.0.0", "abc", missing), null));
        var page = Open("?area=backup");
        page.Find("[data-testid=wtx-backup-ready]").TextContent.Should().Be("Backup: Unavailable");
        page.Find("[data-testid=wtx-backup-blocker]").TextContent.Should().Contain("PostgreSQL backup tools could not be found").And.Contain("Looked in").And.Contain("PATH");
        page.Find("[data-testid=wtx-create-reason]").TextContent.Should().StartWith("Backups cannot run yet");
        page.Find("[data-testid=wtx-create-backup]").HasAttribute("disabled").Should().BeTrue();
        page.Find("[data-testid=wtx-restore-blocker]").TextContent.Should().Contain("cannot run here yet");
        page.Find("[data-testid=wtx-retry-detection]").Click();
        _api.Verify(a => a.BackupStatusAsync(It.IsAny<CancellationToken>()), Times.AtLeast(2), "Retry detection asks the backend again");
    }

}
