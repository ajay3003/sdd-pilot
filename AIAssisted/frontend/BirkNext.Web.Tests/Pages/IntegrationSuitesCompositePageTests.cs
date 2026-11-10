using BirkNext.ContractTesting;
using BirkNext.Integrations;
using BirkNext.IntegrationSuites;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// The multi-source workflow on /integration-suites: discovery per stage with one source per stage (ambiguous stages never preselected), human
/// approval per source, the stage strip, compatibility with acceptance-by-reason (the state stays incompatible), three readiness levels with
/// actionable blockers, plan approval, a preflight that names FULL_E2E vs SUBFLOW_TEST and data-changing steps, an authorised run and the run view
/// (verdict, stage timeline, correlation). The page shows what the backend returned; it never computes a verdict.
/// </summary>
public sealed class IntegrationSuitesCompositePageTests : BunitContext
{
    private static readonly Guid A = Guid.Parse("aaaaaaaa-3333-0000-0000-000000000001");
    private static readonly Guid B = Guid.Parse("aaaaaaaa-3333-0000-0000-000000000002");
    private static readonly Guid B2 = Guid.Parse("aaaaaaaa-3333-0000-0000-000000000003");
    private static readonly Guid Ctx = Guid.Parse("cccccccc-3333-0000-0000-000000000001");
    private static readonly Guid SuiteId = Guid.Parse("dddddddd-3333-0000-0000-000000000001");
    private static readonly Guid RunId = Guid.Parse("eeeeeeee-3333-0000-0000-000000000001");
    private readonly Mock<IIntegrationSuiteApiService> _api = new();
    private CompositeContextSaveRequest? _saved;
    private CompositeRunRequest? _ran;

    public IntegrationSuitesCompositePageTests()
    {
        _api.Setup(a => a.SnapshotsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(([], null));
        _api.Setup(a => a.ScopesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(([new IntegrationSuiteScope
        {
            ScopeId = "skolenaervaer", DisplayName = "Altinn → MU → MM → Skolenærvær → Bufdata",
            Stages = [new("mu", "MU reception", "MU", []) { Source = new() { Tokens = ["Meldingsutveksler"] } }, new("mm", "MM routing and validation", "MM", []) { Source = new() { Tokens = ["MeldingsutvekslerReceiver"] } }],
        }], null));
        _api.Setup(a => a.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(([], null));
        _api.Setup(a => a.ContextsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(([], null));
        _api.Setup(a => a.DiscoverAsync("skolenaervaer", It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync((new CompositeDiscovery
        {
            ScopeId = "skolenaervaer", SnapshotsConsidered = 9,
            Stages =
            [
                new() { StageId = "mu", Label = "MU reception", Mandatory = true, Class = CompositeMatchClass.Candidate, Candidates = [Candidate(A, "tiltak-2", "Tiltak 2.zip")] },
                new() { StageId = "mm", Label = "MM routing and validation", Mandatory = true, Class = CompositeMatchClass.Ambiguous, Candidates = [Candidate(B, "m2lb", "M2LB.zip"), Candidate(B2, "m2lb-language", "M2LB-language.zip")] },
            ],
            Supporting = [Candidate(Guid.NewGuid(), "terraform-iac-m2lb", "Terraform-iac-m2lb.zip") with { Kind = CompositeSourceKind.Infrastructure }],
        }, null));
        _api.Setup(a => a.SaveContextAsync(It.IsAny<CompositeContextSaveRequest>(), It.IsAny<CancellationToken>()))
            .Callback((CompositeContextSaveRequest r, CancellationToken _) => _saved = r)
            .ReturnsAsync((CompositeContextSaveRequest r, CancellationToken _) => (new CompositeContextSaveResult(Context(r), []), null));
        _api.Setup(a => a.GetAsync(SuiteId, It.IsAny<CancellationToken>())).ReturnsAsync((Suite("Draft"), null));
        _api.Setup(a => a.ContextAsync(Ctx, It.IsAny<CancellationToken>())).ReturnsAsync((Context(null), null));
        _api.Setup(a => a.ApprovePlanAsync(SuiteId, It.IsAny<CancellationToken>())).ReturnsAsync((new CompositePlan { State = "Approved" }, null));
        _api.Setup(a => a.PreflightAsync(SuiteId, It.IsAny<CompositeRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, CompositeRunRequest r, CancellationToken _) => (new CompositePreflight(true, r.Stages.Count == 0 ? CompositeRunKind.FullE2E : CompositeRunKind.SubflowTest,
                r.Stages.Count == 0 ? ["mu", "mm"] : r.Stages, ["mu: POST /mu/messages"], [], []), null));
        _api.Setup(a => a.RunCompositeAsync(SuiteId, It.IsAny<CompositeRunRequest>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, CompositeRunRequest r, CancellationToken _) => _ran = r)
            .ReturnsAsync((new CompositeRunResult(RunId, "Running", "Started.", new(true, CompositeRunKind.FullE2E, ["mu", "mm"], [], [], [])), null));
        _api.Setup(a => a.CompositeRunAsync(SuiteId, RunId, It.IsAny<CancellationToken>())).ReturnsAsync((new CompositeRunView
        {
            RunId = RunId, SuiteId = SuiteId, Kind = CompositeRunKind.FullE2E, Verdict = CompositeRunVerdict.WrongCorrelation, VerdictReason = "mu → mm: The response echoes messageId = msg-other, not msg-1.",
            Stages = [new("mu", "mu", CompositeStageState.Succeeded, [], 2, "Observer: GET → 200 after 2 reads."), new("mm", "mm", CompositeStageState.Succeeded, [], 1, "Observer: GET → 200.")],
            Correlation = [new("mu → mm", "s1", "s2", "messageId", CorrelationState.WrongCorrelation, "msg-1", "The response echoes messageId = msg-other, not msg-1.")],
        }, null));
        Services.AddSingleton(_api.Object);
        var factory = new Mock<IFrontendAnalysisContextFactory>();
        Services.AddSingleton(factory.Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static CompositeSourceCandidate Candidate(Guid id, string project, string archive) => new()
    {
        SnapshotId = id, ProjectKey = project, ArchiveFileName = archive, ArchiveSha256 = new string('b', 64), AnalyzedAt = DateTimeOffset.UnixEpoch, AnalysisStatus = "Ready",
        Kind = CompositeSourceKind.Application, MatchedOn = [$"Folder: {project}"], Modules = [project], Artifacts = ["XSD: urn:x:v2.0.1"], Confidence = "High (names and stage artifacts)",
    };

    private static CompositeSourceContext Context(CompositeContextSaveRequest? r) => new()
    {
        Id = Ctx, ContextKey = "skolenaervaer:m2lb", Revision = 1, OwningProject = "m2lb", ScopeId = "skolenaervaer",
        Snapshots = [.. (r?.Associations ?? [new(A, ["mu"], CompositeApproval.Approved), new(B, ["mm"], CompositeApproval.Approved)]).Select(a => new CompositeSnapshotAssociation
            { SnapshotId = a.SnapshotId, ProjectKey = a.SnapshotId == A ? "tiltak-2" : "m2lb", Stages = a.Stages, Approval = a.Approval, ArchiveSha256 = new string('b', 64) })],
        StageMappings = [new() { StageId = "mu", Label = "MU reception", Class = CompositeMatchClass.Confirmed }, new() { StageId = "mm", Label = "MM routing and validation", Class = CompositeMatchClass.Confirmed }],
        Compatibility =
        [
            new() { Key = "mu->mm", FromStage = "mu", ToStage = "mm", Channel = TransitionChannel.Message, Overall = TransitionCompatibility.Incompatible,
                Accepted = r?.Acceptances.Any(x => x.TransitionKey == "mu->mm") == true, AcceptedBy = "Local session", AcceptedReason = r?.Acceptances.FirstOrDefault()?.Reason,
                Dimensions = [new("Message schema (XML)", TransitionCompatibility.Incompatible, "Namespace/version mismatch — sender declares v2.0.0; receiver v2.0.1", [])] },
        ],
        SourceCoverage = new("Source coverage", CompositeReadiness.Blocked, "2 of 2 mandatory stage(s) have an approved source; 1 incompatible transition(s).", []),
    };

    private static IntegrationSuite Suite(string planState) => new()
    {
        Id = SuiteId, SuiteKey = "skolenaervaer:composite:m2lb:x", Revision = 1, Status = SuiteStatus.NeedsReview, Scope = new() { ScopeId = "skolenaervaer", DisplayName = "Altinn → MU → MM" },
        Runs = [new(RunId, "composite-e2e", DateTimeOffset.UnixEpoch, "Tester", "2 passed of 2", [])],
        Composite = new SuiteComposite
        {
            ContextId = Ctx, ContextKey = "skolenaervaer:m2lb", ContextRevision = 1,
            Sources = [new() { SnapshotId = A, ProjectKey = "tiltak-2", ArchiveFileName = "Tiltak 2.zip", ArchiveSha256 = new string('b', 64), Approval = "Approved", Stages = ["mu"] }],
            Stages =
            [
                new() { Order = 1, StageId = "mu", Label = "MU reception", Mandatory = true, Readiness = CompositeReadiness.Ready, Coverage = CompositeCoverageLevel.Configured, Executor = "Contract test POST /mu/messages", Observer = "Its own synchronous response" },
                new() { Order = 2, StageId = "mm", Label = "MM routing and validation", Mandatory = true, Readiness = CompositeReadiness.NotSupported, Coverage = CompositeCoverageLevel.None, Executor = "MISSING_EXECUTOR", Observer = "NOT_OBSERVABLE", WhyNotVerified = "No executor/observer." },
            ],
            Readiness =
            [
                new("Source coverage", CompositeReadiness.Ready, "2 of 2", []),
                new("Stage execution", CompositeReadiness.Partial, "1 of 2", ["MM routing and validation: NotSupported"]),
                new("Complete E2E", CompositeReadiness.Blocked, "A full E2E run cannot be VERIFIED yet.", ["Stages outside the plan (never verified end to end): MM routing and validation."]),
            ],
            Blockers =
            [
                new() { Id = "observer:mm", Stage = "MM routing and validation", Reason = "No executor and no observer can see this stage.", Action = "Configure an existing supported observer.", Owner = "Platform", Severity = "Blocking", ReviewStatus = "Open" },
                new() { Id = "kode67:security:bufdata-aggregate", Stage = "Kode 6/7", Reason = "Aggregates?", Action = "Needs domain decision.", Owner = "Domain owner", Severity = "Limiting", ReviewStatus = "NEEDS_DOMAIN_DECISION" },
            ],
            Plan = new CompositePlan
            {
                ChainId = Guid.NewGuid(), State = planState,
                Steps = [new() { StepId = "s1-mu", StageId = "mu", Role = "Executor", Operation = "POST /mu/messages", ContractRef = "mu@v1", Mutating = true },
                         new() { StepId = "s2-mm", StageId = "mm", Role = "Observer", Operation = "GET /mm/messages/{messageId}", ContractRef = "mm@v1", FromStep = "s1-mu", FromField = "messageId", Parameter = "messageId", EchoField = "messageId", ObserveAttempts = 5, ObserveDelayMs = 2000 }],
            },
            Coverage = [new("Complete E2E transaction", "Complete E2E", CompositeCoverageLevel.Configured, "A full E2E run cannot be VERIFIED yet.")],
            PeriodLock = ["Operation: operator-only in the real system"], BufdataDelivery = "SPECIFIED_BUT_NOT_RUNTIME_VERIFIED: …",
        },
    };

    private IRenderedComponent<IntegrationSuitesPage> At(string query)
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo($"integration-suites?{query}");
        return Render<IntegrationSuitesPage>();
    }

    [Fact]
    public void Discovery_preselects_only_single_candidates_and_saves_one_source_per_stage_with_explicit_approval()
    {
        var cut = At("area=sources");
        cut.WaitForAssertion(() => cut.Find("[data-testid=its-discover]"));
        cut.Find("[data-testid=its-discover]").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=its-stage-card]").Should().HaveCount(2));
        cut.Find("[data-stage=mm][data-testid=its-stage-card]").GetAttribute("data-class").Should().Be("Ambiguous");
        cut.FindAll("[data-testid=its-selected-source]").Should().ContainSingle(x => x.GetAttribute("data-project") == "tiltak-2", "an ambiguous stage is never preselected");
        cut.Find("[data-testid=its-supporting]").TextContent.Should().Contain("terraform-iac-m2lb").And.Contain("Infrastructure");
        cut.Find("[data-testid=its-strip-stage][data-stage=mm]").GetAttribute("data-state").Should().Be("NeedsReview");

        cut.FindAll("[data-stage=mm] [data-testid=its-candidate] input")[0].Change(B.ToString("D"));
        for (var i = 0; i < cut.FindAll("[data-testid=its-approve-source]").Count; i++) cut.FindAll("[data-testid=its-approve-source]")[i].Click();
        cut.Find("[data-testid=its-strip-stage][data-stage=mm]").GetAttribute("data-state").Should().Be("Ready");
        cut.Find("[data-testid=its-save-context]").Click();
        cut.WaitForAssertion(() => _saved.Should().NotBeNull());
        _saved!.Associations.Should().BeEquivalentTo(new[] { new CompositeAssociationInput(A, ["mu"], CompositeApproval.Approved), new CompositeAssociationInput(B, ["mm"], CompositeApproval.Approved) });
        cut.WaitForAssertion(() => cut.Find("[data-testid=its-source-coverage]").GetAttribute("data-state").Should().Be("Blocked"));
    }

    [Fact]
    public void A_link_to_an_older_revision_says_so_and_opens_the_current_one_and_the_url_follows_it()
    {
        var current = Guid.Parse("cccccccc-3333-0000-0000-000000000003");
        _api.Setup(a => a.ContextsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(([new CompositeContextSummary(current, "skolenaervaer:m2lb", 3, "skolenaervaer", "m2lb", 2, 2, 2, 2, DateTimeOffset.UnixEpoch)], null));
        _api.Setup(a => a.ContextAsync(current, It.IsAny<CancellationToken>())).ReturnsAsync((Context(null) with { Id = current, Revision = 3 }, null));
        var cut = At($"area=sources&context={Ctx:D}");
        cut.WaitForAssertion(() => cut.Find("[data-testid=its-older-revision]").TextContent.Should().Contain("revision 3 is current"));
        cut.Find("[data-testid=its-open-latest]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=its-context-summary]").TextContent.Should().Contain("revision 3"));
        cut.FindAll("[data-testid=its-older-revision]").Should().BeEmpty();
        Services.GetRequiredService<NavigationManager>().Uri.Should().Contain($"context={current:D}", "a reload or a shared link opens the revision on screen");
    }

    [Fact]
    public void An_incompatible_transition_is_accepted_only_with_a_reason_and_stays_incompatible()
    {
        var cut = At($"area=sources&context={Ctx:D}");
        cut.WaitForAssertion(() => cut.Find("[data-testid=its-transition]"));
        cut.Find("[data-testid=its-transition]").GetAttribute("data-state").Should().Be("Incompatible");
        cut.Find("[data-testid=its-accept]").HasAttribute("disabled").Should().BeTrue("a reason is required");
        cut.Find("[data-testid=its-accept-reason]").Input("MU transports the payload opaquely");
        cut.Find("[data-testid=its-accept]").Click();
        cut.WaitForAssertion(() => _saved.Should().NotBeNull());
        _saved!.Acceptances.Should().ContainSingle(a => a.TransitionKey == "mu->mm" && a.Reason == "MU transports the payload opaquely");
        cut.WaitForAssertion(() => cut.Find("[data-testid=its-accepted]").TextContent.Should().Contain("opaquely"));
        cut.Find("[data-testid=its-transition]").GetAttribute("data-state").Should().Be("Incompatible");
    }

    [Fact]
    public void Readiness_shows_three_levels_and_actionable_blockers_filtered_by_owner()
    {
        var cut = At($"area=readiness&suite={SuiteId:D}");
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=its-level]").Should().HaveCount(3));
        cut.Find("[data-level='Complete E2E']").GetAttribute("data-state").Should().Be("Blocked");
        cut.Find("[data-testid=its-stage-row][data-stage=mm]").TextContent.Should().Contain("NOT_OBSERVABLE");
        cut.FindAll("[data-testid=its-blocker]").Should().HaveCount(2);
        cut.FindAll("[data-testid=its-owner-filter]").Single(b => b.TextContent == "Domain owner").Click();
        cut.FindAll("[data-testid=its-blocker]").Should().ContainSingle().Which.TextContent.Should().Contain("Needs domain decision");
        cut.Find("[data-testid=its-strip-stage][data-stage=mm]").GetAttribute("data-state").Should().Be("NotSupported");
    }

    [Fact]
    public void A_draft_plan_is_approved_first_then_a_run_needs_preflight_confirmation_and_authorisation()
    {
        var cut = At($"area=execute&suite={SuiteId:D}");
        cut.WaitForAssertion(() => cut.Find("[data-testid=its-plan]"));
        cut.Find("[data-testid=its-plan-state]").TextContent.Should().Be("Draft");
        cut.Find("[data-testid=its-plan-mapping]").TextContent.Should().Contain("messageId ← messageId of s1-mu").And.Contain("checks messageId");
        cut.Find("[data-testid=its-no-environment]").TextContent.Should().Contain("Target Environment", "no active target environment in this test");
        _api.Setup(a => a.GetAsync(SuiteId, It.IsAny<CancellationToken>())).ReturnsAsync((Suite("Approved"), null));
        cut.Find("[data-testid=its-approve-plan]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=its-plan-state]").TextContent.Should().Be("Approved"));
        cut.FindAll("[data-testid=its-approve-plan]").Should().BeEmpty();
        _api.Verify(a => a.RunCompositeAsync(It.IsAny<Guid>(), It.IsAny<CompositeRunRequest>(), It.IsAny<CancellationToken>()), Times.Never, "nothing runs without a target and authorisation");
    }

    [Fact]
    public void The_run_view_shows_the_backend_verdict_timeline_and_correlation()
    {
        var cut = At($"area=execute&suite={SuiteId:D}");
        cut.WaitForAssertion(() => cut.Find("[data-testid=its-run-open]"));
        cut.Find("[data-testid=its-run-open]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=its-verdict]").TextContent.Should().Be("WRONG_CORRELATION"));
        cut.FindAll("[data-testid=its-run-stage]").Select(s => s.GetAttribute("data-state")).Should().Equal("Succeeded", "Succeeded");
        cut.Find("[data-testid=its-correlation]").GetAttribute("data-state").Should().Be("WrongCorrelation", "succeeded stages do not hide a wrong correlation");
        cut.Find("[data-testid=its-correlation-value]").TextContent.Should().Be("msg-1", "the tester sees the identifier BirkNext carried");
        cut.Find("[data-testid=its-bufdata]").TextContent.Should().StartWith("SPECIFIED_BUT_NOT_RUNTIME_VERIFIED");
        cut.Find("[data-testid=its-coverage-row]").GetAttribute("data-level").Should().Be("Configured");
    }
}
