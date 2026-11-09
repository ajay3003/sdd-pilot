using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BirkNext.Messaging;

// Read-only message evidence: observations of Service Bus messages (peeked elsewhere, never settled) and Wolverine envelope-store rows, imported as
// files and sanitized before anything is stored or shown. BirkNext never connects to a namespace or an envelope database for this: an observation
// set is evidence someone exported with their own read access. Separate layers are kept apart — CONFIGURATION (Terraform, Wolverine source: existing
// Service Bus / application-messaging evidence), RUNTIME INFRASTRUCTURE (existing ARM metadata and Azure Monitor counts), MESSAGE CONTENT and
// APPLICATION PROCESSING (this file), OBSERVED END-TO-END EVIDENCE (correlation with Execution Evidence runs).

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MessageSubQueue { Active, DeadLetter, Scheduled }

/// <summary>Where an observation set came from. Every kind is a file someone exported; none is a live connection.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MessageObservationSource { ServiceBusPeekExport, WolverineEnvelopeExport }

/// <summary>Body handling chosen at import. MetadataOnly is the default: no body text is kept, only size and SHA-256.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MessageBodyPolicy { MetadataOnly, SanitizedPreview }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MessageCorrelationState { Confirmed, Ambiguous, NotCorrelated }

/// <summary>Succeeded only when a handler outcome says so; a message that is no longer in a queue is NotObserved, never success.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MessageProcessingState { Succeeded, Failed, Pending, DeadLettered, Inconclusive, NotObserved }

public sealed record MessageEntityRef(string? Namespace, string Entity, string? Subscription, MessageSubQueue SubQueue)
{
    public string Display => $"{(Namespace is { Length: > 0 } ns ? ns + "/" : "")}{Entity}{(Subscription is { Length: > 0 } s ? "/subscriptions/" + s : "")}{(SubQueue == MessageSubQueue.DeadLetter ? "/$deadletterqueue" : "")}";
}

public sealed record ObservedMessage
{
    public string MessageId { get; init; } = "";
    public string? CorrelationId { get; init; }
    public string? Subject { get; init; }
    public string? ContentType { get; init; }
    public string? MessageType { get; init; }
    public long? SequenceNumber { get; init; }
    public DateTimeOffset? EnqueuedAt { get; init; }
    public DateTimeOffset? ScheduledAt { get; init; }
    public int? DeliveryCount { get; init; }
    public string? SessionId { get; init; }
    public MessageEntityRef Entity { get; init; } = new(null, "", null, MessageSubQueue.Active);
    /// <summary>Application properties after redaction (values of sensitive keys and identity-number-like values are masked).</summary>
    public Dictionary<string, string> Properties { get; init; } = [];
    public string? DeadLetterReason { get; init; }
    public string? DeadLetterDescription { get; init; }
    public long BodyBytes { get; init; }
    /// <summary>SHA-256 of the original body (identity of the content without keeping it).</summary>
    public string? BodySha256 { get; init; }
    /// <summary>Redacted, bounded preview (null under MetadataOnly or when the content type is not text/JSON/XML).</summary>
    public string? BodyPreview { get; init; }
    public bool BodyTruncated { get; init; }
    public int RedactedFields { get; init; }
}

/// <summary>One row of a Wolverine durable envelope store (incoming = inbox, outgoing = outbox, dead letters).</summary>
public sealed record ObservedEnvelope
{
    public string Id { get; init; } = "";
    /// <summary>Incoming, Outgoing or DeadLetter (the table it came from).</summary>
    public string Store { get; init; } = "";
    /// <summary>Wolverine's status column (Incoming, Scheduled, Handled …) when present.</summary>
    public string? Status { get; init; }
    public string? MessageType { get; init; }
    public string? Destination { get; init; }
    public int? Attempts { get; init; }
    public DateTimeOffset? ExecutionTime { get; init; }
    public DateTimeOffset? SentOrReceivedAt { get; init; }
    public string? ExceptionType { get; init; }
    public string? ExceptionMessage { get; init; }
    /// <summary>Envelope / Service Bus message identity the row refers to, when the export carries it.</summary>
    public string? MessageId { get; init; }
    public string? CorrelationId { get; init; }
}

public sealed record MessageObservationSet
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string EnvironmentId { get; init; } = "";
    public string Name { get; init; } = "";
    public MessageObservationSource Source { get; init; }
    public MessageBodyPolicy BodyPolicy { get; init; }
    public DateTimeOffset ImportedAt { get; init; }
    public string ImportedBy { get; init; } = "";
    /// <summary>When the exporter observed the messages (as declared in the file), never inferred.</summary>
    public DateTimeOffset? ObservedAt { get; init; }
    public string FileName { get; init; } = "";
    public string FileSha256 { get; init; } = "";
    public List<ObservedMessage> Messages { get; init; } = [];
    public List<ObservedEnvelope> Envelopes { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public int RedactedFields { get; init; }
}

public sealed record MessageObservationSummary(Guid Id, string Name, MessageObservationSource Source, MessageBodyPolicy BodyPolicy, DateTimeOffset ImportedAt, string ImportedBy,
    DateTimeOffset? ObservedAt, int Messages, int DeadLettered, int Envelopes, int RedactedFields);

public sealed record MessageObservationImport
{
    public string EnvironmentId { get; init; } = "";
    public string? Name { get; init; }
    public string FileName { get; init; } = "";
    public string Content { get; init; } = "";
    public MessageBodyPolicy BodyPolicy { get; init; } = MessageBodyPolicy.MetadataOnly;
}

public sealed record MessageCorrelation(MessageCorrelationState State, Guid? RunId, int? CallSequence, string? CallName, string Rationale);

public sealed record MessageTimelineEvent(DateTimeOffset? At, string Event, string Source, string Detail);

public sealed record MessageAssessment
{
    public ObservedMessage Message { get; init; } = new();
    public MessageProcessingState Processing { get; init; }
    public string ProcessingReason { get; init; } = "";
    public MessageCorrelation Correlation { get; init; } = new(MessageCorrelationState.NotCorrelated, null, null, null, "");
    public List<MessageTimelineEvent> Timeline { get; init; } = [];
    public List<ObservedEnvelope> Envelopes { get; init; } = [];
}

public sealed record EnvelopeStoreSummary(int InboxPending, int InboxHandled, int OutboxPending, int DeadLetters, int Retried, string Explanation);

public sealed record MessageObservationView
{
    public MessageObservationSet Set { get; init; } = new();
    public List<MessageAssessment> Messages { get; init; } = [];
    public EnvelopeStoreSummary? EnvelopeStores { get; init; }
    public List<string> Limitations { get; init; } = [];
}

/// <summary>A run's correlation ids as Execution Evidence sent them ("{run}.{sequence}.{attempt}" in X-BirkNext-Execution-Id).</summary>
public sealed record CorrelationCandidate(Guid RunId, int Sequence, string CallName, IReadOnlyCollection<string> CorrelationIds);

public static class MessagingObservationRules
{
    public const int MaxMessages = 500;
    public const int MaxEnvelopes = 2000;
    public const int MaxFileBytes = 5 * 1024 * 1024;
    public const int PreviewChars = 2048;
    public const string Mask = "[redacted]";
    public const string ExecutionIdProperty = "X-BirkNext-Execution-Id";

    private static readonly Regex SensitiveKey = new("(pass(word)?|secret|token|authorization|api[-_]?key|connection[-_]?string|sas|cookie|signature|fnr|fødselsnummer|fodselsnummer|personnummer|ssn|national[-_]?id|birth|email|e-post|phone|telefon|mobil|address|adresse)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex IdentityNumber = new(@"(?<!\d)\d{11}(?!\d)", RegexOptions.CultureInvariant);
    private static readonly Regex EmailAddress = new(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant);
    private static readonly Regex BearerLike = new(@"(?i)bearer\s+[A-Za-z0-9\-_.~+/]+=*|eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]*|SharedAccessSignature\s+\S+|Endpoint=sb://\S+", RegexOptions.CultureInvariant);

    public static bool IsSensitiveKey(string key) => SensitiveKey.IsMatch(key);

    /// <summary>Masks identity-number-like values (11 digits), e-mail addresses, bearer tokens / JWTs / SAS / connection strings in free text.</summary>
    public static (string Text, int Redactions) RedactText(string text)
    {
        var count = 0;
        string Replace(Regex r, string s) => r.Replace(s, _ => { count++; return Mask; });
        var result = Replace(BearerLike, text);
        result = Replace(IdentityNumber, result);
        result = Replace(EmailAddress, result);
        return (result, count);
    }

    /// <summary>Redacts a JSON body: values under sensitive keys are masked whole; other strings are text-redacted. Returns null when not JSON.</summary>
    public static (string Json, int Redactions)? RedactJson(string body)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(body); } catch (JsonException) { return null; }
        var count = 0;
        JsonNode? Walk(JsonNode? n, string? key)
        {
            switch (n)
            {
                case JsonObject o:
                    foreach (var p in o.ToList()) o[p.Key] = Walk(p.Value?.DeepClone(), p.Key);
                    return o;
                case JsonArray a:
                    for (var i = 0; i < a.Count; i++) a[i] = Walk(a[i]?.DeepClone(), key);
                    return a;
                case JsonValue v when key is not null && IsSensitiveKey(key):
                    count++;
                    return JsonValue.Create(Mask);
                case JsonValue v when v.TryGetValue<string>(out var s):
                    var (text, r) = RedactText(s);
                    count += r;
                    return JsonValue.Create(text);
                case JsonValue v when v.TryGetValue<long>(out var l) && l.ToString(CultureInfo.InvariantCulture).Length == 11:
                    count++;
                    return JsonValue.Create(Mask);
                default:
                    return n;
            }
        }
        var redacted = Walk(node, null);
        return (redacted?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null", count);
    }

    /// <summary>A bounded, redacted preview of a body. Only JSON, XML and plain text are previewed; anything else is metadata only.</summary>
    public static (string? Preview, bool Truncated, int Redactions, string? Warning) Preview(string? body, string? contentType)
    {
        if (string.IsNullOrEmpty(body)) return (null, false, 0, null);
        var ct = (contentType ?? "").ToLowerInvariant();
        var json = ct.Contains("json") || (ct.Length == 0 && body.TrimStart().StartsWith('{') || ct.Length == 0 && body.TrimStart().StartsWith('['));
        string text; int redactions;
        if (json && RedactJson(body) is { } j) (text, redactions) = j;
        else if (json || ct.Contains("xml") || ct.StartsWith("text/", StringComparison.Ordinal) || ct.Length == 0)
        {
            (text, redactions) = RedactText(body);
            // XML: element values under sensitive element names are masked whole.
            text = Regex.Replace(text, @"<(?<n>[\w:.-]+)(?<a>[^>]*)>(?<v>[^<]*)</\k<n>>", m =>
            {
                if (!IsSensitiveKey(m.Groups["n"].Value) || m.Groups["v"].Value == Mask) return m.Value;
                redactions++;
                return $"<{m.Groups["n"].Value}{m.Groups["a"].Value}>{Mask}</{m.Groups["n"].Value}>";
            });
        }
        else return (null, false, 0, $"Content type {contentType} is not previewed (metadata only).");
        var truncated = text.Length > PreviewChars;
        return (truncated ? text[..PreviewChars] : text, truncated, redactions, null);
    }

    public static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    // ── Correlation: exact identifiers only ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Confirmed when the message carries a correlation id Execution Evidence sent (correlation id, or the X-BirkNext-Execution-Id application
    /// property) and it matches exactly one call; Ambiguous when it matches several; NotCorrelated otherwise. Timestamps are never used.
    /// </summary>
    public static MessageCorrelation Correlate(ObservedMessage message, IReadOnlyCollection<CorrelationCandidate> candidates)
    {
        var ids = new List<(string Value, string Where)>();
        if (!string.IsNullOrWhiteSpace(message.CorrelationId)) ids.Add((message.CorrelationId.Trim(), "correlation id"));
        foreach (var (key, value) in message.Properties)
            if (key.Equals(ExecutionIdProperty, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value)) ids.Add((value.Trim(), $"application property {key}"));
        if (ids.Count == 0)
            return new(MessageCorrelationState.NotCorrelated, null, null, null, "The message carries no correlation id or BirkNext execution id; time proximity is not used.");
        var matches = candidates.SelectMany(c => ids.Where(i => c.CorrelationIds.Contains(i.Value, StringComparer.Ordinal)).Select(i => (Candidate: c, i.Where, i.Value)))
            .DistinctBy(m => (m.Candidate.RunId, m.Candidate.Sequence)).ToList();
        return matches.Count switch
        {
            0 => new(MessageCorrelationState.NotCorrelated, null, null, null, $"{string.Join(", ", ids.Select(i => i.Where))} does not match any execution of this environment."),
            1 => new(MessageCorrelationState.Confirmed, matches[0].Candidate.RunId, matches[0].Candidate.Sequence, matches[0].Candidate.CallName,
                $"The {matches[0].Where} {matches[0].Value} is the execution id call {matches[0].Candidate.Sequence} sent."),
            _ => new(MessageCorrelationState.Ambiguous, null, null, null, $"The ids match {matches.Count} executions; a person decides."),
        };
    }

    // ── Processing state and timeline ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What the evidence says about processing. In the dead-letter queue → DeadLettered. A Wolverine inbox row Handled → Succeeded. A dead-letter
    /// envelope row → Failed (with its exception). An active message still in the queue → Pending. Anything else → NotObserved (absence of errors and
    /// a message no longer in a queue are not success). Delivery count alone never explains a cause.
    /// </summary>
    public static (MessageProcessingState State, string Reason) Processing(ObservedMessage m, IReadOnlyCollection<ObservedEnvelope> envelopes)
    {
        if (m.Entity.SubQueue == MessageSubQueue.DeadLetter)
            return (MessageProcessingState.DeadLettered, m.DeadLetterReason is { Length: > 0 } r ? $"Dead-lettered: {r}." : "In the dead-letter queue; no reason recorded.");
        if (envelopes.FirstOrDefault(e => e.Store == "DeadLetter") is { } dead)
            return (MessageProcessingState.Failed, $"Wolverine dead-letter row: {dead.ExceptionType ?? "exception not recorded"}{(dead.ExceptionMessage is { } msg ? " — " + msg : "")}.");
        if (envelopes.Any(e => e.Store == "Incoming" && string.Equals(e.Status, "Handled", StringComparison.OrdinalIgnoreCase)))
            return (MessageProcessingState.Succeeded, "Wolverine's inbox marks the envelope Handled.");
        if (envelopes.Any(e => e.Store == "Incoming"))
            return (MessageProcessingState.Pending, "Received into Wolverine's inbox, not yet handled.");
        if (m.Entity.SubQueue is MessageSubQueue.Active or MessageSubQueue.Scheduled)
            return (MessageProcessingState.Pending, m.DeliveryCount is > 0
                ? $"Still in the queue after {m.DeliveryCount} delivery attempt(s) (the count does not say why)."
                : "Still in the queue; not received yet.");
        return (MessageProcessingState.NotObserved, "No processing evidence.");
    }

    public static List<MessageTimelineEvent> Timeline(ObservedMessage m, IReadOnlyCollection<ObservedEnvelope> envelopes, MessageCorrelation correlation)
    {
        var events = new List<MessageTimelineEvent>();
        if (correlation.State == MessageCorrelationState.Confirmed)
            events.Add(new(null, "Produced by test execution", "Execution Evidence", $"Run {correlation.RunId:N}, call {correlation.CallSequence} ({correlation.CallName})."));
        if (m.ScheduledAt is { } scheduled) events.Add(new(scheduled, "Scheduled", "Service Bus", m.Entity.Display));
        events.Add(new(m.EnqueuedAt, "Enqueued", "Service Bus", m.Entity.Display));
        foreach (var e in envelopes.OrderBy(e => e.SentOrReceivedAt ?? e.ExecutionTime))
        {
            var label = e.Store switch
            {
                "Outgoing" => "In Wolverine's outbox (not yet sent)",
                "DeadLetter" => "Handler failed → Wolverine dead letter",
                _ when string.Equals(e.Status, "Handled", StringComparison.OrdinalIgnoreCase) => "Handled by Wolverine",
                _ => "Received into Wolverine's inbox",
            };
            events.Add(new(e.SentOrReceivedAt ?? e.ExecutionTime, label, "Wolverine envelope store",
                $"{e.MessageType}{(e.Attempts is > 0 ? $", {e.Attempts} attempt(s)" : "")}{(e.ExceptionType is { } x ? $", {x}" : "")}"));
        }
        if (m.DeliveryCount is > 1) events.Add(new(null, $"Delivered {m.DeliveryCount} times", "Service Bus", "Redeliveries observed; the configured retry policy is not proof of retries."));
        if (m.Entity.SubQueue == MessageSubQueue.DeadLetter)
            events.Add(new(null, "Dead-lettered", "Service Bus", $"{m.DeadLetterReason ?? "no reason recorded"}{(m.DeadLetterDescription is { } d ? " — " + d : "")}"));
        return events;
    }

    public static EnvelopeStoreSummary? Envelopes(IReadOnlyCollection<ObservedEnvelope> envelopes)
    {
        if (envelopes.Count == 0) return null;
        var inbox = envelopes.Where(e => e.Store == "Incoming").ToList();
        return new(
            inbox.Count(e => !string.Equals(e.Status, "Handled", StringComparison.OrdinalIgnoreCase)),
            inbox.Count(e => string.Equals(e.Status, "Handled", StringComparison.OrdinalIgnoreCase)),
            envelopes.Count(e => e.Store == "Outgoing"),
            envelopes.Count(e => e.Store == "DeadLetter"),
            envelopes.Count(e => e.Attempts is > 1),
            "Counts of the exported rows at export time: inbox pending (received, not handled), inbox handled (kept for duplicate detection), outbox pending (not yet sent), dead letters.");
    }

    /// <summary>Envelope rows that refer to the message (by message id, envelope id or correlation id — exact match only).</summary>
    public static List<ObservedEnvelope> EnvelopesFor(ObservedMessage m, IReadOnlyCollection<ObservedEnvelope> envelopes) =>
        envelopes.Where(e => (e.MessageId is { Length: > 0 } id && id == m.MessageId) || e.Id == m.MessageId
            || (e.CorrelationId is { Length: > 0 } c && c == m.CorrelationId)).ToList();
}
