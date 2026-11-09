using BirkNext.Messaging;
using BirkNext.Web.Components;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Messages tab: observation sets are listed with their source and counts, a set opens master–detail, processing and correlation come from the
/// backend's assessment (a confirmed correlation links to the run), filters narrow the list, metadata-only sets say no body is kept, and the panel
/// offers no message operation. Synthetic fixtures.
/// </summary>
public sealed class MessageObservationsPanelTests : BunitContext
{
    private static readonly Guid SetId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid RunId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private readonly Mock<IMessageObservationApiService> _api = new();

    public MessageObservationsPanelTests()
    {
        _api.Setup(a => a.ListAsync("env-a", It.IsAny<CancellationToken>())).ReturnsAsync(([new MessageObservationSummary(SetId, "Peek · person-updated", MessageObservationSource.ServiceBusPeekExport,
            MessageBodyPolicy.MetadataOnly, DateTimeOffset.UtcNow, "tester", DateTimeOffset.UtcNow, 3, 1, 0, 2)], null));
        _api.Setup(a => a.GetAsync("env-a", SetId, It.IsAny<CancellationToken>())).ReturnsAsync((View(), null));
        Services.AddSingleton(_api.Object);
    }

    private static MessageAssessment A(string id, MessageProcessingState state, MessageCorrelation? correlation = null, MessageSubQueue q = MessageSubQueue.Active) => new()
    {
        Message = new ObservedMessage { MessageId = id, Subject = "PersonUpdated", BodyBytes = 120, BodySha256 = new string('a', 64), Entity = new("sbns", "person-updated", "skole", q),
            DeadLetterReason = q == MessageSubQueue.DeadLetter ? "MaxDeliveryCountExceeded" : null },
        Processing = state, ProcessingReason = state.ToString() + " because evidence",
        Correlation = correlation ?? new(MessageCorrelationState.NotCorrelated, null, null, null, "No execution id."),
        Timeline = [new(DateTimeOffset.UtcNow, "Enqueued", "Service Bus", "sbns/person-updated")],
    };

    private static MessageObservationView View() => new()
    {
        Set = new MessageObservationSet { Id = SetId, Name = "Peek · person-updated", BodyPolicy = MessageBodyPolicy.MetadataOnly },
        Messages =
        [
            A("msg-correlated", MessageProcessingState.Succeeded, new(MessageCorrelationState.Confirmed, RunId, 3, "POST /children", "The correlation id is the execution id call 3 sent.")),
            A("msg-dead", MessageProcessingState.DeadLettered, q: MessageSubQueue.DeadLetter),
            A("msg-waiting", MessageProcessingState.Pending),
        ],
        EnvelopeStores = new EnvelopeStoreSummary(1, 2, 0, 1, 1, "Counts of the exported rows."),
        Limitations = ["Imported observations: what the exporter saw at export time."],
    };

    private IRenderedComponent<MessageObservationsPanel> Render() => Render<MessageObservationsPanel>(p => p.Add(x => x.EnvironmentId, "env-a"));

    [Fact]
    public void Sets_are_listed_and_a_set_opens_with_processing_correlation_and_envelope_counts()
    {
        var cut = Render();
        cut.Find("[data-testid=mo-set]").TextContent.Should().Contain("Service Bus peek export").And.Contain("tester");
        cut.Find("[data-testid=mo-set-open]").Click();
        cut.FindAll("[data-testid=mo-message]").Should().HaveCount(3);
        cut.Find("[data-testid=mo-stores]").TextContent.Should().Contain("Inbox pending").And.Contain("Dead letters");
        var detail = cut.Find("[data-testid=mo-message-detail]");
        detail.TextContent.Should().Contain("Succeeded").And.Contain("call 3 sent");
        cut.Find("[data-testid=mo-correlation-link]").GetAttribute("href").Should().Be($"execution-results?run={RunId:D}");
        cut.Find("[data-testid=mo-no-preview]").TextContent.Should().Contain("metadata only");
    }

    [Fact]
    public void Filters_narrow_the_messages_and_the_dead_letter_reason_is_shown()
    {
        var cut = Render();
        cut.Find("[data-testid=mo-set-open]").Click();
        cut.Find("[data-testid=mo-filter-dead-lettered]").Click();
        cut.FindAll("[data-testid=mo-message]").Should().ContainSingle().Which.GetAttribute("data-processing").Should().Be("DeadLettered");
        cut.Find("[data-testid=mo-message-open]").Click();
        cut.Find("[data-testid=mo-message-detail]").TextContent.Should().Contain("MaxDeliveryCountExceeded");
        cut.Find("[data-testid=mo-filter-correlated]").Click();
        cut.FindAll("[data-testid=mo-message]").Should().ContainSingle().Which.GetAttribute("data-correlation").Should().Be("Confirmed");
    }

    [Fact]
    public void Delete_asks_first_and_no_message_operation_is_offered()
    {
        var cut = Render();
        cut.Find("[data-testid=mo-set-delete]").Click();
        cut.Find("[data-testid=mo-set-delete]").TextContent.Should().Be("Confirm delete");
        _api.Verify(a => a.DeleteAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        cut.Find("[data-testid=mo-set-open]").Click();
        var text = cut.Markup;
        foreach (var operation in new[] { "Resubmit", "Replay", "Purge", "Complete", "Abandon", "Receive" })
            cut.FindAll("button").Should().NotContain(b => b.TextContent.Contains(operation), operation);
        text.Should().Contain("No message can be received, completed, resubmitted, replayed or purged from here");
    }

    [Fact]
    public void An_empty_environment_says_so()
    {
        _api.Setup(a => a.ListAsync("env-a", It.IsAny<CancellationToken>())).ReturnsAsync((new List<MessageObservationSummary>(), null));
        Render().Find("[data-testid=mo-empty]").TextContent.Should().Contain("No message observations");
    }
}
