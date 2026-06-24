namespace BirkNext.Api.Services.ImplementationTraceability;

public sealed class MockImplementationEvidenceProvider : IImplementationEvidenceProvider
{
    public Task<ImplementationTraceabilityReport> FetchAsync(
        IReadOnlyList<int> workItemIds,
        string? repositoryId,
        string? branch,
        CancellationToken ct = default)
    {
        var ids = workItemIds.Count > 0 ? workItemIds : [1001, 1002];

        var tasks = ids.Select((id, i) => BuildMockTask(id, i)).ToList();

        var unmapped = new List<ChangedFileEvidence>
        {
            new() { Path = "src/Infrastructure/Migrations/AddIndexes.cs", ChangeType = "add", Category = FileCategory.Migration },
            new() { Path = "docker-compose.override.yml",                 ChangeType = "edit", Category = FileCategory.Configuration },
        };

        var testEvidence = tasks
            .SelectMany(t => t.PullRequests)
            .SelectMany(pr => pr.ChangedFiles)
            .Where(f => f.Category == FileCategory.Source)
            .Select(f => new TestEvidenceItem
            {
                SourceFile        = f.Path,
                ExpectedTestFile  = DeriveTestFileName(f.Path),
                HasTest           = f.HasTestEvidence,
                FoundTestFile     = f.HasTestEvidence ? DeriveTestFileName(f.Path) : null,
                PullRequestId     = f.PullRequestId,
            })
            .ToList();

        var gaps = BuildGaps(tasks);

        var report = new ImplementationTraceabilityReport
        {
            Tasks          = tasks,
            UnmappedChanges = unmapped,
            TestEvidence   = testEvidence,
            Gaps           = gaps,
            Source         = "Mock",
            StatusMessage  = "Azure DevOps integration is not configured. Showing local/demo evidence.",
        };

        return Task.FromResult(report);
    }

    private static TaskImplementationEvidence BuildMockTask(int id, int index)
    {
        var commits = new List<CommitEvidence>
        {
            new()
            {
                ExternalId   = $"abc{id:x4}1",
                DisplayTitle = $"feat: implement core logic for #{id}",
                Author       = "dev@example.com",
                Date         = DateTime.UtcNow.AddDays(-3 - index),
            },
            new()
            {
                ExternalId   = $"abc{id:x4}2",
                DisplayTitle = $"test: add unit tests for #{id}",
                Author       = "dev@example.com",
                Date         = DateTime.UtcNow.AddDays(-2 - index),
            },
        };

        var changedFiles = new List<ChangedFileEvidence>
        {
            new()
            {
                Path             = $"src/Features/Feature{index + 1}/Feature{index + 1}Service.cs",
                ChangeType       = "edit",
                CommitId         = commits[0].ExternalId,
                PullRequestId    = $"PR-{id}",
                Category         = FileCategory.Source,
                RelatedTestFile  = $"tests/Unit/Feature{index + 1}/Feature{index + 1}ServiceTests.cs",
                HasTestEvidence  = true,
            },
            new()
            {
                Path             = $"tests/Unit/Feature{index + 1}/Feature{index + 1}ServiceTests.cs",
                ChangeType       = "add",
                CommitId         = commits[1].ExternalId,
                PullRequestId    = $"PR-{id}",
                Category         = FileCategory.Test,
            },
            new()
            {
                Path             = $"src/Features/Feature{index + 1}/Feature{index + 1}Controller.cs",
                ChangeType       = "edit",
                CommitId         = commits[0].ExternalId,
                PullRequestId    = $"PR-{id}",
                Category         = FileCategory.Source,
                HasTestEvidence  = false,
            },
        };

        var pr = new PullRequestEvidence
        {
            ExternalId   = $"PR-{id}",
            DisplayTitle = $"#{id}: Implement feature {index + 1}",
            Status       = "completed",
            SourceBranch = $"feature/work-item-{id}",
            TargetBranch = "main",
            CreatedBy    = "dev@example.com",
            CreatedDate  = DateTime.UtcNow.AddDays(-4 - index),
            ClosedDate   = DateTime.UtcNow.AddDays(-1 - index),
            MergeCommitId = $"merge{id:x4}",
            LinkReason   = EvidenceLinkReason.WorkItemRelation,
            Commits      = commits,
            ChangedFiles = changedFiles,
        };

        return new TaskImplementationEvidence
        {
            ExternalId   = id.ToString(),
            DisplayTitle = $"Work Item #{id}: Feature {index + 1} implementation",
            State        = "Done",
            AssignedTo   = "dev@example.com",
            WorkItemType = "Task",
            Confidence   = EvidenceConfidence.Confirmed,
            PullRequests = [pr],
        };
    }

    private static List<TraceabilityGapItem> BuildGaps(List<TaskImplementationEvidence> tasks)
    {
        var gaps = new List<TraceabilityGapItem>();

        foreach (var task in tasks)
        {
            if (task.PullRequests.Count == 0)
                gaps.Add(new TraceabilityGapItem
                {
                    Description      = $"Work item {task.ExternalId} has no linked pull requests.",
                    RelatedExternalId = task.ExternalId,
                    GapKind          = "NoPullRequests",
                });

            foreach (var pr in task.PullRequests)
            {
                var sourceFilesWithoutTests = pr.ChangedFiles
                    .Where(f => f.Category == FileCategory.Source && !f.HasTestEvidence)
                    .ToList();

                foreach (var f in sourceFilesWithoutTests)
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

    private static string DeriveTestFileName(string sourcePath)
    {
        var fileName = System.IO.Path.GetFileNameWithoutExtension(sourcePath);
        var ext      = System.IO.Path.GetExtension(sourcePath);
        return ext switch
        {
            ".cs"  => $"tests/Unit/{fileName}Tests.cs",
            ".ts"  => $"tests/{fileName}.test.ts",
            ".tsx" => $"tests/{fileName}.test.tsx",
            _      => $"tests/{fileName}.test{ext}",
        };
    }
}
