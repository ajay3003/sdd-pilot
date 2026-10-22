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
