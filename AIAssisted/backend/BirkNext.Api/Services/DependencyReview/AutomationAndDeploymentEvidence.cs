using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BirkNext.Api.Configuration;
using BirkNext.Api.Services.WasmPerformance;
using BirkNext.Dependencies;
using Microsoft.Extensions.Options;

namespace BirkNext.Api.Services.DependencyReview;

/// <summary>Renovate runtime evidence from a repository provider — separate from Renovate configuration.</summary>
public interface IDependencyAutomationSource
{
    Task<AutomationEvidence> GetAsync(string? repository, int? stalePullRequestAfterDays, CancellationToken ct);
}

/// <summary>
/// Azure DevOps (the existing AzureDevOps connector options; PAT from configuration/ADO_PAT, never logged or stored): active pull requests
/// from renovate/* branches and runs of pipelines named like "renovate". GET only. Not configured → "Not configured", never a guess.
/// A last successful run does not mean dependencies are current; no open PR does not mean no update exists; an open PR is not "safe".
/// </summary>
public sealed partial class AzureDevOpsRenovateAutomationSource(HttpClient http, IOptions<AzureDevOpsOptions> options) : IDependencyAutomationSource
{
    private const string Provider = "Azure DevOps";
    private const string Dashboard = "Not read: Azure DevOps has no issue-based Dependency Dashboard that BirkNext can read.";

    public async Task<AutomationEvidence> GetAsync(string? repository, int? stalePullRequestAfterDays, CancellationToken ct)
    {
        var o = options.Value;
        if (!o.IsConfigured)
            return new AutomationEvidence { State = AutomationState.NotConfigured, Provider = Provider, Repository = repository, DependencyDashboard = "Not assessed", StalePullRequestThresholdDays = stalePullRequestAfterDays,
                Detail = "No repository provider is configured (AzureDevOps:Enabled, OrganizationUrl, Project and a PAT). Renovate runs, open PRs and failures are not read; the static Renovate configuration is reviewed separately." };
        var repo = string.IsNullOrWhiteSpace(repository) ? o.RepositoryId : repository;
        if (string.IsNullOrWhiteSpace(repo))
            return new AutomationEvidence { State = AutomationState.NotAssessed, Provider = Provider, Detail = "The inventory names no repository and AzureDevOps:RepositoryId is not set.", DependencyDashboard = Dashboard };
        var now = DateTimeOffset.UtcNow;
        var root = $"{o.OrganizationUrl.TrimEnd('/')}/{Uri.EscapeDataString(o.Project)}/_apis";
        var (prState, prs) = await GetAsync($"{root}/git/repositories/{Uri.EscapeDataString(repo)}/pullrequests?searchCriteria.status=active&$top=200&api-version=7.1", o.Pat, ct);
        if (prs is null) return Failure(prState, repo, now, stalePullRequestAfterDays);
        var pulls = new List<DependencyPullRequest>();
        using (prs)
            foreach (var pr in prs.RootElement.GetProperty("value").EnumerateArray())
            {
                var branch = pr.TryGetProperty("sourceRefName", out var b) ? b.GetString() ?? "" : "";
                if (!branch.StartsWith("refs/heads/renovate/", StringComparison.OrdinalIgnoreCase)) continue;
                var title = pr.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                var created = pr.TryGetProperty("creationDate", out var c) && c.TryGetDateTimeOffset(out var cd) ? cd : (DateTimeOffset?)null;
                var age = created is { } at ? (int?)Math.Max(0, (int)Math.Floor((now - at).TotalDays)) : null;
                var labels = pr.TryGetProperty("labels", out var l) && l.ValueKind == JsonValueKind.Array ? l.EnumerateArray().Select(x => x.TryGetProperty("name", out var n) ? n.GetString() : null).OfType<string>().ToList() : [];
                var parsed = TitlePattern().Match(title);
                pulls.Add(new DependencyPullRequest
                {
                    Id = pr.TryGetProperty("pullRequestId", out var id) ? id.ToString() : "", Title = title, SourceBranch = branch["refs/heads/".Length..], CreatedAt = created, AgeDays = age,
                    Status = pr.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "", MergeStatus = pr.TryGetProperty("mergeStatus", out var ms) ? ms.GetString() : null,
                    PackageName = parsed.Success ? parsed.Groups["package"].Value : null, ToVersion = parsed.Success ? parsed.Groups["to"].Value : null,
                    SecurityMarked = title.Contains("[SECURITY]", StringComparison.OrdinalIgnoreCase) || labels.Any(x => x.Contains("security", StringComparison.OrdinalIgnoreCase)),
                    Stale = stalePullRequestAfterDays is { } limit && age is { } a ? a > limit : null,
                });
            }
        AutomationRun? last = null, lastSuccess = null;
        int? failed = null;
        var (defState, defs) = await GetAsync($"{root}/build/definitions?name=*renovate*&api-version=7.1", o.Pat, ct);
        var runDetail = "";
        if (defs is not null)
        {
            List<string> ids;
            using (defs) ids = defs.RootElement.GetProperty("value").EnumerateArray().Select(d => d.GetProperty("id").ToString()).ToList();
            if (ids.Count == 0) runDetail = "No pipeline named like \"renovate\" in the project.";
            else
            {
                var (buildState, builds) = await GetAsync($"{root}/build/builds?definitions={string.Join(",", ids.Take(5))}&$top=20&queryOrder=finishTimeDescending&api-version=7.1", o.Pat, ct);
                if (builds is null) runDetail = $"Pipeline runs could not be read: {buildState}";
                else
                    using (builds)
                    {
                        var runs = builds.RootElement.GetProperty("value").EnumerateArray().Select(r => new AutomationRun(
                            r.TryGetProperty("buildNumber", out var bn) ? bn.GetString() ?? "" : r.GetProperty("id").ToString(),
                            r.TryGetProperty("startTime", out var st) && st.TryGetDateTimeOffset(out var sdt) ? sdt : null,
                            r.TryGetProperty("finishTime", out var ft) && ft.TryGetDateTimeOffset(out var fdt) ? fdt : null,
                            r.TryGetProperty("result", out var res) ? res.GetString() ?? "" : r.TryGetProperty("status", out var stt) ? stt.GetString() ?? "" : "",
                            r.TryGetProperty("_links", out var links) && links.TryGetProperty("web", out var web) && web.TryGetProperty("href", out var href) ? href.GetString() : null)).ToList();
                        last = runs.FirstOrDefault();
                        lastSuccess = runs.FirstOrDefault(r => r.Result.Equals("succeeded", StringComparison.OrdinalIgnoreCase));
                        failed = runs.Count(r => r.Result.Equals("failed", StringComparison.OrdinalIgnoreCase));
                        runDetail = runs.Count == 0 ? "No Renovate pipeline runs observed." : $"{runs.Count} recent run(s) read.";
                    }
            }
        }
        else runDetail = $"Pipeline definitions could not be read: {defState}";
        return new AutomationEvidence
        {
            State = last is null && pulls.Count == 0 ? AutomationState.NoRunsObserved : AutomationState.Observed, Provider = Provider, Repository = repo, RetrievedAt = now,
            LastRun = last, LastSuccessfulRun = lastSuccess, FailedRunsInWindow = failed, OpenPullRequests = pulls, DependencyDashboard = Dashboard, StalePullRequestThresholdDays = stalePullRequestAfterDays,
            Detail = $"{pulls.Count} open Renovate PR(s). {runDetail} A successful run does not mean dependencies are current, and no open PR does not mean no update exists.",
        };
    }

    [GeneratedRegex(@"(?i)update (?:dependency )?(?<package>[\w.\-/@]+) to v?(?<to>[\w.\-+]+)")] private static partial Regex TitlePattern();

    private static AutomationEvidence Failure(string state, string repo, DateTimeOffset now, int? stale) => new()
    {
        State = state.StartsWith("Unauthorized", StringComparison.Ordinal) ? AutomationState.Unauthorized : AutomationState.ProviderUnavailable, Provider = Provider, Repository = repo, RetrievedAt = now,
        Detail = $"Azure DevOps evidence unavailable: {state} This is not \"no Renovate activity\".", DependencyDashboard = Dashboard, StalePullRequestThresholdDays = stale,
    };

    private async Task<(string State, JsonDocument? Doc)> GetAsync(string url, string pat, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}")));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await http.SendAsync(request, ct);
            // Azure DevOps answers an invalid PAT with 203 and a sign-in page.
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NonAuthoritativeInformation) return ($"Unauthorized (HTTP {(int)response.StatusCode}).", null);
            if (response.StatusCode == HttpStatusCode.NotFound) return ("Not found in the configured Azure DevOps project (HTTP 404).", null);
            if (response.StatusCode == HttpStatusCode.TooManyRequests) return ("Rate limited (HTTP 429).", null);
            if (!response.IsSuccessStatusCode) return ($"HTTP {(int)response.StatusCode}.", null);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return ("", await JsonDocument.ParseAsync(stream, cancellationToken: ct));
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return ("Timed out.", null); }
        catch (HttpRequestException ex) { return ($"Not reachable ({ex.HttpRequestError}).", null); }
        catch (JsonException) { return ("Unreadable response.", null); }
    }
}

/// <summary>Deployed dependency evidence for a target (read-only GETs of public deployment metadata).</summary>
public interface IDeployedDependencySource
{
    Task<(DependencyInventorySnapshot? Inventory, string? Error)> CaptureAsync(string targetUrl, string? environment, CancellationToken ct);
}

/// <summary>
/// Blazor WebAssembly boot manifest (_framework/blazor.boot.json, parsed by the existing WASM asset discovery parser): the assemblies a
/// deployment serves, with their integrity hashes. It proves which assembly FILES are deployed — not package identities, not versions,
/// not that anything is loaded — so every item has version Unknown.
/// </summary>
public sealed partial class BootManifestDeployedSource(HttpClient http) : IDeployedDependencySource
{
    private const long MaxBytes = 5 * 1024 * 1024;

    public async Task<(DependencyInventorySnapshot? Inventory, string? Error)> CaptureAsync(string targetUrl, string? environment, CancellationToken ct)
    {
        if (!Uri.TryCreate(targetUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var root) || root.Scheme is not ("http" or "https"))
            return (null, "Enter an http(s) target URL.");
        try
        {
            var index = await GetTextAsync(root, ct);
            var baseHref = index.Text is { } html && BaseHref().Match(html) is { Success: true } m ? m.Groups["href"].Value : "/";
            var frameworkBase = new Uri(root, baseHref.TrimEnd('/') + "/");
            var bootUri = new Uri(frameworkBase, "_framework/blazor.boot.json");
            var boot = await GetTextAsync(bootUri, ct);
            if (boot.Text is null)
                return (null, $"No blazor.boot.json at {bootUri.GetLeftPart(UriPartial.Path)} ({boot.Status}). The target may not be Blazor WebAssembly, or it embeds the boot configuration in dotnet.js (.NET 9+), which BirkNext does not read.");
            var manifest = WasmAssetDiscoveryService.ParseBootManifest(boot.Text);
            if (manifest?.Resources?.Assembly is not { Count: > 0 } assemblies) return (null, "The boot manifest lists no assemblies.");
            var now = DateTimeOffset.UtcNow;
            var host = root.GetLeftPart(UriPartial.Authority);
            var items = assemblies.Select(a => new InventoryDependency
            {
                PackageName = System.IO.Path.GetFileNameWithoutExtension(a.Key), PackageManager = "assembly", Relationship = DependencyRelationship.Unknown, Stage = InventoryStage.Deployed,
                Location = $"{host}/_framework/{a.Key}", Hashes = string.IsNullOrEmpty(a.Value) ? [] : [new DependencyHash("SHA-256 (boot manifest integrity)", a.Value.StartsWith("sha256-", StringComparison.Ordinal) ? a.Value[7..] : a.Value)],
            }).OrderBy(i => i.PackageName, StringComparer.OrdinalIgnoreCase).ToList();
            return (new DependencyInventorySnapshot
            {
                Id = Guid.NewGuid(), Name = $"{root.Host}{(environment is { Length: > 0 } e ? $" ({e})" : "")} · deployed assemblies {now:yyyy-MM-dd HH:mm} UTC", SourceType = InventorySourceType.Deployment,
                SourceName = host, Stage = InventoryStage.Deployed, CapturedAt = now, RecordedAt = now, Environment = string.IsNullOrWhiteSpace(environment) ? null : environment.Trim(),
                ArtifactId = manifest.MainAssemblyName, ContentSha256 = InventorySources.Sha256(Encoding.UTF8.GetBytes(boot.Text)), Dependencies = items,
                Provenance = $"Blazor boot manifest {bootUri.GetLeftPart(UriPartial.Path)} captured {now:yyyy-MM-dd HH:mm} UTC (read-only GET).",
                Limitations =
                [
                    "A boot manifest lists deployed assembly files, not NuGet package identities or versions: deployed versions are not proven.",
                    "Deployed is not runtime-loaded: lazy-loaded or unused assemblies are listed the same way.",
                ],
            }, null);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return (null, "The target did not answer within the timeout."); }
        catch (HttpRequestException ex) { return (null, $"The target could not be reached ({ex.HttpRequestError})."); }
    }

    [GeneratedRegex(@"<base\s+href\s*=\s*[""'](?<href>[^""']*)[""']", RegexOptions.IgnoreCase)] private static partial Regex BaseHref();

    private async Task<(string? Text, string Status)> GetTextAsync(Uri uri, CancellationToken ct)
    {
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) return (null, $"HTTP {(int)response.StatusCode}");
        if (response.Content.Headers.ContentLength > MaxBytes) return (null, "larger than the 5 MB limit");
        var text = await response.Content.ReadAsStringAsync(ct);
        return text.Length > MaxBytes ? (null, "larger than the 5 MB limit") : (text, "HTTP 200");
    }
}
