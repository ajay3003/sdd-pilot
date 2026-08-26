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
        var config = new PassiveSecurityConfiguration();
        config.ActiveScan.Should().BeFalse(); config.Spider.Should().BeFalse(); config.AjaxSpider.Should().BeFalse(); config.AttackMode.Should().BeFalse(); config.Fuzzing.Should().BeFalse();
    }

    [Fact]
    public void Docker_command_is_pinned_isolated_and_has_no_sensitive_mounts_or_privilege()
    {
        var args=FrontendZapPassiveReviewService.BuildContainerArguments("birknext-zap-passive-test",43217);
        var command=string.Join(' ',args);
        command.Should().Contain(FrontendZapPassiveReviewService.Image).And.Contain("--name birknext-zap-passive-test").And.Contain("--label birknext.engine=zap-passive").And.Contain("127.0.0.1:43217:8080");
        command.Should().NotContain("latest").And.NotContain("--privileged").And.NotContain("--network host").And.NotContain("/var/run/docker.sock").And.NotContain(" -v ").And.NotContain("--mount");
    }
}
