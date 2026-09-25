using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Text.Json;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.Integrations;

// Evidence ports of Integration Quality Review. Each one is read-only by contract: nothing publishes, consumes, moves a
// checkpoint, creates a consumer group or changes Azure configuration. A port that has no adapter reports "not available"
// with a reason — it never returns a default that reads like a measurement.

/// <summary>Result of probing a messaging namespace endpoint: DNS, TCP and TLS only. Never an AMQP session, never data.</summary>
public sealed record NamespaceProbeResult(bool Resolved, bool Reachable, bool TlsEstablished, string Detail, double ElapsedMs, DateTimeOffset CapturedAt);

public interface IIntegrationNamespaceProbe
{
    Task<NamespaceProbeResult> ProbeAsync(string fqdn, CancellationToken ct);
}

/// <summary>DNS resolution, TCP connect to 443 and a TLS handshake to the namespace FQDN. Sends no application data.</summary>
public sealed class TlsNamespaceProbe : IIntegrationNamespaceProbe
{
    public async Task<NamespaceProbeResult> ProbeAsync(string fqdn, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var addresses = await System.Net.Dns.GetHostAddressesAsync(fqdn, timeout.Token);
            if (addresses.Length == 0) return new(false, false, false, "The namespace name did not resolve.", started.Elapsed.TotalMilliseconds, DateTimeOffset.UtcNow);
            using var client = new TcpClient();
            await client.ConnectAsync(addresses, 443, timeout.Token);
            await using var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = fqdn }, timeout.Token);
            return new(true, true, true, $"Resolved, TCP 443 connected and TLS established ({tls.SslProtocol}).", started.Elapsed.TotalMilliseconds, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or System.Security.Authentication.AuthenticationException or IOException)
        {
            var resolved = ex is not SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData };
            return new(resolved, false, false, ex is OperationCanceledException ? "Timed out while connecting." : $"Not reachable ({ex.GetType().Name}).", started.Elapsed.TotalMilliseconds, DateTimeOffset.UtcNow);
        }
    }
}

/// <summary>
/// Runtime evidence for one integration. Every field is nullable: null means "no evidence", never zero or false. Provenance
/// and freshness travel with it.
/// </summary>
public sealed record IntegrationRuntimeEvidence
{
    public IntegrationEvidenceSource Source { get; init; }
    public DateTimeOffset CapturedAt { get; init; }
    /// <summary>Metadata lookup result: true exists, false explicitly not found, null not looked up.</summary>
    public bool? HubExists { get; init; }
    public DateTimeOffset? LastEnqueuedAt { get; init; }
    public DateTimeOffset? LastConsumerProgressAt { get; init; }
    public long? CheckpointEventsBehind { get; init; }
    public int? DeserializationFailures { get; init; }
    public int? ConsumerExceptions { get; init; }
    public int? AuthorizationFailures { get; init; }
    public double? ProcessingLatencyMs { get; init; }
    public bool? EnvelopeObserved { get; init; }
    public List<string>? OperationTypesObserved { get; init; }
    public List<string>? RequiredFieldsMissing { get; init; }
}

/// <summary>What a runtime evidence adapter can supply. Drives pre-run readiness; an absent capability is "Not assessable".</summary>
public sealed record IntegrationEvidenceCapabilities(bool HubMetadata, bool MessageActivity, bool ConsumerCheckpoints, bool ConsumerErrors, bool Timing, bool PayloadStructure, string Description)
{
    public static readonly IntegrationEvidenceCapabilities None = new(false, false, false, false, false, false,
        "No runtime evidence adapter is configured in this build (no Event Hub metadata, Application Insights or checkpoint access).");
}

public interface IIntegrationRuntimeEvidenceSource
{
    IntegrationEvidenceCapabilities Capabilities { get; }
    Task<IReadOnlyDictionary<string, IntegrationRuntimeEvidence>> GetAsync(IntegrationPlatform platform, IReadOnlyList<IntegrationDefinition> integrations, CancellationToken ct);
}

/// <summary>The build's default: no runtime adapter. Every runtime domain is then Not assessed with this reason.</summary>
public sealed class NoRuntimeEvidenceSource : IIntegrationRuntimeEvidenceSource
{
    public IntegrationEvidenceCapabilities Capabilities => IntegrationEvidenceCapabilities.None;
    public Task<IReadOnlyDictionary<string, IntegrationRuntimeEvidence>> GetAsync(IntegrationPlatform platform, IReadOnlyList<IntegrationDefinition> integrations, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, IntegrationRuntimeEvidence>>(new Dictionary<string, IntegrationRuntimeEvidence>());
}

/// <summary>Producer and consumer contract documents (JSON Schema-like) for one integration, when an adapter can retrieve them.</summary>
public sealed record IntegrationContractEvidence(string? ProducerSchemaJson, string? ConsumerSchemaJson, string Source);

public interface IIntegrationContractSource
{
    bool CanRetrieve { get; }
    Task<IntegrationContractEvidence?> GetAsync(IntegrationDefinition definition, CancellationToken ct);
}

/// <summary>The build's default: contract references are pointers only; nothing retrieves event contracts yet.</summary>
public sealed class NoContractSource : IIntegrationContractSource
{
    public bool CanRetrieve => false;
    public Task<IntegrationContractEvidence?> GetAsync(IntegrationDefinition definition, CancellationToken ct) => Task.FromResult<IntegrationContractEvidence?>(null);
}

/// <summary>
/// Structural producer/consumer comparison of two JSON Schema-like documents ({ "properties": {...}, "required": [...] }):
/// a field the consumer requires that the producer does not provide, or a type that differs, is incompatible. Additions by
/// the producer are compatible. No payload values are read.
/// </summary>
public static class EventContractComparer
{
    public sealed record Difference(string Code, string Field, string Detail);

    public static List<Difference> Compare(string producerSchemaJson, string consumerSchemaJson)
    {
        var producer = Fields(producerSchemaJson);
        var consumer = Fields(consumerSchemaJson);
        var differences = new List<Difference>();
        foreach (var (field, (type, required)) in consumer)
        {
            if (!producer.TryGetValue(field, out var provided))
            {
                if (required) differences.Add(new("REQUIRED_FIELD_MISSING", field, $"The consumer requires `{field}`, which the producer contract does not define."));
                continue;
            }
            if (type is not null && provided.Type is not null && !string.Equals(type, provided.Type, StringComparison.Ordinal))
                differences.Add(new("TYPE_MISMATCH", field, $"`{field}` is `{provided.Type}` in the producer contract but `{type}` in the consumer contract."));
            if (required && !provided.Required)
                differences.Add(new("NULLABILITY_MISMATCH", field, $"The consumer requires `{field}`, but the producer contract marks it optional."));
        }
        return differences;
    }

    private static Dictionary<string, (string? Type, bool Required)> Fields(string schemaJson)
    {
        using var document = JsonDocument.Parse(schemaJson);
        var root = document.RootElement;
        var required = root.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.Array
            ? r.EnumerateArray().Select(e => e.GetString()).OfType<string>().ToHashSet(StringComparer.Ordinal) : [];
        var fields = new Dictionary<string, (string?, bool)>(StringComparer.Ordinal);
        if (root.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
            foreach (var property in properties.EnumerateObject())
                fields[property.Name] = (property.Value.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null, required.Contains(property.Name));
        return fields;
    }
}
