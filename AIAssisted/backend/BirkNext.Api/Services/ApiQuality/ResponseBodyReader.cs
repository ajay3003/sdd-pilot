using System.IO.Compression;
using System.Text;
using BirkNext.ApiReview;

namespace BirkNext.Api.Services.ApiQuality;

/// <summary>A response body read for review: its transfer/decoded evidence and, only when complete, the decoded text.</summary>
public sealed record ResponseBodyRead(ApiReviewResponseBody Evidence, string? Text);

/// <summary>
/// Reads a response body the way the review must see it. The public API client disables automatic decompression (so Content-Encoding
/// stays observable) and advertises gzip/br, so this is the one place the body is decoded — never twice.
/// <list type="bullet">
/// <item>Transfer size is the count of encoded bytes actually received (works for chunked responses without Content-Length).</item>
/// <item>Decoded size is the count of bytes after Content-Encoding removal; it is what the REST payload threshold evaluates.</item>
/// <item>Decoding streams: at most <see cref="MaxInspectedBytes"/> decoded bytes are kept for analysis; beyond that bytes are only
/// counted up to <see cref="MaxCountedBytes"/> and discarded, so a highly compressed body cannot allocate without bound.</item>
/// <item>An unsupported or undecodable encoding yields no decoded size and no text — never a 0-byte body.</item>
/// </list>
/// Content-Encoding (gzip/br) and the character set (charset) are different layers: the first is removed here, then the charset decodes text.
/// </summary>
public static class ResponseBodyReader
{
    /// <summary>Decoded bytes retained for JSON / ProblemDetails / internal-detail analysis. A larger body is sized but not analysed.</summary>
    public const long MaxInspectedBytes = 1024 * 1024;
    /// <summary>Bytes counted (not retained) to size a body; decoding stops here and the decoded size becomes a lower bound.</summary>
    public const long MaxCountedBytes = 64L * 1024 * 1024;
    /// <summary>The codings the review advertises (Accept-Encoding: gzip, br) and implements. Deflate is not requested and not claimed.</summary>
    public static readonly IReadOnlyList<string> SupportedEncodings = ["gzip", "x-gzip", "br"];

    public static async Task<ResponseBodyRead> ReadAsync(HttpResponseMessage response, CancellationToken ct, long maxInspected = MaxInspectedBytes, long maxCounted = MaxCountedBytes)
    {
        // Content-Encoding lists codings in the order they were applied; they are removed in reverse. "identity" is no coding.
        var codings = response.Content.Headers.ContentEncoding.Select(c => c.Trim().ToLowerInvariant()).Where(c => c.Length > 0 && c != "identity").ToList();
        var label = codings.Count == 0 ? null : string.Join(", ", codings);
        var declared = response.Content.Headers.ContentLength;
        await using var raw = await response.Content.ReadAsStreamAsync(ct);
        var wire = new CountingStream(raw);

        if (codings.FirstOrDefault(c => !SupportedEncodings.Contains(c)) is { } unsupported)
        {
            var (_, drained) = await DrainAsync(wire, maxCounted, ct);
            return new(new ApiReviewResponseBody
            {
                ContentEncoding = label, TransferBytes = drained ? wire.Count : declared, Decoding = ApiResponseBodyDecoding.UnsupportedEncoding,
                Reason = codings.Count > 1 ? $"Unsupported Content-Encoding chain: {label}." : $"Unsupported Content-Encoding: {unsupported}.",
            }, null);
        }

        Stream content = wire;
        var decoders = new List<Stream>();
        for (var i = codings.Count - 1; i >= 0; i--)
        {
            content = codings[i] == "br" ? new BrotliStream(content, CompressionMode.Decompress, leaveOpen: true) : new GZipStream(content, CompressionMode.Decompress, leaveOpen: true);
            decoders.Add(content);
        }

        using var kept = new MemoryStream();
        long decoded = 0;
        var lowerBound = false;
        try
        {
            var chunk = new byte[16384];
            while (true)
            {
                var read = await content.ReadAsync(chunk, ct);
                if (read <= 0) break;
                if (decoded < maxInspected) kept.Write(chunk, 0, (int)Math.Min(read, maxInspected - decoded));
                decoded += read;
                if (decoded > maxCounted) { lowerBound = true; break; }
            }
            // Bytes a decoder did not need (e.g. after the gzip trailer) are still transfer bytes.
            var wireComplete = !lowerBound && (await DrainAsync(wire, maxCounted, ct)).Complete;
            var transfer = wireComplete ? wire.Count : codings.Count == 0 ? declared : null;
            if (decoded == 0 && (transfer ?? 0) == 0)
                return new(new ApiReviewResponseBody { ContentEncoding = label, TransferBytes = 0, DecodedBytes = 0, Decoding = ApiResponseBodyDecoding.NoBody, Inspected = true }, "");

            var status = codings.Count == 0 ? ApiResponseBodyDecoding.NotEncoded : ApiResponseBodyDecoding.Decoded;
            // An identity body larger than the counting limit still has an exact size when Content-Length declares it.
            long? decodedSize = !lowerBound ? decoded : codings.Count == 0 && declared is { } d ? d : decoded;
            var isLowerBound = lowerBound && !(codings.Count == 0 && declared is not null);
            var complete = decoded <= maxInspected;
            return new(new ApiReviewResponseBody
            {
                ContentEncoding = label, TransferBytes = transfer, DecodedBytes = decodedSize, DecodedBytesIsLowerBound = isLowerBound, Decoding = status, Inspected = complete,
                Reason = complete ? null : $"Decoded response exceeded the {ApiReviewPolicy.Bytes(maxInspected)} body inspection limit; body analysis not tested.",
            }, complete ? TextOf(kept, response) : null);
        }
        catch (Exception ex) when (codings.Count > 0 && ex is InvalidDataException or InvalidOperationException)
        {
            var (_, drained) = await DrainAsync(wire, maxCounted, ct);
            return new(new ApiReviewResponseBody
            {
                ContentEncoding = label, TransferBytes = drained ? wire.Count : declared, Decoding = ApiResponseBodyDecoding.DecodeFailed,
                Reason = $"The body could not be decoded as {label} ({ex.GetType().Name}).",
            }, null);
        }
        catch (IOException ex)
        {
            return new(new ApiReviewResponseBody
            {
                ContentEncoding = label, Decoding = ApiResponseBodyDecoding.Incomplete, Reason = $"The response body was not received completely ({ex.GetType().Name}).",
            }, null);
        }
        finally
        {
            foreach (var decoder in decoders) await decoder.DisposeAsync();
        }
    }

    /// <summary>Text of the decoded bytes in the response charset (UTF-8 when absent or unknown, the JSON default).</summary>
    private static string TextOf(MemoryStream bytes, HttpResponseMessage response)
    {
        var encoding = Encoding.UTF8;
        if (response.Content.Headers.ContentType?.CharSet is { Length: > 0 } charset)
            try { encoding = Encoding.GetEncoding(charset.Trim('"')); } catch (ArgumentException) { }
        return encoding.GetString(bytes.GetBuffer(), 0, (int)bytes.Length);
    }

    /// <summary>Reads (and discards) the rest of a stream to count it; stops at the limit.</summary>
    private static async Task<(long Read, bool Complete)> DrainAsync(CountingStream stream, long limit, CancellationToken ct)
    {
        var chunk = new byte[16384];
        long total = 0;
        while (stream.Count <= limit)
        {
            var read = await stream.ReadAsync(chunk, ct);
            if (read <= 0) return (total, true);
            total += read;
        }
        return (total, false);
    }

    /// <summary>Read-only pass-through that counts the bytes read from the underlying (encoded) stream.</summary>
    private sealed class CountingStream(Stream inner) : Stream
    {
        public long Count { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => Count; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { var n = inner.Read(buffer, offset, count); Count += n; return n; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { var n = await inner.ReadAsync(buffer, cancellationToken); Count += n; return n; }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
