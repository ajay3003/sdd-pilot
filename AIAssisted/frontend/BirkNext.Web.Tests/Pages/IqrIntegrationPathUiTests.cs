using BirkNext.Integrations;
using BirkNext.Web.Components;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;

namespace BirkNext.Web.Tests.Pages;

/// <summary>Integration path in the IQR source details and export: hops, field traceability, data minimization, reused developer tests and gaps.</summary>
public sealed class IqrIntegrationPathUiTests : BunitContext
{
    private static readonly SourceLocation At = new("Service/src/Service.cs", 12);

    private static IqrSourceSnapshot Snapshot() => new()
    {
        IntegrationId = "customer", Archive = new("customer.zip", new string('b', 64), 12), Status = SourceAnalysisStatus.Ready, AnalyzerVersion = 2,
        Tests = [new() { Id = "t-session", Class = "OutboxTests", Method = "SessionIdIsCustomerId", Layer = DeveloperTestLayer.Integration, Location = new("Service/tests/OutboxTests.cs", 7) },
            new() { Id = "t-shape", Class = "EventShapeTests", Method = "CreatedCarriesNoRawNationalId", Layer = DeveloperTestLayer.Contract, Location = new("Service/tests/OutboxTests.cs", 30) }],
        IntegrationPath = new IntegrationPathEvidence
        {
            Stages = [new("cdc", SourceStageKind.CdcField, "CDC payload", "Debezium event", ["NationalId"], At, SourceConfidence.StrongSourceEvidence, ""),
                new("CustomerRecord", SourceStageKind.AdapterModel, "CustomerRecord", "Adapter", ["NationalId"], At, SourceConfidence.StrongSourceEvidence, ""),
                new("Customer", SourceStageKind.DomainEntity, "Customer", "Service", ["NationalId"], At, SourceConfidence.StrongSourceEvidence, ""),
                new("sb:customer.events", SourceStageKind.ServiceBus, "customer.events", "Service", ["body"], At, SourceConfidence.StrongSourceEvidence, "")],
            Hops =
            [
                new() { Id = "event-outbox", From = "CustomerCreatedEvent", To = "OutboxRow", Mechanism = "Envelope serialized into OutboxRow", DeveloperTestIds = ["t-session"],
                    Coverage = [SourceCoverageStatus.DeveloperIntegrationCovered, SourceCoverageStatus.RuntimeGap], Note = "An outbox row is not a delivered message." },
                new() { Id = "outbox-servicebus", From = "OutboxRow", To = "customer.events", Mechanism = "OutboxDispatcher → ServiceBusSender", Coverage = [SourceCoverageStatus.SourceEvidenceOnly, SourceCoverageStatus.RuntimeGap] },
            ],
            Fields =
            [
                new() { Key = "CDC CustomerRecord.NationalId", OriginField = "NationalId", Sensitive = true, Minimization = "Intentional reduction / metadata projection",
                    Steps = [new(SourceStageKind.AdapterModel, "CustomerRecord", "NationalId", FieldTransformation.PassThrough, "Copied unchanged", At),
                        new(SourceStageKind.DomainEntity, "Customer", "NationalId", FieldTransformation.PassThrough, "Copied unchanged", At),
                        new(SourceStageKind.DomainEvent, "CustomerCreatedEvent", "HasNationalId", FieldTransformation.Booleanized, "Presence flag (value not copied)", At)],
                    Coverage = [SourceCoverageStatus.DeveloperContractCovered, SourceCoverageStatus.RuntimeGap], DeveloperTestIds = ["t-shape"] },
                new() { Key = "CDC CustomerRecord.Legacy", OriginField = "Legacy", Steps = [new(SourceStageKind.IngestionRequest, "CustomerIngestRequest", "LegacyCode", FieldTransformation.Dropped, "Receiver has no such property", At)],
                    Coverage = [SourceCoverageStatus.SourceEvidenceOnly], Gap = "Not resolved: CustomerRecord.LegacyCode is dropped at the adapter → ingestion boundary" },
            ],
            Boundaries = [new() { From = "CustomerRecord", To = "CustomerIngestRequest", ImplementationContract = "Partial (see mismatches)", DeveloperContractTests = "One-sided only: 1 test(s) cover the sender or the receiver; none spans the boundary",
                Mismatches = ["CustomerRecord.LegacyCode is sent but CustomerIngestRequest has no such property (ignored by the receiver)"] }],
            Events = [new() { EventType = "CustomerCreatedEvent", Topics = ["customer.events"], Subjects = ["CustomerCreated"], SessionId = "CustomerDto.CustomerId", Priority = "Normal (publisher default)",
                Fields = [new("HasNationalId", "bool", "CustomerDto.NationalId", FieldTransformation.Booleanized, false)] }],
            Rules = [new() { Kind = "SessionId", Title = "Session id on customer.events", DeveloperTestIds = ["t-session"], Coverage = [SourceCoverageStatus.DeveloperIntegrationCovered, SourceCoverageStatus.RuntimeGap],
                BirkNextAction = "Covered by developer integration test — reused as evidence; no duplicate BirkNext test." }],
            Gaps = [new(SourceCoverageStatus.CrossLayerGap, "Customer.Region is assigned on update but not change-tracked", "A change to only this field is not applied. Needs confirmation.", SourceConfidence.StrongSourceEvidence, []),
                new(SourceCoverageStatus.RuntimeGap, "Outbox → Service Bus delivery", "Not observed here.", SourceConfidence.StrongSourceEvidence, [])],
            Minimization = new DataMinimizationSummary { SensitiveFieldsEntering = ["NationalId"], RetainedInternally = ["Customer.NationalId"], ReducedMetadata = ["CustomerCreatedEvent.HasNationalId (Booleanized)"] },
            Inspected = ["Outbound HTTP PUT /api/customers/ingest with CustomerRecord"],
        },
    };

    [Fact]
    public void PathShowsHopsFieldsMinimizationRulesAndGaps_WithSourceDeveloperAndRuntimeEvidenceApart()
    {
        var cut = Render<IqrSourceDetails>(p => p.Add(c => c.Snapshot, Snapshot()));
        cut.Find("[data-testid=iqr-path-chain]").TextContent.Should().Be("CDC field → Adapter model → Domain entity → Service Bus");
        var hops = cut.FindAll("[data-testid=iqr-path-hop]");
        hops.Should().HaveCount(2);
        hops[0].TextContent.Should().Contain("Developer test exists (1: integration)").And.Contain("An outbox row is not a delivered message.");
        hops[1].QuerySelector("[data-testid=iqr-path-hop-dev]")!.TextContent.Should().Be("Source trace only");
        hops[1].TextContent.Should().Contain("Not assessed");
        var fields = cut.FindAll("[data-testid=iqr-path-field]");
        fields[0].GetAttribute("data-sensitive").Should().Be("true", "sensitive fields are listed first");
        fields[0].TextContent.Should().Contain("Sensitive (name)").And.Contain("Reduced to boolean/metadata before Service Bus").And.Contain("CustomerCreatedEvent.HasNationalId (Booleanized (flag only))")
            .And.Contain("Developer test exists (1: contract)").And.Contain("Runtime delivery: Not assessed");
        fields[1].TextContent.Should().Contain("Dropped at a boundary (reason not resolved)").And.Contain("Not resolved: CustomerRecord.LegacyCode");
        cut.Find("[data-testid=iqr-path-emitted-raw]").TextContent.Should().Be("None found");
        cut.Find("[data-testid=iqr-path-rule]").TextContent.Should().Contain("no duplicate BirkNext test").And.Contain("OutboxTests.SessionIdIsCustomerId (integration)").And.Contain("execution result unavailable");
        cut.Find("[data-testid=iqr-path-boundary]").TextContent.Should().Contain("formal schema: Not available").And.Contain("none spans the boundary").And.Contain("LegacyCode is sent");
        cut.FindAll("[data-testid=iqr-path-gap]").Select(g => g.GetAttribute("data-kind")).Should().Equal("CrossLayerGap", "RuntimeGap");
        cut.Markup.Should().NotContain("passed", "a discovered developer test is never shown as passed");
        cut.Find(".path-table").GetAttribute("tabindex").Should().Be("0");
        cut.Find(".path-table").GetAttribute("role").Should().Be("region");
    }

    [Fact]
    public void AnalyzerV1SnapshotsShowNoPathSection()
    {
        var cut = Render<IqrSourceDetails>(p => p.Add(c => c.Snapshot, Snapshot() with { IntegrationPath = null, AnalyzerVersion = 1 }));
        cut.FindAll("[data-testid=iqr-path]").Should().BeEmpty();
    }

    [Fact]
    public void ExportCarriesPathBoundariesFieldsMinimizationRulesGapsAndInspectedSource_WithoutValues()
    {
        var html = new ReportExportService().ExportIntegrationReview(new IntegrationReviewResult { SourceSnapshots = [Snapshot()] }, "Example");
        html.Should().Contain("Integration path").And.Contain("CDC field → Adapter model → Domain entity → Service Bus")
            .And.Contain("CustomerRecord → CustomerIngestRequest").And.Contain("Not available")
            .And.Contain("CDC CustomerRecord.NationalId (sensitive by name)").And.Contain("Reduced to boolean/metadata before Service Bus")
            .And.Contain("Data minimization (field names only)").And.Contain("Session id on customer.events").And.Contain("exists; execution unavailable")
            .And.Contain("Customer.Region is assigned on update but not change-tracked").And.Contain("Source inspected").And.Contain("Outbound HTTP PUT /api/customers/ingest");
        html[html.IndexOf("<body", StringComparison.Ordinal)..].Should().NotContain("test passed").And.NotContain("tests passed", "the shared stylesheet has a badge-passed class; the report text never claims a pass");
    }

    [Fact]
    public void Presentation_KeepsTransformationVocabularyExplicit()
    {
        IqrPathPresentation.Transformation(FieldTransformation.MetadataOnly).Should().Be("Metadata only (field name)");
        IqrPathPresentation.Transformation(FieldTransformation.FilteredIntentionally).Should().Be("Filtered intentionally");
        IqrPathPresentation.Coverage([SourceCoverageStatus.SourceEvidenceOnly, SourceCoverageStatus.RuntimeGap], 0).Should().Be("Source trace only");
        IqrPathPresentation.KeyTransformation(new FieldTrace { Minimization = "Potential data-minimization issue", Steps = [new(SourceStageKind.DomainEvent, "E", "Email", FieldTransformation.PassThrough, "", null)] })
            .Should().Be("Raw value emitted in an event");
        IqrPathPresentation.GapKind(SourceCoverageStatus.E2EGap).Should().Be("E2E gap");
    }
}
