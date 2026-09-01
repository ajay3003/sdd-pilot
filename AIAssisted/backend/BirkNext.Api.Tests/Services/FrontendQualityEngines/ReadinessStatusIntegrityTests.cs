using BirkNext.Api.Services.FrontendAccessibility;
using BirkNext.Api.Services.FrontendBrowserRuntime;
using BirkNext.Api.Services.FrontendLighthouse;
using BirkNext.Api.Services.FrontendPassiveSecurity;
using BirkNext.Api.Services.FrontendQualityEngines;
using BirkNext.Api.Services.FrontendQualityEngines.Readiness;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Tests.Services.FrontendQualityEngines;

public sealed class ReadinessStatusIntegrityTests
{
    private const string SentinelPath = @"C:\Users\SENSITIVE_USER\source\SECRET_REPO";

    [Fact]
    public async Task AccessibilityPreferenceTrue_OverridesLegacyFalse_AndReachesDependencyProbe()
    {
        using var factory = FactoryWith(new Dictionary<string, string?>
        {
            ["FrontendQualityEnginePreferences:AccessibilityEnabled"] = "true",
            ["FrontendAccessibility:Enabled"] = "false"
        });
        using var scope = factory.Services.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<AccessibilityReadinessProvider>()
            .CheckAsync(CancellationToken.None);

        result.Reason.Should().NotBe(FrontendQualityEngineReadinessReason.DisabledInSystemSettings);
        result.StatusReason.Should().NotBe("Accessibility is disabled in System Settings.");
    }

    [Fact]
    public async Task AccessibilityPreferenceFalse_OverridesLegacyTrue_AsDisabledInSystemSettings()
    {
        using var factory = FactoryWith(new Dictionary<string, string?>
        {
            ["FrontendQualityEnginePreferences:AccessibilityEnabled"] = "false",
            ["FrontendAccessibility:Enabled"] = "true"
        });
        using var scope = factory.Services.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<AccessibilityReadinessProvider>()
            .CheckAsync(CancellationToken.None);

        result.Reason.Should().Be(FrontendQualityEngineReadinessReason.DisabledInSystemSettings);
        result.StatusReason.Should().Be("Accessibility is disabled in System Settings.");
    }

    [Fact]
    public async Task LighthouseMissingRunner_ReturnsSafeNonEmptyTypedReason()
    {
        var service = LighthouseService(Path.Combine(SentinelPath, "missing-runner.mjs"), "node");
        var result = await new LighthouseReadinessProvider(service, NullLogger<LighthouseReadinessProvider>.Instance)
            .CheckAsync(CancellationToken.None);

        result.Reason.Should().Be(FrontendQualityEngineReadinessReason.RuntimePrerequisiteUnavailable);
        result.StatusReason.Should().Be("Lighthouse runner is unavailable.");
        AssertSafe(result.StatusReason);
    }

    [Fact]
    public async Task LighthouseProcessStartFailure_ReturnsSafeNodeReasonWithoutRawPath()
    {
        var runner = Path.GetTempFileName();
        try
        {
            var service = LighthouseService(runner, Path.Combine(SentinelPath, "node.exe"));
            var direct = await service.CheckReadinessAsync();
            var result = await new LighthouseReadinessProvider(service, NullLogger<LighthouseReadinessProvider>.Instance)
                .CheckAsync(CancellationToken.None);

            direct.Error.Should().Be("Node.js runtime is unavailable.");
            result.Reason.Should().Be(FrontendQualityEngineReadinessReason.ExecutableUnavailable);
            result.StatusReason.Should().Be("Node.js runtime is unavailable.");
            AssertSafe(direct.Error);
            AssertSafe(result.StatusReason);
        }
        finally
        {
            File.Delete(runner);
        }
    }

    [Fact]
    public async Task PassiveSecurityProcessFailure_IsSafeAtServiceAndStatusProviderBoundaries()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FrontendQualityEnginePreferences:PassiveSecurityEnabled"] = "true",
            ["FrontendPassiveSecurity:Enabled"] = "false"
        }).Build();
        var service = new FrontendZapPassiveReviewService(
            NullLogger<FrontendZapPassiveReviewService>.Instance,
            new PassiveSecurityTargetAuthorizer(new BrowserTargetValidator(), config),
            new PassiveSecurityEvidenceSanitizer(),
            config,
            new FailingRunner());

        var direct = await service.CheckReadinessAsync();
        var result = await new PassiveSecurityReadinessProvider(service, NullLogger<PassiveSecurityReadinessProvider>.Instance)
            .CheckAsync(CancellationToken.None);

        direct.State.Should().Be(PassiveSecurityReadinessState.DockerUnavailable);
        direct.Error.Should().Be("Container runtime is unavailable.");
        result.Reason.Should().Be(FrontendQualityEngineReadinessReason.ContainerRuntimeUnavailable);
        result.StatusReason.Should().Be("Container runtime is unavailable.");
        AssertSafe(direct.Error);
        AssertSafe(result.StatusReason);
    }

    private static WebApplicationFactory<Program> FactoryWith(Dictionary<string, string?> values) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(values)));

    private static FrontendLighthouseReviewService LighthouseService(string runner, string node) => new(
        NullLogger<FrontendLighthouseReviewService>.Instance,
        new BrowserTargetValidator(),
        new LighthouseEvidenceSanitizer(new BrowserEvidenceSanitizer()),
        runner,
        node);

    private static void AssertSafe(string? value)
    {
        value.Should().NotBeNullOrWhiteSpace();
        value.Should().NotContain("SENSITIVE_USER").And.NotContain("SECRET_REPO")
            .And.NotContain("working directory").And.NotContain(@"C:\Users\");
    }

    private sealed class FailingRunner : IZapProcessRunner
    {
        public Task<ZapProcessResult> RunAsync(string file, IReadOnlyList<string> args, int timeoutMs, CancellationToken cancellationToken) =>
            Task.FromResult(new ZapProcessResult(-1, "", $"raw process failure in working directory '{SentinelPath}'"));
    }
}
