using System.Reflection;
using System.Text.Json;
using BirkNext.Api.Controllers;
using BirkNext.Api.Data;
using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.ExecutionEvidence;
using BirkNext.Api.Services.Integrations.Messaging;
using BirkNext.ExecutionEvidence;
using BirkNext.Messaging;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BirkNext.Api.Tests.Services.Integrations;

/// <summary>
/// Imported message evidence: parsing of a Service Bus peek export and a Wolverine envelope export, redaction before storage (metadata only by
/// default), processing state that never calls a vanished message a success, exact-id correlation with Execution Evidence (never time), and an API
/// surface without any message operation. Synthetic fixtures only.
/// </summary>
public sealed class MessageObservationTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly MessageObservationService _service;
    private static readonly Guid Run = Guid.Parse("11111111-2222-3333-4444-555555555555");

    public MessageObservationTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"msg-{Guid.NewGuid():N}").Options);
        _service = new MessageObservationService(_db, new ExecutionEvidenceStore(_db));
    }

    public void Dispose() => _db.Dispose();

    private static string Peek(string subQueue, params object[] messages) => JsonSerializer.Serialize(new
    {
        kind = "servicebus-peek", @namespace = "sbns-synthetic", entity = "person-updated", subscription = "skole-adapter", subQueue, observedAt = "2026-11-09T10:00:00Z", messages,
    });

    private static object Message(string id, string? correlationId = null, string? body = null, int delivery = 1, Dictionary<string, object>? props = null, string? reason = null) => new
    {
        messageId = id, correlationId, subject = "PersonUpdated", contentType = "application/json", sequenceNumber = 42, enqueuedTime = "2026-11-09T09:59:00Z", deliveryCount = delivery,
        applicationProperties = props ?? new Dictionary<string, object>(), deadLetterReason = reason, deadLetterErrorDescription = reason is null ? null : "Handler threw for person 12345678901",
        body = body ?? """{"personId":"p-1","fodselsnummer":"12345678901","name":"Synthetic Person","email":"person@example.test","note":"call 12345678901"}""",
    };

    private async Task<MessageObservationSet> ImportAsync(string content, MessageBodyPolicy policy = MessageBodyPolicy.MetadataOnly, string env = "env-a")
    {
        var (set, error) = await _service.ImportAsync(new MessageObservationImport { EnvironmentId = env, FileName = "peek.json", Content = content, BodyPolicy = policy }, "tester", DateTimeOffset.UtcNow, default);
        error.Should().BeNull();
        return set!;
    }

    private async Task SeedRunAsync(string env = "env-a")
    {
        var store = new ExecutionEvidenceStore(_db);
        await store.CreateAsync(new ExecutionRunSummary { RunId = Run, Name = "Register child", EnvironmentId = env, StartedAt = DateTimeOffset.UtcNow, State = ExecutionState.Passed },
            [new ExecutionCallRecord { CallId = Guid.NewGuid(), Sequence = 3, Definition = new ExecutionCallDefinition { Name = "POST /children" }, State = ExecutionState.Passed,
                Attempts = [new ExecutionAttempt { AttemptNumber = 1, StartedAt = DateTimeOffset.UtcNow }] }], default);
    }

    [Fact]
    public async Task Metadata_only_is_the_default_and_nothing_sensitive_is_stored()
    {
        var set = await ImportAsync(Peek("Active", Message("m-1", props: new() { ["tenant"] = "synthetic", ["Authorization"] = "Bearer abc.def.ghi", ["caseRef"] = "ref 12345678901" })));
        var m = set.Messages.Single();
        m.BodyPreview.Should().BeNull("metadata only keeps no body text");
        m.BodyBytes.Should().BeGreaterThan(0);
        m.BodySha256.Should().HaveLength(64);
        m.Properties["Authorization"].Should().Be(MessagingObservationRules.Mask);
        m.Properties["caseRef"].Should().Be("ref " + MessagingObservationRules.Mask);
        var stored = (await _db.MessageObservationSets.SingleAsync()).DocumentJson;
        stored.Should().NotContain("12345678901").And.NotContain("abc.def.ghi").And.NotContain("Synthetic Person");
    }

    [Fact]
    public async Task A_sanitized_preview_masks_sensitive_fields_identity_numbers_and_emails_and_is_bounded()
    {
        var set = await ImportAsync(Peek("Active", Message("m-1"), Message("m-2", body: new string('x', 5000))), MessageBodyPolicy.SanitizedPreview);
        var preview = set.Messages[0].BodyPreview!;
        preview.Should().Contain("\"personId\": \"p-1\"").And.Contain("Synthetic Person").And.NotContain("12345678901").And.NotContain("person@example.test");
        set.Messages[0].RedactedFields.Should().BeGreaterThanOrEqualTo(3);
        set.Messages[1].BodyTruncated.Should().BeTrue();
        set.Messages[1].BodyPreview!.Length.Should().Be(MessagingObservationRules.PreviewChars);
        (await _db.MessageObservationSets.SingleAsync()).DocumentJson.Should().NotContain("12345678901");
    }

    [Fact]
    public async Task Binary_content_is_never_previewed_and_xml_elements_with_sensitive_names_are_masked()
    {
        var binary = Peek("Active", new { messageId = "b-1", contentType = "application/octet-stream", body = "AAECAw==" });
        (await ImportAsync(binary, MessageBodyPolicy.SanitizedPreview)).Should().Match<MessageObservationSet>(s => s.Messages[0].BodyPreview == null && s.Warnings.Any(w => w.Contains("not previewed")));
        var (preview, _, redactions, _) = MessagingObservationRules.Preview("<Person><Navn>Syntetisk</Navn><Fodselsnummer>01010112345</Fodselsnummer></Person>", "application/xml");
        preview.Should().Contain("<Navn>Syntetisk</Navn>").And.NotContain("01010112345");
        redactions.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Correlation_is_confirmed_only_by_an_execution_id_never_by_time_and_ambiguity_is_reported()
    {
        await SeedRunAsync();
        var id = $"{Run:N}.3.1";
        var set = await ImportAsync(Peek("Active",
            Message("by-correlation", correlationId: id),
            Message("by-property", props: new() { [MessagingObservationRules.ExecutionIdProperty] = id }),
            Message("unrelated", correlationId: "something-else"),
            Message("none")));
        var view = (await _service.ViewAsync("env-a", set.Id, default))!;
        var c = view.Messages.ToDictionary(m => m.Message.MessageId, m => m.Correlation);
        c["by-correlation"].Should().Match<MessageCorrelation>(x => x.State == MessageCorrelationState.Confirmed && x.RunId == Run && x.CallSequence == 3 && x.CallName == "POST /children");
        c["by-property"].State.Should().Be(MessageCorrelationState.Confirmed);
        c["unrelated"].State.Should().Be(MessageCorrelationState.NotCorrelated);
        c["none"].Rationale.Should().Contain("time proximity is not used");
        view.Messages.Single(m => m.Message.MessageId == "by-correlation").Timeline[0].Event.Should().Be("Produced by test execution");

        var twice = new[] { new CorrelationCandidate(Run, 1, "a", [id]), new CorrelationCandidate(Guid.NewGuid(), 2, "b", [id]) };
        MessagingObservationRules.Correlate(new ObservedMessage { MessageId = "x", CorrelationId = id }, twice).State.Should().Be(MessageCorrelationState.Ambiguous);
    }

    [Fact]
    public async Task Executions_of_another_environment_never_correlate()
    {
        await SeedRunAsync("env-other");
        var set = await ImportAsync(Peek("Active", Message("m", correlationId: $"{Run:N}.3.1")));
        (await _service.ViewAsync("env-a", set.Id, default))!.Messages.Single().Correlation.State.Should().Be(MessageCorrelationState.NotCorrelated);
        (await _service.ViewAsync("env-other", set.Id, default)).Should().BeNull("a set is read only in its own environment");
    }

    [Fact]
    public async Task Processing_state_comes_from_evidence_and_a_vanished_message_is_not_a_success()
    {
        var active = await ImportAsync(Peek("Active", Message("handled"), Message("failed"), Message("waiting", delivery: 4), Message("inbox-only")));
        var dlq = await ImportAsync(Peek("DeadLetter", Message("dead", reason: "MaxDeliveryCountExceeded")));
        await ImportAsync(JsonSerializer.Serialize(new
        {
            kind = "wolverine-envelopes", observedAt = "2026-11-09T10:01:00Z",
            incoming = new object[] { new { id = "handled", status = "Handled", message_type = "PersonUpdated", attempts = 1 }, new { id = "inbox-only", status = "Incoming", message_type = "PersonUpdated", attempts = 2 } },
            outgoing = new object[] { new { id = "out-1", destination = "asb://topic/skole", message_type = "SchoolChanged" } },
            deadLetters = new object[] { new { id = "failed", message_type = "PersonUpdated", exception_type = "InvalidOperationException", exception_message = "Person 12345678901 not found", body = "AAAA" } },
        }));
        var view = (await _service.ViewAsync("env-a", active.Id, default))!;
        var s = view.Messages.ToDictionary(m => m.Message.MessageId, m => m);
        s["handled"].Processing.Should().Be(MessageProcessingState.Succeeded);
        s["failed"].Processing.Should().Be(MessageProcessingState.Failed);
        s["failed"].ProcessingReason.Should().Contain("InvalidOperationException").And.NotContain("12345678901");
        s["waiting"].Should().Match<MessageAssessment>(m => m.Processing == MessageProcessingState.Pending && m.ProcessingReason.Contains("does not say why"));
        s["inbox-only"].Processing.Should().Be(MessageProcessingState.Pending);
        s["handled"].Timeline.Select(t => t.Event).Should().Contain("Handled by Wolverine");
        (await _service.ViewAsync("env-a", dlq.Id, default))!.Messages.Single().Should().Match<MessageAssessment>(m => m.Processing == MessageProcessingState.DeadLettered
            && m.Timeline.Any(t => t.Event == "Dead-lettered") && !m.Message.DeadLetterDescription!.Contains("12345678901"));
        MessagingObservationRules.Processing(new ObservedMessage { Entity = new(null, "q", null, (MessageSubQueue)99) }, []).State.Should().Be(MessageProcessingState.NotObserved);
    }

    [Fact]
    public async Task A_Wolverine_export_keeps_store_columns_only_and_summarises_inbox_outbox_and_dead_letters()
    {
        var set = await ImportAsync(JsonSerializer.Serialize(new
        {
            kind = "wolverine-envelopes",
            incoming = new object[] { new { id = "a", status = "Incoming", attempts = 3, body = "SECRET-BODY" }, new { id = "b", status = "Handled" } },
            outgoing = new object[] { new { id = "c" } },
            deadLetters = new object[] { new { id = "d", exception_type = "TimeoutException" } },
        }));
        JsonSerializer.Serialize(set).Should().NotContain("SECRET-BODY");
        MessagingObservationRules.Envelopes(set.Envelopes).Should().Be(new EnvelopeStoreSummary(1, 1, 1, 1, 1, MessagingObservationRules.Envelopes(set.Envelopes)!.Explanation));
    }

    [Fact]
    public async Task Invalid_files_are_refused_with_a_reason()
    {
        (await _service.ImportAsync(new MessageObservationImport { EnvironmentId = "env-a", Content = "not json" }, "t", DateTimeOffset.UtcNow, default)).Error.Should().Contain("Not a readable");
        (await _service.ImportAsync(new MessageObservationImport { EnvironmentId = "env-a", Content = "{\"kind\":\"other\"}" }, "t", DateTimeOffset.UtcNow, default)).Error.Should().Contain("Unknown file kind");
        var tooMany = Peek("Active", Enumerable.Range(0, MessagingObservationRules.MaxMessages + 1).Select(i => Message($"m{i}")).ToArray());
        (await _service.ImportAsync(new MessageObservationImport { EnvironmentId = "env-a", Content = tooMany }, "t", DateTimeOffset.UtcNow, default)).Error.Should().Contain("At most");
        (await _service.ImportAsync(new MessageObservationImport { EnvironmentId = "", Content = Peek("Active") }, "t", DateTimeOffset.UtcNow, default)).Error.Should().Contain("Target Environment");
    }

    [Fact]
    public async Task Deletion_removes_one_set_of_its_environment()
    {
        var set = await ImportAsync(Peek("Active", Message("m")));
        (await _service.DeleteAsync("env-other", set.Id, default)).Should().BeFalse();
        (await _service.DeleteAsync("env-a", set.Id, default)).Should().BeTrue();
        (await _service.ListAsync("env-a", default)).Should().BeEmpty();
    }

    [Fact]
    public void The_API_has_no_message_operation_writes_need_permission_and_the_import_is_size_limited()
    {
        var actions = typeof(MessageObservationsController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any()).ToList();
        actions.Select(a => a.Name).Should().BeEquivalentTo("Import", "List", "Get", "Delete");
        foreach (var write in actions.Where(a => a.Name is "Import" or "Delete"))
            write.GetCustomAttributes<AuthorizeAttribute>().Should().Contain(a => a.Policy == BirkNextPermissions.IntegrationConfigurationWritePolicy, write.Name);
        actions.Single(a => a.Name == "Import").GetCustomAttribute<RequestSizeLimitAttribute>().Should().NotBeNull();
        // No client, receiver or settlement API in the messaging evidence code (Peek elsewhere is never Receive; here nothing connects at all).
        var source = File.ReadAllText(Path.Combine(SourceRoot(), "backend", "BirkNext.Api", "Services", "Integrations", "Messaging", "MessageObservationService.cs"))
            + File.ReadAllText(Path.Combine(SourceRoot(), "shared", "MessagingObservationContracts.cs"));
        foreach (var banned in new[] { "ServiceBusClient", "ServiceBusReceiver", "CompleteMessageAsync", "AbandonMessageAsync", "DeadLetterMessageAsync", "ReceiveMessagesAsync", "PeekMessagesAsync", "NpgsqlConnection", "SqlConnection", "ExecuteSqlRaw" })
            source.Should().NotContain(banned);
    }

    private static string SourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "shared"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
