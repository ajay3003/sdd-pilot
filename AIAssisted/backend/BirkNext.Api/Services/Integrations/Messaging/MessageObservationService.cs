using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.ExecutionEvidence;
using BirkNext.ExecutionEvidence;
using BirkNext.Messaging;
using Microsoft.EntityFrameworkCore;
using Path = System.IO.Path;

namespace BirkNext.Api.Services.Integrations.Messaging;

/// <summary>
/// Imported, sanitized message evidence (read-only). Parses a Service Bus peek export (property names of the Azure SDK's received message) or a
/// Wolverine envelope-store export (rows of the incoming / outgoing / dead-letter tables), redacts before anything is stored, and assembles per-message
/// processing state, timeline and correlation with Execution Evidence (exact ids only). No namespace, queue or database is ever contacted, and no
/// message operation exists here: nothing can be received, completed, abandoned, dead-lettered, resubmitted, replayed or purged.
/// </summary>
public sealed class MessageObservationService(AppDbContext db, ExecutionEvidenceStore executions)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<(MessageObservationSet? Set, string? Error)> ImportAsync(MessageObservationImport request, string initiator, DateTimeOffset now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.EnvironmentId)) return (null, "Choose the Target Environment the observations belong to.");
        if (string.IsNullOrWhiteSpace(request.Content)) return (null, "The file is empty.");
        if (System.Text.Encoding.UTF8.GetByteCount(request.Content) > MessagingObservationRules.MaxFileBytes)
            return (null, $"The file exceeds {MessagingObservationRules.MaxFileBytes / 1024 / 1024} MB.");
        JsonObject root;
        try { root = JsonNode.Parse(request.Content) as JsonObject ?? throw new JsonException("Not a JSON object."); }
        catch (JsonException ex) { return (null, $"Not a readable observation file: {ex.Message}"); }
        var kind = Str(root, "kind");
        var set = kind switch
        {
            "servicebus-peek" => ParseServiceBus(root, request.BodyPolicy),
            "wolverine-envelopes" => ParseWolverine(root),
            _ => null,
        };
        if (set is null) return (null, "Unknown file kind: expected \"kind\": \"servicebus-peek\" or \"wolverine-envelopes\".");
        if (set.Messages.Count > MessagingObservationRules.MaxMessages) return (null, $"At most {MessagingObservationRules.MaxMessages} messages per file.");
        if (set.Envelopes.Count > MessagingObservationRules.MaxEnvelopes) return (null, $"At most {MessagingObservationRules.MaxEnvelopes} envelope rows per file.");
        set = set with
        {
            EnvironmentId = request.EnvironmentId.Trim(), ImportedAt = now, ImportedBy = initiator, FileName = Path.GetFileName(request.FileName ?? ""),
            FileSha256 = MessagingObservationRules.Sha256(request.Content),
            Name = string.IsNullOrWhiteSpace(request.Name) ? $"{(set.Source == MessageObservationSource.ServiceBusPeekExport ? "Peek" : "Envelopes")} · {Path.GetFileName(request.FileName ?? "upload")}" : request.Name.Trim(),
            RedactedFields = set.Messages.Sum(m => m.RedactedFields),
        };
        db.MessageObservationSets.Add(new MessageObservationSetRecord
        {
            Id = set.Id, EnvironmentId = set.EnvironmentId, Name = set.Name, ImportedAt = now, DocumentJson = JsonSerializer.Serialize(set, Json),
        });
        await db.SaveChangesAsync(ct);
        return (set, null);
    }

    public async Task<List<MessageObservationSummary>> ListAsync(string environmentId, CancellationToken ct) =>
        (await db.MessageObservationSets.AsNoTracking().Where(r => r.EnvironmentId == environmentId).OrderByDescending(r => r.ImportedAt).Take(200).ToListAsync(ct))
            .Select(r => JsonSerializer.Deserialize<MessageObservationSet>(r.DocumentJson, Json)!)
            .Select(s => new MessageObservationSummary(s.Id, s.Name, s.Source, s.BodyPolicy, s.ImportedAt, s.ImportedBy, s.ObservedAt, s.Messages.Count,
                s.Messages.Count(m => m.Entity.SubQueue == MessageSubQueue.DeadLetter), s.Envelopes.Count, s.RedactedFields)).ToList();

    /// <summary>One set with every message assessed. Envelope rows of the environment's other sets are joined by exact id, and correlation uses the
    /// execution ids Execution Evidence sent in the same environment.</summary>
    public async Task<MessageObservationView?> ViewAsync(string environmentId, Guid id, CancellationToken ct)
    {
        var row = await db.MessageObservationSets.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.EnvironmentId == environmentId, ct);
        if (row is null) return null;
        var set = JsonSerializer.Deserialize<MessageObservationSet>(row.DocumentJson, Json)!;
        var envelopes = (await db.MessageObservationSets.AsNoTracking().Where(r => r.EnvironmentId == environmentId).ToListAsync(ct))
            .SelectMany(r => JsonSerializer.Deserialize<MessageObservationSet>(r.DocumentJson, Json)!.Envelopes).ToList();
        var candidates = await CandidatesAsync(environmentId, ct);
        var assessed = set.Messages.Select(m =>
        {
            var mine = MessagingObservationRules.EnvelopesFor(m, envelopes);
            var correlation = MessagingObservationRules.Correlate(m, candidates);
            var (state, reason) = MessagingObservationRules.Processing(m, mine);
            return new MessageAssessment { Message = m, Processing = state, ProcessingReason = reason, Correlation = correlation, Timeline = MessagingObservationRules.Timeline(m, mine, correlation), Envelopes = mine };
        }).ToList();
        var limitations = new List<string>
        {
            "Imported observations: what the exporter saw at export time. BirkNext did not connect to the namespace or the envelope store.",
            "A message that is no longer in a queue is not evidence of successful processing.",
        };
        if (set.BodyPolicy == MessageBodyPolicy.MetadataOnly && set.Source == MessageObservationSource.ServiceBusPeekExport) limitations.Add("Bodies were not kept (metadata only): size and SHA-256 identify the content.");
        return new MessageObservationView { Set = set, Messages = assessed, EnvelopeStores = MessagingObservationRules.Envelopes(set.Envelopes), Limitations = limitations };
    }

    public async Task<bool> DeleteAsync(string environmentId, Guid id, CancellationToken ct)
    {
        var row = await db.MessageObservationSets.FirstOrDefaultAsync(r => r.Id == id && r.EnvironmentId == environmentId, ct);
        if (row is null) return false;
        db.MessageObservationSets.Remove(row);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>The execution ids ("{run}.{sequence}.{attempt}") of the environment's recent runs (bounded).</summary>
    private async Task<List<CorrelationCandidate>> CandidatesAsync(string environmentId, CancellationToken ct)
    {
        var result = new List<CorrelationCandidate>();
        foreach (var run in await executions.ListAsync(new ExecutionRunQuery(environmentId, Take: 50), ct))
        {
            if (await executions.GetAsync(run.RunId, new ExecutionCallQuery(Take: 1000), ct) is not { } detail) continue;
            foreach (var call in detail.Calls)
            {
                var ids = call.Attempts.Select(a => $"{run.RunId:N}.{call.Sequence}.{a.AttemptNumber}").ToList();
                if (ids.Count > 0) result.Add(new CorrelationCandidate(run.RunId, call.Sequence, call.Definition.Name, ids));
            }
        }
        return result;
    }

    // ── Parsing (case-insensitive property names; unknown fields ignored, never stored) ────────────────────────────────────

    private static MessageObservationSet ParseServiceBus(JsonObject root, MessageBodyPolicy policy)
    {
        var warnings = new List<string>();
        var subQueue = Enum.TryParse<MessageSubQueue>(Str(root, "subQueue") ?? "Active", true, out var sq) ? sq : MessageSubQueue.Active;
        var entity = new MessageEntityRef(Str(root, "namespace"), Str(root, "entity") ?? "", Str(root, "subscription"), subQueue);
        if (entity.Entity.Length == 0) warnings.Add("The file names no entity (queue or topic).");
        var messages = new List<ObservedMessage>();
        foreach (var node in Arr(root, "messages"))
        {
            if (node is not JsonObject m) continue;
            var properties = new Dictionary<string, string>(StringComparer.Ordinal);
            var redactions = 0;
            if (Get(m, "applicationProperties") is JsonObject props)
                foreach (var (key, value) in props)
                {
                    var text = value is JsonValue v ? v.ToString() : value?.ToJsonString() ?? "";
                    if (MessagingObservationRules.IsSensitiveKey(key)) { properties[key] = MessagingObservationRules.Mask; redactions++; continue; }
                    var (clean, r) = MessagingObservationRules.RedactText(text);
                    properties[key] = clean.Length > 500 ? clean[..500] : clean;
                    redactions += r;
                }
            var body = Get(m, "body") switch { JsonValue v when v.TryGetValue<string>(out var s) => s, null => null, var other => other.ToJsonString() };
            var contentType = Str(m, "contentType");
            string? preview = null; var truncated = false;
            if (policy == MessageBodyPolicy.SanitizedPreview)
            {
                var (p, t, r, w) = MessagingObservationRules.Preview(body, contentType);
                preview = p; truncated = t; redactions += r;
                if (w is not null && !warnings.Contains(w)) warnings.Add(w);
            }
            string? Clean(string? s) { if (s is null) return null; var (c, r) = MessagingObservationRules.RedactText(s); redactions += r; return c.Length > 1000 ? c[..1000] : c; }
            messages.Add(new ObservedMessage
            {
                MessageId = Str(m, "messageId") ?? "", CorrelationId = Clean(Str(m, "correlationId")), Subject = Clean(Str(m, "subject")), ContentType = contentType,
                MessageType = Str(m, "messageType") ?? (properties.TryGetValue("message-type", out var mt) ? mt : null),
                SequenceNumber = Long(m, "sequenceNumber"), EnqueuedAt = Time(m, "enqueuedTime"), ScheduledAt = Time(m, "scheduledEnqueueTime"), DeliveryCount = (int?)Long(m, "deliveryCount"),
                SessionId = Str(m, "sessionId"), Entity = entity, Properties = properties,
                DeadLetterReason = Clean(Str(m, "deadLetterReason")), DeadLetterDescription = Clean(Str(m, "deadLetterErrorDescription")),
                BodyBytes = body is null ? 0 : System.Text.Encoding.UTF8.GetByteCount(body), BodySha256 = body is null ? null : MessagingObservationRules.Sha256(body),
                BodyPreview = preview, BodyTruncated = truncated, RedactedFields = redactions,
            });
        }
        if (messages.Any(m => m.MessageId.Length == 0)) warnings.Add("Some messages have no message id; they cannot be joined to envelope rows.");
        return new MessageObservationSet { Source = MessageObservationSource.ServiceBusPeekExport, BodyPolicy = policy, ObservedAt = Time(root, "observedAt"), Messages = messages, Warnings = warnings };
    }

    private static MessageObservationSet ParseWolverine(JsonObject root)
    {
        var envelopes = new List<ObservedEnvelope>();
        foreach (var (key, store) in new[] { ("incoming", "Incoming"), ("outgoing", "Outgoing"), ("deadLetters", "DeadLetter") })
            foreach (var node in Arr(root, key))
            {
                if (node is not JsonObject e) continue;
                static string? Clean(string? s) { if (s is null) return null; var c = MessagingObservationRules.RedactText(s).Text; return c.Length > 1000 ? c[..1000] : c; }
                envelopes.Add(new ObservedEnvelope
                {
                    Id = Str(e, "id") ?? "", Store = store, Status = Str(e, "status"), MessageType = Str(e, "message_type") ?? Str(e, "messageType"),
                    Destination = Str(e, "destination") ?? Str(e, "source"),
                    Attempts = (int?)(Long(e, "attempts")), ExecutionTime = Time(e, "execution_time") ?? Time(e, "executionTime"),
                    SentOrReceivedAt = Time(e, "sent_at") ?? Time(e, "sentAt") ?? Time(e, "received_at_time") ?? Time(e, "receivedAt") ?? Time(e, "timestamp"),
                    ExceptionType = Str(e, "exception_type") ?? Str(e, "exceptionType"), ExceptionMessage = Clean(Str(e, "exception_message") ?? Str(e, "exceptionMessage")),
                    MessageId = Str(e, "message_id") ?? Str(e, "messageId"), CorrelationId = Str(e, "correlation_id") ?? Str(e, "correlationId"),
                });
            }
        var warnings = new List<string>();
        if (envelopes.Count == 0) warnings.Add("No envelope rows were found (incoming, outgoing, deadLetters).");
        warnings.Add("Envelope bodies are never imported: only the store columns are kept.");
        return new MessageObservationSet { Source = MessageObservationSource.WolverineEnvelopeExport, BodyPolicy = MessageBodyPolicy.MetadataOnly, ObservedAt = Time(root, "observedAt"), Envelopes = envelopes, Warnings = warnings };
    }

    private static JsonNode? Get(JsonObject o, string name) => o.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
    private static string? Str(JsonObject o, string name) => Get(o, name) switch { JsonValue v when v.TryGetValue<string>(out var s) => s, JsonValue v => v.ToString(), _ => null };
    private static long? Long(JsonObject o, string name) => Get(o, name) switch
    {
        JsonValue v when v.TryGetValue<long>(out var l) => l,
        JsonValue v when v.TryGetValue<string>(out var s) && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) => p,
        _ => null,
    };
    private static DateTimeOffset? Time(JsonObject o, string name) =>
        Str(o, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t.ToUniversalTime() : null;
    private static IEnumerable<JsonNode?> Arr(JsonObject o, string name) => Get(o, name) as JsonArray ?? [];
}
