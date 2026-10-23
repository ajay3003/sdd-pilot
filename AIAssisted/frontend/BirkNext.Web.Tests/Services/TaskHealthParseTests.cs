using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// The parsed task tree's health is what the rule packs read (QA Auditor TASK-002/TASK-003, Delivery Readiness). It used to leave testing and
/// link counts at 0 (only the Task Explorer's enriched health set them), so TASK-002 "No testing tasks defined" fired for every task list.
/// </summary>
public sealed class TaskHealthParseTests
{
    private const string Tasks = """
        # Tasks

        ## Phase 1
        - [ ] T001 Implement auth for FR-001
        - [ ] T002 Write unit tests for the auth service
        - [ ] T003 Add Testcontainers integration tests (FR-001)
        - [ ] T004 Tidy the README
        """;

    [Fact]
    public void Parse_counts_testing_and_link_health_the_rules_read()
    {
        var health = TaskExplorerService.Parse(Tasks).Health;

        health.TotalTasks.Should().Be(4);
        health.TestingTasks.Should().Be(2, "plural \"tests\" and \"Testcontainers\" are testing tasks");
        health.FrLinkedTasks.Should().Be(2);
        health.UnlinkedTasks.Should().Be(2);
        health.LinkedTasks.Should().Be(2);
    }

    [Fact]
    public void TASK_002_fires_only_when_a_task_list_really_has_no_testing_task()
    {
        var auditor = new QaAuditorService(new ArtifactTraceabilityService(), new ConstitutionComplianceService());

        auditor.Audit(null, null, null, TaskExplorerService.Parse(Tasks)).Findings.Should().NotContain(f => f.RuleCode == "TASK-002");
        auditor.Audit(null, null, null, TaskExplorerService.Parse("# Tasks\n\n## Phase 1\n- [ ] T001 Implement auth\n")).Findings
            .Should().Contain(f => f.RuleCode == "TASK-002");
    }
}
