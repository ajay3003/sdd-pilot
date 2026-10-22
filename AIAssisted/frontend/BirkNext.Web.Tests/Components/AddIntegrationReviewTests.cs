using BirkNext.Integrations;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Tests.Integration;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Add integration as configuration plus review: the template's consumer is a proposal with a Confirm action (never a field holding a state),
/// a source is recommended only on content evidence (never by date), saving is allowed with open review items and never resolves them, and
/// the save readiness summary says what can and cannot be saved now.
/// </summary>
public sealed class AddIntegrationReviewTests : BunitContext
{
    private const string Webhook = "https://mu.example.test/api/altinn/webhook";
    private static readonly Guid SnapshotA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid SnapshotB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private readonly FakeIntegrationCatalogApi _api = new() { Catalog = M2lbFixture.Catalog() };
    private readonly Mock<IIntegrationJourneysApiService> _journeys = new();
    private readonly FrontendAnalysisProfile _profile = new() { Id = "dev", Name = "DEV", EnvironmentType = FrontendEnvironmentType.Development, TargetUrl = "https://dev.example.test/" };
    private IReadOnlyList<IntegrationTemplateView> _templates = [];

    private static readonly TestPackSourceRole Mu = new("mu", "MU (Meldingsutveksleren)", "Report intake service") { ExpectedArtifact = "Repository source archive" };

    public AddIntegrationReviewTests()
    {
        Services.AddSingleton<IIntegrationCatalogApiService>(_api);
        Services.AddSingleton(new IntegrationMappingEvidenceSession());
        Services.AddSingleton(_journeys.Object);
        _journeys.Setup(j => j.IntegrationTemplatesAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ActiveEventApiResult<IReadOnlyList<IntegrationTemplateView>>(_templates, null));
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static TestPackSourceCandidate Candidate(Guid id, string archive, DateTimeOffset analyzed, params string[] matchedOn) =>
        new(id, archive, new string(archive[0], 64), analyzed) { MatchedOn = matchedOn };

    private static IntegrationTemplateView Intake(TestPackSourceStatus? evidence) =>
        new("pack-report-intake", "Pack – report intake", "The portal delivers reports to MU.", IntegrationKind.HttpApi, "demo.pack", "Demo pack")
        {
            Values = new Dictionary<string, IntegrationTemplateValue>
            {
                ["DisplayName"] = new("Pack – report intake", "Template"), ["SystemName"] = new("Portal → MU", "Template"),
                ["Producer"] = new("Portal", "Template"), ["Consumer"] = new("MU", "Template"), ["EndpointOrTopic"] = new(Webhook, "Template · Development"),
            },
            Journeys = ["Rapportmottak"], EvidenceSource = evidence,
        };

    private IRenderedComponent<IntegrationsPane> OpenWithTemplate()
    {
        var cut = Render<IntegrationsPane>(p => p.Add(c => c.Profile, _profile));
        cut.Find("[data-testid=ip-add]").Click();
        cut.Find("[data-testid=ip-edit-template-select]").Change("pack-report-intake");
        return cut;
    }

    [Fact]
    public void ConsumerMapping_IsAProposalWithAConfirmAction_NeverATextFieldHoldingAState()
    {
        _templates = [Intake(null)];
        var cut = OpenWithTemplate();

        cut.Find("[data-testid=ip-edit-mapping-proposed]").TextContent.Should().Be("MU");
        cut.Find("[data-testid=ip-edit-mapping-state]").TextContent.Should().Be("Needs confirmation");
        cut.Find("[data-testid=ip-edit-mapping-why]").TextContent.Should().Contain("template flow identifies MU as the consumer").And.Contain("has not been explicitly confirmed");
        cut.FindAll("input").Should().NotContain(i => i.GetAttribute("value") == "Needs confirmation", "a review state is never typed into a text field");
        cut.Find("[data-testid=ip-edit-summary-review]").TextContent.Should().Be("1 remaining");

        cut.Find("[data-testid=ip-edit-mapping-confirm]").Click();
        cut.Find("[data-testid=ip-edit-mapping-state]").TextContent.Should().Be("Confirmed");
        cut.Find("[data-testid=ip-edit-summary-review]").TextContent.Should().Be("None remaining");
        cut.Find("[data-testid=ip-edit-mapping-unconfirm]").Click();
        cut.Find("[data-testid=ip-edit-mapping-state]").TextContent.Should().Be("Needs confirmation");
    }

    [Fact]
    public void SavingWithOpenReviewItems_IsAllowed_ResolvesNothing_AndSaysWhatRemains()
    {
        _templates = [Intake(new TestPackSourceStatus(Mu, TestPackSourceState.AvailableNotBound) { Candidates = [Candidate(SnapshotA, "M2LB.zip", DateTimeOffset.Parse("2026-10-22T08:37:00Z"), "Buf.Meldingsutveksler.Service")] })];
        var cut = OpenWithTemplate();

        cut.Find("[data-testid=ip-edit-readiness] h4").TextContent.Should().Be("Ready to save");
        cut.FindAll("[data-testid=ip-edit-readiness-review] li").Select(l => l.TextContent.Trim()).Should().Contain(l => l.Contains("Consumer mapping")).And.Contain(l => l.Contains("Source evidence"));
        cut.Find("[data-testid=ip-edit-readiness-note]").TextContent.Should().Contain("You can save now");
        cut.Find("[data-testid=ip-edit-evidence-without]").TextContent.Should().Contain("Source-based integration review remains Not assessed until evidence is bound");

        cut.Find("[data-testid=ip-edit-save]").Click();
        cut.WaitForAssertion(() => _api.Saved.Should().ContainSingle());
        var saved = _api.Saved.Single();
        saved.Consumer.MappingState.Should().NotBe(ConsumerMappingState.Confirmed, "saving never confirms a mapping");
        saved.ContractRelationship.Should().NotBe(ContractRelationshipState.RelationshipVerified);
        _api.BindCalls.Should().BeEmpty("saving never binds source evidence that was not chosen");
        cut.Find("[data-testid=ip-save-status]").TextContent.Should().StartWith("Integration saved · Review still needed: 2").And.Contain("Consumer mapping").And.Contain("Source evidence");
        cut.Find("[data-testid=ip-saved-review]").TextContent.Should().Contain("not verified");
    }

    [Fact]
    public void MissingRequiredConfiguration_BlocksSave_WithTheExactReason()
    {
        var cut = Render<IntegrationsPane>(p => p.Add(c => c.Profile, _profile));
        cut.Find("[data-testid=ip-add]").Click();
        cut.Find("[data-testid=ip-edit-kind]").Change(nameof(IntegrationKind.HttpApi));
        cut.Find("[data-testid=ip-edit-name]").Input("Manual intake");

        cut.Find("[data-testid=ip-edit-readiness] h4").TextContent.Should().Be("Not ready to save");
        cut.Find("[data-testid=ip-edit-readiness-note]").TextContent.Should().Be("Required before saving: Endpoint.");
        cut.Find("[data-testid=ip-edit-save]").Click();
        cut.Find("[data-testid=ip-editor-errors]").TextContent.Should().Contain("Endpoint");
        _api.Saved.Should().BeEmpty();
    }

    [Fact]
    public void ARecommendation_ComesFromContentEvidence_NeverFromTheNewestDate()
    {
        // The newer snapshot matches on less; the older one is recognized on more content — it is recommended, with its reason.
        _templates = [Intake(new TestPackSourceStatus(Mu, TestPackSourceState.ReviewRequired)
        {
            Candidates =
            [
                Candidate(SnapshotB, "Tiltak 2 - Sikkert digitalt økosystem.zip", DateTimeOffset.Parse("2026-10-22T09:00:00Z"), "Buf.Meldingsutveksler"),
                Candidate(SnapshotA, "M2LB.zip", DateTimeOffset.Parse("2026-10-20T08:00:00Z"), "Buf.Meldingsutveksler", "Buf.Meldingsutveksler.Service", "MottakApi"),
            ],
        })];
        var cut = OpenWithTemplate();

        cut.Find("[data-testid=ip-edit-evidence-recommended]").TextContent.Should().Be("M2LB.zip");
        cut.Find("[data-testid=ip-edit-evidence-reason]").TextContent.Should().Contain("from its content").And.Contain("MottakApi");
        cut.FindAll("[data-testid=ip-edit-evidence-choices]").Should().BeEmpty("with a justified recommendation the full list is one click away");
        cut.FindAll("[data-testid=ip-edit-evidence-effect]").Should().BeEmpty("a recommendation is never selected for you");
        cut.Find("[data-testid=ip-edit-evidence-use-recommended]").Click();
        cut.Find("[data-testid=ip-edit-evidence-effect]").TextContent.Should().Contain("M2LB.zip");
        cut.Find("[data-testid=ip-edit-evidence-state]").TextContent.Should().Be("Selected · binds on save");
        cut.Find("[data-testid=ip-edit-evidence-choose]").Click();
        cut.FindAll("[data-testid=ip-edit-evidence-option]").Should().HaveCount(2);
    }

    [Fact]
    public void TypeChange_RecalculatesTheVisibleFields_BothWays()
    {
        _templates = [Intake(null)];
        var cut = OpenWithTemplate();
        cut.FindAll("[data-testid=ip-edit-consumer-group], [data-testid=ip-edit-advanced], [data-testid=ip-edit-platform]").Should().BeEmpty("HTTP/API");
        cut.Find("[data-testid=ip-edit-kind]").Change(nameof(IntegrationKind.ServiceBus));
        cut.FindAll("[data-testid=ip-edit-consumer-group]").Should().ContainSingle();
        cut.FindAll("[data-testid=ip-edit-advanced]").Should().ContainSingle("messaging types have advanced settings");
        cut.FindAll("[data-testid=ip-edit-auth-mechanism-summary]").Should().BeEmpty("the mechanism is set explicitly for messaging");
        cut.Find("[data-testid=ip-edit-kind]").Change(nameof(IntegrationKind.HttpApi));
        cut.FindAll("[data-testid=ip-edit-consumer-group], [data-testid=ip-edit-advanced]").Should().BeEmpty();
    }
}
