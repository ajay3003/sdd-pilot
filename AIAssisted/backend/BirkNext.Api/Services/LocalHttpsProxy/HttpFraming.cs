using System.Buffers;
using System.Globalization;
using System.Text;

namespace BirkNext.Api.Services.LocalHttpsProxy;

/// <summary>Parsed HTTP/1.x message head (start line + headers) with the raw bytes preserved for transparent forwarding.</summary>
internal sealed class HttpHead
{
    public required string StartLine { get; init; }
    public required IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; }
    public required byte[] Raw { get; init; }

    public string? Header(string name) => Headers.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    public bool HasToken(string headerName, string token) =>
        Headers.Where(h => string.Equals(h.Key, headerName, StringComparison.OrdinalIgnoreCase))
            .SelectMany(h => h.Value.Split(',')).Any(v => string.Equals(v.Trim(), token, StringComparison.OrdinalIgnoreCase));

    public bool IsChunked => HasToken("Transfer-Encoding", "chunked");
    public bool ConnectionClose => HasToken("Connection", "close") || StartLine.EndsWith("HTTP/1.0", StringComparison.Ordinal);
    public bool IsUpgrade => Header("Upgrade") is not null && HasToken("Connection", "upgrade");

    public long? ContentLength
    {
        get
        {
            var value = Header("Content-Length");
            return value is not null && long.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var length) && length >= 0 ? length : null;
        }
    }

    // Request line: METHOD SP target SP HTTP/x.y
    public string Method => StartLine.Split(' ', 3)[0];
    public string Target => StartLine.Split(' ', 3) is { Length: >= 2 } parts ? parts[1] : "";
    public bool IsRequest => StartLine.Split(' ', 3) is { Length: 3 } parts && parts[2].StartsWith("HTTP/", StringComparison.Ordinal);

    // Status line: HTTP/x.y SP code SP reason
    public int StatusCode => StartLine.StartsWith("HTTP/", StringComparison.Ordinal) && StartLine.Split(' ', 3) is { Length: >= 2 } parts &&
        int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var code) ? code : 0;

    public static bool TryParse(byte[] raw, out HttpHead head)
    {
        head = null!;
        var text = Encoding.Latin1.GetString(raw);
        var lines = text.Split("\r\n");
        if (lines.Length < 2 || lines[0].Length == 0 || lines[0].Length > 16384) return false;
        var headers = new List<KeyValuePair<string, string>>();
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0) break;
            if (line[0] is ' ' or '\t')
            {
                if (headers.Count == 0) return false;
                var last = headers[^1];
                headers[^1] = new(last.Key, last.Value + " " + line.Trim());
                continue;
            }
            var colon = line.IndexOf(':');
            if (colon <= 0) return false;
            headers.Add(new(line[..colon].Trim(), line[(colon + 1)..].Trim()));
        }
        head = new HttpHead { StartLine = lines[0], Headers = headers, Raw = raw };
        return true;
    }
}

internal enum BodyKind { None, ContentLength, Chunked, UntilClose }

internal readonly record struct BodyFraming(BodyKind Kind, long Length)
{
    public static BodyFraming ForRequest(HttpHead request)
    {
        if (request.IsChunked) return new(BodyKind.Chunked, 0);
        var length = request.ContentLength;
        return length is > 0 ? new(BodyKind.ContentLength, length.Value) : new(BodyKind.None, 0);
    }

    public static BodyFraming ForResponse(HttpHead response, string requestMethod)
    {
        var status = response.StatusCode;
        if (status is >= 100 and < 200 or 204 or 304 || string.Equals(requestMethod, "HEAD", StringComparison.OrdinalIgnoreCase)) return new(BodyKind.None, 0);
        if (response.IsChunked) return new(BodyKind.Chunked, 0);
        var length = response.ContentLength;
        if (length is not null) return length.Value == 0 ? new(BodyKind.None, 0) : new(BodyKind.ContentLength, length.Value);
        return new(BodyKind.UntilClose, 0);
    }
}

/// <summary>Buffered reader over a network stream that can hand back the bytes it over-read. One reader per stream direction.</summary>
internal sealed class BufferedNetworkReader(Stream stream, int bufferSize = 16384) : IDisposable
{
    private static readonly byte[] HeadTerminator = "\r\n\r\n"u8.ToArray();
    private static readonly byte[] LineTerminator = "\r\n"u8.ToArray();
    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
    private int _start;
    private int _end;

    public bool HasBuffered => _end > _start;

    /// <summary>Reads a complete message head including the terminating blank line. Null when the stream ended before any byte arrived.</summary>
    public async ValueTask<byte[]?> ReadHeadAsync(int maxBytes, CancellationToken ct)
    {
        var scanned = 0;
        while (true)
        {
            var available = _end - _start;
            var from = Math.Max(0, scanned - 3);
            var index = new ReadOnlySpan<byte>(_buffer, _start + from, available - from).IndexOf(HeadTerminator);
            if (index >= 0)
            {
                var length = from + index + HeadTerminator.Length;
                var head = new byte[length];
                Buffer.BlockCopy(_buffer, _start, head, 0, length);
                _start += length;
                return head;
            }
            scanned = available;
            if (available >= maxBytes) throw new InvalidDataException("HTTP message head exceeds the allowed size.");
            if (!await FillAsync(ct)) return available == 0 ? null : throw new IOException("Connection closed inside an HTTP message head.");
        }
    }

    public async ValueTask<string?> ReadLineAsync(int maxBytes, CancellationToken ct)
    {
        while (true)
        {
            var available = _end - _start;
            var index = new ReadOnlySpan<byte>(_buffer, _start, available).IndexOf(LineTerminator);
            if (index >= 0)
            {
                var line = Encoding.Latin1.GetString(_buffer, _start, index);
                _start += index + 2;
                return line;
            }
            if (available >= maxBytes) throw new InvalidDataException("HTTP line exceeds the allowed size.");
            if (!await FillAsync(ct)) return null;
        }
    }

    public async Task CopyExactAsync(Stream destination, long count, CancellationToken ct)
    {
        while (count > 0)
        {
            if (!HasBuffered && !await FillAsync(ct)) throw new IOException("Connection closed inside an HTTP message body.");
            var take = (int)Math.Min(count, _end - _start);
            await destination.WriteAsync(new ReadOnlyMemory<byte>(_buffer, _start, take), ct);
            _start += take;
            count -= take;
        }
    }

    /// <summary>
    /// Copies exactly <paramref name="count"/> body bytes to <paramref name="destination"/> verbatim (no modification of the relayed
    /// stream) while returning a copy of those bytes for transient in-memory inspection. Callers must bound <paramref name="count"/>;
    /// it is used only for small JSON POST bodies (GraphQL classification) and the returned buffer is never stored or logged.
    /// </summary>
    public async Task<byte[]> CopyExactCapturingAsync(Stream destination, long count, CancellationToken ct)
    {
        var captured = new byte[count];
        var offset = 0;
        while (count > 0)
        {
            if (!HasBuffered && !await FillAsync(ct)) throw new IOException("Connection closed inside an HTTP message body.");
            var take = (int)Math.Min(count, _end - _start);
            var slice = new ReadOnlyMemory<byte>(_buffer, _start, take);
            await destination.WriteAsync(slice, ct);
            slice.Span.CopyTo(captured.AsSpan(offset));
            offset += take;
            _start += take;
            count -= take;
        }
        return captured;
    }

    /// <summary>Copies a chunked body verbatim (chunk sizes, extensions and trailers included).</summary>
    public async Task CopyChunkedAsync(Stream destination, CancellationToken ct)
    {
        while (true)
        {
            var line = await ReadLineAsync(8192, ct) ?? throw new IOException("Connection closed inside a chunked body.");
            await destination.WriteAsync(Encoding.Latin1.GetBytes(line + "\r\n"), ct);
            var sizeText = line.Split(';', 2)[0].Trim();
            if (!long.TryParse(sizeText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size) || size < 0) throw new InvalidDataException("Invalid chunk size.");
            if (size == 0)
            {
                while (true)
                {
                    var trailer = await ReadLineAsync(8192, ct) ?? throw new IOException("Connection closed inside chunk trailers.");
                    await destination.WriteAsync(Encoding.Latin1.GetBytes(trailer + "\r\n"), ct);
                    if (trailer.Length == 0) return;
                }
            }
            await CopyExactAsync(destination, size + 2, ct); // data + CRLF
        }
    }

    public async Task CopyToEndAsync(Stream destination, CancellationToken ct)
    {
        await FlushBufferedAsync(destination, ct);
        while (await FillAsync(ct)) await FlushBufferedAsync(destination, ct);
    }

    /// <summary>Removes and returns any bytes read ahead of the current position (for example early TLS bytes after a CONNECT head).</summary>
    public byte[] TakeBuffered()
    {
        var taken = new byte[_end - _start];
        Buffer.BlockCopy(_buffer, _start, taken, 0, taken.Length);
        _start = _end = 0;
        return taken;
    }

    public async Task FlushBufferedAsync(Stream destination, CancellationToken ct)
    {
        if (!HasBuffered) return;
        await destination.WriteAsync(new ReadOnlyMemory<byte>(_buffer, _start, _end - _start), ct);
        _start = _end = 0;
    }

    public Task CopyBodyAsync(Stream destination, BodyFraming framing, CancellationToken ct) => framing.Kind switch
    {
        BodyKind.ContentLength => CopyExactAsync(destination, framing.Length, ct),
        BodyKind.Chunked => CopyChunkedAsync(destination, ct),
        BodyKind.UntilClose => CopyToEndAsync(destination, ct),
        _ => Task.CompletedTask
    };

    private async ValueTask<bool> FillAsync(CancellationToken ct)
    {
        if (_start == _end) _start = _end = 0;
        else if (_start > 0) { Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start); _end -= _start; _start = 0; }
        if (_end == _buffer.Length)
        {
            var bigger = ArrayPool<byte>.Shared.Rent(_buffer.Length * 2);
            Buffer.BlockCopy(_buffer, 0, bigger, 0, _end);
            ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
            _buffer = bigger;
        }
        var read = await stream.ReadAsync(new Memory<byte>(_buffer, _end, _buffer.Length - _end), ct);
        if (read <= 0) return false;
        _end += read;
        return true;
    }

    public void Dispose()
    {
        if (_buffer.Length > 0) ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
        _buffer = [];
        _start = _end = 0;
    }
}
