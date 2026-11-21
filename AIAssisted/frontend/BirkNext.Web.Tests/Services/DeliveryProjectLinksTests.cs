using BirkNext.DeliveryReporting;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>Delivery Snapshots follows the Project list (2026-11-21): a reporting project is created from an imported project and linked to it.</summary>
public sealed class DeliveryProjectLinksTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 10, 0, 0, TimeSpan.Zero);

    private static DeliveryProject Reporting(string name) => new(Guid.NewGuid(), name, null, Now, Now, 0, 0);

    private static SddLifecycleState Workspace()
    {
        var m2lb = new SddApplicationContext { Id = "app-m2lb", Name = "M2LB" };
        var tiltak = new SddApplicationContext { Id = "app-t2", Name = "Tiltak 2" };
        var empty = new SddApplicationContext { Id = "app-empty", Name = "Empty" };
        return new SddLifecycleState
        {
            Applications = [m2lb, tiltak, empty], CurrentApplicationId = m2lb.Id,
            ProjectImports =
            [
                new SddProjectImportRecord { ImportId = "m", ProjectName = "M2LB", ApplicationId = m2lb.Id, ImportedAt = Now },
                new SddProjectImportRecord { ImportId = "t", ProjectName = "Tiltak 2", ApplicationId = tiltak.Id, ImportedAt = Now },
            ],
        };
    }

    [Fact]
    public void The_current_project_opens_its_linked_reporting_project_else_one_of_the_same_name()
    {
        var lifecycle = Workspace();
        var other = Reporting("Other");
        var sameName = Reporting("m2lb");
        DeliveryProjectLinks.ForCurrent(lifecycle, [other, sameName]).Should().Be(sameName, "same name, any case");

        var linked = Reporting("M2LB weekly");
        DeliveryProjectLinks.Link(lifecycle, "app-m2lb", linked.Id).Should().BeTrue();
        DeliveryProjectLinks.ForCurrent(lifecycle, [other, sameName, linked]).Should().Be(linked, "the link wins over a name");
        DeliveryProjectLinks.ForCurrent(lifecycle, [other]).Should().BeNull("a link to a deleted reporting project and no same-named one");
    }

    [Fact]
    public void New_reporting_project_offers_imported_projects_that_have_none_yet()
    {
        var lifecycle = Workspace();
        DeliveryProjectLinks.WithoutReportingProject(lifecycle, [Reporting("M2LB")]).Select(a => a.Name).Should().Equal(["Tiltak 2"],
            "M2LB has one; a project without imports is not listed");
        DeliveryProjectLinks.Link(lifecycle, "app-gone", Guid.NewGuid()).Should().BeFalse();
    }
}
