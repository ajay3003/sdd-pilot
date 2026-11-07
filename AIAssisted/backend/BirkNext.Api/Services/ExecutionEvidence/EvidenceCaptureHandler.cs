using System.Diagnostics;
using BirkNext.ExecutionEvidence;

namespace BirkNext.Api.Services.ExecutionEvidence;

/// <summary>
/// Per-attempt capture slot carried on the request (<see cref="HttpRequestMessage.Options"/>). The handler fills it with what actually went over
/// the wire; raw bodies stay in memory for assertion evaluation only and are never persisted — only the redacted capture is.
/// </summary>
public sealed class EvidenceCaptureSlot
{
    public static readonly HttpRequestOptionsKey<EvidenceCaptureSlot> Key = new("birknext.execution-evidence");
    public CapturePolicy Policy { get; init; } = CapturePolicy.BoundedBody;
    public int StoredTextLimit { get; init; } = 64 * 1024;
    public int ReadLimit { get; init; } = 1024 * 1024;
    public CapturedRequest? Request { get; set; }
    public CapturedResponse? Response { get; set; }
    public byte[]? RawResponse { get; set; }
    public bool ResponseReadTruncated { get; set; }
    public long ResponseBytes { get; set; }
    public long ElapsedMs { get; set; }
}

/// <summary>
/// The transport boundary: the last handler before the socket handler of the <c>execution-evidence</c> client. It records the final request
/// (method, URL, every header actually sent, the serialized body bytes) and the response (status, headers, body read up to a bound) for
/// the attempt in <see cref="EvidenceCaptureSlot"/>. A request that fails before a response leaves Response null — no response is invented.
/// </summary>
public sealed class EvidenceCaptureHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (!request.Options.TryGetValue(EvidenceCaptureSlot.Key, out var slot)) return await base.SendAsync(request, ct);
        byte[]? requestBytes = null;
        if (request.Content is not null)
        {
            await request.Content.LoadIntoBufferAsync();
            requestBytes = await request.Content.ReadAsByteArrayAsync(ct);
        }
        var sentHeaders = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>());
        slot.Request = new CapturedRequest
        {
            Method = request.Method.Method, Url = ExecutionEvidenceRedaction.Url(request.RequestUri!), Headers = ExecutionEvidenceRedaction.Headers(sentHeaders),
            Body = request.Content is null ? null : ExecutionEvidenceRedaction.Body(requestBytes, requestBytes?.LongLength ?? 0, request.Content.Headers.ContentType?.ToString(),
                slot.Policy, slot.StoredTextLimit, readTruncated: false),
            CapturedAt = DateTimeOffset.UtcNow,
        };
        var watch = Stopwatch.StartNew();
        var response = await base.SendAsync(request, ct);
        // Read the body once, bounded: count every byte, keep at most ReadLimit for inspection, then hand the caller the same bytes.
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var kept = new MemoryStream();
        var buffer = new byte[16 * 1024];
        long total = 0; int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (kept.Length < slot.ReadLimit) kept.Write(buffer, 0, (int)Math.Min(read, slot.ReadLimit - kept.Length));
        }
        watch.Stop();
        slot.RawResponse = kept.ToArray(); slot.ResponseBytes = total; slot.ResponseReadTruncated = total > kept.Length; slot.ElapsedMs = watch.ElapsedMilliseconds;
        var replaced = new ByteArrayContent(slot.RawResponse);
        foreach (var h in response.Content.Headers) replaced.Headers.TryAddWithoutValidation(h.Key, h.Value);
        var responseHeaders = response.Headers.Concat(response.Content.Headers).ToList();
        response.Content = replaced;
        slot.Response = new CapturedResponse
        {
            StatusCode = (int)response.StatusCode, ReasonPhrase = response.ReasonPhrase, Headers = ExecutionEvidenceRedaction.Headers(responseHeaders),
            Body = ExecutionEvidenceRedaction.Body(slot.RawResponse, total, response.Content.Headers.ContentType?.ToString(), slot.Policy, slot.StoredTextLimit, slot.ResponseReadTruncated),
            DurationMs = slot.ElapsedMs,
        };
        return response;
    }
}
