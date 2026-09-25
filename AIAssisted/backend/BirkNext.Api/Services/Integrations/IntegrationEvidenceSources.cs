using System.Net.Security;
using System.Net.Sockets;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.Integrations;

// Read-only evidence for Integration Quality Review. Every adapter returns a typed EvidenceResult: Available with a value, or a precise
// state (NotConfigured, NotAuthorized, NotFound, Error…) with the reason — never a fabricated or zero value. No adapter can publish,
// receive, checkpoint, create consumer groups or change Azure configuration (see IntegrationEvidenceSafetyTests).

/// <summary>A runtime fact with its provenance: which source, when it was captured, and why it is missing when it is.</summary>
public sealed record EvidenceResult<T>(IntegrationEvidenceState State, IntegrationEvidenceSource Source, string Reason, DateTimeOffset CapturedAt, T? Value) where T : class
{
    public bool IsAvailable => State == IntegrationEvidenceState.Available && Value is not null;
    public static EvidenceResult<T> Available(IntegrationEvidenceSource source, T value, string reason = "") => new(IntegrationEvidenceState.Available, source, reason, DateTimeOffset.UtcNow, value);
    public static EvidenceResult<T> Missing(IntegrationEvidenceState state, IntegrationEvidenceSource source, string reason) => new(state, source, reason, DateTimeOffset.UtcNow, null);
}

// ── Namespace probe (DNS / TCP 443 / TLS) ─────────────────────────────────────────────────────────────────────────────

public sealed record NamespaceProbeResult(bool Resolved, bool Reachable, bool TlsEstablished, string Detail, double ElapsedMs, DateTimeOffset CapturedAt, string? TlsProtocol = null);

public interface IIntegrationNamespaceProbe
{
    Task<NamespaceProbeResult> ProbeAsync(string fqdn, CancellationToken ct);
}

/// <summary>DNS resolution, a TCP connection on 443 and a TLS handshake — nothing is sent over the connection.</summary>
public sealed class TlsNamespaceProbe : IIntegrationNamespaceProbe
{
    public async Task<NamespaceProbeResult> ProbeAsync(string fqdn, CancellationToken ct)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var at = DateTimeOffset.UtcNow;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var addresses = await System.Net.Dns.GetHostAddressesAsync(fqdn, timeout.Token);
            if (addresses.Length == 0) return new(false, false, false, "DNS returned no address.", started.Elapsed.TotalMilliseconds, at);
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(fqdn, 443, timeout.Token);
            await using var ssl = new SslStream(tcp.GetStream(), false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = fqdn }, timeout.Token);
            return new(true, true, true, $"Resolved, TCP 443 connected and TLS established ({ssl.SslProtocol}).", started.Elapsed.TotalMilliseconds, at, ssl.SslProtocol.ToString());
        }
        catch (SocketException ex) { return new(true, false, false, $"TCP connection failed ({ex.SocketErrorCode}).", started.Elapsed.TotalMilliseconds, at); }
        catch (System.Security.Authentication.AuthenticationException) { return new(true, true, false, "TLS handshake failed.", started.Elapsed.TotalMilliseconds, at); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(false, false, false, "Timed out after 8 s.", started.Elapsed.TotalMilliseconds, at); }
        catch (Exception ex) when (ex is IOException or ArgumentException) { return new(false, false, false, $"Probe failed ({ex.GetType().Name}).", started.Elapsed.TotalMilliseconds, at); }
    }
}

// ── Event Hub metadata ───────────────────────────────────────────────────────────────────────────────────────────────

public sealed record PartitionRuntime(string PartitionId, long LastEnqueuedSequenceNumber, DateTimeOffset? LastEnqueuedTime, bool IsEmpty);

/// <summary>Hub existence and per-partition last-enqueued position. <see cref="Exists"/> false only when the service said "not found".</summary>
public sealed record EventHubRuntimeMetadata(bool Exists, IReadOnlyList<PartitionRuntime> Partitions)
{
    public DateTimeOffset? LastEnqueuedTime => Partitions.Where(p => !p.IsEmpty).Select(p => p.LastEnqueuedTime).Max();
}

public interface IEventHubMetadataSource
{
    /// <summary>Configuration-only readiness (nothing is contacted).</summary>
    IntegrationEvidenceAdapterStatus Describe(IntegrationPlatform platform);
    Task<EvidenceResult<EventHubRuntimeMetadata>> GetHubAsync(IntegrationPlatform platform, string hubName, CancellationToken ct);
}

// ── Consumer groups (Azure Resource Manager, read-only list) ─────────────────────────────────────────────────────────

public sealed record ConsumerGroupList(IReadOnlyList<string> Names);

public interface IEventHubConsumerGroupSource
{
    IntegrationEvidenceAdapterStatus Describe(IntegrationPlatform platform);
    Task<EvidenceResult<ConsumerGroupList>> ListAsync(IntegrationPlatform platform, string hubName, CancellationToken ct);
}

// ── Checkpoints (EventProcessorClient blob checkpoint store, read-only list) ──────────────────────────────────────────

public sealed record PartitionCheckpoint(string PartitionId, long? SequenceNumber, long? Offset, DateTimeOffset? UpdatedAt);

public sealed record CheckpointEvidence(string ConsumerGroup, IReadOnlyList<PartitionCheckpoint> Partitions, int OwnershipRecords)
{
    public DateTimeOffset? LastUpdated => Partitions.Select(p => p.UpdatedAt).Max();
}

public interface ICheckpointEvidenceSource
{
    IntegrationEvidenceAdapterStatus Describe(IntegrationPlatform platform);
    Task<EvidenceResult<CheckpointEvidence>> GetAsync(IntegrationPlatform platform, string hubName, string consumerGroup, CancellationToken ct);
}

// ── Telemetry (Application Insights / Log Analytics, bounded aggregate queries) ───────────────────────────────────────

/// <summary>Aggregates only — counts and timestamps for one consumer role in the review window. No row, message or payload is kept.</summary>
public sealed record ConsumerTelemetry(
    long Exceptions, long DeserializationErrors, long AuthorizationErrors, long RetryIndicators, long DeadLetterIndicators,
    long ProcessingTraces, long CorrelatedRows, long DependencyCalls, long DependencyFailures, double? DependencyMedianMs,
    DateTimeOffset? LastActivity, DateTimeOffset? LastException, int WindowHours);

public interface ITelemetryEvidenceSource
{
    IntegrationEvidenceAdapterStatus Describe(IntegrationPlatform platform);
    Task<EvidenceResult<ConsumerTelemetry>> GetConsumerAsync(IntegrationPlatform platform, string roleName, int windowHours, CancellationToken ct);
}

// ── Null adapters (tests and instances without Azure access) ─────────────────────────────────────────────────────────

public static class NotConfiguredEvidence
{
    public static IntegrationEvidenceAdapterStatus Status(string adapter, IntegrationEvidenceSource source, IntegrationEvidenceState state, string reason) =>
        new() { Adapter = adapter, Source = source, State = state, Reason = reason, CapturedAt = DateTimeOffset.UtcNow };
}
