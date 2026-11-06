using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace BirkNext.Api.Services.LocalSecurity;

/// <summary>
/// The local-session bootstrap (docs/security/localhost-session-design-2026-11-06.md). A launch code — 60 random bits, shown by the launcher from
/// <c>App_Data/local-session/launch-code.txt</c>, never logged, never in a URL, static file or command line — is exchanged once per browser for an
/// HttpOnly, SameSite=Strict session cookie. Only session hashes are kept, in memory: an API restart forgets every session (the tester enters the
/// same code again); rotating the code revokes them all. A process running as the same Windows user can read the code file: this protects against
/// other machines, other users and web pages — not against a compromised same-user process.
/// </summary>
public sealed class LocalSessionService
{
    public const string CookieName = "BirkNext.LocalSession";
    public const string ChallengeScheme = "BirkNext-LocalSession";
    public const string ClientHeader = "X-BirkNext-Client";
    public static readonly TimeSpan IdleLifetime = TimeSpan.FromHours(12);
    public static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromDays(7);
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int MaxFailuresPerMinute = 10;

    private readonly string _codePath;
    private readonly TimeProvider _clock;
    private readonly ILogger<LocalSessionService> _logger;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, (DateTimeOffset Created, DateTimeOffset LastSeen, int Generation)> _sessions = new(StringComparer.Ordinal);
    private readonly Queue<DateTimeOffset> _failures = new();
    private string _code = "";
    private int _generation;

    public LocalSessionService(IHostEnvironment environment, TimeProvider clock, ILogger<LocalSessionService> logger)
        : this(System.IO.Path.Combine(environment.ContentRootPath, "App_Data", "local-session"), clock, logger) { }

    public LocalSessionService(string directory, TimeProvider clock, ILogger<LocalSessionService> logger)
    {
        _clock = clock;
        _logger = logger;
        Directory.CreateDirectory(directory);
        _codePath = System.IO.Path.Combine(directory, "launch-code.txt");
        _code = ReadOrCreateCode();
        // The path, never the code.
        _logger.LogInformation("Local session: the launch code for this installation is in {Path}", _codePath);
    }

    /// <summary>Where the launcher reads the code (and a developer without the launcher finds it).</summary>
    public string CodePath => _codePath;

    /// <summary>The current launch code — for the launcher file and for in-process tests; never written to a log or a response.</summary>
    public string CurrentCode { get { lock (_gate) return _code; } }

    public enum ExchangeResult { Accepted, Rejected, Throttled }

    /// <summary>Proof of possession: a correct code yields a new session token (returned once, set as the cookie). Constant-time comparison,
    /// at most <see cref="MaxFailuresPerMinute"/> wrong attempts per minute for the whole installation.</summary>
    public (ExchangeResult Result, string? Token) Exchange(string? code)
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            while (_failures.Count > 0 && now - _failures.Peek() > TimeSpan.FromMinutes(1)) _failures.Dequeue();
            if (_failures.Count >= MaxFailuresPerMinute) return (ExchangeResult.Throttled, null);
            var given = Normalize(code);
            if (given.Length == 0 || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(Normalize(_code))))
            {
                _failures.Enqueue(now);
                return (ExchangeResult.Rejected, null);
            }
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            _sessions[Hash(token)] = (now, now, _generation);
            return (ExchangeResult.Accepted, token);
        }
    }

    /// <summary>True for a live session of the current code generation (idle and absolute lifetime enforced; a valid use refreshes idle time).</summary>
    public bool Validate(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length != 64) return false;
        var key = Hash(token);
        if (!_sessions.TryGetValue(key, out var s)) return false;
        var now = _clock.GetUtcNow();
        if (s.Generation != Volatile.Read(ref _generation) || now - s.LastSeen > IdleLifetime || now - s.Created > AbsoluteLifetime)
        {
            _sessions.TryRemove(key, out _);
            return false;
        }
        _sessions[key] = s with { LastSeen = now };
        return true;
    }

    public void Revoke(string? token)
    {
        if (!string.IsNullOrEmpty(token)) _sessions.TryRemove(Hash(token), out _);
    }

    /// <summary>A new launch code: every existing session is revoked.</summary>
    public void Rotate()
    {
        lock (_gate)
        {
            _code = NewCode();
            Write(_code);
            Interlocked.Increment(ref _generation);
            _sessions.Clear();
        }
    }

    private string ReadOrCreateCode()
    {
        try
        {
            if (File.Exists(_codePath) && Normalize(File.ReadAllText(_codePath)) is { Length: 12 } existing) return Format(existing);
        }
        catch (IOException) { }
        var code = NewCode();
        Write(code);
        return code;
    }

    private void Write(string code)
    {
        var temp = _codePath + ".tmp";
        File.WriteAllText(temp, code);
        File.Move(temp, _codePath, overwrite: true);
    }

    private static string NewCode()
    {
        var bytes = RandomNumberGenerator.GetBytes(12);
        return Format(new string([.. bytes.Select(b => Alphabet[b % Alphabet.Length])]));
    }

    private static string Format(string raw) => $"{raw[..4]}-{raw[4..8]}-{raw[8..12]}";

    public static string Normalize(string? code) =>
        new([.. (code ?? "").ToUpperInvariant().Where(c => Alphabet.Contains(c))]);

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
