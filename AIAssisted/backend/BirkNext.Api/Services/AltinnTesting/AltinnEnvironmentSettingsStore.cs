using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.IntegrationJourneys.Packs.SkoleNaervaer;
using BirkNext.Integrations;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.AltinnTesting;

/// <summary>
/// The saved Target Environment Altinn settings, held in memory for the synchronous execution policy. Filled at startup from the database
/// and on every save; never written from anywhere else. Contains no secret and no identifier.
/// </summary>
public sealed class AltinnEnvironmentSettingsCache
{
    private readonly ConcurrentDictionary<string, AltinnEnvironmentSettings> _byEnvironment = new(StringComparer.Ordinal);

    public AltinnEnvironmentSettings? Get(string? environmentId) =>
        string.IsNullOrWhiteSpace(environmentId) ? null : _byEnvironment.GetValueOrDefault(environmentId);

    public void Set(string environmentId, AltinnEnvironmentSettings settings) => _byEnvironment[environmentId] = settings;
}

/// <summary>
/// Persists a Target Environment's non-secret Altinn settings. Only an explicit save (or a deliberate apply of selected proposals) writes;
/// reading and detecting never do. Every value is validated: Production is refused, URLs must be https (loopback http for the redirect URI)
/// and nothing that looks like a credential is accepted in a non-secret field.
/// </summary>
public sealed partial class AltinnEnvironmentSettingsStore(AppDbContext db, AltinnEnvironmentSettingsCache cache, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public const int MaxObservations = 50;

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,62}/[a-z0-9][a-z0-9-]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex AppId();
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,199}$", RegexOptions.CultureInvariant)]
    private static partial Regex Token();
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9:._/-]{0,199}$", RegexOptions.CultureInvariant)]
    private static partial Regex Scope();
    [GeneratedRegex("^[a-z]+/[a-z0-9.+-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex MediaType();

    public async Task<AltinnEnvironmentSettings> GetAsync(string environmentId, CancellationToken ct = default)
    {
        var row = await db.AltinnEnvironmentSettings.AsNoTracking().SingleOrDefaultAsync(r => r.EnvironmentId == environmentId, ct);
        var settings = row is null ? new AltinnEnvironmentSettings() : JsonSerializer.Deserialize<AltinnEnvironmentSettings>(row.DocumentJson, Json) ?? new AltinnEnvironmentSettings();
        if (row is not null) cache.Set(environmentId, settings);
        return settings;
    }

    /// <summary>Loads every saved environment into the cache (startup).</summary>
    public async Task WarmAsync(CancellationToken ct = default)
    {
        foreach (var row in await db.AltinnEnvironmentSettings.AsNoTracking().ToListAsync(ct))
            if (JsonSerializer.Deserialize<AltinnEnvironmentSettings>(row.DocumentJson, Json) is { } settings) cache.Set(row.EnvironmentId, settings);
    }

    /// <summary>Applies an explicit update (null = unchanged, empty = cleared). Returns the problems instead of saving when any value is invalid.</summary>
    public async Task<(AltinnEnvironmentSettings? Settings, IReadOnlyList<string> Problems)> UpdateAsync(string environmentId, AltinnEnvironmentSettingsUpdate update, CancellationToken ct = default)
    {
        if (!ValidEnvironmentId(environmentId)) return (null, ["A valid Target Environment id is required."]);
        var current = await GetAsync(environmentId, ct);
        var problems = new List<string>();
        var next = current with
        {
            Environment = update.Environment is null ? current.Environment : ParseEnvironment(update.Environment, problems),
            AppIdentifier = Validated(update.AppIdentifier, current.AppIdentifier, value => AppId().IsMatch(value), "The app identifier must be org/app (lowercase letters, digits and hyphens).", problems),
            AppBaseUrl = Validated(update.AppBaseUrl, current.AppBaseUrl, value => AppBaseUrlProblem(value) is null, AppBaseUrlProblem(update.AppBaseUrl ?? "") ?? "", problems),
            FormDataType = Validated(update.FormDataType, current.FormDataType, value => Token().IsMatch(value), "The form data type must be letters, digits, '_', '.' or '-'.", problems),
            ContentType = Validated(update.ContentType, current.ContentType, value => MediaType().IsMatch(value), "The content type must be a media type such as application/xml.", problems),
            IntegrationId = Validated(update.IntegrationId, current.IntegrationId, value => Token().IsMatch(value) || value.Contains(':') && Scope().IsMatch(value), "The integration id is not a valid id.", problems),
            Unattended = current.Unattended with
            {
                ClientId = Validated(update.ClientId, current.Unattended.ClientId, value => Token().IsMatch(value) || Scope().IsMatch(value), "The client id must be letters, digits and . _ - : /.", problems),
                RedirectUri = Validated(update.RedirectUri, current.Unattended.RedirectUri, value => RedirectProblem(value) is null, RedirectProblem(update.RedirectUri ?? "") ?? "", problems),
                Scopes = update.Scopes is null ? current.Unattended.Scopes : ValidScopes(update.Scopes, problems),
                Enabled = update.Enabled ?? current.Unattended.Enabled,
            },
        };
        if (problems.Count > 0) return (null, problems);
        return (await SaveAsync(environmentId, next with { UpdatedAt = clock.GetUtcNow() }, ct), []);
    }

    public async Task<AltinnEnvironmentSettings> RecordObservationAsync(string environmentId, AltinnRuntimeObservation observation, CancellationToken ct = default)
    {
        var current = await GetAsync(environmentId, ct);
        var observations = current.RuntimeObservations.Append(observation).OrderByDescending(item => item.ObservedAt).Take(MaxObservations).ToList();
        return await SaveAsync(environmentId, current with { RuntimeObservations = observations, UpdatedAt = clock.GetUtcNow() }, ct);
    }

    public async Task<AltinnEnvironmentSettings> RecordBrowserObservationAsync(string environmentId, AltinnBrowserAuthenticationObservation observation, CancellationToken ct = default)
    {
        var current = await GetAsync(environmentId, ct);
        return await SaveAsync(environmentId, current with { BrowserAuthentication = observation, UpdatedAt = clock.GetUtcNow() }, ct);
    }

    private async Task<AltinnEnvironmentSettings> SaveAsync(string environmentId, AltinnEnvironmentSettings settings, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(settings, Json);
        var row = await db.AltinnEnvironmentSettings.SingleOrDefaultAsync(r => r.EnvironmentId == environmentId, ct);
        if (row is null) db.AltinnEnvironmentSettings.Add(new AltinnEnvironmentSettingsRecord { EnvironmentId = environmentId, DocumentJson = json, UpdatedAt = clock.GetUtcNow() });
        else { row.DocumentJson = json; row.UpdatedAt = clock.GetUtcNow(); }
        await db.SaveChangesAsync(ct);
        cache.Set(environmentId, settings);
        return settings;
    }

    public static bool ValidEnvironmentId(string? environmentId) => !string.IsNullOrWhiteSpace(environmentId) && environmentId.Length <= 200;

    private static ExternalTestEnvironment? ParseEnvironment(string value, List<string> problems)
    {
        if (value.Trim().Length == 0) return null;
        var normalized = value.Trim().Equals("TT02", StringComparison.OrdinalIgnoreCase) ? nameof(ExternalTestEnvironment.Test) : value.Trim();
        if (!Enum.TryParse<ExternalTestEnvironment>(normalized, ignoreCase: true, out var parsed)) { problems.Add($"\"{value}\" is not an Altinn environment."); return null; }
        if (parsed == ExternalTestEnvironment.Production) { problems.Add("Altinn Production is never configured for testing."); return null; }
        return parsed;
    }

    private static string? Validated(string? update, string? current, Func<string, bool> valid, string problem, List<string> problems)
    {
        if (update is null) return current;
        var value = update.Trim();
        if (value.Length == 0) return null;
        if (LooksLikeCredential(value)) { problems.Add("A credential-like value is not accepted in a non-secret field."); return current; }
        if (!valid(value)) { problems.Add(problem); return current; }
        return value;
    }

    private static List<string> ValidScopes(IEnumerable<string> scopes, List<string> problems)
    {
        var list = scopes.Select(scope => scope?.Trim() ?? "").Where(scope => scope.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (list.Count > 30) problems.Add("At most 30 scopes.");
        if (list.FirstOrDefault(scope => !Scope().IsMatch(scope)) is { } invalid) problems.Add($"\"{invalid}\" is not a scope.");
        return list;
    }

    /// <summary>The registered redirect URI must be absolute, https (or loopback http), without credentials or fragment.</summary>
    public static string? RedirectProblem(string value) =>
        !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ? "The redirect URI is not an absolute URI."
        : !(uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) ? "The redirect URI must be https, or http on loopback."
        : uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 ? "The redirect URI must not contain user info or a fragment."
        : null;

    /// <summary>The app base URL must be https and never an Altinn Production host.</summary>
    public static string? AppBaseUrlProblem(string value) =>
        !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ? "The app base URL must be an absolute https URL."
        : uri.UserInfo.Length > 0 ? "The app base URL must not contain user info."
        : AltinnTestingPolicy.IsProductionHost(uri.Host.ToLowerInvariant()) ? "The app base URL is an Altinn Production host; Production is never configured."
        : null;

    private static bool LooksLikeCredential(string value) =>
        value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) || value.StartsWith("eyJ", StringComparison.Ordinal)
        || value.Contains("client_secret", StringComparison.OrdinalIgnoreCase) || value.Contains("password", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Values a person enters in BirkNext for this backend session only: an environment's ID-porten TEST client secret and approved synthetic
/// identities. Backend memory only — never persisted, logged, exported or returned; cleared on restart, on expiry or on request. Session
/// identities join the backend's configured ones (the same lists the Test Data provider and the executor read); the configured ones are
/// never touched.
/// </summary>
public sealed class AltinnSessionStore : IDisposable
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(8);
    private readonly AltinnTestingOptions _options;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, (string Secret, DateTimeOffset ExpiresAt)> _secrets = new(StringComparer.Ordinal);
    private readonly object _identityLock = new();
    private readonly ITimer _sweep;

    public AltinnSessionStore(AltinnTestingOptions options, TimeProvider clock)
    {
        _options = options;
        _clock = clock;
        _sweep = clock.CreateTimer(_ => Sweep(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>The environment's session secret, or null when none was entered or it expired.</summary>
    public string? Secret(string? environmentId)
    {
        if (string.IsNullOrWhiteSpace(environmentId) || !_secrets.TryGetValue(environmentId, out var held)) return null;
        if (held.ExpiresAt > _clock.GetUtcNow()) return held.Secret;
        _secrets.TryRemove(environmentId, out _);
        return null;
    }

    public DateTimeOffset? SecretExpiresAt(string? environmentId) => Secret(environmentId) is null ? null : _secrets[environmentId!].ExpiresAt;

    /// <summary>Holds (or replaces) the secret for this session. The previous value is never exposed.</summary>
    public string? SetSecret(string environmentId, string? secret)
    {
        if (!AltinnEnvironmentSettingsStore.ValidEnvironmentId(environmentId)) return "A valid Target Environment id is required.";
        var value = secret?.Trim() ?? "";
        if (value.Length is 0 or > 1000 || value.Any(char.IsControl)) return "The secret is empty or not a usable value.";
        _secrets[environmentId] = (value, _clock.GetUtcNow() + Lifetime);
        return null;
    }

    /// <summary>Clears the environment's session secret. Session identities are cleared separately (they are backend-wide test data).</summary>
    public void ClearSecret(string environmentId) => _secrets.TryRemove(environmentId, out _);

    /// <summary>Adds (or replaces) a session reporter or child. Returns the problem (never containing the identifier) instead of adding.</summary>
    public string? AddIdentity(AltinnSessionIdentityRequest request)
    {
        var alias = request.Alias?.Trim() ?? "";
        var label = string.IsNullOrWhiteSpace(request.Label) ? alias : request.Label.Trim();
        var identifier = request.Identifier?.Trim() ?? "";
        if (!AltinnTestingPolicy.IsValidAlias(alias)) return "The alias must be lowercase letters, digits and hyphens (e.g. reporter-a).";
        if (label.Length > 100) return "The label is too long.";
        var expires = _clock.GetUtcNow() + Lifetime;
        lock (_identityLock)
        {
            Sweep();
            if (string.Equals(request.Role, AltinnTestDataProvider.Reporter, StringComparison.OrdinalIgnoreCase))
            {
                if (!SyntheticPersonIdentifier.IsSynthetic(identifier)) return "The reporter identifier is not a synthetic TestID identifier (11 digits; Test-Norge month + 80).";
                if (_options.SyntheticChildren.Any(child => string.Equals(child.TestPersonIdentifier?.Trim(), identifier, StringComparison.Ordinal)))
                    return "The reporter and the child must be different persons.";
                if (_options.UnattendedIdPortenTest.Reporters.Any(item => item.Alias == alias && item.SessionExpiresAt is null))
                    return $"The reporter {alias} is configured by the backend; choose another alias.";
                _options.UnattendedIdPortenTest.Reporters = [.. _options.UnattendedIdPortenTest.Reporters.Where(item => item.Alias != alias), new SyntheticReporterOptions
                {
                    Alias = alias, Label = label, TestIdPersonIdentifier = identifier,
                    // A deliberate confirmation: the alias typed again. Never inferred from the identifier or its format.
                    AltinnProfileConfirmed = string.Equals(request.ConfirmProfileAlias?.Trim(), alias, StringComparison.Ordinal),
                    SessionExpiresAt = expires,
                }];
                return null;
            }
            if (string.Equals(request.Role, AltinnTestDataProvider.Child, StringComparison.OrdinalIgnoreCase))
            {
                if (!SyntheticPersonIdentifier.IsTestPerson(identifier)) return "The child identifier is not a valid 11-digit person identifier (date part and control digits).";
                if (_options.UnattendedIdPortenTest.Reporters.Any(reporter => string.Equals(reporter.TestIdPersonIdentifier?.Trim(), identifier, StringComparison.Ordinal)))
                    return "The reporter and the child must be different persons.";
                if (_options.SyntheticChildren.Any(item => item.Alias == alias && item.SessionExpiresAt is null))
                    return $"The child {alias} is configured by the backend; choose another alias.";
                _options.SyntheticChildren = [.. _options.SyntheticChildren.Where(item => item.Alias != alias), new SyntheticChildOptions
                {
                    Alias = alias, Label = label, TestPersonIdentifier = identifier, KnownToPerson = request.KnownToPerson, SessionExpiresAt = expires,
                }];
                return null;
            }
            return "The role must be reporter or child.";
        }
    }

    /// <summary>Removes every session identity (configured identities stay).</summary>
    public void ClearIdentities()
    {
        lock (_identityLock)
        {
            _options.UnattendedIdPortenTest.Reporters = [.. _options.UnattendedIdPortenTest.Reporters.Where(item => item.SessionExpiresAt is null)];
            _options.SyntheticChildren = [.. _options.SyntheticChildren.Where(item => item.SessionExpiresAt is null)];
        }
    }

    /// <summary>Drops expired secrets and identities. Lists are replaced, never mutated, so a reader enumerating the old list is unaffected.</summary>
    public void Sweep()
    {
        var now = _clock.GetUtcNow();
        foreach (var (environment, held) in _secrets) if (held.ExpiresAt <= now) _secrets.TryRemove(environment, out _);
        lock (_identityLock)
        {
            if (_options.UnattendedIdPortenTest.Reporters.Any(item => item.SessionExpiresAt <= now))
                _options.UnattendedIdPortenTest.Reporters = [.. _options.UnattendedIdPortenTest.Reporters.Where(item => !(item.SessionExpiresAt <= now))];
            if (_options.SyntheticChildren.Any(item => item.SessionExpiresAt <= now))
                _options.SyntheticChildren = [.. _options.SyntheticChildren.Where(item => !(item.SessionExpiresAt <= now))];
        }
    }

    public void Dispose() => _sweep.Dispose();
}
