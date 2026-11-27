using BirkNext.Api.Services.DataQuality;
using BirkNext.Api.Tests.Services.Specifications;
using BirkNext.Api.Services.Specifications.OpenSpec;
using BirkNext.DataQuality;
using BirkNext.Specifications;
using BirkNext.TestPlanning;
using FluentAssertions;
using Xunit;

namespace BirkNext.Api.Tests.Services.DataQuality;

/// <summary>System Settings → Data Quality with both formats (2026-11-27): a Spec Kit catalog runs the existing reference checks unchanged; an OpenSpec
/// catalog is reconciled by the OpenSpec route's own rules instead, and the report says so.</summary>
public sealed class DataQualityOpenSpecTests
{
    private const string Project = "import:import-dq-openspec";
    private static readonly DataQualityCategory[] All = [DataQualityCategory.Dashboard, DataQualityCategory.RequirementIdentity, DataQualityCategory.SourceImport];

    private static DataQualityEngine Engine(OpenSpecReconciliationReport? report) => new(
        (_, _, _) => Task.FromResult(new DataQualitySnapshot { ProjectKey = Project, Marker = "m1" }), (_, _) => Task.FromResult("m1"),
        (_, _) => Task.FromResult(new ManualTestingOverview { ProjectKey = Project }), (_, _) => Task.FromResult(report));

    [Fact]
    public async Task An_OpenSpec_catalog_is_reconciled_by_the_OpenSpec_rules_and_the_SpecKit_reference_checks_are_replaced_explicitly()
    {
        var b = OpenSpecFixture.Build();
        var report = OpenSpecStatusService.Reconcile(b.Model, OpenSpecRouteTests.View(b), b.Evidence);
        var run = await Engine(report).RunAsync("dq-os", Project, All, _ => { }, CancellationToken.None);
        run.Checks.Should().Contain(c => c.Id == "openspec.format" && c.Status == DataQualityStatus.Pass);
        run.Checks.Where(c => c.Id.StartsWith("openspec.D", StringComparison.Ordinal)).Should().NotBeEmpty().And.OnlyContain(c => c.Status == DataQualityStatus.Pass);
        run.Checks.Should().NotContain(c => c.Id.StartsWith("source.", StringComparison.Ordinal) || c.Id.StartsWith("dashboard.", StringComparison.Ordinal));
        run.Skipped.Should().Contain(s => s.Contains("erstattet av OpenSpec-avstemmingen"));
    }

    [Fact]
    public async Task An_unverified_OpenSpec_schema_stays_NOT_VERIFIED_in_Data_Quality()
    {
        var b = OpenSpecFixture.Build(OpenSpecFixture.Documents(config: "schema: research-first\n"));
        var report = OpenSpecStatusService.Reconcile(b.Model, OpenSpecRouteTests.View(b), b.Evidence);
        var run = await Engine(report).RunAsync("dq-os", Project, All, _ => { }, CancellationToken.None);
        run.Checks.Single(c => c.Id == "openspec.S00").Status.Should().Be(DataQualityStatus.NotVerified);
    }

    [Fact]
    public async Task A_SpecKit_catalog_runs_the_existing_checks_unchanged()
    {
        var notApplicable = new OpenSpecReconciliationReport { ProjectKey = Project, CatalogFormat = SpecificationFormat.SpecKit, Overall = ReconciliationStatus.NotApplicable };
        var withRoute = await Engine(notApplicable).RunAsync("dq-sk", Project, All, _ => { }, CancellationToken.None);
        var without = await new DataQualityEngine((_, _, _) => Task.FromResult(new DataQualitySnapshot { ProjectKey = Project, Marker = "m1" }), (_, _) => Task.FromResult("m1"),
            (_, _) => Task.FromResult(new ManualTestingOverview { ProjectKey = Project })).RunAsync("dq-sk", Project, All, _ => { }, CancellationToken.None);
        withRoute.Checks.Should().NotContain(c => c.Id.StartsWith("openspec.", StringComparison.Ordinal));
        withRoute.Checks.Select(c => (c.Id, c.Status, c.Expected, c.Actual)).Should().Equal(without.Checks.Select(c => (c.Id, c.Status, c.Expected, c.Actual)));
        withRoute.Skipped.Should().Equal(without.Skipped);
    }
}
