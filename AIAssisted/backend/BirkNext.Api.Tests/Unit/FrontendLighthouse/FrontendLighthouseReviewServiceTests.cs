using BirkNext.Api.Services.FrontendBrowserRuntime;
using BirkNext.Api.Services.FrontendLighthouse;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Tests.Unit.FrontendLighthouse;

public sealed class FrontendLighthouseReviewServiceTests
{
    [Fact]
    public void Sanitizer_RedactsUrlTokens_UserInfo_AndSentinel()
    {
        var sanitizer = new LighthouseEvidenceSanitizer(new BrowserEvidenceSanitizer());
        var sanitized = sanitizer.SanitizeUrl("https://user:pass@example.test/page?access_token=SECRET-LIGHTHOUSE-TOKEN-12345&safe=yes");
        Assert.DoesNotContain("user:pass", sanitized);
        Assert.DoesNotContain("SECRET-LIGHTHOUSE-TOKEN-12345", sanitized);
        Assert.Contains("safe=yes", sanitized);
    }

    [Fact]
    public async Task AuthenticationRequired_DoesNotCreateFalseSuccessfulResult()
    {
        var service = Create("missing.mjs");
        var result = await service.ReviewAsync("https://example.com", requiresAuthentication: true);
        Assert.Equal(LighthouseExecutionStatus.AuthenticationRequired, result.ExecutionStatus);
        Assert.Null(result.PerformanceScore);
    }

    [Theory]
    [InlineData("file:///tmp/page.html")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://127.0.0.1:1234")]
    public async Task UnsafeTarget_IsBlockedBeforeNodeLaunch(string target)
    {
        var result = await Create("missing.mjs", allowLoopback: false).ReviewAsync(target);
        Assert.Equal(LighthouseExecutionStatus.Skipped, result.ExecutionStatus);
        Assert.Null(result.PerformanceScore);
    }

    [Fact]
    public async Task ProcessTimeout_ReturnsTimedOutWithoutFakeScore()
    {
        var path = await CreateDelayRunner();
        try
        {
            var result = await Create(path).ReviewAsync("http://127.0.0.1:1234", new LighthouseReviewOptions(50, "Development"));
            Assert.Equal(LighthouseExecutionStatus.TimedOut, result.ExecutionStatus);
            Assert.Null(result.PerformanceScore);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Cancellation_StopsExecutionWithoutFakeScore()
    {
        var path = await CreateDelayRunner();
        try
        {
            using var cts = new CancellationTokenSource(50);
            var result = await Create(path).ReviewAsync("http://127.0.0.1:1234", new LighthouseReviewOptions(5000, "Development"), cancellationToken: cts.Token);
            Assert.NotEqual(LighthouseExecutionStatus.Assessed, result.ExecutionStatus);
            Assert.Null(result.PerformanceScore);
        }
        finally { File.Delete(path); }
    }

    private static FrontendLighthouseReviewService Create(string runner, bool allowLoopback = true) => new(
        NullLogger<FrontendLighthouseReviewService>.Instance, new BrowserTargetValidator(allowLoopback),
        new LighthouseEvidenceSanitizer(new BrowserEvidenceSanitizer()), runner, "node");

    private static async Task<string> CreateDelayRunner()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lighthouse-delay-{Guid.NewGuid():N}.mjs");
        await File.WriteAllTextAsync(path, "await new Promise(resolve => setTimeout(resolve, 30000));");
        return path;
    }
}
