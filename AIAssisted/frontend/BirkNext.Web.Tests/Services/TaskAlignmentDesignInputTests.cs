using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Group 1 (NEXT43 open items): Task Alignment built its context from Specification and Tasks only, so "design artifact links" were always 0 even
/// when tasks cite Constitution rules (M2LB: "PS-08", "PP-05"). The selected Constitution and Data Model are now part of the context. Task text
/// keeps references that are part of authored prose.
/// </summary>
public sealed class TaskAlignmentDesignInputTests
{
    private const string Spec = """
        # Feature Specification: Logging

        ## Requirements

        ### Functional Requirements

        - **FR-001**: The service MUST log in JSON.
        """;

    private const string Tasks = """
        # Tasks: Logging

        ## Phase 1: Setup

        - [x] T004 Implement KorrelasjonsIdMiddleware that reads X-Korrelasjon-Id and stores it in the log context (PS-08)
        - [x] T005 Configure Serilog masking for Foedselsnummer and Navn fields (FR-001, PS-08)
        """;

    private const string Constitution = """
        # Personservice Constitution

        ## Core Principles

        ### PP-05: No hard deletes
        Records are never physically deleted.

        ## Platform Standards

        ### PS-08: No personal data in logs
        Personal data is masked before it is logged.
        """;

    private static AlignmentReport Analyse(string? constitution)
    {
        var parser = new ArtifactParserService(new ConstitutionAnalysisService(), new PlanAnalysisService());
        return new TaskSpecAlignmentService().Analyse(TaskAlignmentContext.Build(parser, constitution, Spec, Tasks, null, null));
    }

    [Fact]
    public void A_task_citing_a_Constitution_rule_is_a_design_artifact_link_once_the_Constitution_is_part_of_the_context()
    {
        var withoutConstitution = Analyse(null).Findings.Single(f => f.TaskId == "T004");
        withoutConstitution.DesignLinks.Should().BeEmpty("without the Constitution there is nothing to match — the old behaviour for every run");

        var t004 = Analyse(Constitution).Findings.Single(f => f.TaskId == "T004");
        t004.DesignLinks.Should().ContainSingle(d => d.ArtifactType == "Constitution" && d.Name == "PS-08");
    }

    [Fact]
    public void Task_text_keeps_references_that_are_part_of_the_authored_sentence()
    {
        var parser = new ArtifactParserService(new ConstitutionAnalysisService(), new PlanAnalysisService());
        var context = TaskAlignmentContext.Build(parser, null, Spec, Tasks, null, null);
        var t005 = context.GetTasks().Single(t => t.Id == "T005");

        (t005.Title + " " + t005.Description).Should().Contain("(FR-001, PS-08)", "removing FR-001 left \"(, PS-08)\" and changed the authored text");
    }

    [Fact]
    public void The_page_keeps_every_count_and_finding_field_of_the_report()
    {
        // The page normalizes the report before display; it dropped DesignLinkedTasks (card read 0 with 11 design-linked tasks on M2LB Person)
        // and each finding's design links, purpose and deviation.
        var report = Analyse(Constitution);
        var shown = BirkNext.Web.Pages.TaskToSpecAlignment.NormalizeReport(report);

        shown.DesignLinkedTasks.Should().Be(report.DesignLinkedTasks).And.BeGreaterThan(0);
        shown.TestImplementationTasks.Should().Be(report.TestImplementationTasks);
        (shown.LinkedTasks + shown.DesignLinkedTasks + shown.TechnicalOnlyTasks + shown.NeedsReviewTasks + shown.PossibleDeviations + shown.TestImplementationTasks)
            .Should().BeLessThanOrEqualTo(shown.TotalTasks);
        shown.Findings.Single(f => f.TaskId == "T004").DesignLinks.Should().ContainSingle(d => d.Name == "PS-08");
    }
}
