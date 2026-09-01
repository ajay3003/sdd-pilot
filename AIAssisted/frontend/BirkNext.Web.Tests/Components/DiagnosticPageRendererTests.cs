using BirkNext.Web.Components;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;

namespace BirkNext.Web.Tests.Components;

public class DiagnosticPageRendererTests : BunitContext
{
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
