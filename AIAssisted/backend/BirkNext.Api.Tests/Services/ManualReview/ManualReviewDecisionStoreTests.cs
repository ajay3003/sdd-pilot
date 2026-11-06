using BirkNext.Api.Data;
using BirkNext.Api.Services.ManualReview;
using BirkNext.ManualReview;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Tests.Services.ManualReview;

/// <summary>
/// Durable manual review decisions: append-only (a new decision supersedes, the old one stays as history), reloadable from a new context,
/// scoped by module + scope + subject, and never carrying a credential. A decision is a person's judgement, not an automated result.
/// </summary>
public sealed class ManualReviewDecisionStoreTests
{
    private static DbContextOptions<AppDbContext> Options() => new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static ManualReviewDecisionRequest Request(string subject = "SEC-03", ManualReviewDecisionValue decision = ManualReviewDecisionValue.NeedsEvidence, string? note = null,
        string module = ManualReviewModules.SecurityClassification, string scope = "dev", params string[] evidence) => new()
    {
        Module = module, ScopeKey = scope, SubjectKey = subject, Reviewer = "Test lead", Decision = decision, Note = note, EvidenceReferences = [.. evidence],
    };

    [Fact]
    public async Task A_saved_decision_reloads_and_a_new_one_supersedes_it_while_history_is_kept()
    {
        var options = Options();
        var clock = new Clock(new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero));
        await using (var db = new AppDbContext(options))
        {
            var store = new ManualReviewDecisionStore(db, clock);
            (await store.RecordAsync(Request(evidence: "TC-101"))).Error.Should().BeNull();
            clock.Now = clock.Now.AddHours(1);
            (await store.RecordAsync(Request(decision: ManualReviewDecisionValue.IssueFound, note: "Tenant B can read tenant A's child.", evidence: "TC-102"))).Error.Should().BeNull();
        }
        await using (var reloaded = new AppDbContext(options))
        {
            var decisions = await new ManualReviewDecisionStore(reloaded).ListAsync(ManualReviewModules.SecurityClassification, "dev", "SEC-03");
            decisions.Should().HaveCount(2, "append-only: the earlier decision stays as audit history");
            decisions[0].Current.Should().BeTrue();
            decisions[0].Decision.Should().Be(ManualReviewDecisionValue.IssueFound);
            decisions[0].EvidenceReferences.Should().Equal("TC-102");
            decisions[1].Current.Should().BeFalse();
            decisions[1].Decision.Should().Be(ManualReviewDecisionValue.NeedsEvidence);
        }
    }

    [Fact]
    public async Task Decisions_are_scoped_by_module_scope_and_subject()
    {
        await using var db = new AppDbContext(Options());
        var store = new ManualReviewDecisionStore(db);
        await store.RecordAsync(Request("SEC-01"));
        await store.RecordAsync(Request("SEC-01", scope: "qa"));
        await store.RecordAsync(Request("Authorization between roles and tenants", module: ManualReviewModules.ApiQuality));
        (await store.ListAsync(ManualReviewModules.SecurityClassification, "dev")).Should().ContainSingle(d => d.SubjectKey == "SEC-01" && d.Current);
        (await store.ListAsync(ManualReviewModules.ApiQuality, "dev")).Should().ContainSingle(d => d.SubjectKey == "Authorization between roles and tenants");
    }

    [Theory]
    [InlineData("Bearer eyJhbGciOiJIUzI1NiJ9.x.y")]
    [InlineData("access_token=abc")]
    [InlineData("password=hunter2")]
    public async Task A_credential_in_the_evidence_or_note_is_refused_and_nothing_is_stored(string secret)
    {
        await using var db = new AppDbContext(Options());
        var store = new ManualReviewDecisionStore(db);
        var (decision, error) = await store.RecordAsync(Request(evidence: secret));
        decision.Should().BeNull();
        error.Should().Contain("never the secret");
        (await store.RecordAsync(Request(note: $"see {secret}"))).Error.Should().NotBeNull();
        (await db.ManualReviewDecisions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Issue_found_and_risk_accepted_need_an_explanation_and_a_reviewer_is_required()
    {
        await using var db = new AppDbContext(Options());
        var store = new ManualReviewDecisionStore(db);
        (await store.RecordAsync(Request(decision: ManualReviewDecisionValue.RiskAccepted))).Error.Should().Contain("Explain the decision");
        (await store.RecordAsync(Request() with { Reviewer = " " })).Error.Should().Contain("reviewer");
        (await store.RecordAsync(Request() with { Module = "unknown" })).Error.Should().Be("Unknown review module.");
        (await db.ManualReviewDecisions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public void No_decision_value_means_passed()
    {
        Enum.GetNames<ManualReviewDecisionValue>().Should().NotContain(n => n.Contains("Pass", StringComparison.OrdinalIgnoreCase) || n.Contains("Verified", StringComparison.OrdinalIgnoreCase));
        foreach (var value in Enum.GetValues<ManualReviewDecisionValue>())
            ManualReviewRules.Label(value).Should().NotContainAny("Passed", "Verified");
    }
}
