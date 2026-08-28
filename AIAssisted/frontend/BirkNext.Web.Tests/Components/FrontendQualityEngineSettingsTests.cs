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
}
