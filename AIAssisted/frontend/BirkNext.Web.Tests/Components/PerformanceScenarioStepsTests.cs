using BirkNext.PerformanceTests;
using BirkNext.TestData;
using BirkNext.Web.Components;
using Bunit;
using FluentAssertions;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Scenario steps as the main working area: runtime values owned by the step (Test Data is one source among previous step output, scenario
/// variable, fixed and generated values), drag and keyboard reordering with announcements, dependency conflicts that are never silently
/// accepted, delete/disable warnings, duplicate with a new identity, step authentication inheritance and one primary status per step.
/// </summary>
public sealed class PerformanceScenarioStepsTests : BunitContext
{
    private PerformanceTestDefinition _d = new();

    public PerformanceScenarioStepsTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private static readonly TestDataAssetSummary UserId = new()
    {
        Id = "user-01", Alias = "user-01", Kind = TestDataKind.Scalar, Approval = TestDataApproval.Approved, Capabilities = ["brukerId"], Source = "Configured",
    };

    private static HttpPerformanceStep Step(string id, string path) => new() { Id = id, Name = id, Method = "GET", RelativePath = path, ExpectedStatusCodes = [200] };

    /// <summary>Opens one part of the focused step editor (Authentication, Parameters & Test Data, Request, Response & extraction, Advanced).</summary>
    private static void Part<T>(IRenderedComponent<T> cut, string part, int step = 0) where T : Microsoft.AspNetCore.Components.IComponent =>
        cut.FindAll("[data-testid=pt-step]")[step].QuerySelector($"[data-testid=pt-step-part][data-part={part}]")!.Click();

    /// <summary>Opens every step's focused editor (one is open by default) — for tests that work across several steps.</summary>
    private static void OpenAll<T>(IRenderedComponent<T> cut) where T : Microsoft.AspNetCore.Components.IComponent
    {
        for (var i = 0; i < cut.FindAll("[data-testid=pt-step-toggle]").Count; i++)
            if (cut.FindAll("[data-testid=pt-step-toggle]")[i].GetAttribute("aria-expanded") == "false") cut.FindAll("[data-testid=pt-step-toggle]")[i].Click();
    }

    /// <summary>Switches every open step editor to one part.</summary>
    private static void PartAll<T>(IRenderedComponent<T> cut, string part) where T : Microsoft.AspNetCore.Components.IComponent
    {
        for (var i = 0; i < cut.FindAll("[data-testid=pt-step]").Count; i++)
            cut.FindAll("[data-testid=pt-step]")[i].QuerySelector($"[data-testid=pt-step-part][data-part={part}]")?.Click();
    }

    /// <summary>Renders the steps with every editor open (most tests work across steps); <see cref="RenderAsIs"/> keeps the default.</summary>
    private IRenderedComponent<PerformanceScenarioSteps> Render(params HttpPerformanceStep[] steps)
    {
        var rendered = RenderAsIs(steps);
        OpenAll(rendered);
        return rendered;
    }

    private IRenderedComponent<PerformanceScenarioSteps> RenderAsIs(params HttpPerformanceStep[] steps)
    {
        _d = new PerformanceTestDefinition
        {
            Id = "def-1", Name = "Baseline", TargetOrigin = "https://app.example.test", EnvironmentName = "Development", TargetType = PerformanceTargetType.RestHttp,
            Scenario = new PerformanceScenario { Name = "Baseline check", Steps = [.. steps] },
        };
        IRenderedComponent<PerformanceScenarioSteps>? cut = null;
        cut = Render<PerformanceScenarioSteps>(p => p.Add(x => x.Definition, _d).Add(x => x.Assets, [UserId])
            .Add(x => x.OnChange, (PerformanceTestDefinition next) => { _d = next; cut?.Render(q => q.Add(x => x.Definition, next)); }));
        return cut;
    }

    private static List<string?> Order(IRenderedComponent<PerformanceScenarioSteps> cut) => cut.FindAll("[data-testid=pt-step]").Select(s => s.GetAttribute("data-step-id")).ToList();

    private static AngleSharp.Dom.IElement Row(IRenderedComponent<PerformanceScenarioSteps> cut, string variable)
    {
        if (cut.FindAll($"[data-testid=pt-step-mapping][data-variable={variable}]").Count == 0) { OpenAll(cut); PartAll(cut, "parameters"); }
        return cut.Find($"[data-testid=pt-step-mapping][data-variable={variable}]");
    }

    [Fact]
    public void A_dynamic_parameter_needs_a_runtime_value_source_and_offers_only_valid_sources()
    {
        var cut = Render(Step("children", "/api/brukere/{brukerId}/barn") with { QueryParameters = [new("correlationId", "{correlationId}")] });
        cut.Find("[data-testid=pt-step-status]").TextContent.Should().Be("Needs configuration");
        Row(cut, "brukerId").QuerySelector("[data-testid=pt-step-mapping-state]")!.TextContent.Should().Contain("Runtime value missing — choose a value source").And.NotContain("Test Data missing");
        cut.Find("[data-testid=pss-readiness]").TextContent.Should().Be("Needs configuration");
        cut.Find("[data-testid=pt-value-suggestion]").TextContent.Should().Contain("Existing Test Data match: user-01");

        Row(cut, "brukerId").QuerySelector("[data-testid=pt-step-map]")!.Click();
        cut.FindAll("[data-testid=pt-value-source-option]").Select(o => o.GetAttribute("data-source")).Should().Equal(["Fixed", "PreviousStep", "ScenarioVariable", "TestData"],
            "a business identifier is never generated; Test Data is optional");
        cut.FindAll("[data-testid=pt-value-source-option]").Single(o => o.GetAttribute("data-source") == "Fixed").HasAttribute("checked").Should().BeTrue("the dialog starts at a scenario value, not Test Data");
        cut.FindAll("[data-testid=pt-value-source-option]").Single(o => o.GetAttribute("data-source") == "TestData").Change(true);
        cut.FindAll("[data-testid=tdm-option]").Select(o => o.GetAttribute("value")).Should().Equal("user-01");
        cut.Find("[data-testid=tdm-option]").Change("user-01");
        cut.Find("[data-testid=tdm-use]").Click();
        _d.Scenario.Steps[0].DataMappings!.Single().Should().Match<ScenarioDataMapping>(m => m.Source == RuntimeValueSource.TestData && m.AssetAliases.Single() == "user-01");

        Row(cut, "correlationId").QuerySelector("[data-testid=pt-step-map]")!.Click();
        cut.FindAll("[data-testid=pt-value-source-option]").Select(o => o.GetAttribute("data-source")).Should().Contain("Generated");
        cut.FindAll("[data-testid=pt-value-source-option]").Single(o => o.GetAttribute("data-source") == "Generated").Change(true);
        cut.Find("[data-testid=pt-value-apply]").HasAttribute("disabled").Should().BeTrue("choose a generator first");
        cut.Find("[data-testid=pt-value-generator]").Change("Uuid");
        cut.Find("[data-testid=pt-value-apply]").Click();
        Row(cut, "correlationId").QuerySelector("[data-testid=pt-value-text]")!.TextContent.Should().Be("UUID");
        cut.Find("[data-testid=pt-step-status]").TextContent.Should().Be("Ready");
        Part(cut, "parameters");
        cut.Find("[data-testid=pt-step-values-summary]").TextContent.Should().Be("brukerId → Test Data · user-01 · correlationId → Generated value · UUID");
    }

    [Fact]
    public void Fixed_values_are_scenario_configuration_and_scenario_variables_are_reusable()
    {
        var cut = Render(Step("children", "/api/brukere/{brukerId}/barn"));
        Row(cut, "brukerId").QuerySelector("[data-testid=pt-step-map]")!.Click();
        cut.FindAll("[data-testid=pt-value-source-option]").Single(o => o.GetAttribute("data-source") == "Fixed").Change(true);
        cut.Find("[data-testid=pt-value-dialog]").TextContent.Should().Contain("not reusable Test Data assets");
        cut.Find("[data-testid=pt-value-fixed]").Input("b-1");
        cut.Find("[data-testid=pt-value-apply]").Click();
        _d.Scenario.Steps[0].DataMappings!.Single().FixedValue.Should().Be("b-1");

        cut.Find("[data-testid=pss-var-name]").Input("bruker");
        cut.Find("[data-testid=pss-var-value]").Input("b-2");
        cut.Find("[data-testid=pss-var-add]").Click();
        _d.Scenario.Variables!.Single().Should().Match<ScenarioVariable>(v => v.Name == "bruker" && v.Source == RuntimeValueSource.Fixed && v.Value == "b-2");
        Row(cut, "brukerId").QuerySelector("[data-testid=pt-step-map]")!.Click();
        cut.FindAll("[data-testid=pt-value-source-option]").Single(o => o.GetAttribute("data-source") == "ScenarioVariable").Change(true);
        cut.Find("[data-testid=pt-value-variable]").Change("bruker");
        cut.Find("[data-testid=pt-value-apply]").Click();
        Row(cut, "brukerId").QuerySelector("[data-testid=pt-value-text]")!.TextContent.Should().Be("{{bruker}}");
        cut.Find("[data-testid=pss-var-remove]").HasAttribute("disabled").Should().BeTrue("a used variable cannot be removed silently");
    }

    [Fact]
    public void Previous_step_output_extracts_from_an_earlier_step_and_never_a_later_one()
    {
        var cut = Render(Step("create", "/api/users"), Step("children", "/api/brukere/{id}/barn"));
        Row(cut, "id").QuerySelector("[data-testid=pt-step-map]")!.Click();
        cut.FindAll("[data-testid=pt-value-source-option]").Single(o => o.GetAttribute("data-source") == "PreviousStep").Change(true);
        cut.FindAll("[data-testid=pt-value-step] option").Select(o => o.TextContent).Should().Equal("Step 1 · create");
        cut.Find("[data-testid=pt-value-path]").GetAttribute("value").Should().Be("$.id");
        cut.Find("[data-testid=pt-value-apply]").Click();
        _d.Scenario.Steps[0].Outputs.Should().Equal(new StepOutput("id", "$.id"));
        _d.Scenario.Steps[1].DataMappings!.Single().Should().Match<ScenarioDataMapping>(m => m.Source == RuntimeValueSource.PreviousStep && m.FromStepId == "create" && m.Output == "id");
        Row(cut, "id").QuerySelector("[data-testid=pt-value-text]")!.TextContent.Should().Be("Step 1 · create · id");
        cut.Find("[data-testid=pss-dependencies]").TextContent.Should().Be("1 step-to-step reference");
        cut.FindAll("[data-testid=pt-step-status]").Select(s => s.TextContent).Should().Equal("Ready", "Ready");
        PartAll(cut, "response");
        cut.Find("[data-testid=pt-step-output]").TextContent.Should().Contain("1 mapping");

        var first = Render(Step("children", "/api/brukere/{id}/barn"), Step("create", "/api/users"));
        Row(first, "id").QuerySelector("[data-testid=pt-step-map]")!.Click();
        first.FindAll("[data-testid=pt-value-source-option]").Single(o => o.GetAttribute("data-source") == "PreviousStep").Change(true);
        first.Find("[data-testid=pt-value-no-earlier]").TextContent.Should().Contain("only come from a step that runs before this one");
    }

    private IRenderedComponent<PerformanceScenarioSteps> Dependent(params HttpPerformanceStep[] extra)
    {
        var producer = Step("create", "/api/users") with { Outputs = [new StepOutput("id", "$.id")] };
        var consumer = Step("children", "/api/brukere/{id}/barn") with
        {
            DataMappings = [new ScenarioDataMapping("id", [], TestDataDataType.SimpleValues, "path") { Source = RuntimeValueSource.PreviousStep, FromStepId = "create", Output = "id" }],
        };
        return Render([producer, consumer, .. extra]);
    }

    [Fact]
    public void Moving_a_consumer_before_its_producer_is_a_dependency_conflict_never_a_silent_reorder()
    {
        var cut = Dependent();
        cut.FindAll("[data-testid=pt-step-up]")[1].Click();
        Order(cut).Should().Equal("create", "children");
        var conflict = cut.Find("[data-testid=pss-conflict]");
        conflict.GetAttribute("role").Should().Be("alertdialog");
        conflict.TextContent.Should().Contain("Dependency conflict").And.Contain("Step 2 · children uses output id from Step 1 · create, which would execute later");
        cut.Find("[data-testid=pt-step-message]").TextContent.Should().Be("Dependency conflict: children was not moved.");
        cut.Find("[data-testid=pss-conflict-keep]").Click();
        cut.FindAll("[data-testid=pss-conflict]").Should().BeEmpty();
        Order(cut).Should().Equal("create", "children");

        // Drag the consumer onto the producer: same protection.
        cut.FindAll("[data-testid=pt-step-drag]")[1].TriggerEvent("ondragstart", new Microsoft.AspNetCore.Components.Web.DragEventArgs());
        cut.FindAll("[data-testid=pt-step]")[0].TriggerEvent("ondrop", new Microsoft.AspNetCore.Components.Web.DragEventArgs());
        cut.Find("[data-testid=pss-conflict]");
        cut.Find("[data-testid=pss-conflict-change]").Click();
        cut.Find("[data-testid=pt-value-dialog]").TextContent.Should().Contain("Configure value").And.Contain("id");
    }

    [Fact]
    public void Independent_steps_reorder_by_drag_or_keyboard_with_an_announcement_and_move_with_dependencies_keeps_order_valid()
    {
        var cut = Dependent(Step("health", "/api/status"));
        cut.FindAll("[data-testid=pt-step-drag]")[2].TriggerEvent("ondragstart", new Microsoft.AspNetCore.Components.Web.DragEventArgs());
        cut.FindAll("[data-testid=pt-step]")[0].TriggerEvent("ondrop", new Microsoft.AspNetCore.Components.Web.DragEventArgs());
        Order(cut).Should().Equal("health", "create", "children");
        cut.Find("[data-testid=pt-step-message]").TextContent.Should().Be("health moved to position 1 of 3.");
        _d.Scenario.Steps.Select(s => s.Id).Should().Equal(new[] { "health", "create", "children" }, "the draft keeps the new order");

        cut.FindAll("[data-testid=pt-step-down]")[0].Click();
        Order(cut).Should().Equal("create", "health", "children");
        cut.Find("[data-testid=pt-step-message]").TextContent.Should().Be("health moved to position 2 of 3.");
        JSInterop.Invocations.Where(i => i.Identifier == "birknextFocusElement").Select(i => i.Arguments[0]?.ToString()).Should().Contain("pt-step-down-health", "focus stays on the moved card's control");

        cut.FindAll("[data-testid=pt-step-up]")[2].Click();
        Order(cut).Should().Equal(new[] { "create", "children", "health" }, "children moved above health; still after its producer");
        cut.FindAll("[data-testid=pss-conflict]").Should().BeEmpty();
    }

    [Fact]
    public void Move_dependency_with_this_step_keeps_the_producer_directly_before_the_consumer()
    {
        var cut = Dependent(Step("health", "/api/status"));
        cut.FindAll("[data-testid=pt-step-drag]")[1].TriggerEvent("ondragstart", new Microsoft.AspNetCore.Components.Web.DragEventArgs());
        cut.FindAll("[data-testid=pt-step]")[0].TriggerEvent("ondrop", new Microsoft.AspNetCore.Components.Web.DragEventArgs());
        cut.Find("[data-testid=pss-conflict-move-with]").Click();
        Order(cut).Should().Equal("create", "children", "health");
        cut.FindAll("[data-testid=pt-step-status]").Select(s => s.GetAttribute("data-status")).Should().NotContain("Blocked");
    }

    [Fact]
    public void Deleting_or_disabling_a_producer_warns_and_never_leaves_a_silent_broken_reference()
    {
        var cut = Dependent();
        cut.FindAll("[data-testid=pt-step-enabled]")[0].Click();
        cut.Find("[data-testid=pss-dependents]").TextContent.Should().Contain("Step 1 · create is used by 1 mapping").And.Contain("Step 2 · children · id uses output id");
        _d.Scenario.Steps[0].Disabled.Should().BeFalse("nothing changes before confirmation");
        cut.Find("[data-testid=pss-dependents-confirm]").Click();
        cut.FindAll("[data-testid=pt-step-status]").Select(s => s.TextContent).Should().Equal("Disabled", "Needs configuration");
        cut.FindAll("[data-testid=pt-step]")[1].QuerySelector("[data-testid=pt-step-reasons]")!.TextContent.Should().Contain("which is disabled");

        var deleted = Dependent();
        deleted.FindAll("[data-testid=pt-remove-step]")[0].Click();
        deleted.Find("[data-testid=pss-dependents-confirm]").TextContent.Should().Be("Delete and clear those mappings");
        deleted.Find("[data-testid=pss-dependents-confirm]").Click();
        Order(deleted).Should().Equal("children");
        Row(deleted, "id").QuerySelector("[data-testid=pt-step-mapping-state]")!.TextContent.Should().Contain("Runtime value missing — choose a value source");
        deleted.Find("[data-testid=pt-step-message]").TextContent.Should().Contain("1 mapping that used its output now need a runtime value source");
    }

    [Fact]
    public void Duplicate_copies_configuration_with_a_new_identity_and_position()
    {
        var cut = Dependent();
        cut.FindAll("[data-testid=pt-step-duplicate]")[1].Click();
        _d.Scenario.Steps.Should().HaveCount(3);
        var copy = _d.Scenario.Steps[2];
        copy.Id.Should().NotBe("children");
        copy.Name.Should().Be("children-copy");
        copy.DataMappings.Should().BeEquivalentTo(_d.Scenario.Steps[1].DataMappings);
        cut.Find("[data-testid=pt-step-message]").TextContent.Should().Be("children duplicated as children-copy at position 3 of 3.");
    }

    [Fact]
    public void Default_authentication_is_inherited_or_overridden_per_step_and_never_a_token()
    {
        var cut = Render(Step("list", "/api/users"));
        Part(cut, "auth");
        cut.Find("[data-testid=pt-step-auth-summary]").TextContent.Should().Be("Inherit scenario default (None)");
        cut.Find("[data-testid=pss-default-auth]").Change("target-environment");
        _d.AuthenticationReference.Should().Be("target-environment");
        cut.Find("[data-testid=pt-step-auth-summary]").TextContent.Should().Be("Inherit scenario default (Target Environment authentication)");
        cut.Find("[data-testid=pt-step-auth]").Change("none");
        _d.Scenario.Steps[0].Authentication.Should().Be(StepAuthenticationMode.None);
        cut.Find("[data-testid=pt-step-auth-summary]").TextContent.Should().Be("None");
        System.Text.Json.JsonSerializer.Serialize(_d).Should().NotContainAny("Bearer ", "token");
    }

    [Fact]
    public void A_step_with_configured_Target_Environment_authentication_is_configured_but_execution_blocked_never_green_ready()
    {
        var cut = Render(Step("list", "/api/users"));
        cut.Find("[data-testid=pss-default-auth]").Change(PerformanceScenarioRuntime.TargetEnvironmentAuthentication);

        cut.Find("[data-testid=pt-step-status]").TextContent.Should().Be("Configured · execution blocked");
        cut.Find("[data-testid=pt-step]").GetAttribute("data-status").Should().Be("ExecutionBlocked");
        cut.Find("[data-testid=pss-readiness]").TextContent.Should().Be("Configured · execution blocked", "the scenario summary applies the same rule");
        cut.Find("[data-testid=pt-step-part-panel]").GetAttribute("data-part").Should().Be("auth", "the editor opens on the part that blocks execution");
        cut.FindAll("[data-testid=pt-step-dim]").Select(d => $"{d.GetAttribute("data-dim")}:{d.GetAttribute("data-state")}").Should().Equal("defined:Ready", "configured:NotApplicable", "executable:Blocked");
        cut.Find("[data-testid=pt-step-auth-source]").TextContent.Should().Be("Target Environment");
        Part(cut, "advanced");
        cut.Find("[data-testid=pt-step-dim-definition]").GetAttribute("data-state").Should().Be("Ready");
        cut.Find("[data-testid=pt-step-dim-values]").GetAttribute("data-state").Should().Be("NotApplicable");
        cut.Find("[data-testid=pt-step-dim-execution]").TextContent.Should().Contain("Blocked — authentication");
        Part(cut, "auth");
        cut.Find("[data-testid=pt-step-execution-reason]").TextContent.Should().Contain("Execution blocked — unattended authentication required");
        cut.Find("[data-testid=pt-step-auth-interactive]").TextContent.Should().Be("Yes");
        cut.Find("[data-testid=pt-step-auth-compatible]").TextContent.Should().Be("No");
        cut.Find("[data-testid=pt-step-auth-credential]").GetAttribute("data-state").Should().Be("Missing");
        cut.Markup.Should().NotContain("approved configuration", "configured authentication is never presented as approved for k6");

        cut.Find("[data-testid=pt-step-auth]").Change("none");
        cut.Find("[data-testid=pt-step-status]").TextContent.Should().Be("Ready");
        cut.FindAll("[data-testid=pt-step-auth-facts]").Should().BeEmpty();
    }

    [Fact]
    public void Endpoint_evidence_on_the_step_comes_from_the_same_history_view_as_readiness()
    {
        var seen = new DateTimeOffset(2026, 11, 3, 9, 27, 0, TimeSpan.Zero);
        var step = Step("barn", "/api/brukere/{brukerId}/barn") with
        {
            Source = new ScenarioStepSource { Origin = ScenarioOperationOrigin.Observed, Evidence = ScenarioEvidence.ObservedOnly, EvidenceIdentity = "GET /api/brukere/{id}/barn", ObservedCount = 2, ObservedLastSeen = seen },
        };
        var cut = Render(step);
        var run = new BirkNext.LocalHttpsProxy.EndpointDiscoveryRunSummary("live", "env", "app.example.test", seen, null, BirkNext.LocalHttpsProxy.EndpointDiscoveryRunSummary.InProgress, 1, 0, 1, 0);
        cut.Render(p => p.Add(x => x.History, new BirkNext.LocalHttpsProxy.EndpointObservationHistoryView("env", "app.example.test", true, run, null, [run],
            [new BirkNext.LocalHttpsProxy.ObservedOperationHistory { Identity = "GET /api/brukere/{id}/barn", LastObservedAt = seen, ObservationCount = 2, LastRunId = "live" }])));

        Part(cut, "advanced");
        var evidence = cut.Find("[data-testid=pt-step-endpoint-evidence]");
        evidence.GetAttribute("data-state").Should().Be("Current");
        evidence.TextContent.Should().Contain("Observed 2× · last observed 09:27 UTC");
        cut.Find("[data-testid=pt-step-endpoint-comparison]").TextContent.Should().Be("Reviewed against discovery 09:27 UTC · Latest discovery 09:27 UTC · No changes");

        cut.Render(p => p.Add(x => x.History, BirkNext.LocalHttpsProxy.EndpointObservationHistoryView.Empty("env", "app.example.test")));
        cut.Find("[data-testid=pt-step-endpoint-evidence]").TextContent.Should().Contain("Not in Endpoint Discovery history");
    }

    [Fact]
    public void Header_summarises_and_one_step_is_open_by_default_the_first_that_needs_attention()
    {
        var cut = RenderAsIs(Step("a", "/api/a"), Step("b", "/api/b/{id}"), Step("c", "/api/c"), Step("d", ""));
        cut.Find("[data-testid=pss-protocol]").TextContent.Should().Be("REST");
        cut.Find("[data-testid=pss-steps-count]").TextContent.Should().Be("4");
        cut.Find("[data-testid=pss-readiness]").TextContent.Should().Be("Blocked", "a step without a path cannot run");
        cut.FindAll("[data-testid=pt-step-toggle]").Select(b => b.GetAttribute("aria-expanded")).Should().Equal(["false", "true", "false", "false"],
            "one step is open: the first that needs attention");
        cut.FindAll("[data-testid=pt-step-status]").Select(s => s.TextContent).Should().Equal("Ready", "Needs configuration", "Ready", "Blocked");
        cut.FindAll("[data-testid=pt-step-issues]").Select(i => i.TextContent).Should().Equal("1 issue", "1 issue");
        cut.FindAll("[data-testid=pt-step-collapsed-reason]").Select(r => r.TextContent).Should().Equal("Step 4: enter the request path");
        cut.FindAll("[data-testid=pt-step-endpoint]")[1].TextContent.Should().Contain("/api/b/{id}", "the full path stays readable");
        cut.Find("[data-testid=pt-step-part-panel]").GetAttribute("data-part").Should().Be("parameters", "the open step starts on the part with the missing value");
        cut.FindAll("[data-testid=pt-runtime-values]").Should().ContainSingle();
        cut.FindAll("[data-testid=pt-request-card], [data-testid=pt-response-card]").Should().BeEmpty("request and response panels only on request");
        cut.FindAll("[data-testid=pt-step-toggle]")[1].Click();
        cut.FindAll("[data-testid=pt-step-toggle]")[1].GetAttribute("aria-expanded").Should().Be("false");
        cut.FindAll("[data-testid=pt-step-toggle]")[3].Click();
        cut.Find("[data-testid=pt-step-part-panel]").GetAttribute("data-part").Should().Be("request", "a missing path is a request issue");
    }

    [Fact]
    public void Use_asset_applies_the_suggested_test_data_only_when_the_tester_chooses_it()
    {
        var cut = Render(Step("children", "/api/brukere/{brukerId}/barn"));
        _d.Scenario.Steps[0].DataMappings.Should().BeNull("a suggestion is never applied silently");
        cut.Find("[data-testid=pt-value-suggestion-use]").Click();
        _d.Scenario.Steps[0].DataMappings!.Single().AssetAliases.Should().Equal("user-01");
        cut.FindAll("[data-testid=pt-value-suggestion]").Should().BeEmpty();
    }

    private static ScenarioDataMapping Auto(string variable, string value, string? type = null, bool review = false) => new(variable, [], TestDataDataType.SimpleValues, "path")
    {
        Source = RuntimeValueSource.AutoFilled, FixedValue = value, ValueOrigin = RuntimeValueOrigins.OpenApiExample, DeclaredType = type, NeedsReview = review,
    };

    [Fact]
    public void An_auto_filled_value_is_Ready_without_Test_Data_and_can_be_explicitly_used_as_Test_Data()
    {
        var cut = Render(Step("tilgang", "/tilgang/{id}") with { DataMappings = [Auto("id", "12345", "integer")] });
        cut.Find("[data-testid=pt-step-status]").TextContent.Should().Be("Ready");
        Row(cut, "id").QuerySelector("[data-testid=pt-value-source]")!.TextContent.Should().Be("Auto-filled · OpenAPI example");
        Row(cut, "id").QuerySelector("[data-testid=pt-value-text]")!.TextContent.Should().Be("12345");
        Row(cut, "id").QuerySelector("[data-testid=pt-step-mapping-state]")!.TextContent.Should().Be("Ready");
        cut.Find("[data-testid=pss-values-summary]").TextContent.Should().Be("1/1 resolved · Test Data 0 assets used");

        Row(cut, "id").QuerySelector("[data-testid=pt-step-promote]")!.Click();
        cut.Find("[data-testid=pt-value-testdata-why]").TextContent.Should().Contain("centrally managed or reused across scenarios").And.Contain("never promoted automatically");
        _d.Scenario.Steps[0].DataMappings!.Single().Source.Should().Be(RuntimeValueSource.AutoFilled, "opening the Test Data choice changes nothing");
        cut.Find("[data-testid=tdm-option]").Change("user-01");
        cut.Find("[data-testid=tdm-use]").Click();
        _d.Scenario.Steps[0].DataMappings!.Single().Should().Match<ScenarioDataMapping>(m => m.Source == RuntimeValueSource.TestData && m.AssetAliases.Single() == "user-01");
        cut.Find("[data-testid=pss-values-summary]").TextContent.Should().Be("1/1 resolved · Test Data 1 asset used");
    }

    [Fact]
    public void A_conflicting_suggested_value_needs_review_until_a_person_uses_it()
    {
        var cut = Render(Step("tilgang", "/tilgang/{id}") with { DataMappings = [Auto("id", "12345", "integer", review: true)] });
        cut.Find("[data-testid=pt-step-status]").TextContent.Should().Be("Needs configuration");
        Row(cut, "id").QuerySelector("[data-testid=pt-step-mapping-state]")!.TextContent.Should().Contain("Needs review");
        Row(cut, "id").QuerySelector("[data-testid=pt-step-confirm]")!.Click();
        _d.Scenario.Steps[0].DataMappings!.Single().Should().Match<ScenarioDataMapping>(m => !m.NeedsReview && m.FixedValue == "12345" && m.Source == RuntimeValueSource.AutoFilled);
        cut.Find("[data-testid=pt-step-status]").TextContent.Should().Be("Ready");
    }

    [Fact]
    public void A_fixed_value_is_validated_against_the_declared_type_and_enums_use_a_list()
    {
        var cut = Render(Step("tilgang", "/tilgang/{id}") with { DataMappings = [Auto("id", "12345", "integer")] });
        Row(cut, "id").QuerySelector("[data-testid=pt-step-map]")!.Click();
        cut.FindAll("[data-testid=pt-value-source-option]").Select(o => o.GetAttribute("data-source")).First().Should().Be("AutoFilled");
        cut.FindAll("[data-testid=pt-value-source-option]").Single(o => o.GetAttribute("data-source") == "Fixed").Change(true);
        cut.Find("[data-testid=pt-value-fixed]").Input("abc");
        cut.Find("[data-testid=pt-value-fixed-error]").TextContent.Should().Be("'abc' is not a valid integer");
        cut.Find("[data-testid=pt-value-fixed-error]").GetAttribute("role").Should().Be("alert");
        cut.Find("[data-testid=pt-value-apply]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=pt-value-fixed]").Input("777");
        cut.Find("[data-testid=pt-value-apply]").Click();
        _d.Scenario.Steps[0].DataMappings!.Single().Should().Match<ScenarioDataMapping>(m => m.Source == RuntimeValueSource.Fixed && m.FixedValue == "777" && m.DeclaredType == "integer");

        var withEnum = Render(Step("list", "/tilgang") with
        {
            QueryParameters = [new("status", "{status}")],
            DataMappings = [Auto("status", "AKTIV") with { In = "query", AllowedValues = ["AKTIV", "INAKTIV"], ValueOrigin = RuntimeValueOrigins.ContractDefault }],
        });
        Row(withEnum, "status").QuerySelector("[data-testid=pt-step-map]")!.Click();
        withEnum.FindAll("[data-testid=pt-value-source-option]").Single(o => o.GetAttribute("data-source") == "Fixed").Change(true);
        withEnum.FindAll("[data-testid=pt-value-fixed-enum] option").Select(o => o.TextContent).Should().Equal("Choose…", "AKTIV", "INAKTIV");
    }

    // ── NEXT55: compact rows and the focused step editor ─────────────────────────────────────────────────────────────────

    /// <summary>The primary scenario: two observed Autorisasjon GETs that need a Bearer token (observed) and a brukerId path value.</summary>
    private IRenderedComponent<PerformanceScenarioSteps> Autorisasjon(IReadOnlySet<string>? observed)
    {
        _d = new PerformanceTestDefinition
        {
            Id = "def-1", Name = "Baseline", TargetOrigin = "https://app.example.test", EnvironmentName = "Development", TargetType = PerformanceTargetType.RestHttp,
            Scenario = new PerformanceScenario { Name = "Baseline check", Steps = [Step("brukere", "/api/autorisasjon/v1/brukere/{brukerId}"), Step("barn", "/api/autorisasjon/v1/brukere/{brukerId}/barn")] },
        };
        IRenderedComponent<PerformanceScenarioSteps>? cut = null;
        cut = Render<PerformanceScenarioSteps>(p => p.Add(x => x.Definition, _d).Add(x => x.Assets, [UserId]).Add(x => x.ObservedAuthentication, observed)
            .Add(x => x.OnChange, (PerformanceTestDefinition next) => { _d = next; cut?.Render(q => q.Add(x => x.Definition, next)); }));
        return cut;
    }

    private static readonly IReadOnlySet<string> BearerObserved = new HashSet<string> { "GET /api/autorisasjon/v1/brukere/{id}", "GET /api/autorisasjon/v1/brukere/{id}/barn" };

    [Fact]
    public void Observed_bearer_and_a_missing_brukerId_are_named_on_compact_rows_and_the_editor_opens_on_authentication()
    {
        var cut = Autorisasjon(BearerObserved);

        cut.FindAll("[data-testid=pt-step-toggle]").Select(t => t.GetAttribute("aria-expanded")).Should().Equal("true", "false");
        cut.FindAll("[data-testid=pt-step-issues]").Should().HaveCount(2).And.OnlyContain(i => i.TextContent.EndsWith("issues") || i.TextContent.EndsWith("issue"));
        cut.FindAll("[data-testid=pt-step]")[0].QuerySelectorAll("[data-testid=pt-step-dim]").Select(d => $"{d.GetAttribute("data-dim")}:{d.GetAttribute("data-state")}")
            .Should().Contain(x => x.StartsWith("executable:") && !x.EndsWith("Ready"), "a step with unresolved authentication is never executable");
        cut.Find("[data-testid=pt-step-part-panel]").GetAttribute("data-part").Should().Be("auth", "authentication blocks first");
        cut.Find("[data-testid=pt-step-auth-requirement]").GetAttribute("data-state").Should().Be("Missing");
        cut.Find("[data-testid=pt-step-auth-problem]").TextContent.Should().Contain("execution stays blocked");
        cut.Find("[data-testid=pt-step-part][data-part=parameters]").GetAttribute("data-issue").Should().Be("true", "brukerId has no value source");
        cut.Find("[data-testid=pss-readiness]").GetAttribute("data-status").Should().NotBe("Ready");
        cut.FindAll("[data-testid=pt-request-card], [data-testid=pt-response-card]").Should().BeEmpty("request and response panels only on request");
    }

    [Fact]
    public void Unloaded_endpoint_evidence_is_never_read_as_no_authentication_required()
    {
        var cut = Autorisasjon(null);
        Part(cut, "auth");
        cut.Find("[data-testid=pt-step-auth-required]").TextContent.Should().Be("Not fully known — endpoint evidence not loaded");
        cut.Markup.Should().NotContain("No authentication observed or declared");
    }

    [Fact]
    public void Editor_parts_are_keyboard_tabs_and_switching_parts_or_steps_keeps_every_edit()
    {
        var cut = Autorisasjon(BearerObserved);
        var tabs = cut.Find("[data-testid=pt-step-parts]");
        tabs.GetAttribute("role").Should().Be("tablist");
        cut.FindAll("[data-testid=pt-step-part]").Select(t => t.TextContent.TrimEnd('!', ' ')).Should().Equal("Authentication", "Parameters & Test Data", "Request", "Response & extraction", "Advanced");
        cut.Find("[data-testid=pt-step-part][data-part=request]").Click();
        cut.Find("[data-testid=pt-step-edit]").Click();
        cut.Find("[data-testid=pt-step-expected]").Change("200, 204");
        tabs = cut.Find("[data-testid=pt-step-parts]");
        tabs.KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "ArrowRight" });
        cut.Find("[data-testid=pt-step-part-panel]").GetAttribute("data-part").Should().Be("response");
        cut.Find("[data-testid=pt-step-parts]").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Home" });
        cut.Find("[data-testid=pt-step-part-panel]").GetAttribute("data-part").Should().Be("auth");
        cut.FindAll("[data-testid=pt-step-toggle]")[1].Click();
        cut.FindAll("[data-testid=pt-step-toggle]")[1].Click();
        _d.Scenario.Steps[0].ExpectedStatusCodes.Should().Equal([200, 204], "an edit is applied when made — switching parts or steps never loses it");
        cut.FindAll("[data-testid=pt-step]")[0].QuerySelector("[data-testid=pt-step-part][data-part=request]")!.Click();
        cut.Find("[data-testid=pt-step-expected]").GetAttribute("value").Should().Be("200, 204");
    }

    [Fact]
    public void Configuring_authentication_and_a_shared_value_clears_the_flags_but_a_defined_step_is_still_not_called_executable_without_a_credential()
    {
        var cut = Autorisasjon(BearerObserved);
        cut.Find("[data-testid=pss-default-auth]").Change(PerformanceScenarioRuntime.TargetEnvironmentAuthentication);
        cut.Find("[data-testid=pt-step-part][data-part=auth]").GetAttribute("data-issue").Should().Be("true", "attended sign-in is configured but not usable by the load generator");
        cut.FindAll("[data-testid=pt-step]")[0].QuerySelector("[data-testid=pt-step-dim][data-dim=defined]")!.GetAttribute("data-state").Should().Be("Ready");
        cut.FindAll("[data-testid=pt-step]")[0].QuerySelector("[data-testid=pt-step-dim][data-dim=executable]")!.GetAttribute("data-state").Should().NotBe("Ready");
    }

    [Fact]
    public void A_step_with_a_missing_runtime_value_is_never_shown_as_executable_on_its_row()
    {
        var cut = Render(Step("b", "/api/b/{id}"));
        var chip = cut.Find("[data-testid=pt-step-dim][data-dim=executable]");
        chip.GetAttribute("data-state").Should().NotBe("Ready", "the same rule as the scenario status strip: defined, configured and a credential");
        chip.GetAttribute("title").Should().Contain("runtime values are resolved");
    }
}
