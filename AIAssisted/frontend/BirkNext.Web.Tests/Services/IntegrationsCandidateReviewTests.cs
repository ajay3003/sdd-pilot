using BirkNext.Integrations;
using BirkNext.SourceArchitecture;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Target Environment → Integrations, candidate review and project-specific setup as rules: review-state labels keep the backend semantics,
/// the default view is ready-to-review first, relationships read in words, several relationships of one producer group, Terraform never
/// reads "0 of 0", setup items are grouped by evidence type and assumptions/historical evidence never read as confirmed or current.
/// </summary>
public sealed class IntegrationsCandidateReviewTests
{
    private static SourceIntegrationCandidate C(string id, string? entity, string? consumer, ArchitectureEvidenceState evidence) => new()
    {
        Id = id, ChannelName = id, SourceEntity = entity, ConsumerCandidate = consumer, ConsumerEvidence = evidence,
        Transport = IntegrationTransport.ServiceBusTopic, Pattern = IntegrationPattern.Messaging,
    };

    private static SourceIntegrationsReport Report(params SourceIntegrationCandidate[] candidates) => new()
    {
        Discovery = new SourceIntegrationDiscoveryResult { ArchiveName = "M2LB.zip", Status = SourceDiscoveryStatus.Partial, Candidates = [.. candidates] },
        UnconfiguredCandidateIds = [.. candidates.Select(c => c.Id)],
    };

    [Theory]
    [InlineData(ArchitectureEvidenceState.Confirmed, "Ready to review")]
    [InlineData(ArchitectureEvidenceState.StronglySupported, "Ready to review")]
    [InlineData(ArchitectureEvidenceState.Inferred, "Needs confirmation")]
    [InlineData(ArchitectureEvidenceState.Unresolved, "Needs mapping")]
    [InlineData(ArchitectureEvidenceState.Conflict, "Conflicting evidence")]
    public void ReviewStateLabels_FollowTheUnchangedSourceSemantics(ArchitectureEvidenceState evidence, string label) =>
        IntegrationsOverviewPresentation.Label(IntegrationsOverviewPresentation.Strength(C("x", "X", "Y", evidence))).Should().Be(label);

    [Fact]
    public void DefaultView_IsReadyToReview_SortedByRelationship_AndAllKeepsStateOrder()
    {
        var report = Report(
            C("t1", "Person", "SkoleService", ArchitectureEvidenceState.StronglySupported),
            C("t2", "Barn", "SkoleService", ArchitectureEvidenceState.StronglySupported),
            C("t3", "Audit", null, ArchitectureEvidenceState.Unresolved),
            C("t4", "Orders", "Orders.Worker", ArchitectureEvidenceState.Inferred));

        IntegrationsOverviewPresentation.Candidates(report, CandidateFilter.Ready).Select(IntegrationsOverviewPresentation.Relationship)
            .Should().Equal("Barn → SkoleService", "Person → SkoleService");
        IntegrationsOverviewPresentation.Candidates(report, CandidateFilter.All).Select(c => c.Id).Should().Equal("t2", "t1", "t4", "t3");
        IntegrationsOverviewPresentation.Candidates(report, CandidateFilter.All, "audit").Single().Id.Should().Be("t3");
        IntegrationsOverviewPresentation.Relationship(report.Discovery!.Candidates[2]).Should().Be("Audit → consumer not identified");
    }

    [Fact]
    public void SeveralRelationshipsOfOneProducer_FormOneExpandableGroup()
    {
        var report = Report(
            C("proxy-a", null, "Clusters:a", ArchitectureEvidenceState.StronglySupported) with { ProducerComponent = "M2LB.Proxy.Api" },
            C("proxy-b", null, "Clusters:b", ArchitectureEvidenceState.StronglySupported) with { ProducerComponent = "M2LB.Proxy.Api" },
            C("person", "Person", "SkoleService", ArchitectureEvidenceState.StronglySupported));

        var groups = IntegrationsOverviewPresentation.GroupByProducer(IntegrationsOverviewPresentation.Candidates(report, CandidateFilter.Ready));
        groups.Single(g => g.Key == "M2LB.Proxy.Api").Should().HaveCount(2);
        groups.Single(g => g.Key == "Person").Should().ContainSingle();
    }

    [Fact]
    public void AddedCandidate_KeepsItsSourceEvidenceWording_AndIsNeverConfirmed()
    {
        var definition = IntegrationsOverviewPresentation.FromCandidate(new IntegrationCatalog(), C("t", "Person", "SkoleService", ArchitectureEvidenceState.StronglySupported), Guid.NewGuid());
        definition.Consumer.MappingState.Should().Be(ConsumerMappingState.Suggested);
        definition.Consumer.MappingSource.Should().Be("Source Analysis suggestion (strongly supported)");
        definition.Origin.Should().Be(IntegrationRecordOrigin.SourceSuggestion);
    }

    [Theory]
    [InlineData(0, 0, 0, "Terraform evidence available. No comparable environment values were found.")]
    [InlineData(2, 3, 1, "2 of 3 compared values match source · 1 needs review.")]
    [InlineData(1, 1, 0, "1 of 1 compared value match source.")]
    public void TerraformSummary_NeverReadsZeroOfZero(int matching, int compared, int reviews, string expected) =>
        IntegrationsOverviewPresentation.TerraformSummary(matching, compared, reviews).Should().Be(expected).And.NotContain("0 of 0");

    [Fact]
    public void SetupItems_AreGroupedByEvidenceType_AndAssumptionsNeverReadAsVerified()
    {
        IntegrationTemplateItem[] items =
        [
            new("Platform", "M2LB DEV Event Hubs", "", IntegrationTemplateProvenance.HistoricalAudit),
            new("Runtime evidence", "Event Hub runtime identifiers", "", IntegrationTemplateProvenance.HistoricalAudit),
            new("Runtime evidence", "Expected consumer group $Default", "", IntegrationTemplateProvenance.ConfiguredAssumption),
            new("Integration", "BIRK Person CDC → Person Adapter", "", IntegrationTemplateProvenance.ConfirmedMapping),
            new("Extension", "M2LB child security classification", "", IntegrationTemplateProvenance.ConfiguredAssumption),
        ];

        items.Select(IntegrationsOverviewPresentation.Group).Should().Equal(SetupGroup.Platforms, SetupGroup.HistoricalRuntimeEvidence, SetupGroup.Assumptions,
            SetupGroup.ConfirmedMappings, SetupGroup.Extensions);
        IntegrationsOverviewPresentation.ProvenanceHelp(items[1]).Should().Be("Previously verified; not re-verified for the current review.");
        IntegrationsOverviewPresentation.ProvenanceHelp(items[2]).Should().Be("Assumed value — not verified at runtime.");
        IntegrationsOverviewPresentation.ProvenanceHelp(items[4]).Should().Contain("Project-specific extension setup, not general integration discovery");
        IntegrationsOverviewPresentation.ProvenanceTone(IntegrationTemplateProvenance.ConfiguredAssumption)
            .Should().NotBe(IntegrationsOverviewPresentation.ProvenanceTone(IntegrationTemplateProvenance.ConfirmedMapping), "an assumption never carries a confirmed mapping's confidence");
        IntegrationsOverviewPresentation.GroupCount(SetupGroup.Platforms, 3).Should().Be("3 platform settings");
        IntegrationsOverviewPresentation.GroupCount(SetupGroup.HistoricalRuntimeEvidence, 1).Should().Be("1 historical runtime evidence record");
    }

    [Fact]
    public void OverviewRuntimeLabel_NeverSoundsLikeAFailedExecution()
    {
        IntegrationsOverviewPresentation.OverviewRuntimeLabel(new IntegrationCatalog(), RuntimeEvidenceStatus.NotConfigured).Should().Be("None — no configured integrations");
        var withIntegration = new IntegrationCatalog { Integrations = [new IntegrationDefinition { Id = "a", Kind = IntegrationKind.EventHub }] };
        IntegrationsOverviewPresentation.OverviewRuntimeLabel(withIntegration, RuntimeEvidenceStatus.NotConfigured).Should().Be("Setup required");
        IntegrationsOverviewPresentation.OverviewRuntimeLabel(withIntegration, RuntimeEvidenceStatus.Configured).Should().NotContain("Observed").And.Contain("read during review");
    }
}
