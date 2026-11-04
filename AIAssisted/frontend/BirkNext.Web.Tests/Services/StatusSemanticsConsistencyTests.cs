using BirkNext.Integrations;
using BirkNext.TestData;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Services.ProjectEvidence;
using BirkNext.Web.Services.ProjectImport;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// One meaning per status word across modules (scenario F): the vocabulary keeps Not run, Not applicable, Partial, Needs review, Blocked,
/// Available, Unresolved, Warning and Cancelled distinct, and module presenters no longer contradict it (Missing never red, Available never Ready,
/// Detected never Ready, Not run never failed, Removed never a problem).
/// </summary>
public sealed class StatusSemanticsConsistencyTests
{
    [Fact]
    public void Requested_terms_exist_once_with_distinct_meanings()
    {
        SemanticStatus[] terms =
        [
            SemanticStatus.Ready, SemanticStatus.Available, SemanticStatus.Completed, SemanticStatus.InventoryComplete, SemanticStatus.Detected, SemanticStatus.Partial,
            SemanticStatus.NeedsReview, SemanticStatus.Blocked, SemanticStatus.Failed, SemanticStatus.NotRun, SemanticStatus.NotAssessed, SemanticStatus.NotApplicable,
            SemanticStatus.Missing, SemanticStatus.Unresolved, SemanticStatus.Inferred, SemanticStatus.Confirmed, SemanticStatus.Warning, SemanticStatus.Conflict, SemanticStatus.Cancelled,
        ];
        var definitions = terms.Select(StatusVocabulary.Of).ToList();
        definitions.Select(d => d.Key).Should().Equal(terms, "every requested term has its own definition (none falls back to Unknown)");
        definitions.Select(d => d.Label).Should().OnlyHaveUniqueItems();
        definitions.Select(d => d.Meaning).Should().OnlyHaveUniqueItems("no two words share one meaning");
        StatusVocabulary.All.Select(d => d.Key).Should().OnlyHaveUniqueItems("one definition per status");
    }

    [Theory]
    [InlineData(SemanticStatus.NotRun, StatusIntent.Neutral)]
    [InlineData(SemanticStatus.NotAssessed, StatusIntent.Neutral)]
    [InlineData(SemanticStatus.NotApplicable, StatusIntent.Neutral)]
    [InlineData(SemanticStatus.Missing, StatusIntent.Neutral)]
    [InlineData(SemanticStatus.Cancelled, StatusIntent.Neutral)]
    [InlineData(SemanticStatus.Available, StatusIntent.Info)]
    [InlineData(SemanticStatus.Detected, StatusIntent.Info)]
    [InlineData(SemanticStatus.Inferred, StatusIntent.Info)]
    [InlineData(SemanticStatus.InventoryComplete, StatusIntent.Info)]
    [InlineData(SemanticStatus.Partial, StatusIntent.Warning)]
    [InlineData(SemanticStatus.NeedsReview, StatusIntent.Warning)]
    [InlineData(SemanticStatus.Warning, StatusIntent.Warning)]
    [InlineData(SemanticStatus.Unresolved, StatusIntent.Warning)]
    [InlineData(SemanticStatus.Blocked, StatusIntent.Danger)]
    [InlineData(SemanticStatus.Failed, StatusIntent.Danger)]
    [InlineData(SemanticStatus.Conflict, StatusIntent.Danger)]
    [InlineData(SemanticStatus.Ready, StatusIntent.Positive)]
    [InlineData(SemanticStatus.Confirmed, StatusIntent.Positive)]
    public void Intents_follow_the_agreed_semantics(SemanticStatus status, StatusIntent intent) => StatusVocabulary.Intent(status).Should().Be(intent);

    [Fact]
    public void Tooltips_use_the_shared_explanations()
    {
        StatusVocabulary.Tooltip(SemanticStatus.InventoryComplete).Should().Be("All supported inventory items were discovered; this does not confirm runtime quality or successful execution.");
        StatusVocabulary.Tooltip(SemanticStatus.NeedsReview).Should().Be("A specific unresolved decision requires human assessment.");
        StatusVocabulary.Tooltip(SemanticStatus.NotApplicable).Should().Be("This dimension is not relevant to the selected source or operation.");
    }

    [Theory]
    [InlineData("not run", SemanticStatus.NotRun)]
    [InlineData("Not Run", SemanticStatus.NotRun)]
    [InlineData("not assessed", SemanticStatus.NotAssessed)]
    [InlineData("available", SemanticStatus.Available)]
    [InlineData("unresolved", SemanticStatus.Unresolved)]
    [InlineData("cancelled", SemanticStatus.Cancelled)]
    [InlineData("canceled", SemanticStatus.Cancelled)]
    [InlineData("warning", SemanticStatus.Warning)]
    [InlineData("something new", SemanticStatus.Unknown)]
    public void Legacy_labels_map_to_one_meaning(string label, SemanticStatus expected) => StatusVocabulary.FromLegacy(label).Should().Be(expected);

    [Fact]
    public void Dashboard_states_mean_the_same_as_the_shared_vocabulary()
    {
        DashboardPresentation.Semantic(DashboardStatus.Available).Should().Be(SemanticStatus.Available);
        DashboardPresentation.Semantic(DashboardStatus.ReadyToReview).Should().Be(SemanticStatus.Available, "the same word has one meaning");
        DashboardPresentation.Semantic(DashboardStatus.Partial).Should().NotBe(SemanticStatus.NeedsReview).And.NotBe(SemanticStatus.Missing);
        DashboardPresentation.Semantic(DashboardStatus.NotAssessed).Should().Be(SemanticStatus.NotAssessed);
        DashboardPresentation.Semantic(DashboardStatus.Failed).Should().Be(SemanticStatus.FailedToAssess);
        DashboardPresentation.Semantic(DashboardStatus.NotApplicable).Should().Be(SemanticStatus.NotApplicable);
        DashboardPresentation.Tone(DashboardStatus.Available).Should().NotBe("good", "available is not ready or passed");
    }

    [Fact]
    public void Scenario_F_missing_and_available_read_the_same_in_project_evidence_and_test_pack_sources()
    {
        static EvidenceRoleRow Row(EvidenceRoleState state) => new(new EnvironmentProjectEvidenceBinding(), state, [], [], []);
        Row(EvidenceRoleState.Missing).Tone.Should().Be("neutral", "Missing is neutral; the consumer that needs it says Blocked");
        Row(EvidenceRoleState.Unavailable).Tone.Should().Be("neutral");
        Row(EvidenceRoleState.Suggested).Tone.Should().Be("info");
        Row(EvidenceRoleState.Conflict).Tone.Should().Be("rejected", "only a conflict is danger");
        SourceSetupPresentation.StateTone(TestPackSourceState.Missing).Should().Be("neutral");
        SourceSetupPresentation.StateTone(TestPackSourceState.AvailableNotBound).Should().Be("info");
        TestDataPresentation.SourceTone(new TestDataSourceSummary("configured", "Configured", "", 2, 0, TestDataSourceStatus.Available, null, null)).Should().Be("info", "available is not ready");
        TestDataPresentation.StatusTone(TestDataAssetStatus.Draft).Should().Be("neutral", "a draft is not a review need");
        IntegrationReviewResultPresentation.ReadinessTone(IntegrationDomainReadiness.Available).Should().NotBe("ready");
    }

    [Fact]
    public void Source_and_delivery_presenters_never_overstate()
    {
        SourceStatusPresentation.Tone(SourceStatusDimension.Completeness, "Detected").Should().NotBe("positive", "detected is not verified or ready");
        SourceStatusPresentation.Tone(SourceStatusDimension.Completeness, "Partial").Should().NotBe(SourceStatusPresentation.Tone(SourceStatusDimension.Completeness, "Needs review"));
        SourceStatusPresentation.Tone(SourceStatusDimension.Observation, "Conflicting").Should().Be("critical");
        DeliveryReportingPresentation.MovementTone(BirkNext.DeliveryReporting.DeliveryMovement.Removed).Should().Be("dlr-tone-neutral");
        DeliveryDashboard.Change(null).Label.Should().Be("—");
    }
}
