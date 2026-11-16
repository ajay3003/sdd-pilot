using BirkNext.Sdd;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Roadmap R5 (feature-portfolio audit 2026-11-15, register row 9): Task Explorer table rows and Task Changes (Task Delta) normalise requirement
/// references with the shared grammar (<see cref="RequirementReferenceParser"/>), as task bodies and the specification already do. Before, both
/// padded to two digits, so a row naming "FR-1" said "FR-01" and never matched the specification's FR-001.
/// </summary>
public sealed class TaskReferenceGrammarTests
{
    private static IEnumerable<TaskNode> Descendants(IEnumerable<TaskNode> nodes) => nodes.SelectMany(n => new[] { n }.Concat(Descendants(n.Children)));

    [Fact]
    public void A_task_table_row_names_requirements_like_the_specification()
    {
        const string markdown = "# Tasks\n\n## Traceability\n\n| Task | Covers |\n| --- | --- |\n| T001 | FR-1, SC-2 |\n| T002 | FR-001 |\n";
        var rows = Descendants(TaskExplorerService.Parse(markdown).Roots).Where(n => n.NodeType == TaskNodeType.TableRow).ToList();

        rows.Should().HaveCount(2);
        rows[0].ReferencedFrIds.Should().Equal("FR-001");
        rows[0].ReferencedScIds.Should().Equal("SC-002");
        rows[1].ReferencedFrIds.Should().Equal(rows[0].ReferencedFrIds, "FR-1 and FR-001 are one requirement");
        rows[0].ReferencedFrIds.Should().Equal(RequirementReferenceParser.Extract("FR-1"), "the same grammar as the specification");
    }

    [Fact]
    public void A_table_row_and_a_task_body_naming_one_requirement_agree()
    {
        const string markdown = "# Tasks\n\n## Phase 1\n\n- [ ] T001 Build login for FR-7\n\n## Traceability\n\n| Task | Covers |\n| --- | --- |\n| T001 | FR-7 |\n";
        var nodes = Descendants(TaskExplorerService.Parse(markdown).Roots).ToList();

        var task = nodes.Single(n => n.NodeType == TaskNodeType.Task);
        var row = nodes.Single(n => n.NodeType == TaskNodeType.TableRow);
        row.ReferencedFrIds.Should().Equal(task.ReferencedFrIds).And.Equal("FR-007");
    }

    [Fact]
    public void Task_Changes_reports_references_in_the_shared_form()
    {
        const string before = "# Tasks\n\n## Phase 1\n\n- [ ] T001 Build login\n";
        const string after = "# Tasks\n\n## Phase 1\n\n- [ ] T001 Build login\n- [ ] T002 Export invoices for FR-3 and SC-12\n";

        var added = TaskDeltaService.Analyse(before, after).Findings.Single(f => f.ReferencedFrIds.Count > 0);

        added.ReferencedFrIds.Should().Equal("FR-003");
        added.ReferencedScIds.Should().Equal("SC-012");
    }
}
