using BirkNext.Web.Components;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;

namespace BirkNext.Web.Tests.Components;

public class DiagnosticPageRendererTests : BunitContext
{
    [Fact]
    public void RendersOwnedCardTitleAndDescriptionStructure()
    {
        var cut = Render<DiagnosticPageRenderer>(parameters => parameters
            .Add(p => p.Title, "Environment Diagnostics")
            .Add(p => p.Description, "A diagnostic description."));

        cut.Find("section.settings-card").GetAttribute("aria-labelledby")
            .Should().Be("diagnostic-page-environment-diagnostics");
        cut.Find("h2.settings-card-title").TextContent.Should().Be("Environment Diagnostics");
        cut.Find("p.settings-card-note").TextContent.Should().Be("A diagnostic description.");
    }

    [Fact]
    public void RendersEmptyState()
    {
        var cut = Render<DiagnosticPageRenderer>(parameters => parameters
            .Add(p => p.Title, "Runtime Diagnostics")
            .Add(p => p.HasRun, false));

        cut.Markup.Should().Contain("Runtime Diagnostics");
        cut.Markup.Should().Contain("Not Run");
        cut.Markup.Should().Contain("Diagnostics have not been executed yet.");
    }

    [Fact]
    public void RendersStatusSummarySectionsItemsAndRecommendations()
    {
        var cut = Render<DiagnosticPageRenderer>(parameters => parameters
            .Add(p => p.Title, "Environment Diagnostics")
            .Add(p => p.HasRun, true)
            .Add(p => p.OverallStatus, SystemSettingsStatus.Warning)
            .Add(p => p.Summary, new StatusSummaryDto
            {
                PassCount = 1,
                WarningCount = 1,
                FailCount = 0,
                UnavailableCount = 0,
                OverallStatus = SystemSettingsStatus.Warning
            })
            .Add(p => p.Sections, new List<SettingsSectionDto>
            {
                new()
                {
                    Title = "Database",
                    Description = "Database checks",
                    Status = SystemSettingsStatus.Warning,
                    Items =
                    [
                        new SettingsItemDto
                        {
                            Name = "Database Reachable",
                            Value = "Connected",
                            Status = SystemSettingsStatus.Pass,
                            Description = "Connected"
                        },
                        new SettingsItemDto
                        {
                            Name = "Optional Table",
                            Value = "Missing",
                            Status = SystemSettingsStatus.Warning,
                            Description = "Missing optional table",
                            Recommendation = "Review migrations"
                        }
                    ]
                }
            }));

        cut.Markup.Should().Contain("Checks Executed");
        cut.Markup.Should().Contain("2");
        cut.Markup.Should().Contain("Database");
        cut.Markup.Should().Contain("Database Reachable");
        cut.Markup.Should().Contain("Optional Table");
        cut.Markup.Should().Contain("Review migrations");
        var rows = cut.FindAll("tr.diag-row");
        rows.Should().HaveCount(2);
        foreach (var row in rows)
        {
            row.QuerySelector("th.diag-row-label").Should().NotBeNull();
            row.QuerySelector("td.diag-row-value").Should().NotBeNull();
            row.QuerySelector("td.diag-row-status .ss-health-sev").Should().NotBeNull();
        }
    }

    [Fact]
    public void SuppressesOnlyMatchingSingleSectionTitle()
    {
        var cut = Render<DiagnosticPageRenderer>(parameters => parameters
            .Add(p => p.Title, "Documentation Health")
            .Add(p => p.HasRun, true)
            .Add(p => p.Sections, new List<SettingsSectionDto>
            {
                new()
                {
                    Title = "Documentation Health",
                    Description = "Documentation navigation and cross-link health",
                    Items = [new() { Name = "Documentation Health", Value = "No issues detected", Status = SystemSettingsStatus.Pass }]
                }
            }));

        cut.FindAll("h2.settings-card-title").Should().ContainSingle();
        cut.FindAll("h3.dev-diag-section-title").Should().BeEmpty();
        cut.Find(".diag-section-description-primary").TextContent
            .Should().Be("Documentation navigation and cross-link health");
    }

    [Fact]
    public void RendersAllStatusVariantsAndLongValueInsideEachRow()
    {
        var longValue = "https://localhost:5000/a/very/long/path/that/must/remain/inside/its/value/cell";
        var cut = Render<DiagnosticPageRenderer>(parameters => parameters
            .Add(p => p.Title, "Diagnostics")
            .Add(p => p.HasRun, true)
            .Add(p => p.Sections, new List<SettingsSectionDto>
            {
                new()
                {
                    Title = "Status Matrix",
                    Items =
                    [
                        new() { Name = "Pass", Value = longValue, Status = SystemSettingsStatus.Pass },
                        new() { Name = "Warning", Value = "Warning value", Status = SystemSettingsStatus.Warning },
                        new() { Name = "Failed", Value = "Failed value", Status = SystemSettingsStatus.Fail },
                        new() { Name = "Unavailable", Value = "Unavailable value", Status = SystemSettingsStatus.Unavailable }
                    ]
                }
            }));

        cut.FindAll("tr.diag-row").Should().HaveCount(4);
        cut.Markup.Should().Contain("ss-health-sev-ok");
        cut.Markup.Should().Contain("ss-health-sev-warn");
        cut.Markup.Should().Contain("ss-health-sev-error");
        cut.Markup.Should().Contain("ss-health-sev-na");
        cut.Find(".diag-value").TextContent.Should().Be(longValue);
    }

    [Fact]
    public void RendersEmptySection()
    {
        var cut = Render<DiagnosticPageRenderer>(parameters => parameters
            .Add(p => p.Title, "Diagnostics")
            .Add(p => p.HasRun, true)
            .Add(p => p.Sections, new List<SettingsSectionDto>
            {
                new() { Title = "Workspace", Items = [] }
            }));

        cut.Markup.Should().Contain("Workspace");
        cut.Markup.Should().Contain("No diagnostics returned for this section.");
    }

    [Fact]
    public void RendersMaintenanceDiagnosticRowsWithTableStructure()
    {
        var cut = Render<DiagnosticPageRenderer>(parameters => parameters
            .Add(p => p.Title, "Maintenance")
            .Add(p => p.Description, "System maintenance and reset controls.")
            .Add(p => p.HasRun, true)
            .Add(p => p.OverallStatus, SystemSettingsStatus.Warning)
            .Add(p => p.Summary, new StatusSummaryDto
            {
                PassCount = 1,
                WarningCount = 1,
                FailCount = 0,
                UnavailableCount = 0,
                OverallStatus = SystemSettingsStatus.Warning
            })
            .Add(p => p.Sections, new List<SettingsSectionDto>
            {
                new()
                {
                    Title = "Maintenance",
                    Description = "System maintenance and reset controls",
                    Status = SystemSettingsStatus.Warning,
                    Items =
                    [
                        new SettingsItemDto
                        {
                            Name = "Database Reset",
                            Value = "Allowed",
                            Status = SystemSettingsStatus.Warning,
                            Description = "Local database reset availability."
                        },
                        new SettingsItemDto
                        {
                            Name = "Database Mode",
                            Value = "Local",
                            Status = SystemSettingsStatus.Pass,
                            Description = "Configured maintenance database mode."
                        }
                    ]
                }
            }));

        // Verify table structure exists
        cut.Markup.Should().Contain("settings-table");

        // Verify data is rendered
        cut.Markup.Should().Contain("Database Reset");
        cut.Markup.Should().Contain("Allowed");
        cut.Markup.Should().Contain("Database Mode");
        cut.Markup.Should().Contain("Local");

        // Verify status badges are rendered
        cut.Markup.Should().Contain("ss-health-sev");

        // Verify raw concatenated strings are absent
        cut.Markup.Should().NotContain("Overall Status Warning");
        cut.Markup.Should().NotContain("Checks Executed 2 Passed 1");
    }
}
