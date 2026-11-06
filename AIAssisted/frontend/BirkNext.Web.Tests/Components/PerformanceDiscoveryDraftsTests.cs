using BirkNext.LocalHttpsProxy;
using BirkNext.PerformanceTests;
using BirkNext.SourceDomains;
using BirkNext.Web.Components;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using DD = BirkNext.PerformanceTests.PerformanceDiscoveryDrafts;
using E = BirkNext.PerformanceTests.PerformanceScenarioEngine;
using R = BirkNext.PerformanceTests.PerformanceScenarioRuntime;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Endpoint Discovery → "Discovered · review required" draft steps: created automatically from operations seen in the latest run, disabled (never
/// executed) until accepted, pre-filled with safe values only; "Follow latest observation" takes newer evidence, "Pinned" only reports it; nothing
/// is overwritten silently and dismissed operations are not re-created. Generic fixtures; nothing runs.
/// </summary>
public sealed class PerformanceDiscoveryDraftsTests : BunitContext
{
    private const string Target = "https://app.example.test", Host = "app.example.test";
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
    private PerformanceTestDefinition? _changed;

    public PerformanceDiscoveryDraftsTests()
    {
        var api = new Mock<IPerformanceTestApiService>();
        api.Setup(a => a.ScenarioSourcesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        Services.AddSingleton(api.Object);
        Services.AddSingleton(new Mock<ITestDataApiService>().Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static ObservedNetworkEndpoint Rest(string method, string path, int count = 3, DateTimeOffset? last = null, bool auth = false) => new()
    {
        Category = ObservedTrafficCategory.Rest, Host = Host, Method = method, Path = path, Count = count, LastStatus = 200, AuthObserved = auth,
        FirstObservedAt = T0, LastObservedAt = last ?? T0.AddMinutes(30),
    };

    private static PerformanceTestDefinition Definition(params HttpPerformanceStep[] steps) => new()
    {
        Id = "def-1", Name = "Baseline", TargetOrigin = Target, EnvironmentName = "Development", EnvironmentType = "Development",
        Scenario = new PerformanceScenario { Name = "Main scenario", Steps = [.. steps] },
    };

    private static IReadOnlyList<ScenarioOperation> Current(params ObservedNetworkEndpoint[] observed) => Current([], observed);

    private static IReadOnlyList<ScenarioOperation> Current(IReadOnlyList<SourceContract> contracts, params ObservedNetworkEndpoint[] observed) =>
        [.. E.Build(contracts, contracts.Count > 0 ? Guid.Empty : null, null, Target, "Development", observed, [], null, false).Operations.Where(o => o.Observation is not null)];

    // ── Engine ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Read_only_application_operations_become_disabled_drafts_that_follow_the_latest_observation()
    {
        var r = DD.Reconcile(Definition(), Current(Rest("GET", "/api/orders/123"), Rest("GET", "/api/reports"), Rest("POST", "/api/orders"), Rest("GET", "/appsettings.json")), "run-2");

        r.Created.Should().Be(2, "POST changes data and appsettings.json is technical traffic");
        r.Definition.Scenario.Steps.Select(s => $"{s.Method} {s.RelativePath}").Should().BeEquivalentTo(["GET /api/orders/{id}", "GET /api/reports"]);
        r.Definition.Scenario.Steps.Should().OnlyContain(s => s.Disabled && DD.IsDraft(s) && s.Discovery!.Tracking == ObservationTracking.FollowLatest && s.Discovery.RunId == "run-2");
        R.ScenarioReadiness(r.Definition).Reasons.Should().Contain("Every step is disabled or a discovered draft awaiting review");
        R.Readiness(r.Definition, 0).Reasons.Single().Should().StartWith(DD.Label).And.Contain("not run until accepted");
    }

    [Fact]
    public void Drafts_never_carry_an_observed_value_and_take_a_declared_default_as_a_safe_prefill()
    {
        var contract = new SourceContract
        {
            Id = "orders", Type = SourceContractType.OpenApi, Name = "Orders API", File = "orders.json",
            Operations = [new ContractOperation("GET /api/orders", "GET", "/api/orders", ["status"])
            {
                Detailed = true, ResponseCodes = ["200"], ParameterDetails = [new ContractParameter("status", "query", true, "string") { Default = "open" }],
            }],
        };
        var r = DD.Reconcile(Definition(), Current([contract], Rest("GET", "/api/orders"), Rest("GET", "/api/customers/42")), "run-2");

        var orders = r.Definition.Scenario.Steps.Single(s => s.RelativePath == "/api/orders");
        orders.DataMappings!.Single().Should().Match<ScenarioDataMapping>(m => m.Source == RuntimeValueSource.AutoFilled && m.FixedValue == "open" && m.ValueOrigin == RuntimeValueOrigins.ContractDefault);
        var customers = r.Definition.Scenario.Steps.Single(s => s.RelativePath.StartsWith("/api/customers"));
        customers.RelativePath.Should().Be("/api/customers/{id}", "the observed identifier is templated, never copied");
        (customers.DataMappings ?? []).Should().BeEmpty("an observed value is never pre-filled");
    }

    [Fact]
    public void Reconciling_again_changes_nothing()
    {
        var current = Current(Rest("GET", "/api/orders/1"), Rest("GET", "/api/reports"));
        var first = DD.Reconcile(Definition(), current, "run-2");
        var second = DD.Reconcile(first.Definition, current, "run-2");

        second.Changed.Should().BeFalse();
        second.Definition.Should().BeSameAs(first.Definition);
    }

    [Fact]
    public void Dismissed_or_deleted_operations_are_not_re_created()
    {
        var current = Current(Rest("GET", "/api/orders/1"), Rest("GET", "/api/reports"));
        var drafted = DD.Reconcile(Definition(), current, "run-2").Definition;
        var removed = drafted.Scenario.Steps[0];
        var scenario = DD.Remember(drafted.Scenario, removed) with { Steps = [.. drafted.Scenario.Steps.Skip(1)] };

        scenario.DismissedDiscoveries.Should().Equal([removed.Source!.OperationKey]);
        DD.Reconcile(drafted with { Scenario = scenario }, current, "run-3").Created.Should().Be(0);
    }

    [Fact]
    public void Graphql_operations_are_not_drafted_into_a_rest_definition_and_the_draft_count_is_capped()
    {
        var many = Enumerable.Range(1, 14).Select(i => Rest("GET", $"/api/resource{i}")).ToArray();
        var r = DD.Reconcile(Definition(), Current(many), "run-2");
        r.Created.Should().Be(DD.MaxDrafts);
        DD.Ineligible(Current(Rest("GET", "/api/x"))[0], PerformanceTargetType.GraphQlHttp).Should().Be("different API style than this definition");
    }

    [Fact]
    public void Follow_latest_takes_newer_evidence_and_never_drops_an_authentication_requirement()
    {
        var drafted = DD.Reconcile(Definition(), Current(Rest("GET", "/api/orders/1", count: 3, auth: true)), "run-1").Definition;
        var newer = DD.Reconcile(drafted, Current(Rest("GET", "/api/orders/1", count: 9, last: T0.AddHours(5), auth: false)), "run-2");

        newer.Updated.Should().Be(1);
        var step = newer.Definition.Scenario.Steps.Single();
        step.Source!.ObservedCount.Should().Be(9);
        step.Source.ObservedLastSeen.Should().Be(T0.AddHours(5));
        step.Source.ObservedAuthentication.Should().NotBeNull("a requirement observed before is never dropped automatically");
        step.Discovery!.RunId.Should().Be("run-2");
        DD.IsDraft(step).Should().BeTrue("following the latest observation never accepts a draft");
    }

    [Fact]
    public void Pinned_steps_are_unchanged_and_only_report_a_newer_observation()
    {
        var drafted = DD.Reconcile(Definition(), Current(Rest("GET", "/api/orders/1")), "run-1").Definition;
        var pinned = drafted with { Scenario = drafted.Scenario with { Steps = [DD.SetTracking(DD.Accept(drafted.Scenario.Steps[0], T0), ObservationTracking.Pinned)] } };
        var r = DD.Reconcile(pinned, Current(Rest("GET", "/api/orders/1", count: 20, last: T0.AddDays(1))), "run-2");

        r.Changed.Should().BeFalse();
        r.Definition.Should().BeSameAs(pinned);
        r.Changes.Single().Kind.Should().Be(ScenarioDiscoveryChange.PinnedNewer);
        r.Changes.Single().Text.Should().Contain("Newer observation available").And.Contain("pinned to");
    }

    [Fact]
    public void Legacy_and_authored_steps_are_pinned_by_default()
    {
        var legacy = new HttpPerformanceStep { Id = "s1", Name = "orders", RelativePath = "/api/orders/{id}",
            Source = new ScenarioStepSource { OperationKey = "x", Origin = ScenarioOperationOrigin.Observed, Evidence = ScenarioEvidence.ObservedOnly, EvidenceIdentity = "GET /api/orders/{id}", ObservedLastSeen = T0 } };
        DD.Tracking(legacy).Should().Be(ObservationTracking.Pinned);
        var r = DD.Reconcile(Definition(legacy), Current(Rest("GET", "/api/orders/1", last: T0.AddDays(1))), "run-2", createDrafts: false);
        r.Changes.Single().Kind.Should().Be(ScenarioDiscoveryChange.PinnedNewer);
        r.Definition.Scenario.Steps.Single().Should().BeSameAs(legacy);
    }

    [Fact]
    public void A_changed_request_is_applied_to_an_unedited_draft_but_only_reported_for_a_reviewed_step()
    {
        var op = Current(Rest("GET", "/api/orders/1"))[0];
        HttpPerformanceStep Stale(StepDiscoveryState state) => new()
        {
            Id = "s1", Name = "orders", Method = "GET", RelativePath = "/api/old-orders/{id}", Disabled = state == StepDiscoveryState.NeedsReview,
            DataMappings = [new ScenarioDataMapping("id", ["order-1"], BirkNext.TestData.TestDataDataType.SimpleValues, "path")],
            Source = new ScenarioStepSource { OperationKey = op.Key, Origin = ScenarioOperationOrigin.Observed, Evidence = ScenarioEvidence.ObservedOnly, ObservedLastSeen = T0 },
            Discovery = new StepDiscovery { State = state, Tracking = ObservationTracking.FollowLatest },
        };

        var draft = DD.Reconcile(Definition(Stale(StepDiscoveryState.NeedsReview)), [op], "run-2", createDrafts: false);
        draft.Definition.Scenario.Steps[0].RelativePath.Should().Be("/api/orders/{id}");
        draft.Definition.Scenario.Steps[0].DataMappings!.Single().AssetAliases.Should().Equal(["order-1"], "a value a person configured is kept");

        var reviewed = DD.Reconcile(Definition(Stale(StepDiscoveryState.Reviewed)), [op], "run-2", createDrafts: false);
        reviewed.Definition.Scenario.Steps[0].RelativePath.Should().Be("/api/old-orders/{id}", "a reviewed step's request is never overwritten");
        reviewed.Changes.Should().Contain(c => c.Kind == ScenarioDiscoveryChange.RequestDiffers && c.Text.Contains("GET /api/orders/{id}"));
    }

    [Fact]
    public void Accepting_enables_the_step_and_keeps_its_tracking()
    {
        var draft = DD.Reconcile(Definition(), Current(Rest("GET", "/api/reports")), "run-2").Definition.Scenario.Steps[0];
        var accepted = DD.Accept(draft, T0);
        accepted.Disabled.Should().BeFalse();
        accepted.Discovery.Should().Match<StepDiscovery>(d => d.State == StepDiscoveryState.Reviewed && d.ReviewedAt == T0 && d.Tracking == ObservationTracking.FollowLatest);
    }

    // ── Builder ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static ObservedOperationHistory History(string path, ObservedOperationState state = ObservedOperationState.New) => new()
    {
        Identity = $"GET {path}", Protocol = "REST", Host = Host, Method = "GET", Path = path, State = state, ObservationCount = 4, LastStatus = 200,
        FirstObservedAt = T0, LastObservedAt = T0.AddMinutes(30), FirstRunId = "run-2", LastRunId = "run-2",
    };

    private IRenderedComponent<PerformanceScenarioBuilder> Builder(string? source, params ObservedOperationHistory[] ops)
    {
        var latest = new EndpointDiscoveryRunSummary("run-2", "dev", Host, T0, T0.AddMinutes(5), EndpointDiscoveryRunSummary.Completed, ops.Length, ops.Length, 0, 0);
        var d = Definition() with { Scenario = new PerformanceScenario { Name = "Main scenario", SourceKind = source } };
        return Render<PerformanceScenarioBuilder>(p => p.Add(x => x.Definition, d).Add(x => x.EnvironmentId, "dev").Add(x => x.EnvironmentName, "Development")
            .Add(x => x.History, new EndpointObservationHistoryView("dev", Host, true, latest, null, [latest], ops)).Add(x => x.HistoryLoaded, true)
            .Add(x => x.OnChange, (PerformanceTestDefinition next) => _changed = next));
    }

    [Fact]
    public void Live_Discovery_creates_drafts_as_an_unsaved_change_and_says_so()
    {
        var cut = Builder(null, History("/api/orders/{id}"), History("/api/reports"), History("/api/archive", ObservedOperationState.PreviouslyObserved));

        cut.WaitForAssertion(() => _changed.Should().NotBeNull(), TimeSpan.FromSeconds(10));
        _changed!.Scenario.Steps.Select(s => s.RelativePath).Should().BeEquivalentTo(["/api/orders/{id}", "/api/reports"], "only operations seen in the latest run");
        _changed.Scenario.Steps.Should().OnlyContain(s => DD.IsDraft(s) && s.Disabled);
        cut.Find("[data-testid=psb-discovery-note]").TextContent.Should().Contain("added 2 discovered steps").And.Contain("not run until accepted").And.Contain("Save the definition");
    }

    [Fact]
    public void Declared_mode_never_creates_drafts()
    {
        var cut = Builder("declared", History("/api/orders/{id}"));
        cut.WaitForAssertion(() => cut.Find("[data-testid=psb]"), TimeSpan.FromSeconds(10));
        _changed.Should().BeNull();
        cut.FindAll("[data-testid=psb-discovery]").Should().BeEmpty();
    }

    // ── Steps ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private IRenderedComponent<PerformanceScenarioSteps> Steps(PerformanceTestDefinition d)
    {
        _changed = d;
        IRenderedComponent<PerformanceScenarioSteps>? cut = null;
        cut = Render<PerformanceScenarioSteps>(p => p.Add(x => x.Definition, d)
            .Add(x => x.OnChange, (PerformanceTestDefinition next) => { _changed = next; cut?.Render(q => q.Add(x => x.Definition, next)); }));
        return cut;
    }

    [Fact]
    public void A_draft_shows_the_amber_review_state_and_can_be_accepted()
    {
        var cut = Steps(DD.Reconcile(Definition(), Current(Rest("GET", "/api/reports")), "run-2").Definition);

        var status = cut.Find("[data-testid=pt-step-status]");
        status.TextContent.Should().Be(DD.Label);
        status.GetAttribute("data-status").Should().Be("Discovered");
        cut.Find("[data-testid=pt-step]").ClassList.Should().Contain("pss-tone-discovered");
        cut.Find("[data-testid=pss-drafts-count]").TextContent.Should().Contain("1 discovered · review required");

        cut.Find("[data-testid=pt-step-accept]").Click();
        _changed!.Scenario.Steps.Single().Should().Match<HttpPerformanceStep>(s => !s.Disabled && s.Discovery!.State == StepDiscoveryState.Reviewed);
        cut.FindAll("[data-testid=pt-step-draft]").Should().BeEmpty();
    }

    [Fact]
    public void Dismissing_a_draft_removes_it_and_remembers_the_operation()
    {
        var drafted = DD.Reconcile(Definition(), Current(Rest("GET", "/api/reports")), "run-2").Definition;
        var cut = Steps(drafted);

        cut.Find("[data-testid=pt-step-dismiss]").Click();
        _changed!.Scenario.Steps.Should().BeEmpty();
        _changed.Scenario.DismissedDiscoveries.Should().Equal([drafted.Scenario.Steps[0].Source!.OperationKey]);
    }

    [Fact]
    public void Observed_steps_offer_follow_latest_or_pinned()
    {
        var accepted = DD.Reconcile(Definition(), Current(Rest("GET", "/api/reports")), "run-2").Definition;
        var cut = Steps(accepted with { Scenario = accepted.Scenario with { Steps = [DD.Accept(accepted.Scenario.Steps[0], T0)] } });

        cut.Find("[data-testid=pt-step-part][data-part=advanced]").Click();
        var tracking = cut.Find("[data-testid=pt-step-tracking]");
        tracking.GetAttribute("value").Should().Be("FollowLatest");
        tracking.Change("Pinned");
        DD.Tracking(_changed!.Scenario.Steps.Single()).Should().Be(ObservationTracking.Pinned);
        cut.Find("[data-testid=pt-step-tracking-help]").TextContent.Should().Contain("newer observations are only reported");
    }
}
