using System.Net;
using System.Text.RegularExpressions;
using BirkNext.Integrations;
using BirkNext.TestData;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// The shared presentation vocabulary across Target Environment, Integration Quality Review, Test Data and test packs: status dimensions are
/// never collapsed into one badge (configured ≠ verified ≠ observed ≠ passed; ready ≠ passed; approved ≠ valid), blockers group by their own
/// category metadata, Target Environment tabs are grouped without becoming different tabs, and generic UI code names no domain or project.
/// </summary>
public sealed class ReconciliationSemanticsTests : BunitContext
{
    private static IntegrationJourneyView View(JourneyMaturity maturity, IReadOnlyList<JourneyPrerequisite> prerequisites, bool canRun = false, IReadOnlyList<string>? runnable = null,
        IReadOnlyList<JourneyIntegrationRequirement>? integrations = null) => new()
    {
        Journey = new IntegrationJourneyDefinition { JourneyId = "j", Integrations = integrations ?? [new("in", "In", IntegrationKind.HttpApi, "A", "B", "p")] },
        Maturity = maturity, Prerequisites = prerequisites, CanRun = canRun, RunnableScenarios = runnable ?? [],
    };

    private static JourneyPrerequisite Source(JourneyReadinessState state) => new("source:x", "X source", JourneyPrerequisiteCategory.SourceEvidence, state, "detail");

    // ── Status dimensions stay distinct ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Configured_is_not_source_verified()
    {
        var view = View(new(Configured: true, SourceVerified: false, Executable: false, RuntimeVerified: false, ""), [Source(JourneyReadinessState.NotReady)]);
        TestPackPresentation.Configuration(view).Should().Be("Configured");
        TestPackPresentation.SourceEvidence(view).Should().Be("Missing");
    }

    [Fact]
    public void Source_verified_is_not_observed_and_ready_is_not_passed()
    {
        var view = View(new(true, true, true, RuntimeVerified: false, ""), [Source(JourneyReadinessState.Ready)], canRun: true) with
        {
            StepStatus = [new("s", Configured: true, Observable: false, Verifiable: false, JourneyStepState.NotAssessed, "No observer")],
        };
        TestPackPresentation.SourceEvidence(view).Should().Be("Verified");
        TestPackPresentation.ExecutionReadiness(view).Should().Be("Ready");
        view.StepStatus.Should().OnlyContain(step => step.State == JourneyStepState.NotAssessed, "ready to run says nothing about an observed or passed step");
    }

    [Fact]
    public void Partial_source_evidence_and_a_runnable_read_only_scenario_stay_separate_facts()
    {
        var view = View(new(true, false, false, false, ""), [Source(JourneyReadinessState.Ready), Source(JourneyReadinessState.NotReady) with { Key = "source:y" }], runnable: ["read-only"]);
        TestPackPresentation.SourceEvidence(view).Should().Be("Partial");
        TestPackPresentation.ExecutionReadiness(view).Should().Be("Ready", "one runnable scenario is execution readiness");
        TestPackPresentation.Configuration(View(new(false, false, false, false, ""), [], integrations: [])).Should().Be("Not applicable");
    }

    [Fact]
    public void Blockers_group_by_category_metadata_once_each_with_their_source()
    {
        JourneyPrerequisite P(string key, JourneyPrerequisiteCategory category, string? source = null) =>
            new(key, key, category, JourneyReadinessState.NotReady, "detail") { Source = source };
        var grouped = TestPackPresentation.Grouped(
        [
            P("secret", JourneyPrerequisiteCategory.Authentication, "Local execution config"), P("trusted", JourneyPrerequisiteCategory.TrustedEnvironment),
            P("reporter", JourneyPrerequisiteCategory.SyntheticData, "Test Data"), P("xsd", JourneyPrerequisiteCategory.Contract), P("observer", JourneyPrerequisiteCategory.DownstreamVerification),
            P("secret", JourneyPrerequisiteCategory.Authentication), P("policy", JourneyPrerequisiteCategory.Authorization), P("intake", JourneyPrerequisiteCategory.Integration),
        ]);
        grouped.Select(g => g.Group).Should().Equal("Environment", "Integration", "Authentication", "Authorization", "Test Data", "Contracts", "Observability");
        grouped.SelectMany(g => g.Items).Select(item => item.Key).Should().OnlyHaveUniqueItems("a blocker appears exactly once");
        TestPackPresentation.Source(grouped.Single(g => g.Group == "Authentication").Items.Single()).Should().Be("Local execution config");
        TestPackPresentation.Source(grouped.Single(g => g.Group == "Environment").Items.Single()).Should().Be("BirkNext");
        foreach (var category in Enum.GetValues<JourneyPrerequisiteCategory>())
            TestPackPresentation.GroupOrder.Should().Contain(TestPackPresentation.Group(category), "every category has a reading group");
    }

    [Fact]
    public void Approved_is_not_valid_and_an_invalid_negative_fixture_is_a_valid_domain_state()
    {
        var negative = new TestDataAssetSummary
        {
            Alias = "bad-report", Approval = TestDataApproval.Approved, Validation = TestDataValidation.Invalid, Capabilities = [TestDataPresentation.NegativeTestCapability],
        };
        TestDataPresentation.ApprovalLabel(negative.Approval).Should().Be("Approved");
        TestDataPresentation.ValidationLabel(negative).Should().Be("Invalid");
        TestDataPresentation.IntentLabel(negative).Should().Be("Negative test");
        TestDataPresentation.ValidationTone(negative).Should().Be("neutral", "invalid is what a negative fixture is for, never styled as an error");
        TestDataPresentation.ValidationTone(negative with { Capabilities = [] }).Should().Be("attention");
    }

    [Fact]
    public void A_source_states_its_own_status_and_the_generic_fallback_never_says_unavailable_for_an_empty_source()
    {
        var plain = new TestDataSourceSummary("x", "X", "", 0, 0, TestDataSourceStatus.Empty);
        TestDataPresentation.SourceStatusLabel(plain).Should().Be("Configured · 0 assets");
        TestDataPresentation.SourceStatusLabel(plain with { StatusLabel = "No registered fixtures" }).Should().Be("No registered fixtures");
        TestDataPresentation.SourceStatusLabel(plain with { Status = TestDataSourceStatus.Unavailable, StatusLabel = "Registered fixtures" }).Should().Be("Unavailable", "a provider label never hides an unreadable source");
    }

    [Fact]
    public void Review_readiness_has_exactly_three_run_states()
    {
        IntegrationReviewPrerunPresentation.ReadinessLabel(new IntegrationReviewReadiness { CanRun = false, Headline = "Cannot run" }).Should().Be("Cannot run");
        IntegrationReviewPrerunPresentation.ReadinessLabel(new IntegrationReviewReadiness { CanRun = true, Headline = "Can run with limitations" }).Should().Be("Can run with limitations");
        IntegrationReviewPrerunPresentation.ReadinessLabel(new IntegrationReviewReadiness { CanRun = true, Headline = "Ready" }).Should().Be("Ready to run");
        IntegrationReviewPrerunPresentation.ReadinessLabel(new IntegrationReviewReadiness { CanRun = false, Headline = "Can run with limitations" }).Should().Be("Cannot run", "the Run gate decides, never a stale headline");
    }

    // ── Target Environment ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Target_environment_tabs_are_grouped_once_each_in_tab_order()
    {
        BirkNext.Web.Components.FrontendAnalysisSettings.TabGroups.Select(group => group.Group).Should().Equal("General", "Discovery", "Quality settings", "Integrations");
        BirkNext.Web.Components.FrontendAnalysisSettings.TabGroups.SelectMany(group => group.Keys).Should().Equal(DocumentationCatalog.TargetEnvironmentTabDefinitions.Select(tab => tab.Key),
            "every tab is in exactly one group, and the visual order is the keyboard order");
    }

    [Fact]
    public void Target_environment_state_shows_four_separate_dimensions_and_trust_from_the_backend()
    {
        var journeys = new Mock<IIntegrationJourneysApiService>();
        journeys.Setup(x => x.PacksAsync("dev", It.IsAny<CancellationToken>())).ReturnsAsync(new ActiveEventApiResult<IReadOnlyList<IntegrationJourneyPackView>>(
            [new IntegrationJourneyPackView { EnvironmentTrusted = false, EnvironmentDetail = "Not enrolled in TargetEnvironments:Trusted." }], null, HttpStatusCode.OK));
        Services.AddSingleton(journeys.Object);
        var cut = Render<TargetEnvironmentStateSummary>(p => p.Add(x => x.Profile, new FrontendAnalysisProfile { Id = "dev", TargetUrl = "https://example.invalid" })
            .Add(x => x.IsActive, true).Add(x => x.ManualVerificationRequired, true));

        cut.FindAll("[data-testid=te-state] dt").Select(dt => dt.TextContent).Should().Equal("Environment state", "Configuration", "Verification", "Execution trust");
        cut.Find("[data-testid=te-state-environment]").TextContent.Should().Be("Active");
        cut.Find("[data-testid=te-state-configuration]").TextContent.Should().Be("Configured");
        cut.Find("[data-testid=te-state-verification]").TextContent.Should().Be("Manual verification required", "configured is not verified");
        cut.WaitForAssertion(() => cut.Find("[data-testid=te-state-trust]").TextContent.Should().Be("Not trusted", "active and configured do not make an environment trusted"));
    }

    // ── Genericity ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Generic_ui_code_names_no_domain_or_project()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "BirkNext.Web");
        if (!Directory.Exists(root)) return; // Source-level check runs where the repository sources are present.
        var files = new[]
        {
            "Services/TestPackPresentation.cs", "Components/IntegrationJourneyPanel.razor", "Components/IqrSourceSetup.razor", "Components/TargetEnvironmentStateSummary.razor",
            "Components/IntegrationsPane.razor", "Pages/TestDataPage.razor", "Services/TestDataPresentation.cs", "Components/TopologyPathsPanel.razor",
            "Services/IntegrationReviewPrerunPresentation.cs", "Pages/IntegrationQualityReview.razor", "Services/SourceGuidancePresentation.cs", "Components/SourceGuidanceList.razor",
            "Components/SourceValueHint.razor", "Components/SourceDomains/ConfigurationWorkspace.razor",
            "Components/SourceDomains/EnvironmentValueHint.razor", "Services/EnvironmentValuePresentation.cs",
        };
        foreach (var file in files)
        {
            var text = File.ReadAllText(Path.Combine(root, file));
            var code = Regex.Replace(text, @"@\*.*?\*@", "", RegexOptions.Singleline);
            code = string.Join('\n', code.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            Regex.IsMatch(code, @"Skolen|skolenaervaer|Rapportmottak|Periodeavslutning|Utdanningsdata|Altinn|TT02|M2LB|Meldingsutveksler|SkoleAdapter|Bufdata", RegexOptions.IgnoreCase)
                .Should().BeFalse($"{file} is generic UI; pack and provider names belong to their own pages and registrations");
        }
    }
}
