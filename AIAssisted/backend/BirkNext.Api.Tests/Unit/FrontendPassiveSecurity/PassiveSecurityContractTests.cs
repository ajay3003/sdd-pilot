using Microsoft.Extensions.Configuration;
using BirkNext.Api.Services.FrontendPassiveSecurity;
using FluentAssertions;

namespace BirkNext.Api.Tests.Unit.FrontendPassiveSecurity;

public class PassiveSecurityContractTests
{
    [Theory]
    [InlineData("High (3)", "High")]
    [InlineData("Medium (2)", "Medium")]
    [InlineData("Low (1)", "Low")]
    [InlineData("Informational (0)", "Informational")]
    public void Risk_mapping_is_deterministic_and_never_invents_critical(string input, string expected) =>
        FrontendZapPassiveReviewService.NormalizeRisk(input).Should().Be(expected);

    [Fact]
    public void Evidence_sanitizer_removes_secrets_and_bounds_output()
    {
        var value = "https://user:SECRET-ZAP-TOKEN-12345@example.test/?access_token=SECRET-ZAP-TOKEN-12345 Authorization: Bearer SECRET-ZAP-TOKEN-12345 Cookie: sid=SECRET-ZAP-TOKEN-12345 " + new string('x', 900);
        var sanitized = new PassiveSecurityEvidenceSanitizer().Sanitize(value);
        sanitized.Should().NotContain("SECRET-ZAP-TOKEN-12345");
        sanitized.Length.Should().BeLessThanOrEqualTo(PassiveSecurityEvidenceSanitizer.MaxEvidenceLength + 1);
    }

    [Fact]
    public void Effective_invocation_has_no_active_scan_or_spider_action()
    {
        var command = string.Join(' ', FrontendZapPassiveReviewService.DaemonArguments);
        command.Should().Contain("-daemon").And.NotContain("-quickurl").And.NotContain("spider.action").And.NotContain("ascan").And.NotContain("ajaxSpider");
        command.Should().NotContain("api.disablekey=true");
        var config = new PassiveSecurityConfiguration();
        config.ActiveScan.Should().BeFalse(); config.Spider.Should().BeFalse(); config.AjaxSpider.Should().BeFalse(); config.AttackMode.Should().BeFalse(); config.Fuzzing.Should().BeFalse();
    }

    [Fact]
    public void Assessed_no_findings_is_not_reported_as_a_pass_or_as_not_assessed()
    {
        new PassiveSecurityResult(ExecutionStatus: PassiveSecurityExecutionStatus.Assessed).Outcome.Should().Be(PassiveSecurityResultOutcome.NoFindings);
        new PassiveSecurityResult(ExecutionStatus: PassiveSecurityExecutionStatus.Assessed, Findings:
            [new("10021", "10021-1", "Alert", "Low", "Medium", "Summary", "https://example.test/", null, null, "Review", [], null, null, 1)])
            .Outcome.Should().Be(PassiveSecurityResultOutcome.Findings);
        new PassiveSecurityResult(ExecutionStatus: PassiveSecurityExecutionStatus.EngineError).Outcome.Should().Be(PassiveSecurityResultOutcome.NotAssessed);
    }

    [Fact]
    public void Docker_command_is_pinned_isolated_and_has_no_sensitive_mounts_or_privilege()
    {
        const string apiKey = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
        var args=FrontendZapPassiveReviewService.BuildContainerArguments("birknext-zap-passive-test",apiKey);
        var command=string.Join(' ',args);
        command.Should().Contain(FrontendZapPassiveReviewService.Image).And.Contain("--name birknext-zap-passive-test").And.Contain("--label birknext.engine=zap-passive").And.Contain("-p 127.0.0.1::8080", "the runtime chooses and binds the loopback host port; nothing picks, releases and reuses one");
        command.Should().Contain($"api.key={apiKey}").And.NotContain("api.disablekey=true");
        command.Should().Contain("/tmp:rw,size=512m").And.Contain("-Xmx512m");
        command.Should().NotContain("latest").And.NotContain("--privileged").And.NotContain("--network host").And.NotContain("/var/run/docker.sock").And.NotContain(" -v ").And.NotContain("--mount");
    }

    [Fact]
    public void Container_resource_limits_are_added_only_when_runtime_delegates_them()
    {
        const string apiKey = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
        var bounded = FrontendZapPassiveReviewService.BuildContainerArguments("birknext-zap-passive-test", apiKey,
            controllers: new HashSet<string>(["cpu", "memory", "pids"], StringComparer.OrdinalIgnoreCase));
        bounded.Should().Contain("--memory=768m").And.Contain("--cpus=1").And.Contain("--pids-limit=512");

        var unboundedByRuntime = FrontendZapPassiveReviewService.BuildContainerArguments("birknext-zap-passive-test", apiKey,
            controllers: new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var command = string.Join(' ', unboundedByRuntime);
        command.Should().NotContain("--memory=").And.NotContain("--cpus=").And.NotContain("--pids-limit=");
    }

    [Fact]
    public void Readiness_probe_is_network_isolated_bounded_and_named_for_cleanup()
    {
        var args = FrontendZapPassiveReviewService.BuildProbeArguments("birknext-zap-readiness-test",
            new HashSet<string>(["cpu", "memory", "pids"], StringComparer.OrdinalIgnoreCase));
        var command = string.Join(' ', args);
        command.Should().Contain("--network none").And.Contain("--read-only").And.Contain("/tmp:rw,size=512m")
            .And.Contain("--memory=768m").And.Contain("--cpus=1").And.Contain("--pids-limit=512")
            .And.Contain("--name birknext-zap-readiness-test").And.Contain("-Xmx512m").And.Contain("-dir /tmp/zap").And.NotContain("-daemon");
    }

    [Fact]
    public void An_alert_about_ZAP_itself_is_not_a_finding_of_the_reviewed_site()
    {
        // The pinned ZAP image reports "ZAP is Out of Date" (10116) on every run; it was shown as a Low issue of the tested site.
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        var service = new FrontendZapPassiveReviewService(Microsoft.Extensions.Logging.Abstractions.NullLogger<FrontendZapPassiveReviewService>.Instance,
            new PassiveSecurityTargetAuthorizer(new BirkNext.Api.Services.FrontendBrowserRuntime.BrowserTargetValidator(), config),
            new PassiveSecurityEvidenceSanitizer(), config, new ZapProcessRunner());
        const string json = """
            { "alerts": [
              { "pluginId": "10116", "alertRef": "10116", "alert": "ZAP is Out of Date", "risk": "Low", "confidence": "High", "url": "http://host.containers.internal:18092/" },
              { "pluginId": "10021", "alertRef": "10021", "alert": "X-Content-Type-Options Header Missing", "risk": "Low", "confidence": "Medium", "url": "http://host.containers.internal:18092/" } ] }
            """;
        var findings = service.Normalize(json, "http://localhost:18092/", "podman");
        findings.Select(f => f.PluginId).Should().Equal("10021");
        findings.Single().Url.Should().Contain("localhost:18092", "the container alias is mapped back to the target");
    }

    // ── Published port (2026-11-25): the runtime binds the host port; the service reads it back, never picks/releases/reuses one ──

    [Theory]
    [InlineData("127.0.0.1:49153\n", 49153)]
    [InlineData("127.0.0.1:49153\r\n", 49153)]
    [InlineData("0.0.0.0:40000\n127.0.0.1:41000\n", 41000)]
    [InlineData("[::1]:42000\n", null)]
    [InlineData("0.0.0.0:40000\n", null)]
    [InlineData("127.0.0.1:0\n", null)]
    [InlineData("127.0.0.1:70000\n", null)]
    [InlineData("Error: no public port", null)]
    [InlineData("", null)]
    public void Only_a_valid_loopback_binding_is_read_as_the_published_port(string output, int? expected) =>
        FrontendZapPassiveReviewService.ParsePublishedPort(output).Should().Be(expected);

    [Fact]
    public async Task When_the_runtime_cannot_report_its_port_the_run_fails_honestly_and_the_container_is_still_removed()
    {
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FrontendQualityCapabilities:PassiveSecurityAllowed"] = "true",
            ["FrontendQualityEnginePreferences:PassiveSecurityEnabled"] = "true",
            ["FrontendPassiveSecurity:Enabled"] = "true",
            ["FrontendPassiveSecurity:TrustedProfiles:local-fixture:BaseUrl"] = "http://localhost:5199",
            ["FrontendPassiveSecurity:TrustedProfiles:local-fixture:EnvironmentType"] = "LoopbackLocalTest",
        }).Build();
        var runner = new PortRunner();
        var service = new FrontendZapPassiveReviewService(Microsoft.Extensions.Logging.Abstractions.NullLogger<FrontendZapPassiveReviewService>.Instance,
            new PassiveSecurityTargetAuthorizer(new BirkNext.Api.Services.FrontendBrowserRuntime.BrowserTargetValidator(), config),
            new PassiveSecurityEvidenceSanitizer(), config, runner);

        var result = await service.ReviewAsync(new PassiveSecurityReviewRequest("http://localhost:5199/", "local-fixture", "http://localhost:5199", "LoopbackLocalTest"));

        result.ExecutionStatus.Should().Be(PassiveSecurityExecutionStatus.EngineError, string.Join(" | ", runner.Calls.Select(c => string.Join(' ', c))));
        result.EngineError.Should().Contain("published port could not be determined");
        var run = runner.Calls.Single(c => c.FirstOrDefault() == "run" && c.Contains("-daemon")); // the review container (the readiness probe is a run too)
        string.Join(' ', run).Should().Contain("-p 127.0.0.1::8080");
        var name = run[run.IndexOf("--name") + 1];
        runner.Calls.Should().ContainSingle(c => c.Count == 3 && c[0] == "port" && c[1] == name && c[2] == "8080/tcp");
        runner.Calls.Should().Contain(c => c.Count == 3 && c[0] == "rm" && c[1] == "--force" && c[2] == name, "the container is removed although the run failed");
    }

    private sealed class PortRunner : IZapProcessRunner
    {
        public List<List<string>> Calls { get; } = [];
        public Task<ZapProcessResult> RunAsync(string file, IReadOnlyList<string> args, int timeoutMs, CancellationToken cancellationToken)
        {
            lock (Calls) Calls.Add([.. args]);
            return Task.FromResult(args.FirstOrDefault() switch
            {
                "version" => new ZapProcessResult(0, "5.4.2", ""),
                "image" => new ZapProcessResult(0, "ghcr.io/zaproxy/zaproxy@sha256:test", ""),
                "info" => new ZapProcessResult(0, "[]", ""),
                "port" => new ZapProcessResult(0, "Error: no public port '8080/tcp' published", ""),
                "container" => new ZapProcessResult(1, "", ""),
                _ when args.Contains("-version") => new ZapProcessResult(0, "ZAP 2.16.1", ""),
                _ => new ZapProcessResult(0, "", ""),
            });
        }
    }
}
