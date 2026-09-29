using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BirkNext.Dependencies;
using NuGet.Versioning;

namespace BirkNext.Api.Services.DependencyReview;

public sealed record AdvisoryRangeEvent(string Kind, string Version);
public sealed record AdvisoryRange(string Type, List<AdvisoryRangeEvent> Events);
public sealed record AdvisoryAffected(string Ecosystem, string PackageName, List<AdvisoryRange> Ranges, List<string> Versions);

/// <summary>One advisory as the source publishes it. Severity is whatever the source states; nothing is computed.</summary>
public sealed record AdvisoryRecord
{
    public string Id { get; init; } = "";
    public List<string> Aliases { get; init; } = [];
    public string? Summary { get; init; }
    public string? SourceSeverity { get; init; }
    public List<string> SeverityVectors { get; init; } = [];
    public bool Withdrawn { get; init; }
    public DateTimeOffset? Modified { get; init; }
    public List<AdvisoryAffected> Affected { get; init; } = [];
    public string? Url { get; init; }
}

/// <summary>Advisories per package name, plus the packages the source could not answer for (unavailable ≠ no advisories).</summary>
public sealed record AdvisoryLookup(Dictionary<string, List<AdvisoryRecord>> ByPackage, Dictionary<string, string> Failed, DateTimeOffset RetrievedAt);

public interface IAdvisoryProvider
{
    string Name { get; }
    bool Supports(InventoryDependency dep);
    Task<AdvisoryLookup> LookupAsync(IReadOnlyList<string> packageNames, CancellationToken ct);
}

/// <summary>
/// OSV (osv.dev) advisories for NuGet packages. Read-only: POST /v1/querybatch is OSV's documented read-only query endpoint (package-only
/// queries, so every advisory of the package is returned and matched locally), then GET /v1/vulns/{id} for details — bounded and deduplicated.
/// No credentials are sent.
/// </summary>
public sealed class OsvAdvisoryProvider(HttpClient http) : IAdvisoryProvider
{
    public const string BaseUrl = "https://api.osv.dev/v1/";
    private const int BatchSize = 500;
    private const int MaxAdvisoryDetails = 400;
    private const int DetailConcurrency = 4;
    public string Name => "OSV (osv.dev)";
    public bool Supports(InventoryDependency dep) => RegistryClassifier.Supported(dep);

    public async Task<AdvisoryLookup> LookupAsync(IReadOnlyList<string> packageNames, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var ids = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var failed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in packageNames.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(BatchSize))
        {
            var body = new { queries = chunk.Select(n => new { package = new { name = n, ecosystem = "NuGet" } }).ToArray() };
            var (state, doc) = await SendAsync(() => http.PostAsJsonAsync($"{BaseUrl}querybatch", body, ct), ct);
            if (doc is null) { foreach (var n in chunk) failed[n] = state; continue; }
            using (doc)
            {
                var results = doc.RootElement.GetProperty("results").EnumerateArray().ToList();
                for (var i = 0; i < chunk.Length && i < results.Count; i++)
                {
                    ids[chunk[i]] = results[i].TryGetProperty("vulns", out var vulns) ? vulns.EnumerateArray().Select(v => v.GetProperty("id").GetString()).OfType<string>().ToList() : [];
                    if (results[i].TryGetProperty("next_page_token", out _)) failed[chunk[i]] = "OSV returned more advisories than one page; the list is incomplete.";
                }
            }
        }
        var unique = ids.Values.SelectMany(v => v).Distinct(StringComparer.Ordinal).ToList();
        var details = new Dictionary<string, AdvisoryRecord?>(StringComparer.Ordinal);
        var detailFailures = new Dictionary<string, string>(StringComparer.Ordinal);
        using var gate = new SemaphoreSlim(DetailConcurrency);
        await Task.WhenAll(unique.Take(MaxAdvisoryDetails).Select(async id =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var (state, doc) = await SendAsync(() => http.GetAsync($"{BaseUrl}vulns/{Uri.EscapeDataString(id)}", ct), ct);
                lock (details) { if (doc is null) detailFailures[id] = state; else using (doc) details[id] = Parse(doc.RootElement); }
            }
            finally { gate.Release(); }
        }));
        foreach (var id in unique.Skip(MaxAdvisoryDetails)) detailFailures[id] = $"More than {MaxAdvisoryDetails} advisories; details not fetched.";
        var byPackage = new Dictionary<string, List<AdvisoryRecord>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (package, list) in ids)
        {
            if (failed.ContainsKey(package)) continue;
            if (list.FirstOrDefault(detailFailures.ContainsKey) is { } missing) { failed[package] = $"Advisory {missing} could not be read: {detailFailures[missing]}"; continue; }
            byPackage[package] = list.Select(id => details.GetValueOrDefault(id)).OfType<AdvisoryRecord>().ToList();
        }
        return new AdvisoryLookup(byPackage, failed, now);
    }

    private static async Task<(string State, JsonDocument? Doc)> SendAsync(Func<Task<HttpResponseMessage>> send, CancellationToken ct)
    {
        try
        {
            using var response = await send();
            if (response.StatusCode == HttpStatusCode.TooManyRequests) return ("OSV rate-limited the request (HTTP 429).", null);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return ($"OSV refused the request (HTTP {(int)response.StatusCode}).", null);
            if (!response.IsSuccessStatusCode) return ($"OSV is unavailable (HTTP {(int)response.StatusCode}).", null);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return ("", await JsonDocument.ParseAsync(stream, cancellationToken: ct));
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return ("OSV did not answer within the timeout.", null); }
        catch (HttpRequestException ex) { return ($"OSV could not be reached ({ex.StatusCode?.ToString() ?? ex.HttpRequestError.ToString()}).", null); }
        catch (JsonException) { return ("OSV returned an unreadable response.", null); }
    }

    internal static AdvisoryRecord Parse(JsonElement v)
    {
        static List<string> Strings(JsonElement e, string name) => e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToList() : [];
        var affected = new List<AdvisoryAffected>();
        if (v.TryGetProperty("affected", out var aff))
            foreach (var a in aff.EnumerateArray())
            {
                var pkg = a.TryGetProperty("package", out var p) ? p : default;
                var ranges = new List<AdvisoryRange>();
                if (a.TryGetProperty("ranges", out var rs))
                    foreach (var r in rs.EnumerateArray())
                        ranges.Add(new AdvisoryRange(r.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "",
                            r.TryGetProperty("events", out var evs) ? evs.EnumerateArray().SelectMany(e => e.EnumerateObject().Select(o => new AdvisoryRangeEvent(o.Name, o.Value.GetString() ?? ""))).ToList() : []));
                affected.Add(new AdvisoryAffected(pkg.ValueKind == JsonValueKind.Object && pkg.TryGetProperty("ecosystem", out var eco) ? eco.GetString() ?? "" : "",
                    pkg.ValueKind == JsonValueKind.Object && pkg.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "", ranges, Strings(a, "versions")));
            }
        var id = v.GetProperty("id").GetString() ?? "";
        return new AdvisoryRecord
        {
            Id = id, Aliases = Strings(v, "aliases"), Summary = v.TryGetProperty("summary", out var s) ? s.GetString() : null,
            SourceSeverity = v.TryGetProperty("database_specific", out var ds) && ds.ValueKind == JsonValueKind.Object && ds.TryGetProperty("severity", out var sev) ? sev.GetString() : null,
            SeverityVectors = v.TryGetProperty("severity", out var sv) && sv.ValueKind == JsonValueKind.Array
                ? sv.EnumerateArray().Select(x => $"{(x.TryGetProperty("type", out var ty) ? ty.GetString() : "")} {(x.TryGetProperty("score", out var sc) ? sc.GetString() : "")}".Trim()).ToList() : [],
            Withdrawn = v.TryGetProperty("withdrawn", out var w) && w.ValueKind == JsonValueKind.String,
            Modified = v.TryGetProperty("modified", out var m) && m.TryGetDateTimeOffset(out var mod) ? mod : null,
            Affected = affected, Url = $"https://osv.dev/vulnerability/{Uri.EscapeDataString(id)}",
        };
    }
}

/// <summary>
/// Local advisory matching against the observed version (OSV range semantics: introduced / fixed / last_affected / limit, or explicit
/// versions). "No matched advisories observed" is never "safe", and an unavailable source is never zero vulnerabilities.
/// </summary>
public static class AdvisoryMatcher
{
    public static SecurityEvidence Evaluate(InventoryDependency dep, IAdvisoryProvider? provider, AdvisoryLookup? lookup)
    {
        if (provider is null || lookup is null || !provider.Supports(dep))
            return new SecurityEvidence { State = AdvisoryState.NotAssessed, Detail = $"No advisory source for {dep.PackageManager} in this review." };
        var source = provider.Name;
        if (lookup.Failed.TryGetValue(dep.PackageName, out var why))
            return new SecurityEvidence { State = AdvisoryState.AdvisorySourceUnavailable, Source = source, RetrievedAt = lookup.RetrievedAt, Detail = $"Security evidence unavailable: {why} This is not \"0 vulnerabilities\"." };
        var records = lookup.ByPackage.GetValueOrDefault(dep.PackageName) ?? [];
        NuGetVersion.TryParse(dep.Version ?? "", out var observed);
        var observations = records.Select(r => Observe(r, dep.PackageName, observed, source)).ToList();
        var active = observations.Where(o => !o.Withdrawn).ToList();
        if (active.Count == 0)
            return new SecurityEvidence { State = AdvisoryState.NoMatchedAdvisoryObserved, Source = source, RetrievedAt = lookup.RetrievedAt, Advisories = observations,
                Detail = $"No matched advisories observed in {source} for {dep.PackageName}. This is not a statement that the dependency is safe." };
        if (observed is null)
            return new SecurityEvidence { State = AdvisoryState.VersionNotComparable, Source = source, RetrievedAt = lookup.RetrievedAt, Advisories = observations,
                Detail = $"{active.Count} advisor{(active.Count == 1 ? "y exists" : "ies exist")} for the package, but the observed version is unknown or a range, so it cannot be compared." };
        var affecting = active.Where(o => o.AffectsObservedVersion == true).ToList();
        if (affecting.Count > 0)
        {
            var fixes = affecting.Select(o => o.FixedVersions.Select(f => NuGetVersion.TryParse(f, out var fv) ? fv : null).Where(fv => fv is not null && fv > observed).Min()).ToList();
            var fixedIn = fixes.Any(f => f is null) ? null : fixes.Max()?.ToNormalizedString();
            return new SecurityEvidence
            {
                State = AdvisoryState.Affected, Source = source, RetrievedAt = lookup.RetrievedAt, Advisories = observations, FixedIn = fixedIn,
                Detail = $"{dep.Version} is inside the affected range of {string.Join(", ", affecting.Select(a => a.Id))}. Exploitability is not assessed."
                    + (fixedIn is null ? " At least one matched advisory states no fixed version." : $" Fixed in {fixedIn} per the advisory source — not a tested or recommended upgrade."),
            };
        }
        if (active.Any(o => o.AffectsObservedVersion is null))
            return new SecurityEvidence { State = AdvisoryState.VersionNotComparable, Source = source, RetrievedAt = lookup.RetrievedAt, Advisories = observations,
                Detail = "An advisory for this package uses ranges BirkNext cannot compare with the observed version." };
        return new SecurityEvidence { State = AdvisoryState.NotAffectedByMatchedAdvisory, Source = source, RetrievedAt = lookup.RetrievedAt, Advisories = observations,
            Detail = $"{active.Count} advisor{(active.Count == 1 ? "y" : "ies")} for the package; {dep.Version} is outside the affected ranges. Not a statement that the dependency is safe." };
    }

    private static AdvisoryObservation Observe(AdvisoryRecord record, string packageName, NuGetVersion? observed, string source)
    {
        var mine = record.Affected.Where(a => a.PackageName.Equals(packageName, StringComparison.OrdinalIgnoreCase) && a.Ecosystem.Equals("NuGet", StringComparison.OrdinalIgnoreCase)).ToList();
        bool? affects = observed is null ? null : false;
        if (observed is not null)
            foreach (var a in mine)
            {
                if (a.Versions.Any(v => NuGetVersion.TryParse(v, out var x) && x == observed)) { affects = true; break; }
                foreach (var range in a.Ranges)
                {
                    var r = InRange(range, observed);
                    if (r == true) { affects = true; break; }
                    if (r is null && affects == false) affects = null;
                }
                if (affects == true) break;
            }
        return new AdvisoryObservation
        {
            Id = record.Id, Aliases = record.Aliases, Summary = record.Summary, SourceSeverity = record.SourceSeverity, SeverityVectors = record.SeverityVectors,
            AffectedRanges = mine.SelectMany(a => a.Ranges.Select(r => string.Join(", ", r.Events.Select(e => $"{e.Kind} {e.Version}"))).Concat(a.Versions.Count > 0 ? [$"versions {string.Join(", ", a.Versions.Take(8))}{(a.Versions.Count > 8 ? " …" : "")}"] : [])).ToList(),
            FixedVersions = mine.SelectMany(a => a.Ranges.SelectMany(r => r.Events.Where(e => e.Kind == "fixed").Select(e => e.Version))).Distinct().ToList(),
            AffectsObservedVersion = affects, Withdrawn = record.Withdrawn, Source = source, Url = record.Url, Modified = record.Modified,
        };
    }

    /// <summary>OSV evaluation: events sorted by version; introduced sets affected, fixed/limit clear it at or above, last_affected clears it above.</summary>
    public static bool? InRange(AdvisoryRange range, NuGetVersion version)
    {
        if (range.Type is not ("ECOSYSTEM" or "SEMVER")) return null;
        var events = new List<(string Kind, NuGetVersion At)>();
        foreach (var e in range.Events)
        {
            if (e.Kind == "introduced" && e.Version == "0") { events.Add((e.Kind, new NuGetVersion(0, 0, 0, "0"))); continue; }
            if (!NuGetVersion.TryParse(e.Version, out var at)) return null;
            events.Add((e.Kind, at));
        }
        var affected = false;
        foreach (var (kind, at) in events.OrderBy(e => e.At))
            switch (kind)
            {
                case "introduced" when version >= at: affected = true; break;
                case "fixed" or "limit" when version >= at: affected = false; break;
                case "last_affected" when version > at: affected = false; break;
            }
        return affected;
    }
}
