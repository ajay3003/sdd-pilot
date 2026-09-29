using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using BirkNext.Dependencies;
using NuGet.Versioning;

namespace BirkNext.Api.Services.DependencyReview;

public sealed record RegistryVersion(string Version, bool Listed, DateTimeOffset? Published, DeprecationEvidence? Deprecation, string? LicenseExpression, string? LicenseUrl);

/// <summary>What a registry said about one package. A failure state (Unavailable, Timeout, …) says nothing about whether the package exists.</summary>
public sealed record RegistryPackageResult(RegistryState State, string Detail, List<RegistryVersion> Versions, DateTimeOffset RetrievedAt, bool FromCache = false);

/// <summary>Read-only package metadata source for one ecosystem. Implementations issue GET requests only and never authenticate to publish.</summary>
public interface IPackageRegistryProvider
{
    string Manager { get; }
    string Registry { get; }
    Task<RegistryPackageResult> GetPackageAsync(string packageName, CancellationToken ct);
}

/// <summary>
/// nuget.org registration metadata (GET only, no credentials): every version with listed state, publish date, deprecation and license
/// expression. Private feeds are not queried, so a package that exists only on a private feed is "Package not found" on nuget.org.
/// </summary>
public sealed class NuGetRegistryProvider(HttpClient http) : IPackageRegistryProvider
{
    public const string RegistrationBase = "https://api.nuget.org/v3/registration5-gz-semver2/";
    private const int MaxPages = 80;
    public string Manager => "nuget";
    public string Registry => "nuget.org";

    public async Task<RegistryPackageResult> GetPackageAsync(string packageName, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        try
        {
            var (status, index) = await GetAsync($"{RegistrationBase}{Uri.EscapeDataString(packageName.ToLowerInvariant())}/index.json", ct);
            if (status != HttpStatusCode.OK) return Failure(status, now);
            var versions = new List<RegistryVersion>();
            var pages = 0;
            foreach (var page in index!.RootElement.GetProperty("items").EnumerateArray())
            {
                if (page.TryGetProperty("items", out var inline)) { versions.AddRange(Parse(inline)); continue; }
                if (++pages > MaxPages) return new(RegistryState.ProviderError, $"More than {MaxPages} registration pages; not read in full.", versions, now);
                var (pageStatus, pageDoc) = await GetAsync(page.GetProperty("@id").GetString()!, ct);
                if (pageStatus != HttpStatusCode.OK) return Failure(pageStatus, now);
                using (pageDoc) versions.AddRange(Parse(pageDoc!.RootElement.GetProperty("items")));
            }
            index.Dispose();
            return new(RegistryState.Observed, $"{versions.Count} version(s) on nuget.org.", versions, now);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return new(RegistryState.Timeout, "nuget.org did not answer within the timeout.", [], now); }
        catch (HttpRequestException ex) { return new(RegistryState.RegistryUnavailable, $"nuget.org could not be reached ({ex.StatusCode?.ToString() ?? ex.HttpRequestError.ToString()}).", [], now); }
        catch (Exception ex) when (ex is JsonException or System.Collections.Generic.KeyNotFoundException or InvalidOperationException) { return new(RegistryState.ProviderError, $"nuget.org metadata could not be read ({ex.GetType().Name}).", [], now); }
    }

    private async Task<(HttpStatusCode, JsonDocument?)> GetAsync(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode != HttpStatusCode.OK) return (response.StatusCode, null);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return (HttpStatusCode.OK, await JsonDocument.ParseAsync(stream, cancellationToken: ct));
    }

    private static RegistryPackageResult Failure(HttpStatusCode status, DateTimeOffset now) => status switch
    {
        HttpStatusCode.NotFound => new(RegistryState.PackageNotFound, "Not found on nuget.org. A package served only by a private feed is not visible here: private registry metadata is not configured.", [], now),
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new(RegistryState.Unauthorized, $"nuget.org refused the request (HTTP {(int)status}).", [], now),
        HttpStatusCode.TooManyRequests => new(RegistryState.RateLimited, "nuget.org rate-limited the request (HTTP 429).", [], now),
        _ when (int)status >= 500 => new(RegistryState.RegistryUnavailable, $"nuget.org is unavailable (HTTP {(int)status}).", [], now),
        _ => new(RegistryState.ProviderError, $"Unexpected nuget.org response (HTTP {(int)status}).", [], now),
    };

    internal static IEnumerable<RegistryVersion> Parse(JsonElement leaves)
    {
        foreach (var leaf in leaves.EnumerateArray())
        {
            var entry = leaf.GetProperty("catalogEntry");
            var published = entry.TryGetProperty("published", out var p) && p.TryGetDateTimeOffset(out var at) && at.Year > 1900 ? at : (DateTimeOffset?)null; // unlisted = 1900-01-01
            DeprecationEvidence? deprecation = null;
            if (entry.TryGetProperty("deprecation", out var d) && d.ValueKind == JsonValueKind.Object)
                deprecation = new DeprecationEvidence(
                    d.TryGetProperty("reasons", out var r) && r.ValueKind == JsonValueKind.Array ? r.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToList() : [],
                    d.TryGetProperty("message", out var m) ? m.GetString() : null,
                    d.TryGetProperty("alternatePackage", out var alt) && alt.TryGetProperty("id", out var altId) ? altId.GetString() : null);
            yield return new RegistryVersion(entry.GetProperty("version").GetString()!, !entry.TryGetProperty("listed", out var l) || l.ValueKind != JsonValueKind.False, published, deprecation,
                entry.TryGetProperty("licenseExpression", out var le) && le.GetString() is { Length: > 0 } expr ? expr : null,
                entry.TryGetProperty("licenseUrl", out var lu) && lu.GetString() is { Length: > 0 } url ? url : null);
        }
    }
}

/// <summary>
/// Pure classification of registry evidence for one inventory item. "Latest published stable" is what the registry lists, never a
/// recommendation; being behind is evidence, not a defect; age is reported without any threshold.
/// </summary>
public static class RegistryClassifier
{
    public static bool Supported(InventoryDependency dep) => dep.PackageManager == "nuget" && dep.Datasource is null or "nuget";

    public sealed record Classification(RegistryObservation Registry, VersionStatus Status, string Detail, int? AgeDays);

    public static Classification Classify(InventoryDependency dep, RegistryPackageResult? result, string registry, DateTimeOffset now)
    {
        if (!Supported(dep) || result is null)
            return new(new RegistryObservation { Registry = registry, State = RegistryState.NotAssessed, Detail = $"No registry provider for {(dep.Datasource == "dotnet-version" ? ".NET SDK versions" : dep.PackageManager)}." },
                VersionStatus.NotAssessed, "Not assessed: no registry provider for this ecosystem.", null);
        if (result.State != RegistryState.Observed)
        {
            var status = result.State == RegistryState.PackageNotFound ? VersionStatus.VersionUnavailable : VersionStatus.RegistryUnavailable;
            return new(new RegistryObservation { Registry = registry, State = result.State, Detail = result.Detail, RetrievedAt = result.RetrievedAt, FromCache = result.FromCache }, status,
                result.State == RegistryState.PackageNotFound ? "The package is not on the public registry; no version comparison." : "Registry evidence unavailable: no outdated conclusion.", null);
        }
        var parsed = result.Versions.Select(v => (Entry: v, Version: NuGetVersion.TryParse(v.Version, out var nv) ? nv : null)).Where(v => v.Version is not null).ToList();
        var latestStable = parsed.Where(v => v.Entry.Listed && !v.Version!.IsPrerelease).OrderByDescending(v => v.Version).FirstOrDefault();
        var latestPre = parsed.Where(v => v.Entry.Listed && v.Version!.IsPrerelease && (latestStable.Version is null || v.Version > latestStable.Version)).OrderByDescending(v => v.Version).FirstOrDefault();
        var baseObservation = new RegistryObservation
        {
            Registry = registry, RetrievedAt = result.RetrievedAt, FromCache = result.FromCache, LatestStable = latestStable.Version?.ToNormalizedString(),
            LatestStablePublished = latestStable.Entry?.Published, LatestPrerelease = latestPre.Version?.ToNormalizedString(),
        };
        if (dep.Version is null || !NuGetVersion.TryParse(dep.Version, out var observed))
            return new(baseObservation with { State = RegistryState.NotAssessed, Detail = dep.VersionRange is { } range ? $"Observed value is a range/floating version ({range}); no exact version to look up." : "Observed version is unknown." },
                VersionStatus.NotComparable, dep.VersionRange is not null ? "Not comparable: the inventory has a range, not an exact version." : "Not comparable: version unknown.", null);
        var match = parsed.FirstOrDefault(v => v.Version == observed);
        if (match.Version is null)
            return new(baseObservation with { State = RegistryState.VersionNotFound, Detail = $"nuget.org has the package but not version {dep.Version}." },
                VersionStatus.VersionUnavailable, "The observed version is not published on the registry.", null);
        var observation = baseObservation with
        {
            State = match.Entry.Listed ? RegistryState.Observed : RegistryState.Unlisted, Detail = match.Entry.Listed ? "Observed version is listed." : "Observed version exists but is unlisted on nuget.org.",
            ObservedPublished = match.Entry.Published, ObservedListed = match.Entry.Listed, Deprecation = match.Entry.Deprecation,
            LicenseExpression = match.Entry.LicenseExpression, LicenseUrl = match.Entry.LicenseUrl,
        };
        var age = match.Entry.Published is { } published ? (int?)Math.Max(0, (int)Math.Floor((now - published).TotalDays)) : null;
        if (latestStable.Version is not { } stable) return new(observation, VersionStatus.NotComparable, "No listed stable version is published.", age);
        var (status2, detail) = Compare(observed, stable);
        return new(observation, status2, detail, age);
    }

    public static (VersionStatus Status, string Detail) Compare(NuGetVersion observed, NuGetVersion latestStable)
    {
        if (observed == latestStable) return (VersionStatus.Current, $"Same as {DependencyHealthLabels.LatestStable.ToLowerInvariant()} {latestStable.ToNormalizedString()}.");
        if (observed > latestStable) return (VersionStatus.NewerThanLatestStable, $"Newer than {DependencyHealthLabels.LatestStable.ToLowerInvariant()} {latestStable.ToNormalizedString()}{(observed.IsPrerelease ? " (prerelease)" : "")}.");
        var status = observed.Major != latestStable.Major ? VersionStatus.MajorBehind : observed.Minor != latestStable.Minor ? VersionStatus.MinorBehind : VersionStatus.PatchBehind;
        var note = status == VersionStatus.PatchBehind && observed.Patch == latestStable.Patch
            ? observed.IsPrerelease ? " (prerelease of that version)" : " (revision)" : "";
        return (status, $"{DependencyHealthLabels.LatestStable} is {latestStable.ToNormalizedString()}{note}. Being behind is not a defect by itself.");
    }
}

/// <summary>
/// Bounded in-process cache for registry and advisory lookups. Entries keep the time they were fetched, so a cached value is shown as
/// cached with its original retrieval time — never as live.
/// </summary>
public sealed class DependencyEvidenceCache
{
    private readonly ConcurrentDictionary<string, (DateTimeOffset FetchedAt, object Value)> _entries = new(StringComparer.Ordinal);
    public TimeSpan Ttl { get; init; } = TimeSpan.FromHours(1);
    public int MaxEntries { get; init; } = 5000;

    public bool TryGet<T>(string key, DateTimeOffset now, out T value)
    {
        if (_entries.TryGetValue(key, out var entry) && now - entry.FetchedAt < Ttl && entry.Value is T typed) { value = typed; return true; }
        value = default!;
        return false;
    }

    public void Set(string key, object value, DateTimeOffset fetchedAt)
    {
        if (_entries.Count >= MaxEntries)
            foreach (var old in _entries.OrderBy(e => e.Value.FetchedAt).Take(MaxEntries / 10).ToList()) _entries.TryRemove(old.Key, out _);
        _entries[key] = (fetchedAt, value);
    }
}

/// <summary>Removes credentials from any provider text before it is logged, stored, shown or exported.</summary>
public static partial class DependencyEvidenceRedaction
{
    public const string Redacted = "[redacted]";

    [GeneratedRegex(@"(?i)(authorization|proxy-authorization)\s*[:=]\s*[^\r\n,;]+")] private static partial Regex AuthHeader();
    [GeneratedRegex(@"(?i)\b(bearer|basic)\s+[A-Za-z0-9._~+/=\-]{6,}")] private static partial Regex Scheme();
    [GeneratedRegex(@"\b(ghp_|gho_|ghs_|github_pat_|glpat-)[A-Za-z0-9_\-]{8,}")] private static partial Regex GitToken();
    [GeneratedRegex(@"(?i)([?&;\s](?:sig|token|access_token|code|pat|password|pwd|key|api[-_]?key|secret)=)[^&\s""']+")] private static partial Regex SecretParameter();
    [GeneratedRegex(@"(?i)(https?://)[^/\s:@]+:[^/\s@]+@")] private static partial Regex UserInfo();
    [GeneratedRegex(@"\b[a-z2-7]{52}\b")] private static partial Regex AzureDevOpsPat();

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var s = AuthHeader().Replace(text, m => $"{m.Groups[1].Value}: {Redacted}");
        s = Scheme().Replace(s, m => $"{m.Groups[1].Value} {Redacted}");
        s = GitToken().Replace(s, Redacted);
        s = SecretParameter().Replace(s, m => m.Groups[1].Value + Redacted);
        s = UserInfo().Replace(s, m => m.Groups[1].Value + Redacted + "@");
        return AzureDevOpsPat().Replace(s, Redacted);
    }
}
