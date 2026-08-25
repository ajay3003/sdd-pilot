using BirkNext.Api.Services.FrontendAccessibility;
using BirkNext.Api.Services.FrontendBrowserRuntime;
using Deque.AxeCore.Commons;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Tests.Unit.FrontendAccessibility;

public sealed class AccessibilityFailureSemanticsTests
{
    [Fact]
    public async Task AxeUnavailable_IsEngineError_NotSuccessfulZeroViolations()
    {
        var service = new FrontendAccessibilityReviewService(
            NullLogger<FrontendAccessibilityReviewService>.Instance,
            new BrowserTargetValidator(allowLoopback: true),
            new AccessibilityNormalizer(new AccessibilityEvidenceSanitizer()),
            new UnavailableAxeProvider());

        var result = await service.ReviewAsync("http://127.0.0.1:12345/", new AccessibilityReviewOptions());

        Assert.Equal(AccessibilityExecutionStatus.EngineError, result.ExecutionStatus);
        Assert.Equal(0, result.ViolationCount);
        Assert.Null(result.AxeVersion);
        Assert.NotNull(result.EngineError);
    }

    private sealed class UnavailableAxeProvider : IAxeScriptProvider
    {
        public string GetScript() => string.Empty;
    }
}
