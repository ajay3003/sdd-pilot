using BirkNext.IntegrationSuites;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// The integration suite result report: one document naming the journey (with its revision), the source, the environment, every stage with its
/// coverage and blockers, the test data and the runs — from the stored suite only, HTML-encoded, and never claiming a run that was not recorded.
/// </summary>
public sealed class IntegrationSuiteReportTests
{
    private static IntegrationSuite Suite(params SuiteRunLink[] runs) => new()
    {
        Id = Guid.NewGuid(), SuiteKey = "skolenaervaer:m2lb:abc", Revision = 3, GeneratedAt = DateTimeOffset.Parse("2026-11-16T10:00:00Z"), GeneratedBy = "anna",
        Scope = new() { ScopeId = "skolenaervaer", DisplayName = "Altinn → MU → MM" },
        Snapshot = new() { ArchiveFileName = "M2LB.zip", ArchiveSha256 = new string('a', 64), AnalyzedAt = DateTimeOffset.Parse("2026-11-14T00:31:00Z") },
        PreparedForJourneys = [new("project.m2lb:school", 1, "School attendance"), new("project.m2lb:school", 2, "School attendance")],
        Stages =
        [
            new() { Order = 2, StageId = "mu", Label = "MU reception", Component = "MU", Coverage = SuiteStageCoverage.Blocked, Readiness = SuiteItemReadiness.Blocked, Blockers = ["No observer <MU>"] },
            new() { Order = 1, StageId = "altinn", Label = "Altinn submission", Component = "Altinn", ContractTests = ["t1"] },
        ],
        TestData = [new() { Role = "child", Purpose = "Kode 7 child", Asset = "child-k7@v2", Approved = true }],
        Runs = [.. runs], Outcome = runs.Length == 0 ? SuiteExecutionOutcome.NotExecuted : SuiteExecutionOutcome.Failed, Status = SuiteStatus.Blocked, StatusReason = "MU is not observable.",
        Limitations = ["Bufdata is checked manually."],
    };

    [Fact]
    public void The_report_names_journey_revision_source_environment_stages_test_data_and_runs()
    {
        var html = IntegrationSuiteReport.Html(Suite(new SuiteRunLink(Guid.NewGuid(), "composite", DateTimeOffset.Parse("2026-11-16T11:00:00Z"), "anna", "1 failed of 2", [Guid.NewGuid()])),
            "M2LB", "M2LB DEV");

        html.Should().Contain("School attendance (revision 2)", "the latest journey revision the suite was prepared for")
            .And.Contain("Altinn → MU → MM — revision 3").And.Contain("M2LB.zip").And.Contain("M2LB DEV").And.Contain("M2LB")
            .And.Contain("child-k7@v2").And.Contain("1 failed of 2").And.Contain("Bufdata is checked manually.").And.Contain("Nothing was executed to produce this report");
        html.IndexOf("Altinn submission", StringComparison.Ordinal).Should().BeLessThan(html.IndexOf("MU reception", StringComparison.Ordinal), "stages in flow order");
        html.Should().Contain("No observer &lt;MU&gt;").And.NotContain("<MU>", "every value is HTML-encoded");
        html.Should().NotContain("<script", "a self-contained document");
    }

    [Fact]
    public void Without_runs_the_report_says_so_and_never_claims_a_result()
    {
        var html = IntegrationSuiteReport.Html(Suite(), null, null);
        html.Should().Contain("No run has been recorded for this suite.").And.Contain("NotExecuted").And.Contain("not recorded");
        IntegrationSuiteReport.FileName(Suite()).Should().MatchRegex(@"^integration-suite-altinn-mu-mm-r3-\d{4}-\d{2}-\d{2}\.html$");
    }
}
