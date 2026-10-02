using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BirkNext.Api.Configuration;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Integrations;
using BirkNext.PipelineReview;
using BirkNext.SourceDomains;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace BirkNext.Api.Services.PipelineReview;

public sealed record PipelineReviewSource(Guid Id, string ArchiveName, string Fingerprint, DateTimeOffset AnalyzedAt, int CiCdAnalyzerVersion, int Pipelines, int Templates, string? Repository);

public sealed record PipelineReviewSources(bool SourceAnalysisEnabled, List<PipelineReviewSource> Snapshots, Guid? DefaultSnapshotId);

public interface IPipelineReviewService
{
    Task<PipelineReviewSources> SourcesAsync(string environmentId, CancellationToken ct = default);
    /// <summary>The review of exactly <paramref name="snapshotId"/> (never substituted), or of the newest snapshot with pipelines when null.</summary>
    Task<PipelineReviewResult?> ReviewAsync(string environmentId, Guid? snapshotId, bool includeMetadata, CancellationToken ct = default);
    Task<PathProbeResult?> ProbeAsync(string environmentId, Guid snapshotId, string path, CancellationToken ct = default);
    Task<PipelineReviewComparison?> CompareAsync(string environmentId, Guid previousSnapshotId, Guid currentSnapshotId, CancellationToken ct = default);
}

/// <summary>
/// Pipeline Review over Source Analysis snapshots (the shared source-evidence provider — no upload, no YAML parsing). Reviews are derived and
/// deterministic, so they are cached by snapshot id + rules version (+ whether Azure DevOps metadata was included), never by "latest".
/// </summary>
public sealed class PipelineReviewService(IReviewSourceEvidenceProvider sources, IPipelineMetadataSource metadata, IMemoryCache cache) : IPipelineReviewService
{
    public async Task<PipelineReviewSources> SourcesAsync(string environmentId, CancellationToken ct = default)
    {
        if (!sources.SourceAnalysisEnabled) return new(false, [], null);
        var list = (await sources.ListAsync(environmentId, ct)).Select(Source).ToList();
        return new(true, list, list.Where(s => s.Pipelines > 0).OrderByDescending(s => s.AnalyzedAt).Select(s => (Guid?)s.Id).FirstOrDefault());
    }

    private static PipelineReviewSource Source(IqrSourceSnapshot s) => new(s.Id, s.Archive.FileName, s.Archive.Sha256, s.AnalyzedAt, s.EvidenceDomains?.CiCd.AnalyzerVersion ?? 0,
        s.EvidenceDomains?.CiCd.Pipelines.Count(p => !p.IsTemplate) ?? 0, s.EvidenceDomains?.CiCd.Pipelines.Count(p => p.IsTemplate) ?? 0, s.Repository?.DisplayName);

    private async Task<IqrSourceSnapshot?> Resolve(string environmentId, Guid? snapshotId, CancellationToken ct)
    {
        if (!sources.SourceAnalysisEnabled) return null;
        if (snapshotId is { } id) return await sources.ResolveAsync(environmentId, id, ct);
        return (await sources.ListAsync(environmentId, ct)).Where(s => s.EvidenceDomains?.CiCd.Pipelines.Count > 0).OrderByDescending(s => s.AnalyzedAt).FirstOrDefault();
    }

    public async Task<PipelineReviewResult?> ReviewAsync(string environmentId, Guid? snapshotId, bool includeMetadata, CancellationToken ct = default)
    {
        if (await Resolve(environmentId, snapshotId, ct) is not { } snapshot) return null;
        var meta = includeMetadata ? await metadata.GetAsync(ct) : null;
        var key = $"pipeline-review:{snapshot.Id}:{PipelineReviewText.RulesVersion}:{meta?.State ?? "none"}";
        if (cache.TryGetValue(key, out PipelineReviewResult? cached) && cached is not null) return cached;
        var result = PipelineReviewBuilder.Build(snapshot, meta);
        cache.Set(key, result, TimeSpan.FromMinutes(30));
        return result;
    }

    public async Task<PathProbeResult?> ProbeAsync(string environmentId, Guid snapshotId, string path, CancellationToken ct = default)
    {
        if (await Resolve(environmentId, snapshotId, ct) is not { } snapshot) return null;
        var review = await ReviewAsync(environmentId, snapshotId, false, ct);
        return PipelineReviewBuilder.Probe(snapshot, review!, path);
    }

    public async Task<PipelineReviewComparison?> CompareAsync(string environmentId, Guid previousSnapshotId, Guid currentSnapshotId, CancellationToken ct = default)
    {
        var previous = await ReviewAsync(environmentId, previousSnapshotId, false, ct);
        var current = await ReviewAsync(environmentId, currentSnapshotId, false, ct);
        return previous is null || current is null ? null : PipelineReviewDiff.Compare(previous, current);
    }
}

/// <summary>Optional Azure DevOps configuration metadata for Pipeline Review: pipeline definitions (name → YAML path) and environments with their
/// checks. Read-only GETs with the existing Azure DevOps connector settings (PAT from configuration, never logged). Never run history.</summary>
public interface IPipelineMetadataSource
{
    Task<PipelineMetadataSummary> GetAsync(CancellationToken ct);
}

public sealed class AzureDevOpsPipelineMetadataSource(HttpClient http, IOptions<AzureDevOpsOptions> options, ILogger<AzureDevOpsPipelineMetadataSource> logger) : IPipelineMetadataSource
{
    public const string NotConfigured = "Azure DevOps is not configured (AzureDevOps:Enabled, OrganizationUrl, Project and a PAT). Pipeline Review works from source; environment approvals/checks are not assessable.";

    public async Task<PipelineMetadataSummary> GetAsync(CancellationToken ct)
    {
        var o = options.Value;
        if (!o.IsConfigured) return new PipelineMetadataSummary { State = "NotConfigured", Detail = NotConfigured };
        var root = $"{o.OrganizationUrl.TrimEnd('/')}/{Uri.EscapeDataString(o.Project)}/_apis";
        try
        {
            var (status, pipelines) = await Get($"{root}/pipelines?api-version=7.1", o.Pat, ct);
            if (pipelines is null) return Failed(status);
            var definitions = new List<PipelineDefinitionMetadata>();
            using (pipelines)
                foreach (var p in pipelines.RootElement.GetProperty("value").EnumerateArray().Take(60))
                {
                    var id = p.GetProperty("id").ToString();
                    var (_, detail) = await Get($"{root}/pipelines/{id}?api-version=7.1", o.Pat, ct);
                    string? path = null, repo = null;
                    if (detail is not null)
                        using (detail)
                            if (detail.RootElement.TryGetProperty("configuration", out var c))
                            {
                                path = c.TryGetProperty("path", out var y) ? y.GetString() : null;
                                repo = c.TryGetProperty("repository", out var r) && r.TryGetProperty("name", out var rn) ? rn.GetString() : null;
                            }
                    definitions.Add(new(id, p.TryGetProperty("name", out var n) ? n.GetString() ?? id : id, p.TryGetProperty("folder", out var f) ? f.GetString() : null, path, repo));
                }
            var environments = new List<EnvironmentChecksMetadata>();
            var (envStatus, envs) = await Get($"{root}/distributedtask/environments?api-version=7.1-preview.1", o.Pat, ct);
            if (envs is not null)
                using (envs)
                    foreach (var env in envs.RootElement.GetProperty("value").EnumerateArray().Take(40))
                    {
                        var id = env.GetProperty("id").ToString();
                        var (_, checks) = await Get($"{root}/pipelines/checks/configurations?resourceType=environment&resourceId={id}&api-version=7.1-preview.1", o.Pat, ct);
                        var names = new List<string>();
                        if (checks is not null)
                            using (checks)
                                names = checks.RootElement.GetProperty("value").EnumerateArray()
                                    .Select(c => c.TryGetProperty("type", out var t) && t.TryGetProperty("name", out var tn) ? tn.GetString() : null).OfType<string>().Distinct().ToList();
                        environments.Add(new(env.TryGetProperty("name", out var en) ? en.GetString() ?? id : id, names));
                    }
            return new PipelineMetadataSummary
            {
                State = "Available", Definitions = definitions, Environments = environments,
                Detail = $"Read {definitions.Count} pipeline definition(s){(envs is null ? $"; environments could not be read ({envStatus})" : $" and {environments.Count} environment(s) with their checks")} — configuration only, no run history.",
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or System.Collections.Generic.KeyNotFoundException or InvalidOperationException)
        {
            logger.LogInformation("Azure DevOps pipeline metadata unavailable: {Error}.", ex.GetType().Name);
            return new PipelineMetadataSummary { State = "Failed", Detail = $"Azure DevOps metadata could not be read ({ex.GetType().Name})." };
        }
    }

    private static PipelineMetadataSummary Failed(HttpStatusCode? status) => new()
    {
        State = status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? "NotAuthorized" : "Failed",
        Detail = status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? "The Azure DevOps PAT may not read pipelines (Build: Read) or environments." : $"Azure DevOps answered {(int?)status}.",
    };

    private async Task<(HttpStatusCode? Status, JsonDocument? Body)> Get(string url, string pat, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}")));
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return (response.StatusCode, null);
        return (response.StatusCode, await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct));
    }
}

/// <summary>Pipeline source changes between two reviewed snapshots, with deterministic interpretations where safe. Source change ≠ runtime failure.</summary>
public static class PipelineReviewDiff
{
    public static PipelineReviewComparison Compare(PipelineReviewResult previous, PipelineReviewResult current)
    {
        var changes = new List<PipelineReviewChange>();
        var before = previous.Pipelines.Where(p => !p.IsTemplate).ToDictionary(p => p.Id, StringComparer.Ordinal);
        var after = current.Pipelines.Where(p => !p.IsTemplate).ToDictionary(p => p.Id, StringComparer.Ordinal);
        foreach (var p in after.Values.Where(p => !before.ContainsKey(p.Id))) changes.Add(new(PipelineChangeKind.PipelineAdded, p.Name, $"`{p.File}` ({p.Role})", null, null));
        foreach (var p in before.Values.Where(p => !after.ContainsKey(p.Id))) changes.Add(new(PipelineChangeKind.PipelineRemoved, p.Name, $"`{p.File}` ({p.Role})", "Validation or deployment this pipeline did is no longer defined here.", PipelineFindingSeverity.Medium));
        foreach (var p in after.Values.Where(p => before.ContainsKey(p.Id)))
        {
            var o = before[p.Id];
            var oldTriggers = string.Join("; ", o.Triggers.Select(t => $"{t.Kind}:{string.Join(",", t.Branches)}"));
            var newTriggers = string.Join("; ", p.Triggers.Select(t => $"{t.Kind}:{string.Join(",", t.Branches)}"));
            if (oldTriggers != newTriggers) changes.Add(new(PipelineChangeKind.TriggerChanged, p.Name, $"{string.Join("; ", o.Triggers.Select(t => t.Text))} → {string.Join("; ", p.Triggers.Select(t => t.Text))}",
                Production(current, p.Id) && p.Triggers.Count > o.Triggers.Count ? "Production trigger widened — needs review." : null, Production(current, p.Id) ? PipelineFindingSeverity.Medium : null));
            var oldPaths = Paths(o);
            var newPaths = Paths(p);
            if (oldPaths != newPaths)
            {
                var narrowed = p.Triggers.Sum(t => t.PathsInclude.Count) < o.Triggers.Sum(t => t.PathsInclude.Count) || p.Triggers.Sum(t => t.PathsExclude.Count) > o.Triggers.Sum(t => t.PathsExclude.Count);
                changes.Add(new(PipelineChangeKind.PathFilterChanged, p.Name, $"{oldPaths} → {newPaths}",
                    narrowed ? "Path filters narrowed: some changes may no longer start this pipeline." : "Path filters widened: more changes start this pipeline.", narrowed ? PipelineFindingSeverity.Medium : PipelineFindingSeverity.Info));
            }
        }
        var oldTests = previous.Tests.Select(t => (t.PipelineName, t.Category, Where: t.Stage ?? t.Job ?? "")).ToHashSet();
        var newTests = current.Tests.Select(t => (t.PipelineName, t.Category, Where: t.Stage ?? t.Job ?? "")).ToHashSet();
        foreach (var t in newTests.Except(oldTests)) changes.Add(new(PipelineChangeKind.TestAdded, t.PipelineName, $"{PipelineReviewText.Label(t.Category)} in `{t.Where}`", null, null));
        foreach (var t in oldTests.Except(newTests)) changes.Add(new(PipelineChangeKind.TestRemoved, t.PipelineName, $"{PipelineReviewText.Label(t.Category)} in `{t.Where}`", "A validation step no longer exists in this pipeline.", PipelineFindingSeverity.Medium));
        var oldDeploy = previous.Deployments.ToDictionary(d => d.Id, StringComparer.Ordinal);
        var newDeploy = current.Deployments.ToDictionary(d => d.Id, StringComparer.Ordinal);
        foreach (var d in newDeploy.Values.Where(d => !oldDeploy.ContainsKey(d.Id))) changes.Add(new(PipelineChangeKind.DeploymentAdded, d.PipelineName, $"`{d.Stage ?? d.Job}` → {PipelineReviewBuilder.Display(d)}", null, null));
        foreach (var d in oldDeploy.Values.Where(d => !newDeploy.ContainsKey(d.Id))) changes.Add(new(PipelineChangeKind.DeploymentRemoved, d.PipelineName, $"`{d.Stage ?? d.Job}` → {PipelineReviewBuilder.Display(d)}", null, PipelineFindingSeverity.Info));
        foreach (var d in newDeploy.Values.Where(d => oldDeploy.ContainsKey(d.Id)))
        {
            var o = oldDeploy[d.Id];
            static HashSet<ValidationCategory> Gating(DeploymentReview x) => x.Before.Where(g => PipelineReviewBuilder.IsTest(g.Category) && g.State != GateState.Unknown).Select(g => g.Category).ToHashSet();
            var lost = Gating(o).Except(Gating(d)).ToList();
            var gained = Gating(d).Except(Gating(o)).ToList();
            var env = PipelineReviewBuilder.Display(d);
            foreach (var c in lost)
                changes.Add(new(PipelineChangeKind.GatingChanged, d.PipelineName, $"{PipelineReviewText.Label(c)} are no longer on the {env} deployment path (`{d.Stage ?? d.Job}`)",
                    $"{PipelineReviewText.Label(c)} removed from the {env} dependency path — delivery validation concern.",
                    d.EnvironmentKind is SourceEnvironmentKind.Production or SourceEnvironmentKind.QA or SourceEnvironmentKind.Staging ? PipelineFindingSeverity.High : PipelineFindingSeverity.Medium));
            foreach (var c in gained) changes.Add(new(PipelineChangeKind.GatingChanged, d.PipelineName, $"{PipelineReviewText.Label(c)} are now on the {env} deployment path", null, null));
            if ((o.Condition ?? "") != (d.Condition ?? "")) changes.Add(new(PipelineChangeKind.ConditionChanged, d.PipelineName, $"`{o.Condition ?? "(default)"}` → `{d.Condition ?? "(default)"}` on `{d.Stage ?? d.Job}`", "Changes when the deployment runs — needs review.", PipelineFindingSeverity.Low));
            var oldArtifacts = string.Join("; ", o.Artifacts.Select(a => $"{a.Artifact} from {a.Source}"));
            var newArtifacts = string.Join("; ", d.Artifacts.Select(a => $"{a.Artifact} from {a.Source}"));
            if (oldArtifacts != newArtifacts) changes.Add(new(PipelineChangeKind.ArtifactFlowChanged, d.PipelineName, $"{env}: {oldArtifacts} → {newArtifacts}", "What is deployed, and from where, changed — check lineage.", PipelineFindingSeverity.Low));
        }
        var oldTemplates = previous.Dependencies.Where(d => d.Kind == PipelineEdgeKind.IncludesTemplate).Select(d => $"{d.FromPipeline}→{d.ToPipeline}").ToHashSet(StringComparer.Ordinal);
        var newTemplates = current.Dependencies.Where(d => d.Kind == PipelineEdgeKind.IncludesTemplate).Select(d => $"{d.FromPipeline}→{d.ToPipeline}").ToHashSet(StringComparer.Ordinal);
        foreach (var t in newTemplates.Except(oldTemplates)) changes.Add(new(PipelineChangeKind.TemplateChanged, t.Split('→')[0], $"now uses template `{t.Split('→')[1]}`", null, null));
        foreach (var t in oldTemplates.Except(newTemplates)) changes.Add(new(PipelineChangeKind.TemplateChanged, t.Split('→')[0], $"no longer uses template `{t.Split('→')[1]}`", null, null));
        return new PipelineReviewComparison(previous.SourceSnapshotId, current.SourceSnapshotId,
            changes.OrderBy(c => c.Severity ?? PipelineFindingSeverity.Info).ThenBy(c => c.Kind).ThenBy(c => c.Pipeline, StringComparer.Ordinal).ToList(), PipelineReviewText.ChangesBoundary);
    }

    private static bool Production(PipelineReviewResult r, string pipeline) => r.Deployments.Any(d => d.Pipeline == pipeline && d.EnvironmentKind == SourceEnvironmentKind.Production);
    private static string Paths(ReviewedPipeline p) => string.Join("; ", p.Triggers.Where(t => t.PathsInclude.Count + t.PathsExclude.Count > 0)
        .Select(t => $"{t.Kind}: {string.Join(",", t.PathsInclude)}{(t.PathsExclude.Count > 0 ? $" not {string.Join(",", t.PathsExclude)}" : "")}")) is { Length: > 0 } s ? s : "(no path filter)";
}
