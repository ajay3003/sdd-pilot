using System.Collections.Concurrent;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace BirkNext.Api.Services.LocalSecurity;

/// <summary>
/// The local-session bootstrap (docs/security/localhost-session-design-2026-11-06.md). A launch code — 60 random bits, shown by the launcher from
/// <c>App_Data/local-session/launch-code.txt</c>, never logged, never in a URL, static file or command line — is exchanged once per browser for an
/// HttpOnly, SameSite=Strict session cookie. Only session hashes are kept, in memory: an API restart forgets every session (the tester enters the
/// same code again); rotating the code revokes them all. The code file and its folder are owner-only (an explicit ACL, never inherited): this
/// protects against other machines, other local users and web pages — not against a compromised same-user process.
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
    // One-time bootstrap tokens (hash → expiry), in memory only: issued to the launcher for the code it already holds, used once by the browser.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _bootstrap = new(StringComparer.Ordinal);

    /// <summary>How long a launcher-issued bootstrap token can be used (once) to connect the browser it opened.</summary>
    public static readonly TimeSpan BootstrapLifetime = TimeSpan.FromMinutes(2);
    private string _code = "";
    private int _generation;

    public LocalSessionService(IHostEnvironment environment, TimeProvider clock, ILogger<LocalSessionService> logger)
        : this(System.IO.Path.Combine(BirkNext.Api.Services.AppDataLocation.Root(environment), "local-session"), clock, logger) { }

    public LocalSessionService(string directory, TimeProvider clock, ILogger<LocalSessionService> logger)
    {
        _clock = clock;
        _logger = logger;
        Directory.CreateDirectory(directory);
        // Owner-only before the code is read or written; re-applied on every start, so an install created before this rule is repaired.
        RestrictToCurrentUser(directory, isDirectory: true);
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
            var check = CheckCode(code, now);
            return check == ExchangeResult.Accepted ? (ExchangeResult.Accepted, NewSession(now)) : (check, null);
        }
    }

    /// <summary>
    /// Automatic connection for the browser the launcher opens: the launcher proves possession of the launch code (the same check and throttle as
    /// <see cref="Exchange"/>) and receives a random one-time token, valid <see cref="BootstrapLifetime"/>. The token — never the launch code — goes in
    /// the URL fragment of the browser it opens (a fragment is never sent to a server). Only its hash is kept, in memory.
    /// </summary>
    public (ExchangeResult Result, string? Token) IssueBootstrap(string? code)
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            var check = CheckCode(code, now);
            if (check != ExchangeResult.Accepted) return (check, null);
            foreach (var expired in _bootstrap.Where(b => b.Value <= now).Select(b => b.Key).ToList()) _bootstrap.TryRemove(expired, out _);
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            _bootstrap[Hash(token)] = now + BootstrapLifetime;
            return (ExchangeResult.Accepted, token);
        }
    }

    /// <summary>A bootstrap token yields a session exactly once and only before it expires; a rotated code invalidates every unused token.</summary>
    public (ExchangeResult Result, string? Token) ExchangeBootstrap(string? bootstrapToken)
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            while (_failures.Count > 0 && now - _failures.Peek() > TimeSpan.FromMinutes(1)) _failures.Dequeue();
            if (_failures.Count >= MaxFailuresPerMinute) return (ExchangeResult.Throttled, null);
            if (string.IsNullOrEmpty(bootstrapToken) || bootstrapToken.Length != 64 || !_bootstrap.TryRemove(Hash(bootstrapToken), out var expires) || expires <= now)
            {
                _failures.Enqueue(now);
                return (ExchangeResult.Rejected, null);
            }
            return (ExchangeResult.Accepted, NewSession(now));
        }
    }

    private ExchangeResult CheckCode(string? code, DateTimeOffset now)
    {
        while (_failures.Count > 0 && now - _failures.Peek() > TimeSpan.FromMinutes(1)) _failures.Dequeue();
        if (_failures.Count >= MaxFailuresPerMinute) return ExchangeResult.Throttled;
        var given = Normalize(code);
        if (given.Length == 0 || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(Normalize(_code))))
        {
            _failures.Enqueue(now);
            return ExchangeResult.Rejected;
        }
        return ExchangeResult.Accepted;
    }

    private string NewSession(DateTimeOffset now)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        _sessions[Hash(token)] = (now, now, _generation);
        return token;
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
            _bootstrap.Clear();
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
        RestrictToCurrentUser(temp, isDirectory: false);
        File.Move(temp, _codePath, overwrite: true);
        RestrictToCurrentUser(_codePath, isDirectory: false);
    }

    /// <summary>
    /// The launch code is readable by its owner only. Without an explicit ACL the folder inherited its parent's: under C:\ (the documented
    /// "extract to a short folder such as C:\BirkNext") that grants Authenticated Users modify access, so another local account could read the
    /// code and open a session. Windows: a protected DACL (no inheritance) with the current user and SYSTEM only. Elsewhere: 0700 / 0600.
    /// </summary>
    internal static void RestrictToCurrentUser(string path, bool isDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User ?? throw new InvalidOperationException("The current Windows user has no SID.");
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            if (isDirectory)
            {
                var security = new DirectorySecurity();
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                foreach (var sid in new[] { user, system })
                    security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                new DirectoryInfo(path).SetAccessControl(security);
            }
            else
            {
                var security = new FileSecurity();
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                foreach (var sid in new[] { user, system })
                    security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
                new FileInfo(path).SetAccessControl(security);
            }
        }
        else
        {
            File.SetUnixFileMode(path, isDirectory
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                : UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
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
