using BirkNext.ApiReview;
using BirkNext.RuntimeSecurity;
using BirkNext.TechnicalHistory;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>What a specialist review stores on the server: its kind, environment, time, outcome and counts, with the report kept as produced.</summary>
public sealed class TechnicalReviewArchiveTests
{
    [Fact]
    public void Fuzzing_and_authorization_runs_are_summarised_without_reinterpreting_them()
    {
        var at = new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);
        var fuzz = TechnicalReviewArchive.Fuzzing("import:lendo", new ApiFuzzingReport
        {
            EnvironmentId = "env-qa", EnvironmentName = "QA", StartedAt = at.AddMinutes(-2), CompletedAt = at, CasesPlanned = 12, CasesExecuted = 10,
            Completeness = ApiFuzzCompleteness.Partial, Findings = [new()],
        });
        fuzz.Should().Match<StoreTechnicalReviewRunRequest>(r => r.Kind == TechnicalReviewKinds.SafeFuzzing && r.ProjectKey == "import:lendo" && r.EnvironmentId == "env-qa"
            && r.GeneratedAt == at && r.Outcome == "Partial" && r.Checks == 10 && r.Problems == 1);
        fuzz.Report.GetProperty("casesPlanned").GetInt32().Should().Be(12, "the report is kept as produced");

        var authz = TechnicalReviewArchive.Authorization(null, new AuthorizationRunReport
        {
            EnvironmentId = "env-qa", EnvironmentName = "QA", StartedAt = at.AddMinutes(-1), CompletedAt = at,
            Observations = [new() { Outcome = AuthorizationOutcome.VerifiedDeny }, new() { Outcome = AuthorizationOutcome.UnexpectedAllow }],
        });
        authz.Should().Match<StoreTechnicalReviewRunRequest>(r => r.Kind == TechnicalReviewKinds.Authorization && r.Outcome == "Unexpected access" && r.Checks == 2 && r.Problems == 1);
    }
}
