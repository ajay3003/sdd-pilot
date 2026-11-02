using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BirkNext.Api.Services.PerformanceTests;

/// <summary>
/// Cumulative request/iteration evidence read from a provider's own output stream while a run executes. Counts are what the load generator
/// recorded (k6 emits one <c>http_reqs</c> point per request sent and one <c>iterations</c> point per completed iteration); <c>vus</c> points are
/// the engine's periodic liveness sample. Nothing here is estimated, and an unopened stream is "not received" — never zero.
/// </summary>
public sealed record PerformanceLiveTelemetry(
    bool StreamOpened, DateTimeOffset? StreamOpenedAt, int Requests, int FailedRequests, int Iterations,
    DateTimeOffset? FirstRequestAt, DateTimeOffset? LastRequestAt, DateTimeOffset? FirstIterationAt, DateTimeOffset? LastIterationAt,
    DateTimeOffset? LastHeartbeatAt, string? Error)
{
    public static readonly PerformanceLiveTelemetry NotOpened = new(false, null, 0, 0, 0, null, null, null, null, null, null);
}

/// <summary>Incremental reader of a provider telemetry stream; safe to call repeatedly while the file grows.</summary>
public interface IPerformanceTelemetryReader
{
    string Source { get; }
    PerformanceLiveTelemetry Read();
}

/// <summary>
/// Reads k6's <c>--out json</c> NDJSON stream incrementally (offset + partial-line buffer, shared read access). Only the metric name, time
/// and value of each point are used — no tag, URL, header or body is ever kept — and only counts and timestamps leave this class.
/// </summary>
public sealed partial class K6JsonTelemetryReader(string path, TimeProvider? clock = null) : IPerformanceTelemetryReader
{
    /// <summary>Bytes processed per <see cref="Read"/> call, so a burst never stalls the monitor; the rest is read on the next call.</summary>
    public const int MaxBytesPerRead = 8 * 1024 * 1024;
    private const int MaxParseErrors = 25;
    private readonly object _gate = new();
    private readonly StringBuilder _partial = new();
    private long _offset;
    private int _parseErrors;
    private PerformanceLiveTelemetry _state = PerformanceLiveTelemetry.NotOpened;

    public string Source => "k6 JSON output stream (http_reqs, iterations, vus points)";

    public PerformanceLiveTelemetry Read()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(path)) return _state;
                if (!_state.StreamOpened) _state = _state with { StreamOpened = true, StreamOpenedAt = (clock ?? TimeProvider.System).GetUtcNow() };
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length < _offset) { _offset = 0; _partial.Clear(); }
                stream.Seek(_offset, SeekOrigin.Begin);
                var buffer = new byte[(int)Math.Min(MaxBytesPerRead, Math.Max(0, stream.Length - _offset))];
                var read = 0;
                while (read < buffer.Length && stream.Read(buffer, read, buffer.Length - read) is var n && n > 0) read += n;
                _offset += read;
                _partial.Append(Encoding.UTF8.GetString(buffer, 0, read));
                var text = _partial.ToString();
                var last = text.LastIndexOf('\n');
                if (last < 0) return _state;
                _partial.Clear().Append(text[(last + 1)..]);
                foreach (var line in text[..last].Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) Apply(line);
                return _state;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return _state with { Error = "The k6 output stream could not be read." };
            }
        }
    }

    private void Apply(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "Point" || !root.TryGetProperty("data", out var data)) return;
            var metric = root.TryGetProperty("metric", out var m) ? m.GetString() : null;
            var at = data.TryGetProperty("time", out var t) && t.GetString() is { } time ? ParseTime(time) : null;
            var value = data.TryGetProperty("value", out var v) && v.TryGetDouble(out var d) ? d : 0;
            _state = metric switch
            {
                "http_reqs" => _state with { Requests = _state.Requests + Math.Max(1, (int)value), FirstRequestAt = _state.FirstRequestAt ?? at, LastRequestAt = Later(_state.LastRequestAt, at) },
                "http_req_failed" when value > 0 => _state with { FailedRequests = _state.FailedRequests + 1 },
                "iterations" => _state with { Iterations = _state.Iterations + Math.Max(1, (int)value), FirstIterationAt = _state.FirstIterationAt ?? at, LastIterationAt = Later(_state.LastIterationAt, at) },
                "vus" => _state with { LastHeartbeatAt = Later(_state.LastHeartbeatAt, at) },
                _ => _state,
            };
        }
        catch (JsonException)
        {
            if (++_parseErrors >= MaxParseErrors) _state = _state with { Error = $"{_parseErrors} k6 output line(s) could not be parsed." };
        }
    }

    private static DateTimeOffset? Later(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a > b ? a : b;

    /// <summary>k6 writes RFC 3339 with up to nanosecond precision; .NET parses at most 7 fractional digits.</summary>
    public static DateTimeOffset? ParseTime(string value) =>
        DateTimeOffset.TryParse(Fraction().Replace(value, f => f.Value[..Math.Min(f.Value.Length, 8)]), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;

    [GeneratedRegex(@"\.\d+")]
    private static partial Regex Fraction();
}
