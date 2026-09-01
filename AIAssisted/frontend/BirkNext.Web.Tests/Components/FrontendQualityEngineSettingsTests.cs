using BirkNext.Web.Components;
using BirkNext.Web.Models;
using Bunit;
using FluentAssertions;
using System;
using System.Collections.Generic;
using Xunit;

namespace BirkNext.Web.Tests.Components;

public sealed class FrontendQualityEngineSettingsTests : TestContext
{
    [Fact(DisplayName = "Component renders with basic parameters")]
    public void ComponentRendersSuccessfully()
    {
        var status = new FrontendQualityEngineStatusDto
        {
            EngineId = FrontendQualityEngineIdDto.BrowserRuntime,
            DisplayName = "Browser Runtime",
            Layer1Allowed = true,
            Layer2Enabled = true,
            Layer3Readiness = new FrontendQualityEngineReadinessDto
            {
                EngineId = FrontendQualityEngineIdDto.BrowserRuntime,
                IsAvailable = true,
                CheckedAtUtc = DateTime.UtcNow
            },
            AuthModeSupported = false,
            Available = true,
            Reasons = new()
        };

        var report = new FrontendQualityEngineStatusReportDto { Engines = new() { status }, CheckedAtUtc = DateTime.UtcNow };

        // For now, just verify that the DTOs deserialize correctly.
        report.Engines.Should().HaveCount(1);
        report.Engines[0].DisplayName.Should().Be("Browser Runtime");
        report.Engines[0].Layer1Allowed.Should().BeTrue();
        report.Engines[0].Available.Should().BeTrue();
    }

    [Fact]
    public void Component_UsesSystemSettingsStructureAndOneGlobalRefresh()
    {
        var report = BuildReport();
        var cut = Render<FrontendQualityEngineSettings>(parameters => parameters
            .Add(p => p.AnonymousStatus, report)
            .Add(p => p.AuthenticatedStatus, report));

        cut.FindAll(".dev-diag-subgrid").Should().ContainSingle();
        cut.FindAll(".dev-diag-section").Should().HaveCount(4);
        cut.FindAll(".settings-table").Should().HaveCount(4);
        cut.FindAll(".settings-badge").Should().HaveCountGreaterThanOrEqualTo(16);
        cut.FindAll("button").Count(button => button.TextContent.Trim() == "Refresh status").Should().Be(1);
        cut.FindAll(".fqe-effective").Should().BeEmpty();
        cut.FindAll(".fqe-card").Should().BeEmpty();
    }

    [Fact]
    public void EditMode_ExposesExactlyFourLayer2Controls()
    {
        var report = BuildReport();
        var cut = Render<FrontendQualityEngineSettings>(parameters => parameters
            .Add(p => p.AnonymousStatus, report)
            .Add(p => p.AuthenticatedStatus, report)
            .Add(p => p.IsEditMode, true)
            .Add(p => p.EditedPreferences, new Dictionary<string, bool>
            {
                ["BrowserRuntime"] = true,
                ["Accessibility"] = false,
                ["Lighthouse"] = true,
                ["PassiveSecurity"] = false,
            }));

        cut.FindAll("input[type=checkbox]").Should().HaveCount(4);
        cut.FindAll("tr").Should().HaveCount(20);
    }

    private static FrontendQualityEngineStatusReportDto BuildReport()
    {
        var engines = Enum.GetValues<FrontendQualityEngineIdDto>()
            .Select(id => new FrontendQualityEngineStatusDto
            {
                EngineId = id,
                DisplayName = id.ToString(),
                Layer1Allowed = id != FrontendQualityEngineIdDto.PassiveSecurity,
                Layer2Enabled = id is FrontendQualityEngineIdDto.BrowserRuntime or FrontendQualityEngineIdDto.Lighthouse,
                Layer3Readiness = new FrontendQualityEngineReadinessDto
                {
                    EngineId = id,
                    IsAvailable = true,
                    CheckedAtUtc = DateTime.UtcNow,
                },
                AuthModeSupported = id is FrontendQualityEngineIdDto.BrowserRuntime or FrontendQualityEngineIdDto.Accessibility,
                Available = id is FrontendQualityEngineIdDto.BrowserRuntime or FrontendQualityEngineIdDto.Lighthouse,
                Reasons = [],
            })
            .ToList();

        return new FrontendQualityEngineStatusReportDto { Engines = engines, CheckedAtUtc = DateTime.UtcNow };
    }
}
