using BirkNext.Integrations;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Tests.Integration;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Target Environment → Integrations detail: grouped fields with exact states (Not applicable / Not assessed / Needs confirmation are never
/// "Not configured"), source evidence separate from source configuration, causal readiness, and evidence-first integration contracts with
/// upload as the fallback. A generic HTTP/API webhook beside the M2LB Event Hub fixture; no product is special-cased.
/// </summary>
public sealed class IntegrationFieldSemanticsUiTests : BunitContext
{
    private const string WebhookId = "dev:http:report-intake";
    private static readonly Guid Bound = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private readonly FakeIntegrationCatalogApi _api = new() { Catalog = M2lbFixture.Catalog() };
    private readonly FrontendAnalysisProfile _profile = new() { Id = "dev", Name = "Development", EnvironmentType = FrontendEnvironmentType.Development, TargetUrl = "https://app.example.invalid/" };

    private static readonly IntegrationDefinition Webhook = new()
    {
        Id = WebhookId, EnvironmentId = "dev", DisplayName = "Partner – report intake", Kind = IntegrationKind.HttpApi, SystemName = "Partner → Receiver",
        EndpointOrTopic = "https://receiver.example.invalid/api/partner/webhook", Producer = "Partner platform",
        Consumer = new IntegrationConsumer { DisplayName = "Message receiver", MappingState = ConsumerMappingState.NeedsConfirmation },
        Origin = IntegrationRecordOrigin.Template, TemplateKey = "report-intake",
    };

    private static IntegrationContractCandidate Candidate(string file, IntegrationContractEvidenceRank rank, string why, string? version = "2.0.0") => new()
    {
        ContractId = $"xmlschema:{file}", Name = System.IO.Path.GetFileNameWithoutExtension(file), File = file, Type = IntegrationContractType.Xsd, Version = version,
        Rank = rank, Evidence = [new(rank, why, file, 1)], ContentAvailable = true,
    };

    private static IntegrationContractDiscovery Discovery() => new()
    {
        IntegrationId = WebhookId, SourceBound = true, SnapshotId = Bound, Archive = "receiver.zip", SourceSide = IntegrationSourceSide.Consumer,
        SourceSideBasis = "The bound source implements the consumer side (template metadata).",
        Producer = new IntegrationContractSide
        {
            Role = IntegrationContractRole.Producer, Party = "Partner platform", State = IntegrationContractCandidateState.Candidate,
            Primary = Candidate("Receiver/Schemas/partner_report_v2.xsd", IntegrationContractEvidenceRank.ContractMetadata, "Named exactly like the declared data type partner_report_v2"),
            Candidates = [Candidate("Receiver/Schemas/partner_report_v2.xsd", IntegrationContractEvidenceRank.ContractMetadata, "Named exactly like the declared data type partner_report_v2")],
            Explanation = "Candidate found (declared contract metadata); no handler or validator reference resolves it. Review it before using it.",
            ReferenceHints = ["Form data type: partner_report_v2 (Partner app reference · metadata, not a contract)"],
        },
        Consumer = new IntegrationContractSide
        {
            Role = IntegrationContractRole.Consumer, Party = "Message receiver", State = IntegrationContractCandidateState.NeedsConfirmation,
            Candidates =
            [
                Candidate("Receiver/Schemas/report.v1.0.0.xsd", IntegrationContractEvidenceRank.ComponentAssociation, "Packaged with its project", "1.0.0"),
                Candidate("Receiver/Schemas/report.v2.0.0.xsd", IntegrationContractEvidenceRank.ComponentAssociation, "Packaged with its project"),
            ],
            Supporting = [Candidate("Receiver/Schemas/report.lib.xsd", IntegrationContractEvidenceRank.ComponentAssociation, "Packaged with its project") with { IsEntry = false }],
            Explanation = "2 consumer candidates rest on equally strong evidence (component association); none is chosen. 2 versions are present; the highest is never chosen automatically.",
        },
        Compatibility = IntegrationContractCompatibilityState.NotAssessable, CompatibilityReason = "Needs a usable contract on both sides (resolved from source evidence, or selected/uploaded): producer and consumer missing.",
    };

    public IntegrationFieldSemanticsUiTests()
    {
        _api.Catalog = _api.Catalog with { Integrations = [.. _api.Catalog.Integrations, Webhook] };
        _api.EvidenceBindings = [new IntegrationEvidenceBinding { EnvironmentId = "dev", IntegrationId = WebhookId, SnapshotId = Bound, ArchiveName = "receiver.zip" }];
        _api.Discoveries[WebhookId] = Discovery();
        Services.AddSingleton<IIntegrationCatalogApiService>(_api);
        Services.AddSingleton(new IntegrationMappingEvidenceSession());
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<IntegrationsPane> Open() => Render<IntegrationsPane>(p => p.Add(c => c.Profile, _profile));

    private static AngleSharp.Dom.IElement Row(IRenderedComponent<IntegrationsPane> cut, string id) =>
        cut.FindAll("[data-testid=ip-row]").Single(r => r.GetAttribute("data-integration-id") == id);

    private static AngleSharp.Dom.IElement OpenDetail(IRenderedComponent<IntegrationsPane> cut, string id)
    {
        Row(cut, id).QuerySelector("[data-testid=ip-row-view]")!.Click();
        return cut.Find("[data-testid=ip-row-detail]");
    }

    private static AngleSharp.Dom.IElement? FieldOf(AngleSharp.Dom.IElement detail, IntegrationField field) =>
        detail.QuerySelectorAll("[data-testid=ip-field]").FirstOrDefault(f => f.GetAttribute("data-field") == field.ToString());

    [Fact]
    public void HttpApiDetail_IsGrouped_AndNeverCallsNotApplicableOrUnassessedFieldsNotConfigured()
    {
        var cut = Open();
        Row(cut, WebhookId).GetAttribute("data-configuration").Should().Be(nameof(IntegrationConfigurationState.NeedsConfirmation), "only the consumer mapping is open; no auth is required");
        var detail = OpenDetail(cut, WebhookId);
        detail.QuerySelectorAll("section.ip-group h4").Select(h => h.TextContent).Should().Equal(
            ["Identity & routing", "Evidence", "Authentication", "Integration contracts", "Runtime / health", "Readiness"]);

        FieldOf(detail, IntegrationField.ConsumerGroup).Should().BeNull("a not-applicable field is not rendered as a row");
        FieldOf(detail, IntegrationField.WorkerEndpoint).Should().BeNull();
        detail.QuerySelector("[data-testid=ip-detail-not-applicable]")!.TextContent.Should().Contain("Not applicable for HTTP/API").And.Contain("Consumer group").And.Contain("Worker endpoint").And.Contain("Source resource");
        FieldOf(detail, IntegrationField.ProducerAuthentication)!.GetAttribute("data-resolution").Should().Be(nameof(IntegrationFieldResolution.NotAssessed));
        FieldOf(detail, IntegrationField.ConsumerAuthentication)!.QuerySelector(".ip-pill")!.TextContent.Should().Be("Not assessed");
        FieldOf(detail, IntegrationField.HealthEndpoint)!.TextContent.Should().Contain("Not assessed").And.Contain("Optional");
        FieldOf(detail, IntegrationField.ConsumerMapping)!.GetAttribute("data-resolution").Should().Be(nameof(IntegrationFieldResolution.NeedsConfirmation));
        FieldOf(detail, IntegrationField.ConfiguredChannel)!.TextContent.Should().Contain("Configured").And.Contain("Provenance: Template");
        detail.QuerySelector("[data-testid=ip-detail-origin]")!.TextContent.Should().Contain("not source or runtime verification");
        detail.TextContent.Should().NotContain("Unknown / not configured").And.NotContain("Health / worker URL").And.NotContain("not implemented");
        detail.QuerySelectorAll(".ip-pill").Select(p => p.TextContent).Should().NotContain("Not configured");
    }

    [Fact]
    public void SourceEvidenceIsBound_WhileSourceConfigurationIsNotApplicable_AndRuntimeEvidenceIsUnavailableNotNotConfigured()
    {
        var cut = Open();
        Row(cut, WebhookId).QuerySelector("[data-testid=ip-row-runtime]")!.GetAttribute("data-runtime").Should().Be(nameof(RuntimeEvidenceStatus.Unavailable));
        Row(cut, WebhookId).QuerySelector("[data-testid=ip-row-runtime-configure]").Should().BeNull("there is no runtime provider to configure for HTTP/API");
        var detail = OpenDetail(cut, WebhookId);
        detail.QuerySelector("[data-testid=ip-detail-source-evidence]")!.GetAttribute("data-bound").Should().Be("true");
        detail.QuerySelector("[data-testid=ip-detail-source-evidence]")!.TextContent.Should().Contain("Bound").And.Contain("receiver.zip");
        detail.QuerySelector("[data-testid=ip-detail-not-applicable]")!.TextContent.Should().Contain("Source resource");
        var runtime = detail.QuerySelector("[data-testid=ip-detail-runtime]")!;
        runtime.GetAttribute("data-runtime").Should().Be(nameof(RuntimeEvidenceStatus.Unavailable));
        runtime.TextContent.Should().Contain("No runtime evidence provider exists for HTTP/API");
        detail.QuerySelector("[data-testid=ip-detail-origin]")!.TextContent.Should().Contain("not source or runtime verification");
    }

    [Fact]
    public void Readiness_ListsExactCauses_AndNotApplicableFieldsOnlyAsNotBlocking()
    {
        var detail = OpenDetail(Open(), WebhookId);
        var configuration = detail.QuerySelector("[data-testid=ip-detail-configuration]")!;
        configuration.GetAttribute("data-state").Should().Be(nameof(IntegrationConfigurationState.NeedsConfirmation));
        configuration.QuerySelector("[data-testid=ip-config-missing]").Should().BeNull("nothing required is missing");
        configuration.QuerySelector("[data-testid=ip-config-confirm]")!.TextContent.Should().Contain("Consumer mapping");
        configuration.QuerySelector("[data-testid=ip-config-not-assessed]")!.TextContent.Should().Contain("Producer authentication").And.Contain("Health endpoint (optional)");
        configuration.QuerySelector("[data-testid=ip-config-not-applicable]")!.TextContent.Should().Contain("Consumer group");

        var readiness = detail.QuerySelector("[data-testid=ip-detail-readiness]")!;
        readiness.GetAttribute("data-readiness").Should().Be("Partial");
        readiness.QuerySelector("[data-testid=ip-readiness-attention]")!.TextContent.Should().Contain("Consumer mapping needs confirmation")
            .And.Contain("Producer contract: candidate found").And.Contain("Consumer contract: needs confirmation").And.Contain("Contract compatibility not assessed")
            .And.NotContain("Consumer group");
        readiness.QuerySelector("[data-testid=ip-readiness-ready]")!.TextContent.Should().Contain("Source evidence bound (receiver.zip)");
        readiness.QuerySelector("[data-testid=ip-readiness-not-blocking]")!.TextContent.Should().Contain("Consumer group not applicable").And.Contain("Worker endpoint not applicable");
        readiness.TextContent.Should().NotContain("Configuration only");
    }

    [Fact]
    public void Contracts_AreEvidenceFirst_PerSide_WithUploadAsTheFallback()
    {
        var cut = Open();
        OpenDetail(cut, WebhookId);
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=ip-contract-state]").Should().HaveCount(2));
        cut.Find("[data-testid=ip-contracts] h4").TextContent.Should().Be("Integration contracts");
        cut.Markup.Should().NotContain("Event contracts (JSON Schema)");
        var sides = cut.FindAll("[data-testid=ip-contract]");
        sides.Select(s => s.GetAttribute("data-state")).Should().Equal(["Candidate", "NeedsConfirmation"]);
        sides[0].QuerySelector("h5")!.TextContent.Should().Be("Producer — Partner platform");
        sides[0].QuerySelector("[data-testid=ip-contract-why]")!.TextContent.Should().Contain("declared data type");
        sides[0].TextContent.Should().Contain("metadata, not a contract");
        sides[1].QuerySelector("[data-testid=ip-contract-primary]").Should().BeNull("several versions: none is chosen");
        // Candidates exist: review is the primary action, upload is "Upload different contract".
        sides[0].QuerySelector("[data-testid=ip-contract-review]")!.TextContent.Should().Be("Review candidate (1)");
        sides[0].QuerySelector(".ip-file-button span")!.TextContent.Should().Be("Upload different contract");
        sides[0].QuerySelector("input[type=file]")!.GetAttribute("accept").Should().Be(".json,.xsd");
        cut.Find("[data-testid=ip-contract-compatibility]").GetAttribute("data-state").Should().Be(nameof(IntegrationContractCompatibilityState.NotAssessable));
    }

    [Fact]
    public void ContractRelationship_ShowsProducerDeclaration_Candidates_Differences_AndRuntimeValidationNotAssessed()
    {
        _api.Discoveries[WebhookId] = Discovery() with
        {
            Relationship = new BirkNext.ContractSchemaEvidence.ContractRelationshipAssessment(
                new("Partner app reference", DataType: "Report", ContentType: "application/xml", Task: "Task_1"),
                [
                    new("xsd:Receiver/Schemas/report.v1.0.0.xsd", "report.v1.0.0", "Receiver/Schemas/report.v1.0.0.xsd", "1.0.0", Bound,
                        [new("root", "Message root Report matches the declared data type", BirkNext.ContractSchemaEvidence.ContractMatchStrength.Strong)],
                        BirkNext.ContractSchemaEvidence.ContractRelationshipReviewState.StrongCandidate, "Strong candidate · needs review"),
                    new("xsd:Receiver/Schemas/report.v2.0.0.xsd", "report.v2.0.0", "Receiver/Schemas/report.v2.0.0.xsd", "2.0.0", Bound,
                        [new("root", "Message root Report matches the declared data type", BirkNext.ContractSchemaEvidence.ContractMatchStrength.Strong)],
                        BirkNext.ContractSchemaEvidence.ContractRelationshipReviewState.StrongCandidate, "Strong candidate · needs review"),
                ],
                BirkNext.ContractSchemaEvidence.ContractRelationshipReviewState.Ambiguous, "Ambiguous · needs selection",
                "2 candidates are equally supported; none is chosen (never the newest by version or date).", ["Version difference: producer 2.0.0, consumer candidate report.v1.0.0 1.0.0 · review required"]),
        };
        var cut = Open();
        OpenDetail(cut, WebhookId);
        cut.WaitForAssertion(() => cut.Find("[data-testid=ip-contract-relationship]"));
        var relationship = cut.Find("[data-testid=ip-contract-relationship]");
        relationship.GetAttribute("data-state").Should().Be("Ambiguous");
        relationship.TextContent.Should().Contain("Ambiguous · needs selection").And.Contain("none is chosen");
        cut.Find("[data-testid=ip-contract-producer-declaration]").TextContent.Should().Contain("data type Report · application/xml · task Task_1").And.Contain("not the consumer schema");
        cut.FindAll("[data-testid=ip-contract-relationship-candidate]").Should().HaveCount(2);
        cut.Find("[data-testid=ip-contract-relationship-differences]").TextContent.Should().Contain("Version difference").And.NotContain("ncompatib");
        cut.Find("[data-testid=ip-contract-runtime-validation]").TextContent.Should().Contain("Not assessed");
        cut.FindAll("[data-testid=ip-contract-view-consumer]")[1].GetAttribute("href").Should().Be($"schema-studio?snapshot={Bound}&contract=Receiver%2FSchemas%2Freport.v2.0.0.xsd");
        _api.Calls.Should().NotContain(c => c.StartsWith("contract-select") || c.StartsWith("save"), "reviewing a relationship selects, binds and saves nothing");
    }

    [Fact]
    public void ReviewingCandidates_ShowsEvidenceAndSupportingSchemas_AndUsingOneStoresIt()
    {
        var cut = Open();
        OpenDetail(cut, WebhookId);
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=ip-contract-review]").Should().HaveCount(2));
        cut.FindAll("[data-testid=ip-contract-review]")[1].Click();
        var list = cut.Find("[data-testid=ip-contract-candidates]");
        cut.FindAll("[data-testid=ip-contract-review]")[1].GetAttribute("aria-expanded").Should().Be("true");
        list.QuerySelectorAll("[data-testid=ip-contract-candidate]").Should().HaveCount(2);
        list.QuerySelector("[data-testid=ip-contract-supporting]")!.TextContent.Should().Contain("report.lib.xsd").And.Contain("never the primary contract");
        list.QuerySelectorAll("[data-testid=ip-contract-use]")[1].Click();
        cut.WaitForAssertion(() => _api.Calls.Should().Contain($"contract-select:{WebhookId}:Consumer:xmlschema:Receiver/Schemas/report.v2.0.0.xsd"));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=ip-contract]")[1].GetAttribute("data-configured").Should().Be("true"));
        cut.FindAll("[data-testid=ip-contract]")[1].QuerySelector("[data-testid=ip-contract-origin]")!.TextContent.Should().Contain("Selected from source evidence (receiver.zip)");
        _api.EvidenceBindings.Single().SnapshotId.Should().Be(Bound, "selecting a contract never changes the binding");
    }

    [Fact]
    public void ASelectionFromAnEarlierBinding_IsFlaggedStale()
    {
        _api.Contracts.Add(new IntegrationContractArtifact
        {
            EnvironmentId = "dev", IntegrationId = WebhookId, Role = IntegrationContractRole.Producer, FileName = "partner_report_v2.xsd", Format = "XSD", ContentHash = new string('c', 64),
            Origin = IntegrationContractOrigin.SourceSelection, SourceSnapshotId = Guid.NewGuid(), SourceArchive = "receiver-old.zip", SourceContractId = "xmlschema:Receiver/Schemas/partner_report_v2.xsd",
        });
        var cut = Open();
        OpenDetail(cut, WebhookId);
        cut.WaitForAssertion(() => cut.Find("[data-testid=ip-contract-stale]").TextContent.Should().Contain("earlier source binding"));
        cut.Find("[data-testid=ip-readiness-attention]").TextContent.Should().Contain("Producer contract was selected from an earlier source binding");
        cut.Find("[data-testid=ip-contract-use-primary]").TextContent.Should().Be("Use this contract", "it can be confirmed again explicitly");
    }

    [Fact]
    public void WithoutDiscovery_UploadIsOffered_AndNothingIsInvented()
    {
        _api.Discoveries.Clear();
        var cut = Open();
        OpenDetail(cut, WebhookId);
        cut.WaitForAssertion(() => cut.Find("[data-testid=ip-contract-discovery-unavailable]"));
        cut.FindAll("[data-testid=ip-contract-review]").Should().BeEmpty();
        cut.FindAll(".ip-contract .ip-file-button span").Select(s => s.TextContent).Should().Equal(["Upload contract", "Upload contract"]);
    }

    [Fact]
    public void MessagingDetail_KeepsConsumerGroupAndWorkerEndpoint()
    {
        var cut = Open();
        var detail = OpenDetail(cut, "dev:eventhub:birk-cdc:dbo.Person");
        FieldOf(detail, IntegrationField.ConsumerGroup)!.GetAttribute("data-requirement").Should().Be(nameof(IntegrationFieldRequirement.Conditional));
        FieldOf(detail, IntegrationField.WorkerEndpoint)!.GetAttribute("data-requirement").Should().Be(nameof(IntegrationFieldRequirement.Optional));
        detail.QuerySelector("[data-testid=ip-detail-runtime]")!.GetAttribute("data-runtime").Should().NotBe(nameof(RuntimeEvidenceStatus.Unavailable));
        detail.QuerySelector("[data-testid=ip-detail-not-applicable]").Should().BeNull();
    }
}
