using BirkNext.Integrations;
using BirkNext.Web.Components;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

public sealed class MessageFlowReviewPanelTests : BunitContext
{
    private readonly Mock<IIntegrationCatalogApiService> _api = new();

    public MessageFlowReviewPanelTests()
    {
        _api.Setup(x => x.GetMessageFlowReviewAsync("pilot", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IntegrationMessageFlowPackage(new MessageFlowDefinition { EnvironmentId = "pilot", Name = "Message flow review" }, new(), new(false, 0, 0, [], []), [], []));
        _api.Setup(x => x.IqrSourceScopeAsync("pilot", null, It.IsAny<CancellationToken>())).ReturnsAsync(new BirkNext.SourceEvidence.ReviewSourceOptions());
        Services.AddSingleton(_api.Object);
    }

    [Fact]
    public void PilotIsExplicitlyLoadedAsDocumentedAndActiveExecutionIsBlocked()
    {
        var cut = Render<MessageFlowReviewPanel>(p => p.Add(x => x.EnvironmentId, "pilot"));
        cut.Find("[data-testid=mfr-panel]").TextContent.Should().Contain("Active test: blocked");
        cut.FindAll("button").Single(b => b.TextContent.Contains("Load documented Skolenærvær pilot")).Click();

        cut.Markup.Should().Contain("Documented").And.Contain("Not source verified").And.Contain("Needs clarification");
        cut.Markup.Should().Contain("Encrypted blob").And.Contain("Skoletjenesten").And.Contain("Person");
        cut.Markup.Should().Contain("FNR or DUF").And.Contain("BirkID").And.Contain("BarnRegistreringId");
        cut.Markup.Should().Contain("Active test: blocked").And.Contain("no messages are sent");
        cut.Markup.Should().NotContain("Run Altinn Test");
    }

    [Fact]
    public void GenericFlowCanBeLoadedWithoutProjectSpecificProductionRules()
    {
        var cut = Render<MessageFlowReviewPanel>(p => p.Add(x => x.EnvironmentId, "pilot"));
        cut.FindAll("button").Single(b => b.TextContent.Contains("Load generic example")).Click();
        cut.Markup.Should().Contain("ExternalPortal").And.Contain("MessageExchange").And.Contain("PayloadStore");
        cut.Markup.Should().Contain("Transport retry").And.Contain("Domain hold");
    }

    [Fact]
    public void XsdEvidenceComesFromTheSelectedImmutableSourceSnapshot()
    {
        var snapshotId = Guid.NewGuid();
        var contract = new BirkNext.SourceDomains.SourceContract
        {
            Id = "report-xsd", Type = BirkNext.SourceDomains.SourceContractType.XmlSchema, Name = "Report.xsd", File = "contracts/Report.xsd",
            Version = "2.0.0", XmlSchema = new BirkNext.SourceDomains.XmlSchemaEvidence("urn:report", "2.0.0", "Explicit schema version",
                [new("/Report", "{urn:report}Report", "ReportType", 1, "1", false, null, null, null, [])], [], [], []),
        };
        _api.Setup(x => x.IqrSourceScopeAsync("pilot", null, It.IsAny<CancellationToken>())).ReturnsAsync(new BirkNext.SourceEvidence.ReviewSourceOptions
        {
            Snapshots = [new BirkNext.SourceEvidence.ReviewSourceSnapshot { SnapshotId = snapshotId, ArchiveName = "report-source.zip", Fingerprint = "a1b2c3d4" }],
        });
        _api.Setup(x => x.MessageFlowSourceContractsAsync("pilot", snapshotId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MessageFlowSourceContractOptions(snapshotId, "report-source.zip", "a1b2c3d4", [contract]));

        var cut = Render<MessageFlowReviewPanel>(p => p.Add(x => x.EnvironmentId, "pilot"));
        var snapshotSelector = cut.FindAll("select").Single(s => s.ParentElement!.TextContent.Contains("Source Analysis snapshot"));
        snapshotSelector.Change(snapshotId.ToString());
        var contractSelector = cut.FindAll("select").Single(s => s.ParentElement!.TextContent.Contains("XML Schema contract"));
        contractSelector.Change("report-xsd");

        cut.Markup.Should().Contain("SourceConfirmed from this exact Source Analysis snapshot").And.Contain("a1b2c3d4").And.Contain("urn:report");
        _api.Verify(x => x.MessageFlowSourceContractsAsync("pilot", snapshotId, It.IsAny<CancellationToken>()), Times.Once);
    }
}
