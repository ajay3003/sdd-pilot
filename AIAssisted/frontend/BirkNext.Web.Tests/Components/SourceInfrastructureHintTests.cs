using BirkNext.Integrations;
using BirkNext.SourceArchitecture;
using BirkNext.SourceDomains;
using BirkNext.Web.Components;
using BirkNext.Web.Components.SourceDomains;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// The shared "Declared in source" hint and its use in integration settings: suggestions are compared with the value being edited through
/// the shared comparer, "Use detected value" fills the form only (nothing is saved until the person saves), the chosen value records that it
/// came from a source declaration, and no hint appears when Source Analysis has no infrastructure evidence.
/// </summary>
public sealed class SourceInfrastructureHintTests : BunitContext
{
    private readonly Mock<IIntegrationCatalogApiService> _api = new();

    public SourceInfrastructureHintTests()
    {
        Services.AddSingleton(_api.Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static SourceResourceCandidate Candidate(string name, InfrastructureResourceKind kind, string type) =>
        new($"infra/{type}.x", name, kind, type, new(SourceEnvironmentKind.QA, "qa"), "tfvars infra/qa.tfvars", "infra/main.tf", 7, ArchitectureEvidenceState.Confirmed);

    private void Offer(InfrastructureResourceKind kind, params SourceResourceCandidate[] candidates) =>
        _api.Setup(a => a.InfrastructureSuggestionAsync(It.IsAny<string>(), kind, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SourceInfrastructureSuggestion
            {
                Comparison = new() { Kind = kind, Candidates = [.. candidates], SourceFingerprint = "abcdef0123456789", AnalyzerVersion = 2, EnvironmentBasis = "explicit environment variable file" },
                SnapshotId = Guid.NewGuid(), ArchiveName = "payments.zip", Fingerprint = "abcdef0123456789", SnapshotBasis = "newest snapshot with infrastructure evidence",
            });

    [Fact]
    public void Hint_compares_the_edited_value_and_shows_provenance()
    {
        Offer(InfrastructureResourceKind.ConsumerGroup, Candidate("ledger-qa", InfrastructureResourceKind.ConsumerGroup, "azurerm_eventhub_consumer_group"));
        var cut = Render<SourceInfrastructureHint>(p => p.Add(x => x.EnvironmentId, "payments-qa").Add(x => x.Kind, InfrastructureResourceKind.ConsumerGroup).Add(x => x.Field, "Consumer group").Add(x => x.TestId, "h"));
        cut.WaitForElement("[data-testid=h]").GetAttribute("data-state").Should().Be("NoConfiguredValue");
        cut.Find("[data-testid=h-candidate]").TextContent.Should().Contain("ledger-qa").And.Contain("qa").And.Contain("infra/main.tf:7");
        cut.Find("[data-testid=h-provenance]").TextContent.Should().Contain("payments.zip").And.Contain("abcdef01").And.Contain("Open Infrastructure evidence");
        cut.Render(p => p.Add(x => x.Configured, "ledger-other"));
        cut.Find("[data-testid=h]").GetAttribute("data-state").Should().Be("Differs");
        cut.Find("[data-testid=h-note]").TextContent.Should().Contain("stays as configured");
        cut.Render(p => p.Add(x => x.Configured, "LEDGER-QA"));
        cut.Find("[data-testid=h-state]").TextContent.Should().Be("Matches source");
        cut.FindAll("[data-testid=h-use]").Should().BeEmpty();
    }

    [Fact]
    public void No_infrastructure_evidence_renders_nothing()
    {
        var cut = Render<SourceInfrastructureHint>(p => p.Add(x => x.EnvironmentId, "payments-qa").Add(x => x.Kind, InfrastructureResourceKind.ConsumerGroup).Add(x => x.Field, "Consumer group"));
        cut.Markup.Trim().Should().BeEmpty("source evidence is optional enrichment; the form works without it");
    }

    [Fact]
    public void Use_detected_value_fills_the_form_and_does_not_save()
    {
        Offer(InfrastructureResourceKind.ConsumerGroup, Candidate("ledger-qa", InfrastructureResourceKind.ConsumerGroup, "azurerm_eventhub_consumer_group"));
        Offer(InfrastructureResourceKind.StorageAccount, Candidate("stpayqackpt", InfrastructureResourceKind.StorageAccount, "azurerm_storage_account"));
        var saved = new List<IntegrationRuntimeEvidenceSettings>();
        var platform = new IntegrationPlatform { Id = "eh", EnvironmentId = "payments-qa", Name = "Payments", Kind = IntegrationKind.EventHub, Namespace = "evhns-payments-qa",
            RuntimeEvidence = new IntegrationRuntimeEvidenceSettings { ExpectedConsumerGroup = "configured-group" } };
        var cut = Render<EventHubRuntimeEvidenceForm>(p => p.Add(x => x.Platform, platform).Add(x => x.OnSave, s => { saved.Add(s); return Task.FromResult(true); }));
        cut.WaitForElement("[data-testid=ip-runtime-src-group]").GetAttribute("data-state").Should().Be("Differs", "configured-group stays configured; the source name is only offered");
        cut.Find("[data-testid=ip-runtime-expected-group]").GetAttribute("value").Should().Be("configured-group");
        cut.Find("[data-testid=ip-runtime-src-group-use]").Click();
        cut.Find("[data-testid=ip-runtime-expected-group]").GetAttribute("value").Should().Be("ledger-qa");
        cut.Find("[data-testid=ip-runtime-expected-group-provenance]").GetAttribute("value").Should().Be(nameof(IntegrationValueProvenance.DeclaredInSource));
        cut.Find("[data-testid=ip-runtime-src-storage-use]").Click();
        cut.Find("[data-testid=ip-runtime-blob-endpoint]").GetAttribute("value").Should().Be("https://stpayqackpt.blob.core.windows.net/");
        saved.Should().BeEmpty("using a detected value never persists it — only Save does");
        platform.RuntimeEvidence!.ExpectedConsumerGroup.Should().Be("configured-group");
    }
}
