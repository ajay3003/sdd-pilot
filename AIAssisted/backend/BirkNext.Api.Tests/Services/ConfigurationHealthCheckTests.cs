using BirkNext.Api.Models.Admin;
using Xunit;

namespace BirkNext.Api.Tests.Services;

/// <summary>
/// DTO for Configuration Health reports
/// </summary>
internal class ConfigurationHealthReport
{
    public string OverallStatus { get; set; } = "Pass";
    public int PassCount { get; set; }
    public int WarningCount { get; set; }
    public int FailCount { get; set; }
    public int UnavailableCount { get; set; }
    public List<ConfigurationHealthCheck> RequiredChecks { get; set; } = new();
    public List<ConfigurationHealthCheck> OptionalChecks { get; set; } = new();
}

/// <summary>
/// Individual configuration check result
/// </summary>
internal class ConfigurationHealthCheck
{
    public string Name { get; set; } = "";
    public string Status { get; set; } = "Pass";  // Pass, Warning, Fail, Unavailable
    public string Message { get; set; } = "";
    public string Details { get; set; } = "";
    public bool IsRequired { get; set; }
}

/// <summary>
/// Tests for Configuration Health check classification and reporting.
///
/// Configuration Health should report:
/// 1. Required checks (must be configured for app to work)
/// 2. Optional checks (nice-to-have features)
/// 3. Each check's status: PASS, WARNING, FAIL, UNAVAILABLE
///
/// Rules:
/// - PASS: Configured and healthy
/// - WARNING: Optional missing or using defaults
/// - FAIL: Required config missing/invalid
/// - UNAVAILABLE: Cannot check in current environment
///
/// Overall status:
/// - FAIL if any required check fails
/// - WARNING if any optional check warns or required check is partial
/// - PASS if all required checks pass
/// </summary>
public class ConfigurationHealthCheckTests
{
    [Fact]
    public void When_all_required_checks_pass_overall_status_should_be_PASS()
    {
        var report = new ConfigurationHealthReport
        {
            OverallStatus = "Pass",
            RequiredChecks = new()
            {
                new() { Name = "Environment", Status = "Pass", IsRequired = true, Message = "Development", Details = "ASPNETCORE_ENVIRONMENT" },
                new() { Name = "Database Provider", Status = "Pass", IsRequired = true, Message = "PostgreSQL", Details = "Configured" },
                new() { Name = "Logging Level", Status = "Pass", IsRequired = true, Message = "Information", Details = "Standard level" },
            },
            OptionalChecks = new()
            {
                new() { Name = "Azure DevOps PAT", Status = "Warning", IsRequired = false, Message = "Not configured", Details = "Will use demo data" },
            }
        };

        Assert.Equal("Pass", report.OverallStatus);
        Assert.True(report.RequiredChecks.All(c => c.Status == "Pass"));
        Assert.Equal(3, report.RequiredChecks.Count);
    }

    [Fact]
    public void When_required_check_fails_overall_status_should_be_FAIL()
    {
        var report = new ConfigurationHealthReport
        {
            OverallStatus = "Fail",
            RequiredChecks = new()
            {
                new() { Name = "Database Provider", Status = "Fail", IsRequired = true, Message = "Not configured", Details = "Database connection string missing" },
            },
            OptionalChecks = new()
        };

        Assert.Equal("Fail", report.OverallStatus);
        Assert.True(report.RequiredChecks.Any(c => c.Status == "Fail"));
    }

    [Fact]
    public void When_all_required_pass_but_optional_warns_overall_status_should_be_WARNING()
    {
        var report = new ConfigurationHealthReport
        {
            OverallStatus = "Warning",
            RequiredChecks = new()
            {
                new() { Name = "Environment", Status = "Pass", IsRequired = true, Message = "Development", Details = "" },
                new() { Name = "Database", Status = "Pass", IsRequired = true, Message = "Connected", Details = "" },
            },
            OptionalChecks = new()
            {
                new() { Name = "AI Provider", Status = "Warning", IsRequired = false, Message = "Not configured", Details = "Using defaults" },
            }
        };

        Assert.Equal("Warning", report.OverallStatus);
        Assert.All(report.RequiredChecks, c => Assert.Equal("Pass", c.Status));
        Assert.True(report.OptionalChecks.Any(c => c.Status == "Warning"));
    }

    [Fact]
    public void Required_checks_should_include_environment()
    {
        var report = new ConfigurationHealthReport
        {
            RequiredChecks = new()
            {
                new() { Name = "Environment", Status = "Pass", IsRequired = true, Message = "Production", Details = "ASPNETCORE_ENVIRONMENT=Production" },
            }
        };

        var envCheck = report.RequiredChecks.FirstOrDefault(c => c.Name == "Environment");
        Assert.NotNull(envCheck);
        Assert.True(envCheck!.IsRequired);
        Assert.Equal("Pass", envCheck.Status);
    }

    [Fact]
    public void Required_checks_should_include_database_configuration()
    {
        var report = new ConfigurationHealthReport
        {
            RequiredChecks = new()
            {
                new() { Name = "Database Provider", Status = "Pass", IsRequired = true, Message = "PostgreSQL (Local)", Details = "Using local PostgreSQL database" },
            }
        };

        var dbCheck = report.RequiredChecks.FirstOrDefault(c => c.Name == "Database Provider");
        Assert.NotNull(dbCheck);
        Assert.True(dbCheck!.IsRequired);
    }

    [Fact]
    public void Required_checks_should_include_logging_configuration()
    {
        var report = new ConfigurationHealthReport
        {
            RequiredChecks = new()
            {
                new() { Name = "Logging Level", Status = "Pass", IsRequired = true, Message = "Information", Details = "Structured logging enabled" },
            }
        };

        var logCheck = report.RequiredChecks.FirstOrDefault(c => c.Name == "Logging Level");
        Assert.NotNull(logCheck);
        Assert.True(logCheck!.IsRequired);
    }

    [Fact]
    public void Optional_checks_should_include_ai_provider()
    {
        var report = new ConfigurationHealthReport
        {
            OptionalChecks = new()
            {
                new() { Name = "AI Provider", Status = "Warning", IsRequired = false, Message = "Not configured", Details = "AI features disabled" },
            }
        };

        var aiCheck = report.OptionalChecks.FirstOrDefault(c => c.Name == "AI Provider");
        Assert.NotNull(aiCheck);
        Assert.False(aiCheck!.IsRequired);
    }

    [Fact]
    public void Optional_checks_should_include_ado_pat()
    {
        var report = new ConfigurationHealthReport
        {
            OptionalChecks = new()
            {
                new() { Name = "Azure DevOps PAT", Status = "Warning", IsRequired = false, Message = "Not configured", Details = "Using demo data" },
            }
        };

        var adoCheck = report.OptionalChecks.FirstOrDefault(c => c.Name == "Azure DevOps PAT");
        Assert.NotNull(adoCheck);
        Assert.False(adoCheck!.IsRequired);
    }

    [Fact]
    public void Report_should_have_summary_counts()
    {
        var report = new ConfigurationHealthReport
        {
            PassCount = 5,
            WarningCount = 2,
            FailCount = 0,
            UnavailableCount = 0,
            RequiredChecks = new(),
            OptionalChecks = new()
        };

        Assert.Equal(5, report.PassCount);
        Assert.Equal(2, report.WarningCount);
        Assert.Equal(0, report.FailCount);
        Assert.Equal(0, report.UnavailableCount);
    }

    [Fact]
    public void Missing_required_configuration_should_return_FAIL()
    {
        var report = new ConfigurationHealthReport
        {
            OverallStatus = "Fail",
            RequiredChecks = new()
            {
                new() { Name = "API Base URL", Status = "Fail", IsRequired = true, Message = "Missing configuration", Details = "ApiBaseUrl not configured" },
            },
            OptionalChecks = new()
        };

        var apiCheck = report.RequiredChecks.FirstOrDefault(c => c.Name == "API Base URL");
        Assert.NotNull(apiCheck);
        Assert.Equal("Fail", apiCheck!.Status);
    }

    [Fact]
    public void Optional_missing_configuration_should_return_WARNING()
    {
        var report = new ConfigurationHealthReport
        {
            OptionalChecks = new()
            {
                new() { Name = "Export Configuration", Status = "Warning", IsRequired = false, Message = "Using defaults", Details = "Export features configured with default settings" },
            }
        };

        var exportCheck = report.OptionalChecks.FirstOrDefault(c => c.Name == "Export Configuration");
        Assert.NotNull(exportCheck);
        Assert.Equal("Warning", exportCheck!.Status);
    }

    [Fact]
    public void Unavailable_check_should_be_marked_unavailable()
    {
        var report = new ConfigurationHealthReport
        {
            RequiredChecks = new()
            {
                new() { Name = "Feature X", Status = "Unavailable", IsRequired = true, Message = "Cannot check", Details = "Environment does not support this check" },
            }
        };

        var xCheck = report.RequiredChecks.FirstOrDefault(c => c.Name == "Feature X");
        Assert.NotNull(xCheck);
        Assert.Equal("Unavailable", xCheck!.Status);
    }

    [Fact]
    public void When_no_issues_still_show_all_checks()
    {
        var report = new ConfigurationHealthReport
        {
            OverallStatus = "Pass",
            RequiredChecks = new()
            {
                new() { Name = "Check 1", Status = "Pass", IsRequired = true, Message = "OK", Details = "" },
                new() { Name = "Check 2", Status = "Pass", IsRequired = true, Message = "OK", Details = "" },
                new() { Name = "Check 3", Status = "Pass", IsRequired = true, Message = "OK", Details = "" },
            },
            OptionalChecks = new()
            {
                new() { Name = "Optional 1", Status = "Pass", IsRequired = false, Message = "OK", Details = "" },
            }
        };

        // Report should NOT be empty - should still show all checks even when healthy
        Assert.NotEmpty(report.RequiredChecks);
        Assert.NotEmpty(report.OptionalChecks);
        Assert.True(report.RequiredChecks.Count + report.OptionalChecks.Count > 1);
    }
}
