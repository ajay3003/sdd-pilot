using BirkNext.Integrations;
using BirkNext.Web.Services;
using BirkNext.Web.Services.ProjectImport;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// One source state, four pages: Import Project, Add integration, IQR (source guidance) and the test pack page read the same backend status
/// and must say the same thing. Missing only when nothing matches; an unbound match is "Available · needs binding"; several matches are
/// "Review required" (never the newest); a canonical binding is "Bound". Generic: no pack is named.
/// </summary>
public sealed class SourceStateConsistencyTests
{
    private static readonly TestPackSourceRole Role = new("repo", "Repo", "The implementing repository") { Journeys = ["intake"] };
    private static TestPackSourceCandidate C(char fill, bool bound = false) => new(Guid.NewGuid(), $"{fill}.zip", new string(fill, 64), DateTimeOffset.UnixEpoch) { Bound = bound };

    public static TheoryData<string, string> Cases => new()
    {
        { "missing", "Missing" },
        { "available", SourceSetupPresentation.AvailableNeedsBinding },
        { "ambiguous", "Review required" },
        { "bound", "Bound" },
    };

    private static (TestPackSourceStatus Status, IntegrationEvidenceBinding? OwnBinding) Fixture(string kind) => kind switch
    {
        "missing" => (new(Role, TestPackSourceState.Missing), null),
        "available" => (new(Role, TestPackSourceState.AvailableNotBound) { Candidates = [C('a')] }, null),
        "ambiguous" => (new(Role, TestPackSourceState.ReviewRequired) { Candidates = [C('a'), C('b')] }, null),
        _ => (new(Role, TestPackSourceState.Bound) { Candidates = [C('a', bound: true)], BoundArchives = ["a.zip"] },
            new IntegrationEvidenceBinding { IntegrationId = "intake", ArchiveName = "a.zip" }),
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void EveryPage_SaysTheSameSourceState(string kind, string expected)
    {
        var (status, own) = Fixture(kind);
        var guidance = new TestPackSourceGuidance("pack", []) { Sources = [status] };

        SourceSetupPresentation.StateLabel(status).Should().Be(expected, "Import Project / source setup");
        SourceGuidancePresentation.SourceStateLabel(status).Should().Be(expected, "IQR and the test pack page");
        IntegrationTemplatePresentation.Label(IntegrationTemplatePresentation.Evidence(status, own)).Should().Be(expected, "Add integration");
        TestPackPresentation.Dimensions(guidance, "intake", null).Single(d => d.Key == "source:repo").Value.Should().Be(expected, "journey readiness");
    }

    [Theory]
    [InlineData("available")]
    [InlineData("ambiguous")]
    [InlineData("bound")]
    public void NoPage_SaysMissing_OrAsksToImport_WhenAMatchingSnapshotExists(string kind)
    {
        var (status, own) = Fixture(kind);
        SourceSetupPresentation.CardState(status).Should().NotBe("Missing");
        SourceSetupPresentation.Primary(status, "#import")!.Href.Should().NotBe("#import", "a stored compatible snapshot is used, never imported again");
        SourceGuidancePresentation.SourceShort(status).Should().NotContain("missing");
        IntegrationTemplatePresentation.Evidence(status, own).Should().NotBe(TemplateEvidenceState.Missing);
    }

    [Fact]
    public void OnlyNoMatch_IsMissing_AndOffersImport()
    {
        var (status, own) = Fixture("missing");
        SourceSetupPresentation.CardState(status).Should().Be("Missing");
        SourceSetupPresentation.Primary(status, "#import")!.Href.Should().Be("#import");
        IntegrationTemplatePresentation.Evidence(status, own).Should().Be(TemplateEvidenceState.Missing);
    }

    [Fact]
    public void JourneySourceEvidence_FoundOnlyInUnboundSnapshots_IsAvailableNeedsBinding_NeverMissing()
    {
        static IntegrationJourneyView View(params JourneyReadinessState[] states) => new()
        {
            Prerequisites = [.. states.Select((state, i) => new JourneyPrerequisite($"source:{i}", $"Source {i}", JourneyPrerequisiteCategory.SourceEvidence, state, ""))],
        };
        TestPackPresentation.SourceEvidence(View(JourneyReadinessState.Partial, JourneyReadinessState.Partial)).Should().Be(SourceSetupPresentation.AvailableNeedsBinding);
        TestPackPresentation.SourceEvidence(View(JourneyReadinessState.NotReady, JourneyReadinessState.NotReady)).Should().Be("Missing");
        TestPackPresentation.SourceEvidence(View(JourneyReadinessState.Partial, JourneyReadinessState.NotReady)).Should().Be("Partial");
        TestPackPresentation.SourceEvidence(View(JourneyReadinessState.Ready, JourneyReadinessState.Ready)).Should().Be("Verified");
    }

    [Fact]
    public void JourneyDimensions_KeepReferenceRuntimeAndExecutionSeparate_AndExecutionComesOnlyFromTheBackendGate()
    {
        var reference = new TestPackSourceStatus(new("app", "App", "") { Provider = TestPackSourceProvider.TestPackReference, Journeys = ["intake"] }, TestPackSourceState.ReferenceAvailable);
        var (available, _) = Fixture("available");
        var guidance = new TestPackSourceGuidance("pack", []) { Sources = [available, reference] };

        var notAllowed = TestPackPresentation.Dimensions(guidance, "intake", new IntegrationJourneyPackView { EnvironmentTrusted = false });
        notAllowed.Select(d => (d.Label, d.Value)).Should().Equal(("Repo source", SourceSetupPresentation.AvailableNeedsBinding), ("App reference", "Available"),
            ("Runtime", "Not verified"), ("Active execution", "Not allowed"));
        TestPackPresentation.Dimensions(guidance, "intake", new IntegrationJourneyPackView { EnvironmentTrusted = true }).Last().Value.Should().Be("Allowed");
        TestPackPresentation.Dimensions(guidance, "intake", null).Last().Value.Should().Be("Not known", "never assumed before the backend says so");
        notAllowed.Should().NotContain(d => d.Value == "Ready", "readiness is never collapsed into one state");
    }
}
