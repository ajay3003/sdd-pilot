using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BirkNext.Api.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BirkNext.Api.Services.ImplementationTraceability;

public sealed partial class AzureDevOpsImplementationEvidenceProvider : IImplementationEvidenceProvider
{
    private readonly HttpClient _http;
    private readonly AzureDevOpsOptions _options;
    private readonly ILogger<AzureDevOpsImplementationEvidenceProvider> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public AzureDevOpsImplementationEvidenceProvider(
        HttpClient http,
        IOptions<AzureDevOpsOptions> options,
        ILogger<AzureDevOpsImplementationEvidenceProvider> logger)
    {
        _http    = http;
        _options = options.Value;
        _logger  = logger;

        ConfigureAuth();
    }

    // ── Public entry point ─────────────────────────────────────────────────

    public async Task<ImplementationTraceabilityReport> FetchAsync(
        IReadOnlyList<int> workItemIds,
        string? repositoryId,
        string? branch,
        CancellationToken ct = default)
    {
        var repoId  = repositoryId ?? _options.RepositoryId;
        var tasks   = new List<TaskImplementationEvidence>();
        var allPrIds = new HashSet<string>();

        foreach (var id in workItemIds)
        {
            try
            {
                var evidence = await FetchWorkItemEvidenceAsync(id, repoId, ct);
                tasks.Add(evidence);
                foreach (var pr in evidence.PullRequests)
                    allPrIds.Add(pr.ExternalId);
            }
            catch (AzureDevOpsApiException ex)
            {
                _logger.LogWarning("ADO fetch failed for work item {WorkItemId}: {StatusCode}",
                    id, ex.StatusCode);

                tasks.Add(new TaskImplementationEvidence
                {
                    ExternalId   = id.ToString(),
                    DisplayTitle = $"Work item #{id}",
                    Source       = "AzureDevOps",
                    Confidence   = EvidenceConfidence.Missing,
                    State        = ex.StatusCode == HttpStatusCode.NotFound ? "NotFound" : "FetchError",
                });
            }
        }

        var allChangedFiles = tasks
            .SelectMany(t => t.PullRequests)
            .SelectMany(pr => pr.ChangedFiles)
            .ToList();

        var unmapped     = BuildUnmappedChanges(allChangedFiles, allPrIds);
        var testEvidence = BuildTestEvidence(allChangedFiles);
        var gaps         = BuildGaps(tasks);

        return new ImplementationTraceabilityReport
        {
            Tasks           = tasks,
            UnmappedChanges = unmapped,
            TestEvidence    = testEvidence,
            Gaps            = gaps,
            Source          = "AzureDevOps",
        };
    }

    // ── Work item → PRs ────────────────────────────────────────────────────

    private async Task<TaskImplementationEvidence> FetchWorkItemEvidenceAsync(
        int workItemId, string repoId, CancellationToken ct)
    {
        _logger.LogInformation("Fetching work item {WorkItemId}", workItemId);

        var wiUrl = $"{_options.OrganizationUrl}/{Uri.EscapeDataString(_options.Project)}" +
                    $"/_apis/wit/workitems/{workItemId}?$expand=relations&api-version=7.1";

        var wiJson = await GetAsync(wiUrl, ct);
        var wi     = ParseWorkItem(wiJson, workItemId);

        var linkedPrIds = ExtractLinkedPrIds(wi.Relations);
        _logger.LogInformation("Work item {WorkItemId} has {PrCount} linked PRs", workItemId, linkedPrIds.Count);

        var prs = new List<PullRequestEvidence>();
        foreach (var (prId, linkReason) in linkedPrIds)
        {
            try
            {
                var pr = await FetchPullRequestAsync(prId, repoId, workItemId.ToString(), linkReason, ct);
                prs.Add(pr);
            }
            catch (AzureDevOpsApiException ex)
            {
                _logger.LogWarning("ADO fetch failed for PR {PrId}: {StatusCode}", prId, ex.StatusCode);
            }
        }

        var confidence = prs.Count > 0
            ? (linkedPrIds.Any(p => p.Reason == EvidenceLinkReason.WorkItemRelation)
                ? EvidenceConfidence.Confirmed
                : EvidenceConfidence.Likely)
            : EvidenceConfidence.Missing;

        return new TaskImplementationEvidence
        {
            ExternalId   = workItemId.ToString(),
            DisplayTitle = wi.Title,
            Source       = "AzureDevOps",
            SourceUrl    = BuildWorkItemUrl(workItemId),
            State        = wi.State,
            AssignedTo   = wi.AssignedTo,
            WorkItemType = wi.WorkItemType,
            Confidence   = confidence,
            PullRequests = prs,
        };
    }

    // ── PR ─────────────────────────────────────────────────────────────────

    private async Task<PullRequestEvidence> FetchPullRequestAsync(
        int prId, string repoId, string taskId, EvidenceLinkReason linkReason, CancellationToken ct)
    {
        _logger.LogInformation("Fetching PR {PrId}", prId);

        var prUrl = $"{_options.OrganizationUrl}/{Uri.EscapeDataString(_options.Project)}" +
                    $"/_apis/git/repositories/{Uri.EscapeDataString(repoId)}/pullrequests/{prId}?api-version=7.1";

        var prJson   = await GetAsync(prUrl, ct);
        var prData   = ParsePullRequest(prJson, prId);

        // If link reason was not from work item relation, try to upgrade via PR metadata.
        var resolvedReason = linkReason;
        if (linkReason != EvidenceLinkReason.WorkItemRelation)
        {
            if (prData.Title.Contains(taskId) || prData.Description?.Contains(taskId) == true)
                resolvedReason = EvidenceLinkReason.PrTitle;
            else if (prData.SourceBranch.Contains(taskId))
                resolvedReason = EvidenceLinkReason.BranchName;
        }

        var commits = await FetchPrCommitsAsync(prId, repoId, ct);

        // Prefer PR iteration changes; fall back to commit changes.
        List<ChangedFileEvidence> changedFiles;
        try
        {
            changedFiles = await FetchPrChangesAsync(prId, repoId, ct);
        }
        catch
        {
            changedFiles = await FetchCommitChangesAsync(commits, repoId, prId.ToString(), ct);
        }

        // Check commit messages for task ID if we haven't confirmed yet.
        if (resolvedReason == EvidenceLinkReason.BranchName &&
            commits.Any(c => c.DisplayTitle.Contains(taskId)))
            resolvedReason = EvidenceLinkReason.CommitMessage;

        return new PullRequestEvidence
        {
            ExternalId   = prId.ToString(),
            DisplayTitle = prData.Title,
            Source       = "AzureDevOps",
            SourceUrl    = BuildPrUrl(repoId, prId),
            Status       = prData.Status,
            SourceBranch = prData.SourceBranch,
            TargetBranch = prData.TargetBranch,
            CreatedBy    = prData.CreatedBy,
            CreatedDate  = prData.CreatedDate,
            ClosedDate   = prData.ClosedDate,
            MergeCommitId = prData.MergeCommitId,
            LinkReason   = resolvedReason,
            Commits      = commits,
            ChangedFiles = changedFiles,
        };
    }

    // ── Commits ────────────────────────────────────────────────────────────

    private async Task<List<CommitEvidence>> FetchPrCommitsAsync(
        int prId, string repoId, CancellationToken ct)
    {
        var url = $"{_options.OrganizationUrl}/{Uri.EscapeDataString(_options.Project)}" +
                  $"/_apis/git/repositories/{Uri.EscapeDataString(repoId)}/pullrequests/{prId}/commits?api-version=7.1";

        var json = await GetAsync(url, ct);
        using var doc = JsonDocument.Parse(json);

        var commits = new List<CommitEvidence>();
        if (!doc.RootElement.TryGetProperty("value", out var values)) return commits;

        foreach (var c in values.EnumerateArray())
        {
            var commitId = c.GetStringOrEmpty("commitId");
            var comment  = c.GetStringOrEmpty("comment");
            commits.Add(new CommitEvidence
            {
                ExternalId   = commitId,
                DisplayTitle = comment.Length > 100 ? comment[..100] : comment,
                Source       = "AzureDevOps",
                SourceUrl    = BuildCommitUrl(repoId, commitId),
                Author       = c.TryGetProperty("author", out var author)
                                   ? author.GetStringOrEmpty("email")
                                   : null,
                Date         = c.TryGetProperty("author", out var a2) && a2.TryGetProperty("date", out var d)
                                   ? d.GetDateTime()
                                   : null,
            });
        }

        return commits;
    }

    // ── Changed files via PR iterations ────────────────────────────────────

    private async Task<List<ChangedFileEvidence>> FetchPrChangesAsync(
        int prId, string repoId, CancellationToken ct)
    {
        // Get latest iteration id
        var iterUrl = $"{_options.OrganizationUrl}/{Uri.EscapeDataString(_options.Project)}" +
                      $"/_apis/git/repositories/{Uri.EscapeDataString(repoId)}/pullrequests/{prId}/iterations?api-version=7.1";

        var iterJson = await GetAsync(iterUrl, ct);
        using var iterDoc = JsonDocument.Parse(iterJson);

        if (!iterDoc.RootElement.TryGetProperty("value", out var iters) || iters.GetArrayLength() == 0)
            throw new InvalidOperationException("No PR iterations found");

        var latestIter = iters.EnumerateArray().Last();
        var iterId     = latestIter.GetProperty("id").GetInt32();

        var changesUrl = $"{_options.OrganizationUrl}/{Uri.EscapeDataString(_options.Project)}" +
                         $"/_apis/git/repositories/{Uri.EscapeDataString(repoId)}/pullrequests/{prId}" +
                         $"/iterations/{iterId}/changes?api-version=7.1";

        var changesJson = await GetAsync(changesUrl, ct);
        return ParseChangedFiles(changesJson, null, prId.ToString());
    }

    // ── Changed files via commit changes (fallback) ─────────────────────────

    private async Task<List<ChangedFileEvidence>> FetchCommitChangesAsync(
        List<CommitEvidence> commits, string repoId, string prId, CancellationToken ct)
    {
        var allFiles = new List<ChangedFileEvidence>();

        foreach (var commit in commits.Take(20)) // guard against giant PRs
        {
            var url = $"{_options.OrganizationUrl}/{Uri.EscapeDataString(_options.Project)}" +
                      $"/_apis/git/repositories/{Uri.EscapeDataString(repoId)}/commits/{commit.ExternalId}/changes?api-version=7.1";

            try
            {
                var json = await GetAsync(url, ct);
                allFiles.AddRange(ParseChangedFiles(json, commit.ExternalId, prId));
            }
            catch (AzureDevOpsApiException ex)
            {
                _logger.LogWarning("Failed to fetch changes for commit {CommitId}: {StatusCode}",
                    commit.ExternalId, ex.StatusCode);
            }
        }

        return allFiles
            .GroupBy(f => f.Path)
            .Select(g => g.Last())
            .ToList();
    }

    // ── HTTP helper ─────────────────────────────────────────────────────────

    private async Task<string> GetAsync(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("ADO API returned {StatusCode} for URL pattern (details in debug logs)",
                (int)response.StatusCode);
            throw new AzureDevOpsApiException(response.StatusCode);
        }

        return await response.Content.ReadAsStringAsync(ct);
    }

    // ── Auth ────────────────────────────────────────────────────────────────

    private void ConfigureAuth()
    {
        // Basic auth: ":" + PAT, base64-encoded. Never log the PAT.
        var encoded = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{_options.Pat}"));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", encoded);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    // ── Parse helpers ───────────────────────────────────────────────────────

    private static WorkItemData ParseWorkItem(string json, int id)
    {
        using var doc    = JsonDocument.Parse(json);
        var fields       = doc.RootElement.GetProperty("fields");
        var relations    = doc.RootElement.TryGetProperty("relations", out var r) ? r : default;

        var relationList = new List<WorkItemRelation>();
        if (relations.ValueKind == JsonValueKind.Array)
        {
            foreach (var rel in relations.EnumerateArray())
            {
                var url  = rel.GetStringOrEmpty("url");
                var attrs = rel.TryGetProperty("attributes", out var a) ? a : default;
                var name = attrs.ValueKind != JsonValueKind.Undefined ? attrs.GetStringOrEmpty("name") : string.Empty;
                relationList.Add(new WorkItemRelation(url, name));
            }
        }

        return new WorkItemData(
            Id:          id,
            Title:       fields.GetStringOrEmpty("System.Title"),
            State:       fields.GetStringOrEmpty("System.State"),
            AssignedTo:  fields.TryGetProperty("System.AssignedTo", out var at)
                             ? at.GetStringOrEmpty("displayName")
                             : null,
            WorkItemType: fields.GetStringOrEmpty("System.WorkItemType"),
            Relations:   relationList);
    }

    private static List<(int PrId, EvidenceLinkReason Reason)> ExtractLinkedPrIds(
        IReadOnlyList<WorkItemRelation> relations)
    {
        var results = new List<(int, EvidenceLinkReason)>();

        foreach (var rel in relations)
        {
            // ADO PR artifact links look like:
            // vstfs:///Git/PullRequestId/{prId}  or
            // https://dev.azure.com/{org}/{proj}/_git/{repo}/pullrequest/{prId}
            var m = PrIdFromRelationUrl().Match(rel.Url);
            if (m.Success && int.TryParse(m.Groups["id"].Value, out var prId))
                results.Add((prId, EvidenceLinkReason.WorkItemRelation));
        }

        return results;
    }

    private static PullRequestData ParsePullRequest(string json, int prId)
    {
        using var doc  = JsonDocument.Parse(json);
        var root       = doc.RootElement;

        var mergeCommit = root.TryGetProperty("lastMergeCommit", out var lmc)
            ? lmc.GetStringOrEmpty("commitId")
            : null;

        var createdBy = root.TryGetProperty("createdBy", out var cb)
            ? cb.GetStringOrEmpty("displayName")
            : null;

        DateTime? createdDate = root.TryGetProperty("creationDate", out var cd) && cd.ValueKind != JsonValueKind.Null
            ? cd.GetDateTime()
            : null;

        DateTime? closedDate = root.TryGetProperty("closedDate", out var cl) && cl.ValueKind != JsonValueKind.Null
            ? cl.GetDateTime()
            : null;

        return new PullRequestData(
            Id:           prId,
            Title:        root.GetStringOrEmpty("title"),
            Description:  root.TryGetProperty("description", out var desc) ? desc.GetString() : null,
            Status:       root.GetStringOrEmpty("status"),
            SourceBranch: StripRefPrefix(root.GetStringOrEmpty("sourceRefName")),
            TargetBranch: StripRefPrefix(root.GetStringOrEmpty("targetRefName")),
            CreatedBy:    createdBy,
            CreatedDate:  createdDate,
            ClosedDate:   closedDate,
            MergeCommitId: mergeCommit);
    }

    private static List<ChangedFileEvidence> ParseChangedFiles(string json, string? commitId, string prId)
    {
        using var doc  = JsonDocument.Parse(json);
        var root       = doc.RootElement;

        // PR iteration changes use "changeEntries"; commit changes use "changes"
        JsonElement items = default;
        if (root.TryGetProperty("changeEntries", out var ce))       items = ce;
        else if (root.TryGetProperty("changes", out var ch))        items = ch;

        var files = new List<ChangedFileEvidence>();
        if (items.ValueKind != JsonValueKind.Array) return files;

        foreach (var item in items.EnumerateArray())
        {
            var changeType = item.GetStringOrEmpty("changeType");
            if (!item.TryGetProperty("item", out var itemEl)) continue;

            var path = itemEl.GetStringOrEmpty("path");
            if (string.IsNullOrWhiteSpace(path)) continue;

            var objectId = itemEl.TryGetProperty("objectId", out var oid) ? oid.GetString() : null;

            var category = ClassifyFile(path);

            files.Add(new ChangedFileEvidence
            {
                Path          = path,
                ChangeType    = NormalizeChangeType(changeType),
                ObjectId      = objectId,
                CommitId      = commitId,
                PullRequestId = prId,
                Category      = category,
                RelatedTestFile   = category == FileCategory.Source ? DeriveExpectedTestFile(path) : null,
                HasTestEvidence   = false, // resolved later when all files are known
            });
        }

        return files;
    }

    // ── File classification ─────────────────────────────────────────────────

    internal static FileCategory ClassifyFile(string path)
    {
        // Migration before Source — Migrations live under src/ but are not generic source files.
        if (IsMatch(path, @"(?i)(^|/)migrations?/"))
            return FileCategory.Migration;

        if (IsMatch(path, @"(?i)(^|/)tests?/.*|.*Tests?\.(cs|ts|tsx)$|.*\.test\.(ts|tsx)$|.*\.spec\.(ts|tsx)$"))
            return FileCategory.Test;

        if (IsMatch(path, @"(?i)(^|/)src/.*\.(cs|razor|ts|tsx)$"))
            return FileCategory.Source;

        if (IsMatch(path, @"(?i)appsettings.*\.json$|.*\.(ya?ml)$|docker-compose.*\.ya?ml$|^dockerfile$"))
            return FileCategory.Configuration;

        if (IsMatch(path, @"(?i)(^|/)docs/.*|.*\.md$"))
            return FileCategory.Documentation;

        return FileCategory.Unknown;
    }

    // ── Test evidence detection ─────────────────────────────────────────────

    internal static List<TestEvidenceItem> BuildTestEvidence(List<ChangedFileEvidence> allFiles)
    {
        var testFileNames = allFiles
            .Where(f => f.Category == FileCategory.Test)
            .Select(f => System.IO.Path.GetFileName(f.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return allFiles
            .Where(f => f.Category == FileCategory.Source)
            .Select(f =>
            {
                var expectedTest = DeriveExpectedTestFile(f.Path);
                var expectedName = System.IO.Path.GetFileName(expectedTest);
                var hasTest      = testFileNames.Contains(expectedName ?? string.Empty);

                return new TestEvidenceItem
                {
                    SourceFile       = f.Path,
                    ExpectedTestFile = expectedTest,
                    HasTest          = hasTest,
                    FoundTestFile    = hasTest ? expectedTest : null,
                    PullRequestId    = f.PullRequestId,
                };
            })
            .ToList();
    }

    // Produce a list of files changed outside the context of any recognized PR.
    private static List<ChangedFileEvidence> BuildUnmappedChanges(
        List<ChangedFileEvidence> allFiles,
        HashSet<string> allPrIds) =>
        allFiles.Where(f => f.PullRequestId is null || !allPrIds.Contains(f.PullRequestId)).ToList();

    // ── Gap detection ───────────────────────────────────────────────────────

    private static List<TraceabilityGapItem> BuildGaps(List<TaskImplementationEvidence> tasks)
    {
        var gaps = new List<TraceabilityGapItem>();

        foreach (var task in tasks)
        {
            if (task.Confidence == EvidenceConfidence.Missing)
                gaps.Add(new TraceabilityGapItem
                {
                    Description       = $"Work item {task.ExternalId} has no linked pull requests.",
                    RelatedExternalId = task.ExternalId,
                    GapKind           = "NoPullRequests",
                });

            foreach (var pr in task.PullRequests)
            {
                var sourceWithoutTest = pr.ChangedFiles
                    .Where(f => f.Category == FileCategory.Source && !f.HasTestEvidence);

                foreach (var f in sourceWithoutTest)
                    gaps.Add(new TraceabilityGapItem
                    {
                        Description       = $"Source file '{f.Path}' has no test evidence in PR {pr.ExternalId}.",
                        RelatedExternalId = pr.ExternalId,
                        GapKind           = "MissingTestEvidence",
                    });
            }
        }

        return gaps;
    }

    // ── String / URL helpers ────────────────────────────────────────────────

    private string BuildWorkItemUrl(int id) =>
        $"{_options.OrganizationUrl}/{Uri.EscapeDataString(_options.Project)}/_workitems/edit/{id}";

    private string BuildPrUrl(string repoId, int prId) =>
        $"{_options.OrganizationUrl}/{Uri.EscapeDataString(_options.Project)}/_git/{Uri.EscapeDataString(repoId)}/pullrequest/{prId}";

    private string BuildCommitUrl(string repoId, string commitId) =>
        $"{_options.OrganizationUrl}/{Uri.EscapeDataString(_options.Project)}/_git/{Uri.EscapeDataString(repoId)}/commit/{commitId}";

    private static string StripRefPrefix(string refName) =>
        refName.StartsWith("refs/heads/", StringComparison.Ordinal) ? refName[11..] : refName;

    private static string NormalizeChangeType(string raw) => raw.ToLowerInvariant() switch
    {
        "add"    or "1" => "add",
        "edit"   or "2" => "edit",
        "delete" or "3" => "delete",
        "rename" or "4" => "rename",
        _               => raw,
    };

    internal static string? DeriveExpectedTestFile(string sourcePath)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(sourcePath);
        var ext  = System.IO.Path.GetExtension(sourcePath);
        return ext switch
        {
            ".cs"  => $"tests/Unit/{name}Tests.cs",
            ".ts"  => $"tests/{name}.test.ts",
            ".tsx" => $"tests/{name}.test.tsx",
            _      => null,
        };
    }

    private static bool IsMatch(string input, string pattern) =>
        Regex.IsMatch(input, pattern, RegexOptions.None, TimeSpan.FromMilliseconds(200));

    [GeneratedRegex(@"(?i)pullrequest[s]?[/\\](?<id>\d+)|PullRequestId[/\\](?<id>\d+)", RegexOptions.Compiled)]
    private static partial Regex PrIdFromRelationUrl();

    // ── Private record types (ADO response shapes) ──────────────────────────

    private sealed record WorkItemData(
        int Id,
        string Title,
        string State,
        string? AssignedTo,
        string WorkItemType,
        IReadOnlyList<WorkItemRelation> Relations);

    private sealed record WorkItemRelation(string Url, string Name);

    private sealed record PullRequestData(
        int Id,
        string Title,
        string? Description,
        string Status,
        string SourceBranch,
        string TargetBranch,
        string? CreatedBy,
        DateTime? CreatedDate,
        DateTime? ClosedDate,
        string? MergeCommitId);
}

// ── JsonElement extension ───────────────────────────────────────────────────

internal static class JsonElementExtensions
{
    internal static string GetStringOrEmpty(this JsonElement el, string propertyName) =>
        el.TryGetProperty(propertyName, out var prop) ? prop.GetString() ?? string.Empty : string.Empty;
}

// ── Custom exception ────────────────────────────────────────────────────────

public sealed class AzureDevOpsApiException(HttpStatusCode statusCode)
    : Exception($"Azure DevOps API returned {(int)statusCode}")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}
