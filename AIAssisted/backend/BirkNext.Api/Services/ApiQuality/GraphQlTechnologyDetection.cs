using System.Text.Json;
using System.Text.RegularExpressions;
using BirkNext.ApiReview;

namespace BirkNext.Api.Services.ApiQuality;

/// <summary>
/// Hot Chocolate fingerprints in a GraphQL error response of the review's OWN safe requests (the __typename probe, the refused
/// introspection query, the unknown-field probe). Only fingerprint descriptions leave this class — never message text, paths or values.
/// </summary>
public static partial class GraphQlServerFingerprints
{
    public const string ErrorCodeKind = "hc-error-code";
    public const string IntrospectionMessageKind = "hc-introspection-message";
    public const string UnknownFieldMessageKind = "hc-unknown-field-message";

    [GeneratedRegex(@"^HC\d{4}$")] private static partial Regex HotChocolateCode();
    [GeneratedRegex(@"^The field `[^`]+` does not exist on the type `[^`]+`\.$")] private static partial Regex UnknownFieldTemplate();
    private const string IntrospectionNotAllowed = "Introspection is not allowed for the current request.";

    /// <summary>"kind|evidence" entries; empty when the body is not a GraphQL error document or carries no Hot Chocolate fingerprint.</summary>
    public static List<string> From(string? body)
    {
        var found = new List<string>();
        if (string.IsNullOrWhiteSpace(body) || !body.TrimStart().StartsWith('{')) return found;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array) return found;
            foreach (var error in errors.EnumerateArray().Take(10))
            {
                if (error.ValueKind != JsonValueKind.Object) continue;
                if (error.TryGetProperty("extensions", out var extensions) && extensions.ValueKind == JsonValueKind.Object
                    && extensions.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String && code.GetString() is { } value && HotChocolateCode().IsMatch(value))
                    Add(found, ErrorCodeKind, $"GraphQL error code {value} (Hot Chocolate's HC#### error-code format)");
                if (error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String && message.GetString() is { } text)
                {
                    if (text == IntrospectionNotAllowed) Add(found, IntrospectionMessageKind, $"Introspection refusal uses Hot Chocolate's message \"{IntrospectionNotAllowed}\"");
                    else if (UnknownFieldTemplate().IsMatch(text)) Add(found, UnknownFieldMessageKind, "Unknown-field error uses Hot Chocolate's template \"The field `…` does not exist on the type `…`.\"");
                }
            }
        }
        catch (JsonException) { }
        return found;
    }

    private static void Add(List<string> found, string kind, string evidence)
    {
        if (!found.Any(f => f.StartsWith(kind + "|", StringComparison.Ordinal))) found.Add($"{kind}|{evidence}");
    }

    /// <summary>
    /// Runtime evidence can make Hot Chocolate Likely (two or more distinct fingerprints), never Confirmed: Confirmed needs source or package
    /// evidence, which the review does not have for a deployed target. GraphQL behaviour alone is never a detection.
    /// </summary>
    public static GraphQlTechnologyFinding Classify(IEnumerable<string> fingerprints)
    {
        var distinct = fingerprints.GroupBy(f => f.Split('|')[0]).Select(g => g.First().Split('|', 2)[1]).ToList();
        return distinct.Count >= 2
            ? new GraphQlTechnologyFinding { Technology = GraphQlTechnologies.HotChocolate, Confidence = GraphQlTechnologyConfidence.Likely, Source = GraphQlTechnologyEvidenceSource.RuntimeResponse, Evidence = distinct }
            : new GraphQlTechnologyFinding
            {
                Confidence = GraphQlTechnologyConfidence.NotDetected, Source = distinct.Count == 0 ? GraphQlTechnologyEvidenceSource.None : GraphQlTechnologyEvidenceSource.RuntimeResponse, Evidence = distinct,
                Note = distinct.Count == 0 ? "No server-specific fingerprint in the review's own requests." : "One runtime fingerprint only; at least two independent fingerprints are required.",
            };
    }
}

/// <summary>
/// GraphQL client technology from the DEPLOYED frontend build: the Blazor boot manifest (_framework/blazor.boot.json) is public and lists
/// every assembly the app ships. StrawberryShake.* assemblies in it confirm a Strawberry Shake client in that build — a build artifact,
/// not an inference from "Blazor + GraphQL". Read-only GETs, no redirects followed, bounded.
/// </summary>
public static partial class GraphQlClientTechnologyDetector
{
    private const int MaxIndexBytes = 512 * 1024;
    private const int MaxManifestBytes = 2 * 1024 * 1024;

    [GeneratedRegex(@"<base\s+href\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase)] private static partial Regex BaseHref();
    [GeneratedRegex(@"^(StrawberryShake(?:\.[A-Za-z]+)*?)(?:\.[a-z0-9]{8,})?\.(?:wasm|dll)$")] private static partial Regex StrawberryAssembly();

    public static async Task<GraphQlTechnologyFinding> DetectAsync(HttpClient http, string? frontendUrl, CancellationToken ct)
    {
        static GraphQlTechnologyFinding None(string note) => new() { Confidence = GraphQlTechnologyConfidence.NotDetected, Source = GraphQlTechnologyEvidenceSource.None, Note = note };
        if (!Uri.TryCreate(frontendUrl, UriKind.Absolute, out var root) || root.Scheme is not ("https" or "http")) return None("No frontend URL to read a build manifest from.");
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(TimeSpan.FromSeconds(10));
            var (indexStatus, index) = await GetAsync(http, new Uri(root, "/"), MaxIndexBytes, cts.Token);
            var baseHref = index is not null && BaseHref().Match(index) is { Success: true } m ? m.Groups[1].Value : "/";
            var manifestUri = new Uri(new Uri(root, "/"), baseHref.TrimEnd('/') + "/_framework/blazor.boot.json");
            var (status, manifest) = await GetAsync(http, manifestUri, MaxManifestBytes, cts.Token);
            if (manifest is null)
                return None($"The deployed frontend's build manifest was not readable (index HTTP {indexStatus}, blazor.boot.json HTTP {status}).");
            var names = new SortedSet<string>(StringComparer.Ordinal);
            using (var document = JsonDocument.Parse(manifest)) Collect(document.RootElement, names);
            return names.Count == 0
                ? new GraphQlTechnologyFinding { Confidence = GraphQlTechnologyConfidence.NotDetected, Source = GraphQlTechnologyEvidenceSource.DeployedFrontendArtifact, Note = "The deployed frontend build manifest lists no Strawberry Shake assembly." }
                : new GraphQlTechnologyFinding
                {
                    Technology = GraphQlTechnologies.StrawberryShake, Confidence = GraphQlTechnologyConfidence.Confirmed, Source = GraphQlTechnologyEvidenceSource.DeployedFrontendArtifact,
                    Evidence = [$"Deployed frontend build manifest (_framework/blazor.boot.json) lists {string.Join(", ", names.Take(6))}"],
                };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return None($"The deployed frontend's build manifest could not be read ({ex.GetType().Name}).");
        }
    }

    /// <summary>Assembly names found anywhere in the manifest (keys or values), .NET 6–8 layouts including fingerprinted names.</summary>
    internal static void Collect(JsonElement element, SortedSet<string> names)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    Match(property.Name, names);
                    Collect(property.Value, names);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) Collect(item, names);
                break;
            case JsonValueKind.String:
                Match(element.GetString(), names);
                break;
        }
    }

    private static void Match(string? candidate, SortedSet<string> names)
    {
        if (candidate is not null && StrawberryAssembly().Match(candidate) is { Success: true } m) names.Add(m.Groups[1].Value);
    }

    private static async Task<(int Status, string? Text)> GetAsync(HttpClient http, Uri uri, int maxBytes, CancellationToken ct)
    {
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        var status = (int)response.StatusCode;
        if (!response.IsSuccessStatusCode) return (status, null);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[maxBytes];
        var total = 0;
        int read;
        while (total < maxBytes && (read = await stream.ReadAsync(buffer.AsMemory(total, maxBytes - total), ct)) > 0) total += read;
        return (status, System.Text.Encoding.UTF8.GetString(buffer, 0, total));
    }
}
