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
        var args=FrontendZapPassiveReviewService.BuildContainerArguments("birknext-zap-passive-test",43217,apiKey);
        var command=string.Join(' ',args);
        command.Should().Contain(FrontendZapPassiveReviewService.Image).And.Contain("--name birknext-zap-passive-test").And.Contain("--label birknext.engine=zap-passive").And.Contain("127.0.0.1:43217:8080");
        command.Should().Contain($"api.key={apiKey}").And.NotContain("api.disablekey=true");
        command.Should().Contain("/tmp:rw,size=512m").And.Contain("-Xmx512m");
        command.Should().NotContain("latest").And.NotContain("--privileged").And.NotContain("--network host").And.NotContain("/var/run/docker.sock").And.NotContain(" -v ").And.NotContain("--mount");
    }

    [Fact]
    public void Container_resource_limits_are_added_only_when_runtime_delegates_them()
    {
        const string apiKey = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
        var bounded = FrontendZapPassiveReviewService.BuildContainerArguments("birknext-zap-passive-test", 43217, apiKey,
            controllers: new HashSet<string>(["cpu", "memory", "pids"], StringComparer.OrdinalIgnoreCase));
        bounded.Should().Contain("--memory=768m").And.Contain("--cpus=1").And.Contain("--pids-limit=512");

        var unboundedByRuntime = FrontendZapPassiveReviewService.BuildContainerArguments("birknext-zap-passive-test", 43217, apiKey,
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
}
