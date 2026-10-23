using BirkNext.Integrations;
using BirkNext.LocalHttpsProxy;
using BirkNext.TestData;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Guided Setup is a stateless view over a journey's authoritative readiness (Continue resumes at the first required step that is not ready;
/// optional steps never block; setup complete ≠ runtime verified); the M01 context resolves from its owners before anything is typed, never
/// resolves approval and never shows a non-opaque value; a source-only security review never reads as a runtime breach; candidate search ranks by
/// whole words; the setup-profile panel previews before it applies and never preselects a conflict.
/// </summary>
public sealed class GuidedSetupAndSecurityContextTests : BunitContext
{
    private static JourneyPrerequisite P(string key, JourneyPrerequisiteCategory category, JourneyReadinessState state, bool mandatory = true) =>
        new(key, key, category, state, $"{key} detail", mandatory) { Source = "Owner" };

    private static IntegrationJourneyView Rapportmottak(params JourneyPrerequisite[] prerequisites) => new()
    {
        Journey = new IntegrationJourneyDefinition { JourneyId = "rapportmottak", DisplayName = "Rapportmottak" },
        Prerequisites = prerequisites, MatchedIntegrations = [new("report-intake", "dev:http:altinn", "Altinn report intake", IntegrationKind.HttpApi, true, "Configured")],
    };

    [Fact]
    public void Setup_IsReconstructedFromReadiness_ContinueResumesAtTheFirstUnresolvedRequiredStep()
    {
        var setup = JourneySetupModel.Build("skolenaervaer", Rapportmottak(
            P("integration:report-intake", JourneyPrerequisiteCategory.Integration, JourneyReadinessState.Ready),
            P("source:MU", JourneyPrerequisiteCategory.SourceEvidence, JourneyReadinessState.Ready),
            P("altinn-environment", JourneyPrerequisiteCategory.Executor, JourneyReadinessState.NotConfigured),
            P("client-secret", JourneyPrerequisiteCategory.Authentication, JourneyReadinessState.NotConfigured),
            P("app-policy", JourneyPrerequisiteCategory.Authorization, JourneyReadinessState.NotReady),
            P("synthetic-reporter", JourneyPrerequisiteCategory.SyntheticData, JourneyReadinessState.NotConfigured),
            P("report-verifier", JourneyPrerequisiteCategory.DownstreamVerification, JourneyReadinessState.NotAvailable, mandatory: false),
            P("trusted-environment", JourneyPrerequisiteCategory.TrustedEnvironment, JourneyReadinessState.NotReady)));

        setup.Steps.Select(step => step.Kind).Should().Equal(JourneySetupStepKind.Integration, JourneySetupStepKind.SourceEvidence, JourneySetupStepKind.Environment,
            JourneySetupStepKind.Authentication, JourneySetupStepKind.Authorization, JourneySetupStepKind.TestData, JourneySetupStepKind.RuntimeVerification, JourneySetupStepKind.ReadyToRun);
        setup.Steps.Should().NotContain(step => step.Kind == JourneySetupStepKind.Contract, "a step the journey does not need is not shown");
        setup.NextStep.Should().Be(JourneySetupStepKind.Environment, "Ready steps are skipped; Continue stops at the first required step that needs action");
        setup.Steps.Single(step => step.Kind == JourneySetupStepKind.RuntimeVerification).Status.Should().Be(JourneySetupStepStatus.Optional, "optional steps never block");
        setup.Steps.Single(step => step.Kind == JourneySetupStepKind.Authentication).ManualOnly.Should().BeTrue("a secret is a manual requirement");
        setup.Steps.Single(step => step.Kind == JourneySetupStepKind.Authorization).ManualOnly.Should().BeTrue();
        (setup.RequiredReady, setup.RequiredSteps).Should().Be((2, 7));
        JourneySetupPresentation.Progress(setup).Should().Be("Rapportmottak · 2 of 7 required setup steps ready");
        setup.Execution.Should().Be("Blocked", "setup and execution are separate answers");
        setup.Steps.Should().OnlyContain(step => !step.Facts.RuntimeVerified, "nothing in setup is runtime verified");
    }

    [Fact]
    public void SetupComplete_IsNotRuntimeVerified_AndActionsKeepTheirContext()
    {
        var view = Rapportmottak(P("integration:report-intake", JourneyPrerequisiteCategory.Integration, JourneyReadinessState.Ready),
            P("source:MU", JourneyPrerequisiteCategory.SourceEvidence, JourneyReadinessState.NotReady)) with { CanRun = false };
        var setup = JourneySetupModel.Build("skolenaervaer", view);
        var source = setup.Steps.Single(step => step.Kind == JourneySetupStepKind.SourceEvidence);
        JourneySetupPresentation.Action(source, view, "dev").Href.Should().Be("/integration-quality-review?view=setup-evidence&integration=dev%3Ahttp%3Aaltinn&evidence=bind",
            "the binding editor opens on this journey's own integration");
        JourneySetupPresentation.Action(setup.Steps.First(), view, "dev").Href.Should().Contain("focus=integration%3Adev%3Ahttp%3Aaltinn");

        var complete = JourneySetupModel.Build("skolenaervaer", Rapportmottak(P("integration:report-intake", JourneyPrerequisiteCategory.Integration, JourneyReadinessState.Ready)) with { CanRun = true });
        complete.SetupComplete.Should().BeTrue();
        JourneySetupPresentation.Completion(complete).Should().Be("Setup complete · not runtime verified");
    }

    [Fact]
    public void SetupPanel_RendersStepsWithSeparateFacts_AndContinueFocusesTheNextStep()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var view = Rapportmottak(P("integration:report-intake", JourneyPrerequisiteCategory.Integration, JourneyReadinessState.Ready),
            P("synthetic-reporter", JourneyPrerequisiteCategory.SyntheticData, JourneyReadinessState.NotConfigured));
        var cut = Render<JourneySetupPanel>(p => p.Add(x => x.PackId, "skolenaervaer").Add(x => x.View, view).Add(x => x.ProfileId, "dev"));
        cut.Find("[data-testid=js-progress]").TextContent.Should().Be("Rapportmottak · 1 of 2 required setup steps ready");
        cut.FindAll("[data-testid=js-step]").Select(step => step.GetAttribute("data-status")).Should().Equal("Ready", "NeedsAction", "NotApplicable");
        cut.FindAll("[data-testid=js-step-facts]")[0].TextContent.Should().Contain("Configured").And.Contain("Source verified").And.Contain("Runtime verified");
        cut.Find("[data-testid=js-continue]").Click();
        JSInterop.VerifyInvoke("birknextFocusElement").Arguments.Should().Equal(["setup-rapportmottak-testdata"]);
    }

    // ── M01 evidence-first context ─────────────────────────────────────────────────────────────────────────────────

    private static TestDataAssetSummary Asset(string alias, string capability, string? value = null, TestDataApproval approval = TestDataApproval.Approved) => new()
    {
        Id = alias, Alias = alias, Kind = TestDataKind.EntityReference, Approval = approval, Capabilities = [capability], Source = "Approved catalog", DisplayValue = value,
    };

    [Fact]
    public void M01Context_ResolvesFromOwners_OpaqueOnly_NeverApproval()
    {
        var profile = new FrontendAnalysisProfile { Id = "dev", Name = "Development", EnvironmentType = FrontendEnvironmentType.Development, GraphQlEndpoint = "https://person.example.test/graphql" };
        var discovery = new EndpointDiscoverySnapshot();
        discovery.Shared.Add(new ObservedNetworkEndpoint { Category = ObservedTrafficCategory.GraphQl, Scheme = "https", Host = "person.example.test", Path = "/graphql", Method = "POST" });
        var level2 = Guid.NewGuid().ToString();
        var result = ClassificationContextResolution.Resolve(profile, discovery,
        [
            Asset("child-kode7", ClassificationContextResolution.LevelCapability(2), level2),
            Asset("child-kode6", ClassificationContextResolution.LevelCapability(3), "12345678901"),
            Asset("unapproved", ClassificationContextResolution.LevelCapability(0), Guid.NewGuid().ToString(), TestDataApproval.Unapproved),
            Asset("user-no-graded-access", ClassificationContextResolution.UnauthorizedCapability),
        ], [0, 2, 3]);

        Row(result, "environment").Should().Be((ClassificationContextResolution.RowState.Ready, "DEV"));
        Row(result, "endpoint").Should().Be((ClassificationContextResolution.RowState.Ready, "https://person.example.test/graphql"));
        result.Rows.Single(r => r.Key == "endpoint").Source.Should().Be("Target Environment · confirmed by Endpoint Discovery");
        Row(result, "child-0").State.Should().Be(ClassificationContextResolution.RowState.Missing, "an unapproved asset is never used");
        result.Children.Should().ContainSingle().Which.Should().Be(new KeyValuePair<int, string>(2, level2));
        Row(result, "child-3").State.Should().Be(ClassificationContextResolution.RowState.Missing, "a non-opaque value is never used");
        string.Join(" ", result.Rows.Select(r => r.Display)).Should().NotContain("12345678901");
        Row(result, "approval").State.Should().Be(ClassificationContextResolution.RowState.RequiresApproval);
        result.Summary.Should().Be("4 resolved · 3 missing · 0 conflict · 1 requires approval");

        var production = ClassificationContextResolution.Resolve(new FrontendAnalysisProfile { Id = "prod", Name = "Production", EnvironmentType = FrontendEnvironmentType.Production }, null, [], []);
        Row(production, "environment").State.Should().Be(ClassificationContextResolution.RowState.NotAllowed, "Production is never runnable");
        discovery.Shared.Add(new ObservedNetworkEndpoint { Category = ObservedTrafficCategory.GraphQl, Scheme = "https", Host = "other.example.test", Path = "/graphql", Method = "POST" });
        Row(ClassificationContextResolution.Resolve(profile, discovery, [], []), "endpoint").State.Should().Be(ClassificationContextResolution.RowState.Conflict, "evidence that disagrees is never silently chosen");
    }

    private static (ClassificationContextResolution.RowState State, string? Display) Row(ClassificationContextResolution.Result result, string key) =>
        result.Rows.Single(r => r.Key == key) is var row ? (row.State, row.Display) : default;

    [Fact]
    public void M01ApprovalFingerprint_ChangesWithTheContext()
    {
        var context = new ClassificationTestContext { Environment = "DEV", GraphQlEndpoint = "https://a.test/graphql", UnauthorizedIdentityLabel = "u" };
        ClassificationContextResolution.Fingerprint(context).Should().Be(ClassificationContextResolution.Fingerprint(context with { }));
        ClassificationContextResolution.Fingerprint(context).Should().NotBe(ClassificationContextResolution.Fingerprint(context with { GraphQlEndpoint = "https://b.test/graphql" }));
    }

    [Fact]
    public void SourceOnlyReview_ReadsAsSourceReview_WithCategoriesNeverMerged()
    {
        var result = new ClassificationReviewResult
        {
            Findings =
            [
                new() { Severity = ClassificationSeverity.High, Title = "GraphQL hentBarnRegistreringIdFraEksternId has no authorization check" },
                new() { Severity = ClassificationSeverity.Medium, Title = "Grant graded access", Detail = "No grant operation was found." },
                new() { Severity = ClassificationSeverity.Low, Title = "Kode 6/7 naming differs" },
                new() { Severity = ClassificationSeverity.Info, Title = "Context" },
            ],
        };
        ClassificationPresentation.Headline(result).Should().Be(
            "Source security review completed · 1 security concern · 1 evidence gap · 1 review warning · runtime: not tested · 0 live observations");
        ClassificationPresentation.Category(result.Findings[0]).Should().Be("Security finding", "a concrete source finding is never downgraded because runtime is not tested");
        ClassificationPresentation.Headline(result).Should().NotContain("Issue detected");
    }

    [Fact]
    public void CandidateSearch_RanksWholeWords_AndAShortQueryNeverMatchesInsideAWord()
    {
        var mu = new SourceIntegrationCandidate { Id = "mu", ChannelName = "mu-inbound", ProducerComponent = "Meldingsutveksleren (MU)", ConsumerCandidate = "MM" };
        var kommune = new SourceIntegrationCandidate { Id = "k", ChannelName = "kommune.events", ProducerComponent = "Kommune.Api" };
        IntegrationsOverviewPresentation.SearchRank(mu, "MU").Should().Be(0);
        IntegrationsOverviewPresentation.SearchRank(kommune, "MU").Should().BeNull();
        IntegrationsOverviewPresentation.SearchRank(kommune, "Kommune.Api").Should().NotBeNull();
        IntegrationsOverviewPresentation.SearchRank(mu, "meldings").Should().Be(1, "a word prefix ranks after an exact word");
    }

    // ── Setup profile panel ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ProfilePanel_PreviewsFirst_PreselectsOnlySafeNewValues_AndAppliesOnlyTheSelection()
    {
        var journeys = new Mock<IIntegrationJourneysApiService>();
        var preview = new SetupProfilePreview
        {
            Valid = true, Version = 1, ProfileName = "Dev",
            Changes =
            [
                new() { Id = "a", Setting = "Endpoint", Classification = SetupChangeClassification.New, SafeToApply = true, Preselected = true, Owner = "Target Environment → Integrations" },
                new() { Id = "b", Setting = "Producer", Current = "explicit", Proposed = "other", Classification = SetupChangeClassification.Conflict, Owner = "Target Environment → Integrations" },
                new() { Id = "c", Setting = "Create integration", Classification = SetupChangeClassification.RequiresConfirmation, SafeToApply = true, RequiresConfirmation = true },
            ],
            Issues = [new("journeys.x.clientSecret", SetupIssueSeverity.Rejected, "Field \"clientSecret\" cannot be imported because secrets are not allowed in setup profiles.")],
            Summary = new(3, 1, 0, 1, 1, 0, 0, 1, 4),
        };
        journeys.Setup(j => j.PreviewSetupProfileAsync("dev", "skolenaervaer", It.IsAny<SetupProfileRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActiveEventApiResult<SetupProfilePreview>(preview, null));
        SetupApplyRequest? applied = null;
        journeys.Setup(j => j.ApplySetupProfileAsync("dev", "skolenaervaer", It.IsAny<SetupApplyRequest>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, SetupApplyRequest, CancellationToken>((_, _, request, _) => applied = request)
            .ReturnsAsync(new ActiveEventApiResult<SetupApplyResult>(new SetupApplyResult { Items = [new("a", "Endpoint", SetupApplyOutcome.Applied, "set")], ConflictsLeft = 1 }, null));
        Services.AddSingleton(journeys.Object);
        var cut = Render<SetupProfilePanel>(p => p.Add(x => x.EnvironmentId, "dev").Add(x => x.PackId, "skolenaervaer").Add(x => x.Mode, SetupProfileMode.Import));

        cut.FindComponent<Microsoft.AspNetCore.Components.Forms.InputFile>().UploadFiles(InputFileContent.CreateFromText("version: 1\nprofile:\n  name: \"Dev\"\n", "dev.yaml"));
        cut.WaitForAssertion(() => cut.Find("[data-testid=sp-validation]").GetAttribute("data-valid").Should().Be("true"));
        cut.Find("[data-testid=sp-issue]").TextContent.Should().StartWith("Rejected field");
        cut.Find("[data-testid=sp-summary]").TextContent.Should().Contain("0 secrets accepted");
        var rows = cut.FindAll("[data-testid=sp-change]");
        rows.Single(r => r.GetAttribute("data-id") == "b").QuerySelector("[data-testid=sp-change-select]").Should().BeNull("a conflict can never be selected");
        rows.Single(r => r.GetAttribute("data-id") == "a").QuerySelector("[data-testid=sp-change-select]")!.HasAttribute("checked").Should().BeTrue();
        rows.Single(r => r.GetAttribute("data-id") == "c").QuerySelector("[data-testid=sp-change-select]")!.HasAttribute("checked").Should().BeFalse("confirmation items are never preselected");
        journeys.Verify(j => j.ApplySetupProfileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<SetupApplyRequest>(), It.IsAny<CancellationToken>()), Times.Never, "nothing is applied on upload");

        cut.Find("[data-testid=sp-apply]").Click();
        cut.WaitForAssertion(() => applied.Should().NotBeNull());
        applied!.SelectedChangeIds.Should().Equal(["a"]);
        cut.Find("[data-testid=sp-apply-result]").TextContent.Should().Contain("1 settings applied").And.Contain("1 conflicts left unchanged").And.Contain("0 secrets imported").And.Contain("Active execution unchanged");
    }

    [Fact]
    public void ConsumerProjection_ReloadsWhenANewRunIsRecorded()
    {
        var api = new Mock<IClassificationReviewApiService>();
        api.Setup(a => a.ConsumersAsync("dev", It.IsAny<CancellationToken>())).ReturnsAsync([]);
        Services.AddSingleton(api.Object);
        var cut = Render<SecurityConsumerProjection>(p => p.Add(x => x.EnvironmentId, "dev").Add(x => x.RunId, (Guid?)null));
        cut.Render(p => p.Add(x => x.RunId, Guid.NewGuid()));
        api.Verify(a => a.ConsumersAsync("dev", It.IsAny<CancellationToken>()), Times.Exactly(2), "a new run is never hidden behind an older projection");
    }
}
