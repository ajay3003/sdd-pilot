using System.Reflection;
using System.Text.Json;
using BirkNext.AiCodeReview;
using BirkNext.Api.Controllers;
using BirkNext.Api.Models;
using BirkNext.Api.Services.AiCodeReview;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Dependencies;
using BirkNext.SourceEvidence;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace BirkNext.Api.Tests.Services.AiCodeReview;

/// <summary>
/// Code Risk Review (formerly AI-Generated Code Review): the run records analyzer language coverage and the evidence it consumed (Dependency
/// Review runs by exact snapshot), stored rule ids stay AIC-* while CRR-* is shown, the old API route keeps working, and legacy results load.
/// </summary>
public sealed class CodeRiskReviewTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ARunRecordsLanguageCoverageTasksReadinessAndNeverAssumesNoVulnerabilities()
    {
        await using var db = AiCodeReviewFixture.Db();
        var current = await AiCodeReviewFixture.AnalyzeAsync(db, "Shop.zip", AiCodeReviewFixture.Current());

        var review = AiCodeReviewEngine.Review(current, null, new AiCodeReviewRequest { CurrentSnapshotId = current.Id }, DateTimeOffset.UtcNow);

        review.Languages.Should().NotBeNull().And.Contain(l => l.Language == "C#" && l.Supported);
        review.Readiness.Should().Contain(i => i.Key == "tasks" && !i.Available);
        var dep3 = review.Rules.Single(r => r.RuleId == "AIC-DEP-003");
        dep3.State.Should().Be(AiRuleExecutionState.NotAssessed);
        dep3.Reason.Should().Contain("No vulnerability is assumed");
        JsonSerializer.Serialize(review).Should().NotContain("generated change", "finding text never assumes the code was generated");
    }

    private static DependencyReviewRunRecord DependencyRun(DateTimeOffset at, params (Guid Snapshot, string Repository)[] reviewed) => new()
    {
        Id = Guid.NewGuid(), CompletedAt = at, Label = "deps",
        ResultJson = JsonSerializer.Serialize(new DependencyReviewResult
        {
            RunId = Guid.NewGuid(), CompletedAt = at,
            SourceScope = new ReviewSourceScope
            {
                Primary = new SourceScopeEntry { SnapshotId = reviewed[0].Snapshot, RepositoryKey = reviewed[0].Repository },
                Related = [.. reviewed.Skip(1).Select(r => new SourceScopeEntry { SnapshotId = r.Snapshot, RepositoryKey = r.Repository })],
            },
        }, Json),
    };

    [Fact]
    public async Task DependencyReviewIsConsumedByExactSnapshot_OtherwisePotentiallyStale_OtherwiseMissing()
    {
        await using var db = AiCodeReviewFixture.Db();
        var current = await AiCodeReviewFixture.AnalyzeAsync(db, "Shop.zip", AiCodeReviewFixture.Current());
        var repository = ReviewSourceEvidenceProvider.Identity(current).Key;
        var service = new AiCodeReviewService(db, new ReviewSourceEvidenceProvider(new IqrSourceStore(db)));

        (await service.DependencyReviewCoverageAsync(current, CancellationToken.None)).Should().Match<AiReadinessItem>(i => !i.Available && !i.Stale);

        db.DependencyReviewRuns.Add(DependencyRun(DateTimeOffset.UtcNow.AddDays(-3), (Guid.NewGuid(), repository)));
        db.DependencyReviewRuns.Add(DependencyRun(DateTimeOffset.UtcNow.AddDays(-1), (Guid.NewGuid(), "another-repository")));
        await db.SaveChangesAsync();
        var stale = await service.DependencyReviewCoverageAsync(current, CancellationToken.None);
        (stale.Available, stale.Stale).Should().Be((true, true));
        stale.Detail.Should().Contain("another snapshot of this repository");

        db.DependencyReviewRuns.Add(DependencyRun(DateTimeOffset.UtcNow.AddDays(-2), (Guid.NewGuid(), "x"), (current.Id, repository)));
        await db.SaveChangesAsync();
        var exact = await service.DependencyReviewCoverageAsync(current, CancellationToken.None);
        (exact.Available, exact.Stale).Should().Be((true, false));
        exact.Provenance.Should().StartWith("Dependency Review run ");
    }

    [Fact]
    public async Task AStoredRunCarriesTheConsumedDependencyReviewCoverage()
    {
        await using var db = AiCodeReviewFixture.Db();
        var current = await AiCodeReviewFixture.AnalyzeAsync(db, "Shop.zip", AiCodeReviewFixture.Current());
        var service = new AiCodeReviewService(db, new ReviewSourceEvidenceProvider(new IqrSourceStore(db)));

        var outcome = await service.RunAsync(new AiCodeReviewRequest { CurrentSnapshotId = current.Id });

        outcome.Result!.Readiness.Should().Contain(i => i.Key == "dependency-review");
        (await service.GetAsync(outcome.Result.RunId))!.Readiness.Select(i => i.Key).Should().Contain("dependency-review");
    }

    [Fact]
    public void RuleIdsAreStoredAsAicAndDisplayedAsCrr()
    {
        AiCodeReviewText.Title.Should().Be("Code Risk Review");
        AiCodeReviewText.Hero.Should().NotContainEquivalentOf("AI-assisted").And.NotContainEquivalentOf("generated");
        AiCodeReviewText.DisplayId("AIC-SEC-003").Should().Be("CRR-SEC-003");
        AiCodeReviewText.DisplayId("AIC-PLACEHOLDER-004").Should().Be("CRR-PLACEHOLDER-004");
        AiCodeReviewText.StoredId("crr-sec-003").Should().Be("AIC-SEC-003");
        AiCodeReviewText.StoredId("AIC-VAL-001").Should().Be("AIC-VAL-001");
        AiCodeReviewEngine.Catalogue.Should().OnlyContain(r => r.Id.StartsWith("AIC-"), "stored identity is unchanged, so historical runs and links stay valid");
    }

    [Fact]
    public void TheOriginalApiRouteStaysValidBesideTheNewOne()
    {
        typeof(AiCodeReviewController).GetCustomAttributes<RouteAttribute>().Select(r => r.Template).Should().BeEquivalentTo(["api/ai-code-review", "api/code-risk-review"]);
    }

    [Fact]
    public async Task ALegacyStoredResultStaysReadable()
    {
        await using var db = AiCodeReviewFixture.Db();
        var runId = Guid.NewGuid();
        // A run stored before the rename: AIC ids, the old disclaimer, no language coverage and no tasks / dependency-review readiness.
        db.AiCodeReviewRuns.Add(new AiCodeReviewRunRecord
        {
            Id = runId, CompletedAt = DateTimeOffset.UtcNow.AddDays(-5), CurrentSnapshotId = Guid.NewGuid(), Label = "Shop · current snapshot · 1 finding(s)",
            ResultJson = """
                {"runId":"RUNID","mode":"CurrentSnapshot","scope":"EntireSource","engineVersion":1,
                 "current":{"snapshotId":"aaaaaaaa-0000-0000-0000-000000000001","repository":"Shop","archiveName":"Shop.zip","fingerprint":"abc","analyzedAt":"2026-10-01T00:00:00Z","hasCodeRiskEvidence":true},
                 "readiness":[{"key":"source","label":"Source snapshot","available":true,"detail":"Shop.zip"}],
                 "rules":[{"ruleId":"AIC-VAL-001","category":"ValidationErrorHandling","title":"Empty catch block","state":"Executed","findings":1,"sources":["Source"]}],
                 "findings":[{"logicalId":"x","ruleId":"AIC-VAL-001","category":"ValidationErrorHandling","title":"Empty catch block","severity":"Low"}],
                 "disclaimer":"This review does not detect whether code was written by AI."}
                """.Replace("RUNID", runId.ToString()),
        });
        await db.SaveChangesAsync();
        var service = new AiCodeReviewService(db, new ReviewSourceEvidenceProvider(new IqrSourceStore(db)));

        var legacy = await service.GetAsync(runId);
        legacy!.Findings.Single().RuleId.Should().Be("AIC-VAL-001");
        AiCodeReviewText.DisplayId(legacy.Findings.Single().RuleId).Should().Be("CRR-VAL-001");
        legacy.Languages.Should().BeNull("older runs did not record language coverage; nothing is invented");
        (await service.HistoryAsync()).Should().ContainSingle(h => h.RunId == runId && h.Findings == 1);
    }
}
