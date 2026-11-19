using System.Text.Json;

namespace BirkNext.Updater;

/// <summary>Where Windows says a downloaded file came from (its Mark of the Web, the Zone.Identifier alternate data stream).</summary>
public sealed record DownloadOrigin(int? ZoneId, string? HostUrl, string? ReferrerUrl)
{
    public static DownloadOrigin? Read(string file)
    {
        try
        {
            var text = File.ReadAllText(file + ":Zone.Identifier");
            string? Value(string key) => text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))?[(key.Length + 1)..];
            return new(int.TryParse(Value("ZoneId"), out var zone) ? zone : null, Value("HostUrl"), Value("ReferrerUrl"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException) { return null; }
    }
}

public enum TrustVerdict { PipelineOrigin, NoOrigin, OtherOrigin, WrongOrganization }

/// <summary>
/// The update trust policy (decided 2026-11-19 while BirkNext packages are not signed): a package is accepted only when Windows recorded it as
/// downloaded from the approved Azure DevOps — dev.azure.com or *.visualstudio.com — optionally limited to named organizations. Everything else is
/// refused (fail closed). This is provenance, not authentication: the mark is written by the browser and can be changed by the same Windows user.
/// It is checked together with the package's full structure and every file's SHA-256; publisher authentication needs pipeline signing (see the
/// updater audit). The policy comes from the updater itself or a per-user policy file — never from the package being checked.
/// </summary>
public sealed record TrustPolicy
{
    public IReadOnlyList<string> Hosts { get; init; } = ["dev.azure.com", "*.visualstudio.com"];
    /// <summary>Azure DevOps organizations whose pipelines are trusted; empty = any organization on the allowed hosts.</summary>
    public IReadOnlyList<string> Organizations { get; init; } = [];

    public static readonly TrustPolicy Default = new();

    /// <summary>The per-user override (installations folder), else the built-in default. An unreadable file fails closed to the default.</summary>
    public static TrustPolicy Load(string? path)
    {
        if (path is null || !File.Exists(path)) return Default;
        try
        {
            var policy = JsonSerializer.Deserialize<TrustPolicy>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return policy is { Hosts.Count: > 0 } ? policy : Default;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return Default; }
    }

    public (TrustVerdict Verdict, string Detail) Evaluate(DownloadOrigin? origin)
    {
        if (origin is null || (origin.HostUrl is null && origin.ReferrerUrl is null))
            return (TrustVerdict.NoOrigin, "Windows has no record of where this file was downloaded from (it was copied, extracted from another archive or built locally).");
        var urls = new[] { origin.ReferrerUrl, origin.HostUrl }.Where(u => u is not null).Select(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) ? uri : null)
            .Where(u => u is { Scheme: "https" }).Cast<Uri>().ToList();
        var allowed = urls.Where(u => Hosts.Any(h => HostMatches(u.Host, h))).ToList();
        if (allowed.Count == 0) return (TrustVerdict.OtherOrigin, $"It was downloaded from {string.Join(", ", urls.Select(u => u.Host).Distinct()).IfEmpty("an unknown place")}, not from the approved Azure DevOps.");
        if (Organizations.Count == 0) return (TrustVerdict.PipelineOrigin, $"Downloaded from {allowed[0].Host}.");
        var orgs = allowed.Select(OrganizationOf).Where(o => o is not null).ToList();
        return orgs.Any(o => Organizations.Contains(o!, StringComparer.OrdinalIgnoreCase))
            ? (TrustVerdict.PipelineOrigin, $"Downloaded from the {orgs.First(o => Organizations.Contains(o!, StringComparer.OrdinalIgnoreCase))} Azure DevOps organization.")
            : (TrustVerdict.WrongOrganization, $"It was downloaded from Azure DevOps, but not from an approved organization ({string.Join(", ", Organizations)}).");
    }

    /// <summary>"dev.azure.com/{org}/…" or "{org}.visualstudio.com"; null for an artifact host that names no organization.</summary>
    public static string? OrganizationOf(Uri u) =>
        u.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase) ? u.AbsolutePath.Trim('/').Split('/')[0] is { Length: > 0 } org ? org : null
        : u.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase) && u.Host.Count(c => c == '.') == 2 ? u.Host.Split('.')[0]
        : null;

    /// <summary>"*.example.com" matches any sub-domain (never the bare suffix trick "evilexample.com"); otherwise an exact host.</summary>
    public static bool HostMatches(string host, string pattern) =>
        pattern.StartsWith("*.", StringComparison.Ordinal)
            ? host.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase) && host.Length > pattern.Length - 1
            : host.Equals(pattern, StringComparison.OrdinalIgnoreCase);
}

internal static class StringExtensions
{
    public static string IfEmpty(this string s, string fallback) => s.Length == 0 ? fallback : s;
}
