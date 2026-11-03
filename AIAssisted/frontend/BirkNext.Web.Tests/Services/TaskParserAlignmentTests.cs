using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// One tasks.md, one reading: the token-level task parser (shared by the explorers and, through the linked source, the backend) accepts every
/// Markdown bullet Delivery Reporting accepts, and normalizes requirement references with the same grammar as the specification.
/// </summary>
public sealed class TaskParserAlignmentTests
{
    private static List<TaskNode> Tasks(string markdown) => Flatten(TaskExplorerService.Parse(markdown).Roots).Where(n => n.TaskId is not null).ToList();

    private static IEnumerable<TaskNode> Flatten(IEnumerable<TaskNode> nodes) => nodes.SelectMany(n => n.Children.Count == 0 ? [n] : Flatten(n.Children).Prepend(n));

    [Fact]
    public void Plus_bullets_are_tasks_like_dash_and_star_bullets()
    {
        var tasks = Tasks("## Phase 1\n- [ ] T001 Dash task\n* [x] T002 Star task\n+ [ ] T003 Plus task\n");

        tasks.Select(t => t.TaskId).Should().Equal("T001", "T002", "T003");
    }

    [Theory]
    [InlineData("FR-1", "FR-001")]
    [InlineData("FR01", "FR-001")]
    [InlineData("FR-001", "FR-001")]
    [InlineData("FR-12", "FR-012")]
    public void Requirement_references_use_the_specifications_identifier_grammar(string written, string expected)
    {
        var task = Tasks($"## Phase 1\n- [ ] T001 Implement login ({written}, SC-2)\n").Single();

        task.ReferencedFrIds.Should().Equal(expected);
        task.ReferencedScIds.Should().Equal("SC-002");
    }
}
