using System.Collections.Concurrent;
using System.Text;

namespace BirkNext.Api.Services.ExecutionEvidence;

/// <summary>
/// Counts what actually goes on the wire. The .NET HTTP/1.1 stack resends a request without a body (GET, DELETE, …) up to three more times
/// when the connection closes before any response byte — below every DelegatingHandler, with no public switch to disable it. This stream is
/// installed as the connection's plaintext stream (after TLS), so it sees every request head written and counts it by the execution
/// correlation header. An attempt then records how many times it was really transmitted.
/// </summary>
public sealed class TransmissionCountingStream(Stream inner) : Stream
{
    private static readonly ConcurrentDictionary<string, int> Counts = new(StringComparer.Ordinal);
    private static readonly string Marker = ExecutionRunService.CorrelationHeader + ": ";
    private readonly StringBuilder _window = new();

    /// <summary>Wire transmissions seen for one correlation id (and forgets it).</summary>
    public static int Take(string correlation) => Counts.TryRemove(correlation, out var n) ? n : 0;

    private void Observe(ReadOnlySpan<byte> written)
    {
        // Request heads are ASCII; keep a short rolling window so a header split across writes is still found.
        _window.Append(Encoding.ASCII.GetString(written.Length > 4096 ? written[..4096] : written));
        var text = _window.ToString();
        var at = 0;
        while ((at = text.IndexOf(Marker, at, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var end = text.IndexOf("\r\n", at, StringComparison.Ordinal);
            if (end < 0) break;
            Counts.AddOrUpdate(text[(at + Marker.Length)..end].Trim(), 1, (_, n) => n + 1);
            at = end;
        }
        var keep = text.LastIndexOf(Marker, StringComparison.OrdinalIgnoreCase) is var last and >= 0 && text.IndexOf("\r\n", last, StringComparison.Ordinal) < 0
            ? text[last..] : text.Length > Marker.Length ? text[^Marker.Length..] : text;
        _window.Clear().Append(keep);
    }

    public override void Write(byte[] buffer, int offset, int count) { Observe(buffer.AsSpan(offset, count)); inner.Write(buffer, offset, count); }
    public override void Write(ReadOnlySpan<byte> buffer) { Observe(buffer); inner.Write(buffer); }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) { Observe(buffer.Span); await inner.WriteAsync(buffer, ct); }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.ReadAsync(buffer, offset, count, ct);
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
    public override bool CanRead => inner.CanRead;
    public override bool CanWrite => inner.CanWrite;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    public override ValueTask DisposeAsync() => inner.DisposeAsync();
}
