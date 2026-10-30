using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Path = System.IO.Path;

namespace BirkNext.Api.Services.BrowserAuthentication;

/// <summary>Unattended login configuration without secrets: what the settings file holds.</summary>
public sealed record UnattendedLoginSettings
{
    public bool Enabled { get; init; }
    public string? IdentityAlias { get; init; }
    public string? LoginUrl { get; init; }
    public string? SuccessUrlContains { get; init; }
    public string? SuccessSelector { get; init; }
    public string? UsernameSelector { get; init; }
    public string? PasswordSelector { get; init; }
    public string? SubmitSelector { get; init; }
    /// <summary>Increases whenever the login name or password changes, so a previous validation no longer counts.</summary>
    public int CredentialVersion { get; init; }
    public DateTimeOffset? CredentialUpdatedAt { get; init; }
    public string? LoginNameHint { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public StoredValidation? LastValidation { get; init; }
    public List<StoredAuditEntry> Audit { get; init; } = [];
}

public sealed record StoredValidation(string Outcome, string Message, DateTimeOffset ValidatedAt, string Fingerprint, long DurationMs);

public sealed record StoredAuditEntry(DateTimeOffset At, string Event);

/// <summary>The decrypted credential. Lives only in memory for the duration of one login; never logged, serialized to a client or stored in clear.</summary>
public sealed class UnattendedLoginCredential(string loginName, string password)
{
    public string LoginName { get; } = loginName;
    public string Password { get; } = password;
    public override string ToString() => "UnattendedLoginCredential(redacted)";
}

/// <summary>
/// Persistence of the unattended browser login, separate from every attended setting. Two files under the backend's App_Data
/// (git-ignored; never appsettings, source control, the database or Test Data):
/// <list type="bullet">
/// <item><c>unattended-login.json</c> — configuration and validation metadata, no secret and no login name.</item>
/// <item><c>unattended-login.credential</c> — login name and password, encrypted with ASP.NET Core Data Protection. The key ring is
/// dedicated to this purpose (<c>keys/</c> beside it), and on Windows the keys themselves are encrypted with DPAPI for the current user,
/// so a copied file cannot be decrypted elsewhere. No custom cryptography.</item>
/// </list>
/// </summary>
public sealed class UnattendedLoginStore
{
    public const string ProtectionPurpose = "BirkNext.UnattendedBrowserLogin.Credential.v1";
    public const string StorageDescription = "Encrypted at rest on this backend (ASP.NET Core Data Protection, DPAPI-protected keys on Windows)";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private const int AuditLimit = 50;

    private readonly string _settingsPath;
    private readonly string _credentialPath;
    private readonly IDataProtector _protector;
    private readonly object _gate = new();

    public UnattendedLoginStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _settingsPath = Path.Combine(directory, "unattended-login.json");
        _credentialPath = Path.Combine(directory, "unattended-login.credential");
        var keys = new DirectoryInfo(Path.Combine(directory, "keys"));
        var provider = DataProtectionProvider.Create(keys, builder =>
        {
            builder.SetApplicationName("BirkNext.UnattendedBrowserLogin");
            if (OperatingSystem.IsWindows()) builder.ProtectKeysWithDpapi();
        });
        _protector = provider.CreateProtector(ProtectionPurpose);
    }

    public static string DefaultDirectory(IHostEnvironment environment) => Path.Combine(environment.ContentRootPath, "App_Data", "browser-authentication");

    public UnattendedLoginSettings? Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_settingsPath)) return null;
            try { return JsonSerializer.Deserialize<UnattendedLoginSettings>(File.ReadAllText(_settingsPath), Json); }
            catch (JsonException) { return null; }
        }
    }

    public void Save(UnattendedLoginSettings settings)
    {
        lock (_gate) WriteAtomically(_settingsPath, JsonSerializer.Serialize(settings with { Audit = settings.Audit.TakeLast(AuditLimit).ToList() }, Json));
    }

    public bool HasCredential()
    {
        lock (_gate) return File.Exists(_credentialPath);
    }

    public void SaveCredential(string loginName, string password)
    {
        var payload = JsonSerializer.Serialize(new CredentialPayload(loginName, password));
        var protectedText = _protector.Protect(payload);
        lock (_gate) WriteAtomically(_credentialPath, protectedText);
    }

    /// <summary>The decrypted credential, or null when none is stored or it cannot be decrypted here (e.g. copied from another user/machine).</summary>
    public UnattendedLoginCredential? ReadCredential()
    {
        string text;
        lock (_gate)
        {
            if (!File.Exists(_credentialPath)) return null;
            text = File.ReadAllText(_credentialPath);
        }
        try
        {
            var payload = JsonSerializer.Deserialize<CredentialPayload>(_protector.Unprotect(text));
            return payload is { LoginName.Length: > 0, Password.Length: > 0 } ? new(payload.LoginName, payload.Password) : null;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException) { return null; }
    }

    /// <summary>Removes the unattended configuration and credential. Attended settings live elsewhere and are not touched.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            if (File.Exists(_credentialPath)) File.Delete(_credentialPath);
            if (File.Exists(_settingsPath)) File.Delete(_settingsPath);
        }
    }

    /// <summary>Masks a login name for display: first character of each part kept ("a•••@e•••.test").</summary>
    public static string MaskLoginName(string loginName)
    {
        static string Mask(string part) => part.Length <= 1 ? "•" : part[0] + "•••";
        var at = loginName.IndexOf('@');
        if (at < 0) return Mask(loginName);
        var domain = loginName[(at + 1)..];
        var dot = domain.LastIndexOf('.');
        return Mask(loginName[..at]) + "@" + (dot > 0 ? Mask(domain[..dot]) + domain[dot..] : Mask(domain));
    }

    private static void WriteAtomically(string path, string content)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content, Encoding.UTF8);
        File.Move(temp, path, overwrite: true);
    }

    private sealed record CredentialPayload(string LoginName, string Password);
}
