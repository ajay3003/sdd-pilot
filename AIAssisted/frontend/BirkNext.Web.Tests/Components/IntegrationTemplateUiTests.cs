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
        cut.FindAll("[data-testid=ip-prov]").Should().BeEmpty("provenance is stated once per section, never \"Source: Template\" under every field");
        cut.FindAll("[data-testid=ip-section-prefill]").Select(p => p.TextContent).Should().Equal(["Prefilled from template", "Prefilled from template"], "configuration and connection were prefilled");
        cut.Find("[data-testid=ip-edit-summary-name]").TextContent.Should().Be("Pack – report intake");
        cut.Find("[data-testid=ip-edit-summary-config]").TextContent.Should().Be("Ready");
        cut.FindAll("[data-testid=ip-edit-consumer-group], [data-testid=ip-edit-delete-expectation], [data-testid=ip-edit-source]").Should().BeEmpty("HTTP/API has no event-stream fields");
        cut.Find("[data-testid=ip-edit-template-journeys]").TextContent.Should().Be("Rapportmottak");
        Value(cut, "ip-edit-producer-auth").Should().Be(platformAuth, "a template never sets authentication");
        cut.FindAll("[data-testid=ip-edit-platform]").Should().BeEmpty("a messaging platform does not apply to HTTP/API — never a confusing \"None\"");
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
        cut.Find("[data-testid=ip-edit-evidence-state]").TextContent.Should().Be("Available · needs binding", "available is not bound, and unbound is not missing");
        cut.Find("[data-testid=ip-edit-template-evidence-state]").TextContent.Should().Be("Available · needs binding", "the template summary says the same");
        cut.FindAll("[data-testid=ip-edit-evidence-import]").Should().BeEmpty("a compatible stored snapshot exists, so importing is not the recovery action");
        cut.Find("[data-testid=ip-edit-evidence-candidate]").TextContent.Should().Contain("Tiltak 2 - Sikkert digitalt økosystem (1).zip");
        cut.Find("[data-testid=ip-edit-evidence-open]").GetAttribute("href").Should().Be($"source-analysis?snapshot={SnapshotA:D}");
        cut.FindAll("[data-testid=ip-edit-evidence-effect]").Should().BeEmpty();

        cut.Find("[data-testid=ip-edit-evidence-use]").Click();
        cut.Find("[data-testid=ip-edit-evidence-use]").GetAttribute("aria-pressed").Should().Be("true");
        var confirm = cut.Find("[data-testid=ip-edit-evidence-confirm]").TextContent;
        confirm.Should().Contain("Pack – report intake").And.Contain("Tiltak 2 - Sikkert digitalt økosystem (1).zip").And.Contain("MU (Meldingsutveksleren)")
            .And.Contain("Use this Source Analysis snapshot as canonical evidence for this integration.");
        _api.BindCalls.Should().BeEmpty("choosing is not binding: the binding is made after the integration is saved");
        cut.Find("[data-testid=ip-edit-save]").Click();

        cut.WaitForAssertion(() => _api.BindCalls.Should().ContainSingle());
        var created = _api.Saved.Single();
        (created.Origin, created.TemplateKey, created.Kind, created.EndpointOrTopic).Should().Be((IntegrationRecordOrigin.Template, "pack-report-intake", IntegrationKind.HttpApi, Webhook));
        created.Consumer.MappingState.Should().Be(ConsumerMappingState.NeedsConfirmation, "a template never confirms a mapping");
        (created.ProducerAuthentication, created.ConsumerAuthentication, created.PlatformId).Should().Be((IntegrationAuthMechanism.NotConfigured, IntegrationAuthMechanism.NotConfigured, (string?)null),
            "an HTTP/API integration does not inherit the messaging platform or its mechanisms, and a template never sets authentication");
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
        cut.Find("[data-testid=ip-edit-evidence-state]").TextContent.Should().Be("Review required");
        cut.Find("[data-testid=ip-edit-evidence-count]").TextContent.Should().Be("2 matching MU (Meldingsutveksleren) snapshots");
        cut.Find("[data-testid=ip-edit-evidence-choices] legend").TextContent.Should().Be("Choose source evidence");
        cut.Find("[data-testid=ip-edit-evidence-no-recommendation]").TextContent.Should().Contain("choose one").And.Contain("None is preferred by its date");
        cut.FindAll("[data-testid=ip-edit-evidence-recommended]").Should().BeEmpty("equal evidence means no recommendation — never the newest");
        cut.Find("[data-testid=ip-edit-evidence-none-choice]").ParentElement!.TextContent.Trim().Should().Be("Save without source evidence");
        cut.FindAll("[data-testid=ip-edit-evidence-import]").Should().BeEmpty();
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

    // ── Evidence read when the form opens; reference metadata; schema evidence ─────────────────────────────────────────────────────

    private static readonly TestPackSourceRole App = new("app", "Portal application/API", "The portal's API description") { Provider = TestPackSourceProvider.TestPackReference };

    private static IntegrationTemplateView Referenced(TestPackSourceStatus? evidence, params IntegrationTemplateSchemaCandidate[] schemas) => Intake(evidence) with
    {
        ReferenceSource = new TestPackSourceStatus(App, TestPackSourceState.ReferenceAvailable) { Reference = new("OpenAPI", "Portal app") },
        AuthenticationReference = [new("Authentication scheme", "Bearer JWT"), new("Security scheme name", "PortalToken"), new("Token providers", "provider-a, provider-b")],
        ContractReference = [new("Form data type", "portal_report_v2"), new("Content type", "application/xml"), new("Process task", "Task_1"), new("Task name", "Fill in")],
        SchemaName = "portal_report_v2", SchemaCandidates = schemas,
    };

    private static TestPackSourceStatus OneSource() => new(Mu, TestPackSourceState.AvailableNotBound) { Candidates = [Candidate(SnapshotA, "Archive with another name.zip", 'a') with { AnalysisStatus = "Partial" }] };

    private static void Expand(IRenderedComponent<IntegrationsPane> cut, string section) => cut.Find($"[data-testid=ip-edit-section-{section}] .disclosure-toggle").Click();
    private static string Hint(IRenderedComponent<IntegrationsPane> cut, string section) => cut.Find($"[data-testid=ip-edit-section-{section}] .disclosure-hint").TextContent;

    [Fact]
    public void ASnapshotImportedAfterThePageLoaded_IsAvailable_WhenTheFormOpens_NeverAStaleMissing()
    {
        var cut = Open();
        _templates = [Intake(OneSource())]; // imported elsewhere after the Integrations page read its templates
        OpenAdd(cut);
        cut.Find("[data-testid=ip-edit-template-select]").Change("pack-report-intake");

        cut.Find("[data-testid=ip-edit-evidence-state]").TextContent.Should().Be("Available · needs binding");
        cut.Find("[data-testid=ip-edit-evidence-candidate]").TextContent.Should().Be("Archive with another name.zip", "the archive name is provenance; content matched it");
        _journeys.Verify(j => j.IntegrationTemplatesAsync("dev", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.AtLeast(2));
        _api.BindCalls.Should().BeEmpty("opening the form and choosing a template never bind");
    }

    [Fact]
    public void SourceDetails_AreProgressive_AndNameTheLogicalSourceArchiveAndAnalysis()
    {
        _templates = [Intake(OneSource())];
        var cut = OpenAdd(Open());
        cut.Find("[data-testid=ip-edit-template-select]").Change("pack-report-intake");
        cut.Find("[data-testid=ip-edit-evidence-details] .disclosure-body").HasAttribute("hidden").Should().BeTrue("fingerprints and match details stay out of the main form");
        cut.Find("[data-testid=ip-edit-evidence-details] .disclosure-toggle").Click();
        var details = cut.Find("[data-testid=ip-edit-evidence-detail-list]").TextContent;
        details.Should().Contain("MU (Meldingsutveksleren)").And.Contain("Archive with another name.zip").And.Contain("sha256 aaaaaaaa…");
        cut.Find("[data-testid=ip-edit-evidence-analysis]").TextContent.Should().Be("Partial");
    }

    [Fact]
    public void Authentication_ShowsReferenceMetadata_ApartFromRunConfiguration_AndClaimsNoVerification()
    {
        _templates = [Referenced(OneSource())];
        var cut = OpenAdd(Open());
        cut.Find("[data-testid=ip-edit-template-select]").Change("pack-report-intake");
        cut.Find("[data-testid=ip-edit-template-reference]").TextContent.Should().Be("Reference available");
        cut.Find("[data-testid=ip-edit-auth-state]").TextContent.Should().Be("Authentication reference available", "known metadata is never \"Configured\" or \"Verified\"");
        cut.Find("[data-testid=ip-edit-summary-reference]").TextContent.Should().Be("Authentication and submission contract references available");

        cut.FindAll("[data-testid=ip-edit-auth-fact]").Select(f => f.TextContent).Should().Equal(
            "Authentication schemeBearer JWT", "Security scheme namePortalToken", "Token providersprovider-a, provider-b");
        cut.Find("[data-testid=ip-edit-auth-reference] h6").TextContent.Should().Be("Declared authentication");
        cut.Find("[data-testid=ip-edit-auth-reference]").TextContent.Should().Contain("Source: Test Pack reference").And.Contain("does not prove runtime authentication or configure credentials");
        cut.Find("[data-testid=ip-edit-auth-run-state]").TextContent.Should().StartWith("Configured elsewhere");
        cut.Find("[data-testid=ip-edit-auth-run-link]").GetAttribute("href").Should().Be("/integration-quality-review?view=test-verify");
        cut.Find("[data-testid=ip-edit-section-authentication]").TextContent.Should().Contain("No secrets are stored in this integration record");
        cut.Find("[data-testid=ip-edit-auth-mechanism-summary]").TextContent.Should().Contain("Producer authenticationNot required for current configuration")
            .And.Contain("Consumer authenticationNot required for current configuration");
        cut.Find("[data-testid=ip-edit-section-authentication]").TextContent.Should().NotContain("Runtime verified").And.NotContain("Not configured", "an optional mechanism is never shown as missing setup");
        cut.FindAll("[data-testid=ip-edit-section-authentication] input[type=password]").Should().BeEmpty();
    }

    [Fact]
    public void Contract_ShowsSubmissionMetadata_AndSchemaEvidenceSeparately_NeverContractVerified()
    {
        _templates = [Referenced(OneSource(), new IntegrationTemplateSchemaCandidate(SnapshotA, "Archive with another name.zip", "portal_report_v2", "Schemas/portal_report_v2.xsd"))];
        var cut = OpenAdd(Open());
        cut.Find("[data-testid=ip-edit-template-select]").Change("pack-report-intake");
        cut.Find("[data-testid=ip-edit-contract-state]").TextContent.Should().Be("Not confirmed");

        cut.FindAll("[data-testid=ip-edit-contract-fact]").Select(f => f.GetAttribute("data-label")).Should().Equal("Form data type", "Content type", "Process task", "Task name");
        cut.Find("[data-testid=ip-edit-contract-reference] h6").TextContent.Should().Be("Declared submission contract");
        cut.Find("[data-testid=ip-edit-contract-reference]").TextContent.Should().Contain("application/xml").And.Contain("Task_1").And.Contain("Fill in")
            .And.Contain("does not prove which downstream schema the receiving side actively validates");
        cut.Find("[data-testid=ip-edit-contract-schema-summary]").TextContent.Should().Be("Matching schema found in MU (Meldingsutveksleren) source");
        cut.Find("[data-testid=ip-edit-contract-schema-state]").TextContent.Should().Be("Review required", "an exact name match is evidence to review, not a confirmed relationship");
        cut.Find("[data-testid=ip-edit-contract-schema-file]").TextContent.Should().Be("Schemas/portal_report_v2.xsd");
        cut.Find("[data-testid=ip-edit-contract-reference]").TextContent.Should().NotContain("verified");
        cut.Find("[data-testid=ip-edit-contract-schema]").TextContent.Should().NotContain("verified", "an exact name match in the source is not proof of the active schema");
        cut.FindAll("[data-testid=ip-edit-contract]").Should().BeEmpty("the relationship is a state with actions, never a field to type a state into");

        // Known contracts are offered; nothing is selected for you, and confirming needs both sides and an explicit click.
        cut.Find("[data-testid=ip-edit-producer-contract-value]").TextContent.Should().Be("Not selected");
        cut.Find("[data-testid=ip-edit-contract-confirm]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=ip-edit-producer-contract-use]").Click();
        cut.Find("[data-testid=ip-edit-consumer-contract-use]").Click();
        cut.Find("[data-testid=ip-edit-contract-possible]").TextContent.Should().Contain("portal_report_v2 ↔ Schemas/portal_report_v2.xsd");
        cut.Find("[data-testid=ip-edit-contract-state]").TextContent.Should().Be("Not confirmed", "selecting both contracts is not confirming their relationship");
        cut.Find("[data-testid=ip-edit-contract-confirm]").Click();
        cut.Find("[data-testid=ip-edit-contract-state]").TextContent.Should().Be("Relationship confirmed");
        cut.Find("[data-testid=ip-edit-consumer-contract-clear]").Click();
        cut.Find("[data-testid=ip-edit-contract-state]").TextContent.Should().Be("Not confirmed", "changing a contract withdraws the confirmation");
    }

    [Theory]
    [InlineData(0, false, "Not found in the matching source")]
    [InlineData(2, false, "Review required")]
    [InlineData(1, true, "No matching source")]
    public void SchemaEvidence_IsNeverChosen_AndFollowsTheSourceState(int files, bool noSource, string expected)
    {
        var schemas = Enumerable.Range(0, files).Select(i => new IntegrationTemplateSchemaCandidate(SnapshotA, "mu.zip", "portal_report_v2", $"Schemas/v{i}/portal_report_v2.xsd")).ToArray();
        _templates = [Referenced(noSource ? Missing() : OneSource(), schemas)];
        var cut = OpenAdd(Open());
        cut.Find("[data-testid=ip-edit-template-select]").Change("pack-report-intake");
        cut.Find("[data-testid=ip-edit-contract-schema-state]").TextContent.Should().Be(expected);
        cut.FindAll("[data-testid=ip-edit-contract-schema-file]").Should().BeEmpty("a schema is shown as the candidate only when exactly one matches");
    }

    [Fact]
    public void Saving_StoresOnlyIntegrationOwnedValues_NeverReferenceFacts()
    {
        _templates = [Referenced(OneSource())];
        var cut = OpenAdd(Open());
        cut.Find("[data-testid=ip-edit-template-select]").Change("pack-report-intake");
        cut.Find("[data-testid=ip-edit-save]").Click();
        cut.WaitForAssertion(() => _api.Saved.Should().ContainSingle());
        var saved = _api.Saved.Single();
        System.Text.Json.JsonSerializer.Serialize(saved).Should().NotContain("Bearer JWT").And.NotContain("PortalToken").And.NotContain("portal_report_v2").And.NotContain("Task_1");
        (saved.ContractRelationship, saved.ProducerContractReference, saved.ConsumerContractReference).Should().Be((ContractRelationshipState.NotConfigured, (string?)null, (string?)null));
        _api.BindCalls.Should().BeEmpty();
    }

    [Fact]
    public void AMessagingType_KeepsItsPlatform_AndHttpApiNeverShowsIt()
    {
        var cut = OpenAdd(Open());
        cut.Find("[data-testid=ip-edit-kind]").Change(nameof(IntegrationKind.EventHub));
        cut.Find("[data-testid=ip-edit-platform]").GetAttribute("value").Should().Be(_api.Catalog.Platforms.First().Id, "event streams sit on the environment's messaging platform");
        cut.Find("[data-testid=ip-edit-kind]").Change(nameof(IntegrationKind.HttpApi));
        cut.FindAll("[data-testid=ip-edit-platform]").Should().BeEmpty();
        cut.Find("[data-testid=ip-edit-kind]").Change(nameof(IntegrationKind.EventHub));
        cut.Find("[data-testid=ip-edit-platform]").GetAttribute("value").Should().Be(_api.Catalog.Platforms.First().Id, "returning to messaging restores the untouched default");
    }

    // ── One generic Add integration trigger per page state ────────────────────────────────────────────────────────────────────────

    private static List<AngleSharp.Dom.IElement> GenericTriggers(IRenderedComponent<IntegrationsPane> cut) =>
        cut.FindAll("button").Where(b => b.TextContent.Trim() is "Add integration" or "Add manually").ToList();

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
