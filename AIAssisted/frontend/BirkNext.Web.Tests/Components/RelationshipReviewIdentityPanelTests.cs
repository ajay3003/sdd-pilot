using AngleSharp.Dom;
using BirkNext.DeliveryReporting;
using BirkNext.Interpretation;
using BirkNext.Snapshots;
using BirkNext.Web.Components;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Requirement identity panel in Relationship Review (identity/1): shown only while unscoped requirement ids exist; source + Delivery snapshot chosen from
/// selectors (never typed ids); an actionable notice when no Delivery snapshot exists; ambiguous rows need an explicit target; Confirm/Reject go to the
/// shared decision registry with reviewer and rationale; history per id.
/// </summary>
public sealed class RelationshipReviewIdentityPanelTests : BunitContext
{
    const string Fr1 = "Requirement|FR-001", Fr2 = "Requirement|FR-002", Fr9 = "Requirement|FR-009";
    const string PayFr1 = "Requirement|payments/001-payments/FR-001", BillFr1 = "Requirement|billing/001-billing/FR-001", PayFr2 = "Requirement|payments/001-payments/FR-002";
    static readonly Guid Source = Guid.NewGuid(), Project = Guid.NewGuid(), DeliverySnapshot = Guid.NewGuid();

    readonly Mock<ICanonicalInterpretationApiService> _api = new();
    readonly Mock<IDeliveryReportingApiService> _delivery = new();
    RequirementIdentityView? _view;

    static RequirementIdentityTarget T(string unscoped, string key, string module, string feature, IdentityTargetState state = IdentityTargetState.Candidate) =>
        new(key, module, feature, key[(key.LastIndexOf('/') + 1)..], $"{key[(key.LastIndexOf('/') + 1)..]}: text", [new("delivery-requirement", $"{module}/{feature}", "defined in Delivery snapshot", "delivery:x")],
            state, RequirementIdentity.DecisionKey(unscoped, key), null);

    static RequirementIdentityRow Row(string key, RequirementIdentityState state, params RequirementIdentityTarget[] targets) =>
        new(key, key[(key.IndexOf('|') + 1)..], key[(key.IndexOf('|') + 1)..], state, [new("SourceSymbol References", "Payments/src/PaymentService.cs", "PaymentService", "TaFixture.zip")],
            targets, state == RequirementIdentityState.Confirmed ? targets.First(t => t.State == IdentityTargetState.Confirmed).TargetKey : null, $"{state} explanation");

    static RequirementIdentityView View(bool delivery, params RequirementIdentityRow[] rows) => new("dev", Guid.NewGuid(), [], delivery, rows, []);

    static RequirementIdentityView Analyzed() => View(true,
        Row(Fr1, RequirementIdentityState.Ambiguous, T(Fr1, PayFr1, "Payments", "001-payments"), T(Fr1, BillFr1, "Billing", "001-billing")),
        Row(Fr2, RequirementIdentityState.Candidate, T(Fr2, PayFr2, "Payments", "001-payments")),
        Row(Fr9, RequirementIdentityState.Unresolved));

    void Setup(RequirementIdentityView? view, IReadOnlyList<DeliveryProject>? projects = null, IReadOnlyList<DeliverySnapshotSummary>? snapshots = null)
    {
        _view = view;
        _api.Setup(a => a.RequirementIdentitiesAsync("dev", It.IsAny<CancellationToken>())).ReturnsAsync(() => (_view, (string?)null));
        _delivery.Setup(d => d.ProjectsAsync(It.IsAny<CancellationToken>())).ReturnsAsync((projects?.ToList() ?? new List<DeliveryProject>(), (string?)null));
        _delivery.Setup(d => d.SnapshotsAsync(Project, It.IsAny<CancellationToken>())).ReturnsAsync(snapshots?.ToList() ?? new List<DeliverySnapshotSummary>());
        Services.AddSingleton(_api.Object);
        Services.AddSingleton(_delivery.Object);
    }

    IRenderedComponent<RelationshipReviewIdentityPanel> Panel(Action<InterpretationSnapshotView>? analyzed = null) =>
        Render<RelationshipReviewIdentityPanel>(p => p.Add(x => x.Scope, "dev")
            .Add(x => x.Sources, new[] { new RelationshipSource(Source, "ta · TaFixture.zip", DateTimeOffset.UnixEpoch, SnapshotProcessingStatus.Completed, null, true) })
            .Add(x => x.SelectedSources, new[] { Source })
            .Add(x => x.OnAnalyzed, s => analyzed?.Invoke(s)));

    static DeliveryProject DeliveryProject(int snapshots = 1) => new(Project, "TaFixture", null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, snapshots, 0);

    static DeliverySnapshotSummary Snapshot() => new()
    {
        Id = DeliverySnapshot, ProjectId = Project, Label = "TaFixture specs", EffectiveAt = DateTimeOffset.UnixEpoch, SourceFileName = "ta-fixture.zip", FeatureCount = 2,
    };

    [Fact]
    public void Nothing_is_shown_when_every_requirement_is_scoped()
    {
        Setup(View(true));
        var cut = Panel();
        cut.FindAll("[data-testid=cr-identity-panel]").Should().BeEmpty();
    }

    [Fact]
    public void Without_a_Delivery_snapshot_it_explains_how_to_import_one_and_blocks_only_its_own_run()
    {
        Setup(View(false, Row(Fr1, RequirementIdentityState.Unresolved)));
        var cut = Panel();

        cut.WaitForAssertion(() => cut.Find("[data-testid=cr-identity-no-delivery]").TextContent.Should().Contain("Delivery Reports"));
        cut.Find("[data-testid=cr-identity-delivery-import]").GetAttribute("href").Should().Be("delivery-reporting");
        cut.Find("[data-testid=cr-identity-run-panel]").HasAttribute("open").Should().BeTrue("a source-only analysis opens the guided run");
        cut.Find("[data-testid=cr-identity-run]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=cr-identity-run-blocked]").TextContent.Should().Contain("Delivery snapshot");
        cut.Find("[data-testid=cr-identity-row]").GetAttribute("data-state").Should().Be("unresolved");
    }

    [Fact]
    public void Analyze_relationships_runs_with_the_chosen_source_and_Delivery_snapshot()
    {
        Setup(View(false, Row(Fr1, RequirementIdentityState.Unresolved)), [DeliveryProject()], [Snapshot()]);
        InterpretRequest? sent = null;
        _api.Setup(a => a.InterpretAsync("dev", It.IsAny<InterpretRequest>(), It.IsAny<CancellationToken>()))
            .Callback<string, InterpretRequest, CancellationToken>((_, r, _) => sent = r)
            .ReturnsAsync((new InterpretationSnapshotView(Guid.NewGuid(), "dev", DateTimeOffset.UnixEpoch, ["correlation/4"], [], 0, 10, 5, "fp", [], "x", null), (string?)null));
        InterpretationSnapshotView? analyzed = null;
        var cut = Panel(s => analyzed = s);

        cut.WaitForAssertion(() => cut.Find("[data-testid=cr-identity-delivery]").HasAttribute("disabled").Should().BeFalse("the only Delivery project is selected"));
        cut.FindAll("[data-testid=cr-identity-delivery] option").Select(o => o.TextContent).Should().Contain(t => t.Contains("TaFixture specs"));
        cut.Find("[data-testid=cr-identity-run]").HasAttribute("disabled").Should().BeTrue("no Delivery snapshot is chosen silently");
        cut.Find("[data-testid=cr-identity-source]").Change(Source.ToString());
        cut.Find("[data-testid=cr-identity-delivery]").Change(DeliverySnapshot.ToString());
        _view = Analyzed();
        cut.Find("[data-testid=cr-identity-run]").Click();

        cut.WaitForAssertion(() => sent.Should().NotBeNull());
        sent!.SourceSnapshotIds.Should().Equal([Source]);
        sent.DeliverySnapshotId.Should().Be(DeliverySnapshot);
        analyzed.Should().NotBeNull("the page reloads its queue and history");
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=cr-identity-row]").Select(r => r.GetAttribute("data-state")).Should().Equal(["ambiguous", "candidate", "unresolved"]));
    }

    [Fact]
    public void An_ambiguous_row_needs_a_chosen_target_and_a_rationale_before_Confirm()
    {
        Setup(Analyzed(), [DeliveryProject()], [Snapshot()]);
        ReviewDecisionRequest? decided = null;
        _api.Setup(a => a.DecideAsync("dev", It.IsAny<ReviewDecisionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<string, ReviewDecisionRequest, CancellationToken>((_, r, _) => decided = r)
            .ReturnsAsync((new ReviewDecisionView(Guid.NewGuid(), 1, ReviewTargetKind.Identity, "k", ReviewDecisionOutcome.Confirmed, "Tester", DateTimeOffset.UnixEpoch, "why", "fp",
                RequirementIdentity.RuleSetVersion, null, null, "{}"), (string?)null));
        var cut = Panel();

        IElement Fr1Row() => cut.FindAll("[data-testid=cr-identity-row]").Single(r => r.GetAttribute("data-id") == "FR-001");
        cut.WaitForAssertion(() => Fr1Row().GetAttribute("data-state").Should().Be("ambiguous"));
        Fr1Row().QuerySelectorAll("[data-testid=cr-identity-target]").Select(t => t.GetAttribute("data-target")).Should().Equal([PayFr1, BillFr1]);
        Fr1Row().QuerySelectorAll("[data-testid=cr-identity-choose]").Should().OnlyContain(r => !r.HasAttribute("checked"), "there is no default for an ambiguous id");
        Fr1Row().QuerySelector("[data-testid=cr-identity-confirm]")!.HasAttribute("disabled").Should().BeTrue();
        Fr1Row().QuerySelector("[data-testid=cr-identity-decide-hint]")!.TextContent.Should().Contain("Choose the feature pack");

        cut.Find("[data-testid=cr-identity-reviewer]").Input("Tester");
        cut.FindAll("[data-testid=cr-identity-row]").Single(r => r.GetAttribute("data-id") == "FR-001").QuerySelectorAll("[data-testid=cr-identity-choose]")[1].Change(true);
        Fr1Row().QuerySelector("[data-testid=cr-identity-confirm]")!.HasAttribute("disabled").Should().BeTrue("a rationale is required");
        cut.FindAll("[data-testid=cr-identity-rationale]")[0].Input("PaymentService lives in Billing");
        Fr1Row().QuerySelector("[data-testid=cr-identity-confirm]")!.HasAttribute("disabled").Should().BeFalse();
        cut.FindAll("[data-testid=cr-identity-confirm]")[0].Click();

        cut.WaitForAssertion(() => decided.Should().NotBeNull());
        decided!.TargetKind.Should().Be(ReviewTargetKind.Identity);
        decided.TargetKey.Should().Be(RequirementIdentity.DecisionKey(Fr1, BillFr1));
        decided.Outcome.Should().Be(ReviewDecisionOutcome.Confirmed);
        (decided.Reviewer, decided.Notes).Should().Be(("Tester", "PaymentService lives in Billing"));
    }

    [Fact]
    public void A_single_candidate_is_preselected_but_rejecting_still_needs_a_rationale_and_history_is_listed()
    {
        Setup(Analyzed(), [DeliveryProject()], [Snapshot()]);
        ReviewDecisionRequest? decided = null;
        _api.Setup(a => a.DecideAsync("dev", It.IsAny<ReviewDecisionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<string, ReviewDecisionRequest, CancellationToken>((_, r, _) => decided = r)
            .ReturnsAsync(((ReviewDecisionView?)null, (string?)null));
        _api.Setup(a => a.DecisionsAsync("dev", null, It.IsAny<CancellationToken>())).ReturnsAsync(([
            new ReviewDecisionView(Guid.NewGuid(), 2, ReviewTargetKind.Identity, RequirementIdentity.DecisionKey(Fr2, PayFr2), ReviewDecisionOutcome.Confirmed, "Tester",
                DateTimeOffset.UnixEpoch, "Only Payments defines it", "fp", RequirementIdentity.RuleSetVersion, null, null, "{}"),
            new ReviewDecisionView(Guid.NewGuid(), 1, ReviewTargetKind.Relation, "other", ReviewDecisionOutcome.Confirmed, "Tester", DateTimeOffset.UnixEpoch, null, "fp", "c", null, null, "{}"),
        ], (string?)null));
        var cut = Panel();

        IElement Fr2Row() => cut.FindAll("[data-testid=cr-identity-row]").Single(r => r.GetAttribute("data-id") == "FR-002");
        cut.WaitForAssertion(() => Fr2Row().GetAttribute("data-state").Should().Be("candidate"));
        Fr2Row().QuerySelector("[data-testid=cr-identity-choose]")!.HasAttribute("checked").Should().BeTrue("the only feature pack that defines it");
        Fr2Row().QuerySelector("[data-testid=cr-identity-reject]")!.HasAttribute("disabled").Should().BeTrue();

        Fr2Row().QuerySelector("[data-testid=cr-identity-history]")!.Click();
        cut.WaitForAssertion(() => Fr2Row().QuerySelectorAll("[data-testid=cr-identity-history-entry]").Should().ContainSingle().Which.TextContent.Should().Contain("Only Payments"));

        cut.FindAll("[data-testid=cr-identity-row]").Single(r => r.GetAttribute("data-id") == "FR-002").QuerySelector("[data-testid=cr-identity-rationale]")!.Input("Not this pack");
        Fr2Row().QuerySelector("[data-testid=cr-identity-reject]")!.Click();
        cut.WaitForAssertion(() => decided.Should().NotBeNull());
        decided!.TargetKey.Should().Be(RequirementIdentity.DecisionKey(Fr2, PayFr2));
        decided.Outcome.Should().Be(ReviewDecisionOutcome.Rejected);
    }

    [Fact]
    public void A_confirmed_row_shows_the_identity_it_resolves_to()
    {
        Setup(View(true, Row(Fr2, RequirementIdentityState.Confirmed, T(Fr2, PayFr2, "Payments", "001-payments", IdentityTargetState.Confirmed))), [DeliveryProject()], [Snapshot()]);
        var cut = Panel();
        cut.WaitForAssertion(() => cut.Find("[data-testid=cr-identity-row]").GetAttribute("data-state").Should().Be("confirmed"));
        cut.Find("[data-testid=cr-identity-resolved]").TextContent.Should().Be(PayFr2);
        cut.Find("[data-testid=cr-identity-confirm]").HasAttribute("disabled").Should().BeTrue("already confirmed");
        cut.Find("[data-testid=cr-identity-run-panel]").HasAttribute("open").Should().BeFalse("the analysis already has a Delivery snapshot");
    }
}
