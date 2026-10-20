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
/// Add integration with templates: safe prefill with provenance, person-entered values kept unless replacement is confirmed, the evidence
/// source as its own section (Missing / Available / Ambiguous / Bound, bound only when chosen) and one generic Add integration trigger per
/// page state. Templates are test data here: the shared form names no pack.
/// </summary>
public sealed class IntegrationTemplateUiTests : BunitContext
{
    private const string Webhook = "https://mu.example.test/api/altinn/webhook";
    private static readonly Guid SnapshotA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid SnapshotB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private readonly FakeIntegrationCatalogApi _api = new() { Catalog = M2lbFixture.Catalog() };
    private readonly Mock<IIntegrationJourneysApiService> _journeys = new();
    private readonly FrontendAnalysisProfile _profile = new() { Id = "dev", Name = "DEV", EnvironmentType = FrontendEnvironmentType.Development, TargetUrl = "https://dev.example.test/" };
    private IReadOnlyList<IntegrationTemplateView> _templates = [Intake(Missing())];

    public IntegrationTemplateUiTests()
    {
        Services.AddSingleton<IIntegrationCatalogApiService>(_api);
        Services.AddSingleton(new IntegrationMappingEvidenceSession());
        Services.AddSingleton(_journeys.Object);
        _journeys.Setup(j => j.IntegrationTemplatesAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ActiveEventApiResult<IReadOnlyList<IntegrationTemplateView>>(_templates, null));
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static readonly TestPackSourceRole Mu = new("mu", "MU (Meldingsutveksleren)", "Report intake service") { ExpectedArtifact = "Repository source archive" };

    private static TestPackSourceStatus Missing() => new(Mu, TestPackSourceState.Missing);
    private static TestPackSourceCandidate Candidate(Guid id, string archive, char fill) => new(id, archive, new string(fill, 64), new DateTimeOffset(2026, 10, 1, 7, 30, 0, TimeSpan.Zero)) { MatchedOn = ["Buf.Meldingsutveksler.Service"] };

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

    private static IntegrationTemplateView Stream() =>
        new("ledger-cdc", "Ledger – CDC stream", "Ledger changes on an event stream.", IntegrationKind.EventHub, "demo.ledger", "Ledger")
        {
            Values = new Dictionary<string, IntegrationTemplateValue> { ["DisplayName"] = new("Ledger – CDC stream", "Template"), ["Producer"] = new("Debezium", "Template"), ["Consumer"] = new("Ledger.Adapter", "Template") },
            NeedsConfiguration = ["EndpointOrTopic", "SourceResource"],
        };

    private IRenderedComponent<IntegrationsPane> Open() => Render<IntegrationsPane>(p => p.Add(c => c.Profile, _profile));

    private static IRenderedComponent<IntegrationsPane> OpenAdd(IRenderedComponent<IntegrationsPane> cut)
    {
        cut.Find("[data-testid=ip-add]").Click();
        return cut;
    }

    private static string? Value(IRenderedComponent<IntegrationsPane> cut, string testId) => cut.Find($"[data-testid={testId}]").GetAttribute("value");

    [Fact]
    public void ManualCustom_IsTheDefault_AndLeavesTheGenericFormEmpty()
    {
        var cut = OpenAdd(Open());
        var select = cut.Find("[data-testid=ip-edit-template-select]");
        select.GetAttribute("value").Should().BeEmpty();
        select.QuerySelector("option")!.TextContent.Should().Be("Manual / custom integration");
        select.QuerySelector("optgroup")!.GetAttribute("label").Should().Be("Recommended for Demo pack");
        Value(cut, "ip-edit-name").Should().BeNullOrEmpty();
        Value(cut, "ip-edit-kind").Should().BeNullOrEmpty();
        cut.FindAll("[data-testid=ip-prov]").Should().BeEmpty();
        cut.Find("[data-testid=ip-edit-kind]").Change(nameof(IntegrationKind.HttpApi));
        cut.Find("[data-testid=ip-edit-evidence-state]").TextContent.Should().Be("Not bound");
        cut.Find("[data-testid=ip-edit-evidence-none]").TextContent.Should().Contain("can be bound in IQR Evidence after saving");
        cut.Find("[data-testid=ip-edit-save]").TextContent.Should().Be("Save integration", "opening the form and submitting it are different actions");
    }

    [Fact]
    public void ChoosingATemplate_PrefillsTheSafeFields_WithProvenance_AndHidesEventStreamFields()
    {
        var cut = OpenAdd(Open());
        cut.Find("[data-testid=ip-edit-kind]").Change(nameof(IntegrationKind.HttpApi));
        var platformAuth = Value(cut, "ip-edit-producer-auth");
        cut.Find("[data-testid=ip-edit-kind]").Change("");
        cut.Find("[data-testid=ip-edit-template-select]").Change("pack-report-intake");

        Value(cut, "ip-edit-name").Should().Be("Pack – report intake");
        Value(cut, "ip-edit-kind").Should().Be(nameof(IntegrationKind.HttpApi));
        Value(cut, "ip-edit-system").Should().Be("Portal → MU");
        Value(cut, "ip-edit-producer").Should().Be("Portal");
        Value(cut, "ip-edit-consumer").Should().Be("MU");
        Value(cut, "ip-edit-topic").Should().Be(Webhook);
        cut.Find("[data-testid=ip-edit-enabled]").HasAttribute("checked").Should().BeTrue();
        cut.FindAll("[data-testid=ip-prov]").Single(p => p.GetAttribute("data-field") == "EndpointOrTopic").TextContent.Should().Be("Source: Template · Development");
        cut.FindAll("[data-testid=ip-edit-consumer-group], [data-testid=ip-edit-delete-expectation], [data-testid=ip-edit-source]").Should().BeEmpty("HTTP/API has no event-stream fields");
        cut.Find("[data-testid=ip-edit-template-journeys]").TextContent.Should().Be("Rapportmottak");
        Value(cut, "ip-edit-producer-auth").Should().Be(platformAuth, "a template never sets authentication; the platform default stays");
    }

    [Fact]
    public void RequiredFieldErrors_ClearAsSoonAsTheTemplateFillsThem()
    {
        var cut = OpenAdd(Open());
        cut.Find("[data-testid=ip-edit-save]").Click();
        cut.Find("[data-testid=ip-editor-errors]").TextContent.Should().Contain("Display name is required").And.Contain("Type is required");
        cut.Find("[data-testid=ip-edit-template-select]").Change("pack-report-intake");
        cut.FindAll("[data-testid=ip-editor-errors]").Should().BeEmpty("the template filled every required field");
    }

    [Fact]
    public void PersonEnteredValues_AreKeptUnlessReplacementIsConfirmed()
    {
        var cut = OpenAdd(Open());
        cut.Find("[data-testid=ip-edit-name]").Input("Our intake");
        cut.Find("[data-testid=ip-edit-template-select]").Change("pack-report-intake");
        cut.Find("[data-testid=ip-edit-template-confirm]").TextContent.Should().Contain("1 field already contains a value").And.Contain("Display name");
        Value(cut, "ip-edit-name").Should().Be("Our intake", "nothing is applied before the person decides");

        cut.Find("[data-testid=ip-edit-template-keep]").Click();
        Value(cut, "ip-edit-name").Should().Be("Our intake");
        Value(cut, "ip-edit-template-select").Should().BeEmpty("keeping my values leaves the template unapplied");
        cut.FindAll("[data-testid=ip-edit-topic]").Should().BeEmpty();

        cut.Find("[data-testid=ip-edit-template-select]").Change("pack-report-intake");
        cut.Find("[data-testid=ip-edit-template-where-empty]").Click();
        Value(cut, "ip-edit-name").Should().Be("Our intake", "apply-where-empty never overwrites");
        Value(cut, "ip-edit-topic").Should().Be(Webhook);

        cut.Find("[data-testid=ip-edit-template-select]").Change("");
        Value(cut, "ip-edit-name").Should().Be("Our intake", "manual keeps what the person typed");
        Value(cut, "ip-edit-kind").Should().BeNullOrEmpty("the template's untouched values leave with it");

        cut.Find("[data-testid=ip-edit-template-select]").Change("pack-report-intake");
        cut.Find("[data-testid=ip-edit-template-replace]").Click();
        Value(cut, "ip-edit-name").Should().Be("Pack – report intake", "replace only after explicit confirmation");
    }

    [Fact]
    public void MissingEvidence_SaysSo_AndOffersImport_WithoutBinding()
    {
        var cut = OpenAdd(Open());
        cut.Find("[data-testid=ip-edit-template-select]").Change("pack-report-intake");
        cut.Find("[data-testid=ip-edit-evidence-state]").TextContent.Should().Be("Missing");
        cut.Find("[data-testid=ip-edit-evidence-role]").TextContent.Should().Be("MU (Meldingsutveksleren)");
        cut.Find("[data-testid=ip-edit-evidence-meaning]").TextContent.Should().Be("No matching imported source found.");
        cut.Find("[data-testid=ip-edit-evidence-import]").GetAttribute("href").Should().Be("project-import");
        cut.Markup.Should().NotContain("skolenaervaer.json");
    }

    [Fact]
    public void AvailableEvidence_IsNotBound_UntilChosen_ThenBindsAfterTheIntegrationIsCreated()
    {
        _templates = [Intake(new TestPackSourceStatus(Mu, TestPackSourceState.AvailableNotBound) { Candidates = [Candidate(SnapshotA, "Tiltak 2 - Sikkert digitalt økosystem (1).zip", 'a')] })];
        var cut = OpenAdd(Open());
        cut.Find("[data-testid=ip-edit-template-select]").Change("pack-report-intake");
        cut.Find("[data-testid=ip-edit-evidence-state]").TextContent.Should().Be("Available", "available is not bound");
        cut.Find("[data-testid=ip-edit-evidence-candidate]").TextContent.Should().Contain("Tiltak 2 - Sikkert digitalt økosystem (1).zip");
        cut.Find("[data-testid=ip-edit-evidence-open]").GetAttribute("href").Should().Be($"source-analysis?snapshot={SnapshotA:D}");
        cut.FindAll("[data-testid=ip-edit-evidence-effect]").Should().BeEmpty();

        cut.Find("[data-testid=ip-edit-evidence-use]").Click();
        cut.Find("[data-testid=ip-edit-evidence-use]").GetAttribute("aria-pressed").Should().Be("true");
        cut.Find("[data-testid=ip-edit-evidence-effect]").TextContent.Should().Contain("will become the canonical source evidence for this integration when it is saved");
        cut.Find("[data-testid=ip-edit-save]").Click();

        cut.WaitForAssertion(() => _api.BindCalls.Should().ContainSingle());
        var created = _api.Saved.Single();
        (created.Origin, created.TemplateKey, created.Kind, created.EndpointOrTopic).Should().Be((IntegrationRecordOrigin.Template, "pack-report-intake", IntegrationKind.HttpApi, Webhook));
        created.Consumer.MappingState.Should().Be(ConsumerMappingState.NeedsConfirmation, "a template never confirms a mapping");
        var platform = _api.Catalog.Platforms.First();
        (created.ProducerAuthentication, created.ConsumerAuthentication).Should().Be((platform.ProducerAuthentication, platform.DefaultConsumerAuthentication), "authentication comes from the platform, never the template");
        _api.BindCalls.Single().Should().Be((created.Id, (Guid?)SnapshotA));
        cut.Find("[data-testid=ip-binding-outcome]").GetAttribute("data-failed").Should().Be("false");
    }

    [Fact]
    public void WithoutAChoice_NothingIsBound()
    {
        _templates = [Intake(new TestPackSourceStatus(Mu, TestPackSourceState.AvailableNotBound) { Candidates = [Candidate(SnapshotA, "mu.zip", 'a')] })];
        var cut = OpenAdd(Open());
        cut.Find("[data-testid=ip-edit-template-select]").Change("pack-report-intake");
        cut.Find("[data-testid=ip-edit-save]").Click();
        cut.WaitForAssertion(() => _api.Saved.Should().ContainSingle());
        _api.BindCalls.Should().BeEmpty("a suggestion never binds by itself");
        cut.FindAll("[data-testid=ip-binding-outcome]").Should().BeEmpty();
    }

    [Fact]
    public void AmbiguousEvidence_IsNeverResolvedByNewest_AndTheChoiceIsExplicit()
    {
        _templates = [Intake(new TestPackSourceStatus(Mu, TestPackSourceState.ReviewRequired) { Candidates = [Candidate(SnapshotB, "mu-new.zip", 'b'), Candidate(SnapshotA, "mu-old.zip", 'a')] })];
        var cut = OpenAdd(Open());
        cut.Find("[data-testid=ip-edit-template-select]").Change("pack-report-intake");
        cut.Find("[data-testid=ip-edit-evidence-state]").TextContent.Should().Be("Ambiguous");
        cut.Find("[data-testid=ip-edit-evidence-choices] legend").TextContent.Should().Be("Choose source");
        cut.Find("[data-testid=ip-edit-evidence-none-choice]").HasAttribute("checked").Should().BeTrue("no snapshot is preselected");
        cut.FindAll("[data-testid=ip-edit-evidence-option] input").Should().OnlyContain(i => !i.HasAttribute("checked"));

        cut.FindAll("[data-testid=ip-edit-evidence-option] input")[1].Change(SnapshotA.ToString());
        cut.Find("[data-testid=ip-edit-evidence-effect]").TextContent.Should().Contain("mu-old.zip");
        cut.Find("[data-testid=ip-edit-save]").Click();
        cut.WaitForAssertion(() => _api.BindCalls.Should().ContainSingle(c => c.SnapshotId == SnapshotA));
    }

    [Fact]
    public void AFailedBinding_KeepsTheIntegration_AndSaysWhereToBindIt()
    {
        _templates = [Intake(new TestPackSourceStatus(Mu, TestPackSourceState.AvailableNotBound) { Candidates = [Candidate(SnapshotA, "mu.zip", 'a')] })];
        _api.BindError = "The snapshot is not in this environment.";
        var cut = OpenAdd(Open());
        cut.Find("[data-testid=ip-edit-template-select]").Change("pack-report-intake");
        cut.Find("[data-testid=ip-edit-evidence-use]").Click();
        cut.Find("[data-testid=ip-edit-save]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=ip-binding-outcome]").GetAttribute("data-failed").Should().Be("true"));
        var outcome = cut.Find("[data-testid=ip-binding-outcome]");
        outcome.GetAttribute("role").Should().Be("alert");
        outcome.TextContent.Should().Contain("was saved, but its evidence source was not bound").And.Contain("not in this environment");
        cut.Find("[data-testid=ip-binding-outcome-iqr]").GetAttribute("href").Should().Be("/integration-quality-review?view=evidence");
        _api.Saved.Should().ContainSingle();
    }

    [Fact]
    public void EditingAnIntegrationWithABinding_ShowsBound_AndOffersNoReplacement()
    {
        var record = _api.Catalog.Integrations.First() with { TemplateKey = "pack-report-intake", Origin = IntegrationRecordOrigin.Template };
        _api.Catalog = _api.Catalog with { Integrations = [record, .. _api.Catalog.Integrations.Skip(1)] };
        _api.EvidenceBindings = [new IntegrationEvidenceBinding { EnvironmentId = "dev", IntegrationId = record.Id, SnapshotId = SnapshotA, ArchiveName = "bound-mu.zip", Fingerprint = new string('a', 64) }];
        _templates = [Intake(new TestPackSourceStatus(Mu, TestPackSourceState.ReviewRequired) { Candidates = [Candidate(SnapshotB, "newer-mu.zip", 'b'), Candidate(SnapshotA, "bound-mu.zip", 'a') with { Bound = true }] })];
        var cut = Open();
        cut.FindAll("[data-testid=ip-row]").Single(r => r.GetAttribute("data-integration-id") == record.Id).QuerySelector("[data-testid=ip-row-edit]")!.Click();

        cut.Find("[data-testid=ip-edit-template-origin]").TextContent.Should().StartWith("Created from template: Pack – report intake.");
        Value(cut, "ip-edit-template-select").Should().BeEmpty("a template is never reapplied on open");
        Value(cut, "ip-edit-name").Should().Be(record.DisplayName, "saved values are shown as saved");
        cut.Find("[data-testid=ip-edit-evidence-state]").TextContent.Should().Be("Bound");
        cut.Find("[data-testid=ip-edit-evidence-bound]").TextContent.Should().Contain("bound-mu.zip");
        cut.FindAll("[data-testid=ip-edit-evidence-choices], [data-testid=ip-edit-evidence-use]").Should().BeEmpty("a newer snapshot never replaces the binding here");
    }

    [Fact]
    public void AnotherPacksStreamTemplate_UsesTheSameForm_AndLeavesUnknownValuesForThePerson()
    {
        _templates = [Intake(Missing()), Stream()];
        var cut = OpenAdd(Open());
        cut.FindAll("[data-testid=ip-edit-template-select] optgroup").Select(g => g.GetAttribute("label")).Should().Equal("Recommended for Demo pack", "Recommended for Ledger");
        cut.Find("[data-testid=ip-edit-template-select]").Change("ledger-cdc");
        Value(cut, "ip-edit-kind").Should().Be(nameof(IntegrationKind.EventHub));
        Value(cut, "ip-edit-topic").Should().BeNullOrEmpty("an unknown endpoint is never guessed");
        cut.FindAll("[data-testid=ip-needs-config]").Select(n => n.GetAttribute("data-field")).Should().Equal("EndpointOrTopic", "SourceResource");
        cut.Find("[data-testid=ip-edit-consumer-group]").Should().NotBeNull("event streams keep their consumer group");
        cut.Find("[data-testid=ip-edit-delete-expectation]").Should().NotBeNull();
        cut.Find("[data-testid=ip-edit-evidence-state]").TextContent.Should().Be("Not required");
    }

    [Fact]
    public void WithoutTemplates_TheManualFormIsUnchanged()
    {
        _templates = [];
        var cut = OpenAdd(Open());
        cut.FindAll("[data-testid=ip-edit-template]").Should().BeEmpty();
        cut.Find("[data-testid=ip-edit-name]").Should().NotBeNull();
    }

    // ── One generic Add integration trigger per page state ────────────────────────────────────────────────────────────────────────

    private static List<AngleSharp.Dom.IElement> GenericTriggers(IRenderedComponent<IntegrationsPane> cut) =>
        cut.FindAll("button").Where(b => b.TextContent.Trim() == "Add integration").ToList();

    [Fact]
    public void NoIntegrations_OneTriggerInTheEmptyState()
    {
        _api.Catalog = new IntegrationCatalog { EnvironmentId = "dev" };
        var cut = Open();
        GenericTriggers(cut).Should().ContainSingle().Which.GetAttribute("data-testid").Should().Be("ip-empty-add");
        cut.Find("[data-testid=ip-empty]").QuerySelector("[data-testid=ip-empty-add]").Should().NotBeNull();
        cut.Find("[data-testid=ip-overview]").QuerySelectorAll("button").Should().BeEmpty("the summary stays informational");
    }

    [Fact]
    public void Integrations_OneTriggerInTheSectionHeader()
    {
        var cut = Open();
        var trigger = GenericTriggers(cut).Should().ContainSingle().Subject;
        trigger.GetAttribute("data-testid").Should().Be("ip-add");
        trigger.ParentElement!.ClassList.Should().Contain("ip-section-head");
        trigger.Id.Should().Be("ip-add-trigger");
    }

    [Fact]
    public void FormOpen_NoCompetingTrigger_AndCancelBringsItBack()
    {
        var cut = Open();
        cut.Find("[data-testid=ip-add]").Click();
        GenericTriggers(cut).Should().BeEmpty("the open form has its own Save integration / Cancel");
        cut.Find("[data-testid=ip-edit-save]").TextContent.Should().Be("Save integration");
        cut.Find("[data-testid=ip-edit-cancel]").Click();
        GenericTriggers(cut).Should().ContainSingle();
    }
}
