using BirkNext.TestPlanning;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Manual Testing reporting (the gap analysis found no way to hand run results to anyone): every run as a CSV row a spreadsheet opens safely, and the
/// stale "last run" flag uses the same case fingerprint the backend pins on a run.
/// </summary>
public sealed class ManualRunReportTests
{
    private static ManualRunSummary Run(string title, ManualRunOutcome outcome, Guid? retestOf = null, string? env = "QA") =>
        new(Guid.NewGuid(), Draft, "c1", $"{Draft:N}/c1", title, 3, "fp", ManualRunState.Completed, outcome, "Kari", env,
            new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 16, 9, 30, 0, TimeSpan.Zero), 4, 2, 1, 1, 0, 0, 1, 1, 2, retestOf);

    private static readonly Guid Draft = Guid.NewGuid();

    [Fact]
    public void Every_run_is_a_row_with_tester_words_and_the_retest_marked()
    {
        var failed = Run("Borrow a book", ManualRunOutcome.Failed);
        var csv = ManualTestingPresentation.RunResultsCsv([failed, Run("Borrow a book", ManualRunOutcome.Passed, retestOf: failed.Id)], new Dictionary<Guid, string> { [Draft] = "Library plan" });
        var lines = csv.TrimStart('﻿').Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        csv.Should().StartWith("﻿", "Excel reads UTF-8 (æ, ø, å) only with a byte-order mark");
        lines[0].Should().Be(string.Join(",", ManualTestingPresentation.RunReportColumns.Select(c => $"\"{c}\"")));
        lines.Should().HaveCount(3);
        lines.Skip(1).Should().OnlyContain(l => l.StartsWith("\"Borrow a book\",\"Library plan\",\"3\","));
        lines.Count(l => l.EndsWith(",\"Yes\"")).Should().Be(1, "only the retest is marked");
        csv.Should().NotContain(Draft.ToString(), "internal ids are not report text");
    }

    [Fact]
    public void A_cell_a_spreadsheet_would_run_as_a_formula_is_neutralised_and_quotes_are_escaped()
    {
        var csv = ManualTestingPresentation.RunResultsCsv([Run("=HYPERLINK(\"x\")", ManualRunOutcome.Failed, env: "@QA")], new Dictionary<Guid, string>());

        csv.Should().Contain("\"'=HYPERLINK(\"\"x\"\")\"").And.Contain("\"'@QA\"");
    }

    [Fact]
    public void The_stale_flag_uses_the_same_fingerprint_the_run_was_pinned_with()
    {
        var c = new TestCaseDraft { Source = new ScenarioReference { Kind = ScenarioSourceKind.Manual, ScenarioId = "c1" }, Generated = new TestCaseContent { Title = "T", Steps = ["a"] } };
        var edited = c with { Generated = c.Generated with { Steps = ["a", "b"] } };

        ManualRunRules.CaseFingerprint(c).Should().Be(ManualRunRules.CaseFingerprint(c with { }), "deterministic");
        ManualRunRules.CaseFingerprint(edited).Should().NotBe(ManualRunRules.CaseFingerprint(c), "a changed case makes its last run stale");
    }

    [Fact]
    public void The_run_history_step_summary_lists_only_the_outcomes_that_occurred()
    {
        ManualTestingPresentation.StepCounts(Run("A", ManualRunOutcome.Failed) with { Passed = 1, Failed = 1, Blocked = 0, NotRun = 0, Undecided = 0 }).Should().Be("1 pass · 1 fail");
        ManualTestingPresentation.StepCounts(Run("B", ManualRunOutcome.NotDecided) with { Passed = 0, Failed = 0, Blocked = 2, NotRun = 1, Undecided = 3 }).Should().Be("2 blocked · 1 not run · 3 undecided");
        ManualTestingPresentation.StepCounts(Run("C", ManualRunOutcome.NotDecided) with { Passed = 0, Failed = 0, Blocked = 0, NotRun = 0, Undecided = 0 }).Should().Be("No step result");
    }
}
