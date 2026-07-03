using BirkNext.Api.Models.Admin;
using BirkNext.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Tests.Services;

public class EnvironmentDiagnosticsPageServiceTests
{
    [Fact]
    public async Task GetSectionsAsync_ReturnsReportSections()
    {
        var report = CreateExecutedReport();
        var service = CreatePageService(report);

        var sections = await service.GetSectionsAsync();

        Assert.Equal(["Environment", "Database", "Backend / API", "Workspace", "ReviewContext", "Export / Reports"], sections.Select(s => s.Title));
        Assert.All(sections, section => Assert.NotEmpty(section.Items));
    }

    [Fact]
    public async Task GetSectionsAsync_ReturnsEveryDiagnosticAsSettingsItem()
    {
        var report = CreateExecutedReport();
        var service = CreatePageService(report);

        var sections = await service.GetSectionsAsync();

        var itemNames = sections.SelectMany(section => section.Items).Select(item => item.Name).ToList();
        Assert.Contains("Hosting Environment", itemNames);
        Assert.Contains("Database Reachable", itemNames);
        Assert.Contains("Backend Reachable", itemNames);
        Assert.Contains("Workspace Persistence Tables", itemNames);
        Assert.Contains("ReviewContext Available", itemNames);
        Assert.Contains("JSON Export", itemNames);
    }

    [Fact]
    public async Task GetStatusSummaryAsync_ReturnsReportSummary()
    {
        var report = CreateExecutedReport();
        var service = CreatePageService(report);

        var summary = await service.GetStatusSummaryAsync();

        Assert.Equal(4, summary.PassCount);
        Assert.Equal(1, summary.WarningCount);
        Assert.Equal(0, summary.FailCount);
        Assert.Equal(1, summary.UnavailableCount);
        Assert.Equal(SystemSettingsStatus.Warning, summary.OverallStatus);
    }

    [Fact]
    public async Task GetStatusSummaryAsync_ZeroChecks_ReturnsUnavailable()
    {
        var report = new EnvironmentDiagnosticsReport
        {
            OverallStatus = SystemSettingsStatus.Unavailable,
            Summary = new StatusSummary(),
            Sections =
            [
                new SettingsSection { Title = "Environment", Items = [] },
                new SettingsSection { Title = "Database", Items = [] },
                new SettingsSection { Title = "Backend / API", Items = [] },
                new SettingsSection { Title = "Workspace", Items = [] },
                new SettingsSection { Title = "ReviewContext", Items = [] },
                new SettingsSection { Title = "Export / Reports", Items = [] }
            ]
        };
        var service = CreatePageService(report);

        var summary = await service.GetStatusSummaryAsync();

        Assert.Equal(0, summary.PassCount + summary.WarningCount + summary.FailCount + summary.UnavailableCount);
        Assert.Equal(SystemSettingsStatus.Unavailable, summary.OverallStatus);
    }

    [Fact]
    public void StatusSummary_Empty_OverallStatusIsUnavailable()
    {
        var summary = new SystemSettingsStatusEngine().SummarizeStatuses([]);

        Assert.Equal(SystemSettingsStatus.Unavailable, summary.OverallStatus);
    }

    [Fact]
    public void StatusSummary_WithExecutedChecks_UsesSharedStatusRules()
    {
        var summary = new SystemSettingsStatusEngine().SummarizeStatuses(
        [
            SystemSettingsStatus.Pass,
            SystemSettingsStatus.Pass,
            SystemSettingsStatus.Warning,
            SystemSettingsStatus.Unavailable,
            SystemSettingsStatus.Pass
        ]);

        Assert.Equal(2, summary.PassCount);
        Assert.Equal(1, summary.WarningCount);
        Assert.Equal(0, summary.FailCount);
        Assert.Equal(1, summary.UnavailableCount);
        Assert.Equal(SystemSettingsStatus.Warning, summary.OverallStatus);
    }

    private static EnvironmentDiagnosticsPageService CreatePageService(EnvironmentDiagnosticsReport report)
    {
        return new EnvironmentDiagnosticsPageService(
            new StubEnvironmentDiagnosticsService(report),
            NullLogger<EnvironmentDiagnosticsPageService>.Instance);
    }

    private static EnvironmentDiagnosticsReport CreateExecutedReport()
    {
        var summary = new SystemSettingsStatusEngine().SummarizeStatuses(
        [
            SystemSettingsStatus.Pass,
            SystemSettingsStatus.Pass,
            SystemSettingsStatus.Pass,
            SystemSettingsStatus.Warning,
            SystemSettingsStatus.Unavailable
        ]);

        return new EnvironmentDiagnosticsReport
        {
            OverallStatus = summary.OverallStatus,
            Summary = summary,
            Sections =
            [
                Section("Environment", Item("Hosting Environment", SystemSettingsStatus.Pass)),
                Section("Database", Item("Database Reachable", SystemSettingsStatus.Pass)),
                Section("Backend / API", Item("Backend Reachable", SystemSettingsStatus.Pass)),
                Section("Workspace", Item("Workspace Persistence Tables", SystemSettingsStatus.Warning)),
                Section("ReviewContext", Item("ReviewContext Available", SystemSettingsStatus.Unavailable)),
                Section("Export / Reports", Item("JSON Export", SystemSettingsStatus.Pass))
            ]
        };
    }

    private static SettingsSection Section(string title, SettingsItem item) =>
        new()
        {
            Title = title,
            Status = item.Status,
            Items = [item]
        };

    private static SettingsItem Item(string name, SystemSettingsStatus status) =>
        new()
        {
            Name = name,
            Value = name,
            Description = name,
            Status = status
        };

    private sealed class StubEnvironmentDiagnosticsService : IEnvironmentDiagnosticsService
    {
        private readonly EnvironmentDiagnosticsReport _report;

        public StubEnvironmentDiagnosticsService(EnvironmentDiagnosticsReport report)
        {
            _report = report;
        }

        public Task<EnvironmentDiagnosticsReport> RunDiagnosticsAsync() => Task.FromResult(_report);
    }
}
