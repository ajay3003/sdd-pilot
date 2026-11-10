using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.BrowserCompanion;
using BirkNext.Api.Services.CriticalE2E;
using BirkNext.Api.Services.CriticalE2E.Suggestions;
using BirkNext.Api.Services.Integrations.ApplicationMessaging;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.BrowserCompanion;
using BirkNext.CriticalE2E;
using BirkNext.Integrations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BirkNext.Api.Tests.Services.CriticalE2E;

/// <summary>
/// Suggested E2E tests: UI facts from Razor source (never markup), suggestions in the existing flow vocabulary with expected results only from
/// explicit evidence, locator priority, duplicate detection against existing flows, verification states from the companion's checks, human
/// decisions (ambiguity never accepted), approval into a Diagnostic flow, stale verification across snapshots and regression impact.
/// The expected facts below are written from the fixture source by hand (the oracle), not read back from the extractor.
/// </summary>
public sealed class SuggestedTestsTests : IDisposable
{
    private const string ChildPage = """
        @page "/children/new"
        @attribute [Authorize(Roles = "Caseworker")]
        @* FR-012: a caseworker registers a child (synthetic fixture) *@
        <PageTitle>New child</PageTitle>
        <h1 data-testid="page-title">Register child</h1>
        <NavLink href="/children" data-testid="back-link">Back to list</NavLink>
        <EditForm Model="@model" OnValidSubmit="SaveAsync">
            <DataAnnotationsValidator />
            <ValidationSummary />
            <label for="child-name">Name</label>
            <InputText id="child-name" data-testid="child-name" @bind-Value="model.Name" />
            <label for="child-age">Age</label>
            <InputNumber id="child-age" @bind-Value="model.Age" />
            <label>Reference <InputText @bind-Value="model.ReferenceId" /></label>
            @if (showNotes)
            {
                <label for="notes">Notes</label>
                <InputTextArea id="notes" @bind-Value="model.Notes" />
            }
            <SchoolPicker @bind-Value="model.School" />
            <button type="submit" data-testid="save">Save</button>
            @foreach (var x in items) { <button type="button" @onclick="Remove">Remove</button> }
        </EditForm>
        @code {
            private ChildForm model = new();
            private bool showNotes;
        }
        """;

    private const string ListPage = """
        @page "/children"
        <h1>Children</h1>
        <a href="/children/new" data-testid="new-child">New child</a>
        """;

    private const string Model = """
        using System.ComponentModel.DataAnnotations;
        public sealed class ChildForm
        {
            [Required(ErrorMessage = "Name is required")]
            [StringLength(20)]
            public string Name { get; set; } = "";
            [Range(0, 18)]
            public int Age { get; set; }
            [Required]
            public string ReferenceId { get; set; } = "";
            public string Notes { get; set; } = "";
            public string School { get; set; } = "";
        }
        """;

    private static SourceUiSurface Surface(string childPage = ChildPage) => SourceUiExtractor.Extract(
        [new SourceFile("Web/Pages/NewChild.razor", childPage), new SourceFile("Web/Pages/Children.razor", ListPage)], [new SourceFile("Web/Models/ChildForm.cs", Model)]);

    [Fact]
    public void Ui_facts_come_from_source_without_keeping_markup()
    {
        var surface = Surface();
        var page = surface.Pages.Single(p => p.File == "Web/Pages/NewChild.razor");
        page.Routes.Should().Equal("/children/new");
        page.Title.Should().Be("New child");
        page.RequiresAuthorization.Should().BeTrue();
        page.Roles.Should().Equal("Caseworker");
        page.RequirementRefs.Should().Equal("FR-012");
        page.Module.Should().Be("Web");
        page.Forms.Should().ContainSingle(f => f.ModelType == "ChildForm" && f.SubmitHandler == "SaveAsync" && f.HasValidation);
        var name = page.Controls.Single(c => c.Binding == "Name");
        (name.Kind, name.TestId, name.ElementId, name.Label, name.Required, name.MaxLength, name.ErrorMessage, name.Role)
            .Should().Be((SourceUiControlKind.TextInput, "child-name", "child-name", "Name", true, 20, "Name is required", "textbox"));
        var age = page.Controls.Single(c => c.Binding == "Age");
        (age.Kind, age.RangeMin, age.RangeMax, age.Label).Should().Be((SourceUiControlKind.NumberInput, "0", "18", "Age"));
        page.Controls.Single(c => c.Binding == "ReferenceId").Label.Should().Be("Reference", "a wrapping label is associated");
        page.Controls.Single(c => c.Binding == "Notes").Conditional.Should().BeTrue("inside @if");
        page.Controls.Single(c => c.Kind == SourceUiControlKind.Button && c.Text == "Remove").Repeated.Should().BeTrue("inside @foreach");
        page.Controls.Should().ContainSingle(c => c.Kind == SourceUiControlKind.SubmitButton && c.TestId == "save" && c.Text == "Save");
        page.Controls.Should().ContainSingle(c => c.Kind == SourceUiControlKind.Link && c.Href == "/children" && c.Text == "Back to list");
        page.Controls.Should().ContainSingle(c => c.Kind == SourceUiControlKind.Component && c.Text == "SchoolPicker");
        page.Limitations.Should().Contain(l => l.Contains("SchoolPicker") && l.Contains("not known"));
        page.Controls.Should().NotContain(c => c.Text == "private ChildForm model = new();", "the @code block is not markup");
        JsonSerializer.Serialize(surface).Should().NotContain("<InputText").And.NotContain("@bind-Value", "facts only, never markup");
    }

    [Fact]
    public void Suggestions_use_explicit_evidence_for_expected_results_and_never_invent_one()
    {
        var all = SuggestionEngine.Generate("synth.ui", Guid.NewGuid(), Surface(), []);
        var opens = all.Single(s => s.Kind == SuggestionKind.PageOpens && s.Route == "/children/new");
        opens.Steps.Select(s => s.Action).Should().Equal(CompanionActionKind.Navigate, CompanionActionKind.AssertVisible);
        opens.Steps[1].Locators[0].Selector.Should().Match<CompanionSelector>(x => x.Kind == CompanionSelectorKind.TestId && x.Value == "page-title");
        opens.ExpectedBasis.Should().Contain("heading");
        opens.RequirementRefs.Should().Equal("FR-012");
        opens.Origins.Should().Contain(SuggestionOrigin.RequirementDerived);
        opens.Preconditions.Should().ContainSingle(p => p.Contains("Caseworker"));

        all.Should().ContainSingle(s => s.Kind == SuggestionKind.Navigation && s.Route == "/children/new" && s.Steps.Last().Expected == "/children");
        all.Should().ContainSingle(s => s.Kind == SuggestionKind.Navigation && s.Route == "/children" && s.Steps.Last().Expected == "/children/new");

        var required = all.Single(s => s.Kind == SuggestionKind.RequiredFields);
        required.Steps.Select(s => s.Action).Should().Equal(CompanionActionKind.Navigate, CompanionActionKind.Click, CompanionActionKind.AssertVisible);
        required.Steps.Last().Locators.Single().Selector.Value.Should().Be("ul.validation-errors", "Blazor's ValidationSummary rendering, stated as such");
        required.ExpectedBasis.Should().Contain("message text is not declared", "two required fields: only the summary's presence is asserted");

        var tooLong = all.Single(s => s.Kind == SuggestionKind.InvalidInput && s.Title.Contains("Name"));
        tooLong.Steps.Single(s => s.ControlId != null && s.Value?.Length == 21).ValueBasis.Should().Contain("violates exactly one constraint");
        tooLong.Steps.Last().Should().Match<SuggestedStep>(s => s.Action == CompanionActionKind.AssertVisible && s.IsFinalAssertion,
            "the length attribute declares no message: the Required message is never used for a length violation");
        tooLong.TestDataNeeds.Should().ContainSingle(n => n.Contains("Reference") && n.Contains("identifier-like"), "an identifier is never generated");
        all.Should().ContainSingle(s => s.Kind == SuggestionKind.InvalidInput && s.Title.Contains("Age") && s.Steps.Any(st => st.Value == "19"));

        var valid = all.Single(s => s.Kind == SuggestionKind.ValidSubmission);
        valid.Steps.Should().NotContain(s => s.IsFinalAssertion, "what a successful save shows is not in the source");
        valid.ExpectedResult.Should().BeNull();
        valid.Steps.Single(s => s.Description.Contains("Name")).Value.Should().Be("Synthetic");
        valid.Steps.Should().NotContain(s => s.Description.Contains("Notes"), "a conditional field is never filled automatically");
        valid.Steps.Last().Mutating.Should().BeTrue();
        SuggestionEngine.States(valid, false).Should().Contain([SuggestionState.NeedsExpectedResult, SuggestionState.NeedsTestData, SuggestionState.NeedsDomVerification]);

        all.SelectMany(s => s.Steps).SelectMany(s => s.Locators).Should().NotContain(l => l.Selector.Kind == CompanionSelectorKind.Css && (l.Selector.Value.Contains("nth") || l.Selector.Value.Contains(">")),
            "no positional or structural CSS");
        all.Select(s => s.SuggestionId).Should().OnlyHaveUniqueItems();
        SuggestionEngine.Generate("synth.ui", Guid.NewGuid(), Surface(), []).Select(s => s.SuggestionId).Should().Equal(all.Select(s => s.SuggestionId), "stable identity across snapshots");
    }

    [Fact]
    public void Locator_priority_is_test_id_then_role_name_then_label_then_static_id()
    {
        var control = new SourceUiControl { Kind = SourceUiControlKind.SubmitButton, Role = "button", Text = "Save", TestId = "save", ElementId = "save-btn" };
        SuggestionEngine.Locators(control).Select(l => l.Selector.Kind).Should().Equal(CompanionSelectorKind.TestId, CompanionSelectorKind.Role, CompanionSelectorKind.Css);
        var input = new SourceUiControl { Kind = SourceUiControlKind.TextInput, Role = "textbox", Label = "Name", ElementId = "n" };
        SuggestionEngine.Locators(input).Select(l => l.Basis).Should().Equal("associated label in source", "static id in source");
        SuggestionEngine.Locators(new SourceUiControl { Kind = SourceUiControlKind.TextInput, ElementId = "@id" }).Should().BeEmpty("an expression id is not stable");
        SuggestionEngine.ValidValue(new SourceUiControl { Kind = SourceUiControlKind.TextInput, InputType = "password", Label = "Access code" }).Should().BeNull("credentials are never generated");
        SuggestionEngine.ValidValue(new SourceUiControl { Kind = SourceUiControlKind.TextInput, Label = "Fødselsnummer" }).Should().BeNull("identifiers are never generated");
        SuggestionEngine.ValidValue(new SourceUiControl { Kind = SourceUiControlKind.TextInput, MinLength = 12, MaxLength = 14 }).Should().HaveLength(12);
    }

    [Fact]
    public void Existing_flows_are_reused_never_duplicated()
    {
        var all = SuggestionEngine.Generate("synth.ui", Guid.NewGuid(), Surface(), []);
        var opens = all.Single(s => s.Kind == SuggestionKind.PageOpens && s.Route == "/children/new");
        var full = new CriticalE2EFlowDefinition { Id = "flow-1", Name = "Register child smoke", Steps =
            [new() { StepId = "1", BrowserAction = CompanionActionKind.Navigate, Value = "/children/new" }, new() { StepId = "2", BrowserAction = CompanionActionKind.AssertVisible, Selector = new() { Kind = CompanionSelectorKind.TestId, Value = "page-title" } }] };
        SuggestionEngine.Covered(opens, [full]).Should().Match<TestSuggestion>(s => s.Coverage == SuggestionCoverage.AlreadyCovered && s.CoveredByFlowId == "flow-1");
        var required = all.Single(s => s.Kind == SuggestionKind.RequiredFields);
        var partial = full with { Steps = [.. full.Steps, new() { StepId = "3", BrowserAction = CompanionActionKind.Click, Selector = new() { Kind = CompanionSelectorKind.TestId, Value = "save" } }] };
        SuggestionEngine.Covered(required, [partial]).Should().Match<TestSuggestion>(s => s.Coverage == SuggestionCoverage.PartiallyCovered && s.CoverageNote!.Contains("extend"));
        var otherRoute = new CriticalE2EFlowDefinition { Id = "flow-2", Name = "FR-012 elsewhere", RequirementRef = "FR-012", Steps = [new() { StepId = "1", BrowserAction = CompanionActionKind.Navigate, Value = "/other" }] };
        SuggestionEngine.Covered(opens, [otherRoute]).Coverage.Should().Be(SuggestionCoverage.OverlapRequiresReview);
        SuggestionEngine.Covered(opens, []).Coverage.Should().Be(SuggestionCoverage.NewSuggestion);
    }

    [Fact]
    public void Verification_states_come_only_from_the_attended_page_checks()
    {
        var step = new SuggestedStep { StepId = "s2", ControlId = "c", Locators =
            [new(new() { Kind = CompanionSelectorKind.TestId, Value = "save" }, 1, ""), new(new() { Kind = CompanionSelectorKind.Role, Role = "button", Name = "Save" }, 2, "")] };
        CompanionSelectorCheck C(CompanionSelector s, int n, bool visible = true, string? limitation = null) => new() { Selector = s, MatchCount = n, Visible = visible, Limitation = limitation };
        var id = Guid.NewGuid();
        SuggestionEngine.Verify(step, [C(step.Locators[0].Selector, 0), C(step.Locators[1].Selector, 1)], "/x", "http://127.0.0.1", DateTimeOffset.UnixEpoch, id)
            .Should().Match<StepVerification>(v => v.State == LocatorVerification.VerifiedUnique && v.Selector!.Kind == CompanionSelectorKind.Role && v.SourceSnapshotId == id);
        SuggestionEngine.Verify(step, [C(step.Locators[0].Selector, 2), C(step.Locators[1].Selector, 0)], "/x", null, DateTimeOffset.UnixEpoch, id).State.Should().Be(LocatorVerification.Ambiguous);
        SuggestionEngine.Verify(step, [C(step.Locators[0].Selector, 1, visible: false)], "/x", null, DateTimeOffset.UnixEpoch, id).State.Should().Be(LocatorVerification.NotVisible);
        SuggestionEngine.Verify(step, [C(step.Locators[0].Selector, 0, limitation: "Not in the top frame; 1 frame(s) on the page are not searched.")], "/x", null, DateTimeOffset.UnixEpoch, id)
            .State.Should().Be(LocatorVerification.UnsupportedFrame);
        SuggestionEngine.Verify(step, [], "/x", null, DateTimeOffset.UnixEpoch, id).State.Should().Be(LocatorVerification.NotAssessed);
    }

    // ── Service: generate → verify (attended page) → decide → approve → regression impact ─────────────────────────────────────────────────

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bn-suggest-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase("suggest-" + Guid.NewGuid()).Options);
    public void Dispose() { _db.Dispose(); try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private Guid Snapshot(SourceUiSurface surface, DateTimeOffset at)
    {
        var s = new IqrSourceSnapshot { Id = Guid.NewGuid(), IntegrationId = IqrSourceStore.SourceAnalysisOwner, Archive = new SourceArchive("Synth.Ui.zip", new string('a', 64), 3), AnalyzedAt = at, UiSurface = surface };
        _db.IqrSourceSnapshots.Add(new IqrSourceSnapshotRecord { Id = s.Id, IntegrationId = IqrSourceStore.SourceAnalysisOwner, AnalyzedAt = at, EvidenceJson = JsonSerializer.Serialize(s, new JsonSerializerOptions(JsonSerializerDefaults.Web)) });
        _db.SaveChanges();
        return s.Id;
    }

    [Fact]
    public async Task Verify_decide_approve_and_impact_follow_the_attended_page_and_the_reviewer()
    {
        var flows = new CriticalE2EStore(Path.Combine(_dir, "e2e"), NullLogger<CriticalE2EStore>.Instance);
        var critical = new Mock<ICriticalE2EService>();
        critical.Setup(c => c.SaveFlow(It.IsAny<CriticalE2EFlowDefinition>())).Returns((CriticalE2EFlowDefinition f) => flows.Save(f));
        var companion = new Mock<IBrowserCompanionService>();
        var route = "/children/new";
        var caps = new List<string> { CompanionCapabilities.SelectorVerify };
        companion.Setup(c => c.Status("p1")).Returns(() => new BrowserCompanionStatus { Live = new BrowserCompanionLiveSession { ExtensionConnected = true, Capabilities = caps,
            LivePages = [new BrowserCompanionLivePage { PageId = "t1-a", Origin = "http://127.0.0.1:5097", Route = route, ContentScriptInstanceId = "a" }] } });
        CompanionAutomationCommand? sent = null;
        companion.Setup(c => c.Dispatch(It.IsAny<CompanionAutomationCommand>())).Callback((CompanionAutomationCommand c) => sent = c).Returns(new CompanionCommandDispatchResult { Accepted = true });
        // The attended page: page-title and save unique, the Name label unique, "Save" role ambiguous (two Save buttons on the page).
        companion.Setup(c => c.AwaitResultAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => new CompanionAutomationResult
        {
            Status = CriticalE2EStatus.Passed, ObservedRoute = route,
            SelectorChecks = [.. sent!.Selectors!.Select(s => new CompanionSelectorCheck { Selector = s, Visible = true, Enabled = true,
                MatchCount = s.Kind == CompanionSelectorKind.Role && s.Name == "Save" ? 2 : s.Kind == CompanionSelectorKind.TestId && s.Value == "back-link" ? 0 : 1 })],
        });
        var svc = new CriticalE2ESuggestionService(new IqrSourceStore(_db), flows, critical.Object, new CriticalE2ESuggestionStore(Path.Combine(_dir, "rev")), companion.Object, TimeProvider.System);
        var snap = Snapshot(Surface(), DateTimeOffset.UtcNow.AddHours(-1));

        var generated = (await svc.GenerateAsync(new(snap, "env-1"), default))!;
        generated.Summary.Suggestions.Should().Be(generated.Suggestions.Count);
        generated.Summary.NewSuggested.Should().Be(generated.Suggestions.Count);
        var opens = generated.Suggestions.Single(s => s.Kind == SuggestionKind.PageOpens && s.Route == route);
        opens.States.Should().Contain(SuggestionState.NeedsDomVerification);

        // The page must be the suggestion's route; nothing is dispatched otherwise.
        route = "/elsewhere";
        (await svc.VerifyAsync(new(snap, opens.SuggestionId, "env-1", "p1"), default)).Dispatched.Should().BeFalse();
        route = "/children/new";
        caps.Clear();
        (await svc.VerifyAsync(new(snap, opens.SuggestionId, "env-1", "p1"), default)).Message.Should().Contain("cannot verify selectors");
        caps.Add(CompanionCapabilities.SelectorVerify);
        var verified = await svc.VerifyAsync(new(snap, opens.SuggestionId, "env-1", "p1"), default);
        verified.Dispatched.Should().BeTrue();
        sent!.Action.Should().Be(CompanionActionKind.VerifySelectors);
        verified.Suggestion!.Verifications.Single().State.Should().Be(LocatorVerification.VerifiedUnique);

        var required = generated.Suggestions.Single(s => s.Kind == SuggestionKind.RequiredFields);
        var requiredVerified = (await svc.VerifyAsync(new(snap, required.SuggestionId, "env-1", "p1"), default)).Suggestion!;
        requiredVerified.Verifications.Single(v => v.StepId == "s2").State.Should().Be(LocatorVerification.VerifiedUnique, "test id 'save' is unique even though the role+name is ambiguous");
        requiredVerified.Verifications.Single(v => v.StepId == "s3").State.Should().Be(LocatorVerification.NotAssessed, "validation output appears only after submit");

        // Decisions: an ambiguous alternative is refused; a verified locator is accepted.
        var ambiguous = new CompanionSelector { Kind = CompanionSelectorKind.Role, Role = "button", Name = "Save" };
        (await svc.DecideAsync(new(snap, required.SuggestionId, "env-1", "s2", LocatorDecision.AlternativeChosen, ambiguous, "looks right"), default)).Problem.Should().Contain("several elements");
        (await svc.DecideAsync(new(snap, required.SuggestionId, "env-1", "s2", LocatorDecision.Accepted, null, null), default)).Suggestion!
            .States.Should().NotContain(SuggestionState.NeedsDomVerification);

        // Approval: needs every mapping; saves a Diagnostic flow tagged with its provenance; the suggestion becomes covered.
        (await svc.ApproveAsync(new(snap, opens.SuggestionId, "env-1", "p1", null, null), default)).Problems.Should().ContainSingle(p => p.Contains("no accepted locator"));
        await svc.DecideAsync(new(snap, opens.SuggestionId, "env-1", "s2", LocatorDecision.Accepted, null, null), default);
        var approved = await svc.ApproveAsync(new(snap, opens.SuggestionId, "env-1", "p1", null, null), default);
        approved.Problems.Should().BeEmpty();
        approved.Flow!.Should().Match<CriticalE2EFlowDefinition>(f => f.Kind == CriticalE2EFlowKind.Diagnostic && f.Source == CriticalE2EFlowSource.SuggestedFromSource && f.RequirementRef == "FR-012"
            && f.Tags.Contains($"suggestion:{opens.SuggestionId}") && f.FinalOutcome!.Kind == CriticalE2EOutcomeKind.ElementVisible && f.ConfigurationProblem() == null);
        var regenerated = (await svc.GenerateAsync(new(snap, "env-1"), default))!;
        regenerated.Suggestions.Single(s => s.SuggestionId == opens.SuggestionId).Should().Match<TestSuggestion>(s => s.Coverage == SuggestionCoverage.AlreadyCovered && s.States.Contains(SuggestionState.Approved));
        (await svc.ApproveAsync(new(snap, required.SuggestionId, "env-1", "p1", null, null), default)).Flow.Should().NotBeNull("required-field suggestion: mapped submit + validation convention");

        // A valid submission has no expected result until a reviewer states one.
        var valid = regenerated.Suggestions.Single(s => s.Kind == SuggestionKind.ValidSubmission);
        (await svc.ApproveAsync(new(snap, valid.SuggestionId, "env-1", "p1", null, null), default)).Problems.Should().Contain(p => p.Contains("No expected result"));

        // A changed snapshot: verifications become stale; the removed test id is a revalidation proposal; the approved flow is not touched.
        var changed = ChildPage.Replace("data-testid=\"page-title\"", "data-testid=\"heading\"", StringComparison.Ordinal);
        var snap2 = Snapshot(Surface(changed), DateTimeOffset.UtcNow);
        var next = (await svc.GenerateAsync(new(snap2, "env-1"), default))!;
        next.Suggestions.Single(s => s.SuggestionId == required.SuggestionId).Verifications.Should().OnlyContain(v => v.State == LocatorVerification.Stale);
        next.Impact.Should().Contain(i => i.Kind == SuggestionImpactKind.LocatorRevalidationNeeded && i.FlowId == approved.Flow.Id && i.Detail.Contains("page-title"));
        flows.Flow(approved.Flow.Id)!.Steps.Should().Contain(s => s.Selector != null && s.Selector.Value == "page-title", "approved tests are never rewritten");
    }
}
