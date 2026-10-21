using BirkNext.Integrations;
using BirkNext.SourceArchitecture;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// A source-discovered suggestion says what it is and where it was detected, so a bare channel name never reads as a recommended business
/// integration; and it is "already configured" only when a configured record has the same kind and channel — never by a similar name.
/// </summary>
public sealed class SourceSuggestionLabelTests
{
    private static SourceIntegrationCandidate Queue() => new()
    {
        Id = "servicebusqueue:incoming", Pattern = IntegrationPattern.Messaging, Transport = IntegrationTransport.ServiceBusQueue, ChannelName = "hubincoming",
        DestinationType = "Service Bus queue", ConsumerCandidate = "Demo.Panel",
        Evidence = [Field("Repo.Demo.Panel/HealthChecks.cs"), Field("Repo.Demo.Panel/Inspector.cs"), Field("Repo.Api/Program.cs")],
    };

    private static SourceFieldEvidence Field(string file) => new("Channel", "hubincoming", ArchitectureEvidenceState.Confirmed, ArchitectureEvidenceKind.ApplicationSource, file, 1, "", "", "");

    [Fact]
    public void ASuggestion_NamesItsKindAndWhereItWasDetected()
    {
        IntegrationsOverviewPresentation.SuggestionKind(Queue()).Should().Be("Messaging · Service Bus queue");
        IntegrationsOverviewPresentation.DetectedIn(Queue(), "Repo archive.zip").Should().Be("Detected in Repo archive.zip · Repo.Demo.Panel");
        IntegrationsOverviewPresentation.DetectedIn(Queue() with { Evidence = [] }, null).Should().Be("Detected in Source Analysis");
    }

    [Fact]
    public void AnHttpIntegrationWithASimilarName_DoesNotConsumeAQueueSuggestion()
    {
        var catalog = new IntegrationCatalog
        {
            Integrations = [new IntegrationDefinition { Id = "intake", DisplayName = "Hub incoming report intake", Kind = IntegrationKind.HttpApi, EndpointOrTopic = "https://hub.example.test/api/webhook" }],
        };
        IntegrationsOverviewPresentation.AlreadyConfigured(catalog, Queue()).Should().BeFalse("only the same kind and channel prove it is the same integration");
        var same = catalog with { Integrations = [new IntegrationDefinition { Id = "q", DisplayName = "x", Kind = IntegrationKind.ServiceBus, EndpointOrTopic = "hubincoming" }] };
        IntegrationsOverviewPresentation.AlreadyConfigured(same, Queue()).Should().BeTrue();
    }
}
