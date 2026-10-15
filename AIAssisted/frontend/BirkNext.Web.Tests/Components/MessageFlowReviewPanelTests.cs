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

    private IRenderedComponent<MessageFlowReviewPanel> RenderWith(AltinnTestConfiguration configuration, IntegrationTestReadiness? readiness = null)
    {
        _api.Setup(x => x.GetMessageFlowReviewAsync("pilot", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IntegrationMessageFlowPackage(new MessageFlowDefinition { EnvironmentId = "pilot", Name = "Message flow review" }, configuration,
                readiness ?? new(false, 0, 0, [], []), [], []));
        return Render<MessageFlowReviewPanel>(p => p.Add(x => x.EnvironmentId, "pilot"));
    }

    private static AngleSharp.Dom.IElement Field(IRenderedComponent<MessageFlowReviewPanel> cut, string label) =>
        cut.FindAll("label").Single(l => l.TextContent.Trim().StartsWith(label, StringComparison.Ordinal)).QuerySelector("input, select")!;

    [Fact]
    public void AltinnReadiness_IsCompact_PrimaryBlockersFirst_AndEveryPrerequisiteStaysBehindADisclosure()
    {
        var cut = RenderWith(new(), new(false, 1, 5,
        [
            new("app", "App identifier", true, "Configured", "Set."), new("auth", "Authentication type", false, "Missing", "Choose ID-porten."),
            new("scopes", "Scopes", false, "Missing", "Documented scopes are not configured."), new("reporter", "Test reporter", false, "Missing", "No reporter."),
            new("data", "Synthetic data", false, "Missing", "Not approved."),
        ], []));

        cut.Find("[data-testid=mfr-readiness-count]").TextContent.Should().StartWith("1 / 5 prerequisites configured");
        cut.FindAll("[data-testid=mfr-readiness-blockers] li strong").Select(e => e.TextContent).Should().Equal("Authentication type", "Scopes", "Test reporter");
        cut.Find("[data-testid=mfr-readiness]").TextContent.Should().Contain("1 more in the full list");
        cut.Find("[data-testid=mfr-readiness-all-toggle]").GetAttribute("aria-expanded").Should().Be("false");
        var items = cut.FindAll("[data-testid=mfr-readiness-item]");
        items.Select(i => i.GetAttribute("data-satisfied")).Should().Equal(["true", "false", "false", "false", "false"], "the calculation is the backend's; every item is kept");
        items.Should().OnlyContain(i => i.Children.Length == 2, "a mark and one text block: no text can fall into the narrow mark column");
        cut.Find("[data-testid=mfr-configure-altinn]").TextContent.Should().Be("Configure Altinn");
    }

    [Fact]
    public void AltinnConfiguration_IsGrouped_WithOneSafeReferenceWarning_AndLowFrequencyFieldsUnderAdvanced()
    {
        var cut = RenderWith(new());

        cut.FindAll("[data-testid=mfr-config] legend").Select(l => l.TextContent).Should().Equal("Application", "Authentication", "Reporter & test data", "Message transport", "Contracts & correlation");
        cut.Find("[data-testid=mfr-safe-warning]").TextContent.Should().Be("Safe references only. Never enter secrets, tokens, private keys, connection strings, message payloads or real personal data.");
        System.Text.RegularExpressions.Regex.Matches(cut.Markup, "Safe references only").Count.Should().Be(1, "the warning is not repeated per group");
        cut.Find("[data-testid=mfr-advanced-toggle]").GetAttribute("aria-expanded").Should().Be("false");
        var advanced = cut.Find("[data-testid=mfr-advanced-fields]").TextContent;
        foreach (var label in new[] { "Receipt mode/reference", "Checkpoint configuration reference", "Correlation mapping reference", "Official documentation URLs", "Documentation platform/product", "Documentation retrieved on",
            "Certificate reference", "Key reference", "Queue reference", "Payload store reference" })
            advanced.Should().Contain(label, "with no certificate authentication and no broker transfer these are low-frequency");
        cut.Find("[data-testid=mfr-group-application]").TextContent.Should().Contain("App identifier").And.Contain("Submission mode");
        cut.Find("[data-testid=mfr-group-contracts]").TextContent.Should().Contain("Source Analysis snapshot");
        cut.FindAll("[data-testid=mfr-checkpoints-toggle], [data-testid=mfr-test-plan-toggle], [data-testid=mfr-manual-toggle]").Should().HaveCount(3)
            .And.OnlyContain(t => t.GetAttribute("aria-expanded") == "false", "checkpoints, test plan and manual procedure are collapsed");
    }

    [Fact]
    public void CertificateAndBrokerFields_ArePrimary_OnlyWhenTheConfigurationUsesThem()
    {
        var cut = RenderWith(new() { AuthenticationType = ExternalAuthenticationType.Maskinporten, SubmissionMode = ExternalSubmissionMode.BrokerTransfer });

        cut.Find("[data-testid=mfr-group-authentication]").TextContent.Should().Contain("Certificate reference").And.Contain("Key reference");
        cut.Find("[data-testid=mfr-group-transport]").TextContent.Should().Contain("Queue reference").And.Contain("Payload store reference");
        cut.Find("[data-testid=mfr-advanced-fields]").TextContent.Should().NotContain("Certificate reference").And.NotContain("Queue reference");
        cut.FindAll("label").Count(l => l.TextContent.StartsWith("Queue reference", StringComparison.Ordinal)).Should().Be(1, "each field is rendered once");
    }

    [Fact]
    public void EveryFieldIsStillEditable_AndSaved_WhereverItIsShown()
    {
        IntegrationMessageFlowPackage? saved = null;
        _api.Setup(x => x.SaveMessageFlowReviewAsync("pilot", It.IsAny<IntegrationMessageFlowPackage>(), It.IsAny<CancellationToken>()))
            .Callback<string, IntegrationMessageFlowPackage, CancellationToken>((_, package, _) => saved = package)
            .ReturnsAsync((string _, IntegrationMessageFlowPackage package, CancellationToken _) => package);
        var cut = RenderWith(new());
        var values = new Dictionary<string, string>
        {
            ["App identifier"] = "ttd/skole-app", ["Client / app reference"] = "client-ref", ["Configured scopes"] = "altinn:instances.read", ["Test reporter reference"] = "reporter-a",
            ["MU channel reference"] = "mu-channel", ["Contract reference"] = "contract-ref", ["Certificate reference"] = "cert-ref", ["Key reference"] = "key-ref",
            ["Queue reference"] = "queue-ref", ["Payload store reference"] = "store-ref", ["Receipt mode/reference"] = "receipt-ref",
            ["Checkpoint configuration reference"] = "checkpoint-ref", ["Correlation mapping reference"] = "correlation-ref", ["Documentation platform/product"] = "Altinn 3",
        };
        foreach (var (label, value) in values) Field(cut, label).Change(value);
        cut.FindAll("button").Single(b => b.TextContent.Contains("Save review configuration")).Click();

        cut.WaitForAssertion(() => saved.Should().NotBeNull());
        var c = saved!.AltinnConfiguration;
        (c.AppIdentifier, c.ClientReference, c.ReporterReference, c.MuChannelReference, c.ContractReference).Should().Be(("ttd/skole-app", "client-ref", "reporter-a", "mu-channel", "contract-ref"));
        c.ConfiguredScopes.Should().Equal("altinn:instances.read");
        (c.CertificateReference, c.KeyReference, c.QueueReference, c.PayloadStoreReference).Should().Be(("cert-ref", "key-ref", "queue-ref", "store-ref"), "fields under Advanced are saved as before");
        (c.ReceiptMode, c.CheckpointConfigurationReference, c.CorrelationMappingReference, c.DocumentationPlatform).Should().Be(("receipt-ref", "checkpoint-ref", "correlation-ref", "Altinn 3"));
        c.ProductionBlocked.Should().BeTrue();
    }

    private AltinnTestConfiguration SavedAfter(AltinnTestConfiguration stored, string load)
    {
        IntegrationMessageFlowPackage? saved = null;
        _api.Setup(x => x.SaveMessageFlowReviewAsync("pilot", It.IsAny<IntegrationMessageFlowPackage>(), It.IsAny<CancellationToken>()))
            .Callback<string, IntegrationMessageFlowPackage, CancellationToken>((_, package, _) => saved = package)
            .ReturnsAsync((string _, IntegrationMessageFlowPackage package, CancellationToken _) => package);
        var cut = RenderWith(stored);
        cut.FindAll("button").Single(b => b.TextContent.Contains(load)).Click();
        cut.FindAll("button").Single(b => b.TextContent.Contains("Save review configuration")).Click();
        cut.WaitForAssertion(() => saved.Should().NotBeNull());
        return saved!.AltinnConfiguration;
    }

    [Fact]
    public void NewSkolenaervaerConfiguration_DefaultsToTestAndItsApp_AndDefaultsNothingElse()
    {
        var c = SavedAfter(new(), "Load documented Skolenærvær pilot");
        (c.Environment, c.AppIdentifier).Should().Be((ExternalTestEnvironment.Test, "buf/skolenaervaer"));
        c.ClientReference.Should().BeNull("no ID-porten client id is ever defaulted");
        c.ConfiguredScopes.Should().BeEmpty();
        c.VerifiedScopes.Should().BeEmpty();
        (c.ReporterReference, c.SyntheticIdentitySetReference, c.ReporterApprovedForTest, c.ApprovedSyntheticData).Should().Be((null, null, false, false));
        c.VerificationState.Should().Be(FlowEvidenceState.NotAssessed, "a default is not verified");
    }

    [Theory]
    [InlineData(ExternalTestEnvironment.Staging, "another/test-app")]
    [InlineData(ExternalTestEnvironment.Test, "buf/skolenaervaer")]
    [InlineData(ExternalTestEnvironment.Test, "another/test-app")]
    public void SkolenaervaerPilot_KeepsAnExplicitEnvironmentAndApp(ExternalTestEnvironment environment, string app)
    {
        var c = SavedAfter(new() { Environment = environment, AppIdentifier = app }, "Load documented Skolenærvær pilot");
        (c.Environment, c.AppIdentifier).Should().Be((environment, app));
    }

    [Fact]
    public void SkolenaervaerPilot_FillsOnlyTheMissingValue()
    {
        SavedAfter(new() { Environment = ExternalTestEnvironment.Staging }, "Load documented Skolenærvær pilot").AppIdentifier.Should().Be("buf/skolenaervaer");
        SavedAfter(new() { AppIdentifier = "another/test-app" }, "Load documented Skolenærvær pilot").Environment.Should().Be(ExternalTestEnvironment.Test);
    }

    [Fact]
    public void GenericOrBlankConfiguration_NeverGetsTheSkolenaervaerApp()
    {
        var generic = SavedAfter(new(), "Load generic example");
        (generic.Environment, generic.AppIdentifier).Should().Be((ExternalTestEnvironment.Unknown, null));
        var cut = RenderWith(new());
        Field(cut, "App identifier").GetAttribute("value").Should().BeNullOrEmpty("a blank IQR configuration is generic until a pilot is loaded");
    }
}
