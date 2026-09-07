using BirkNext.Api.Models;
using BirkNext.Api.Services.AuthenticatedReview;
using BirkNext.Api.Services.TargetEnvironmentDetection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using System.Text.Json;
using Xunit;

namespace BirkNext.Api.Tests.Services.TargetEnvironmentDetection;

public sealed class LiveAuthenticationFailureRegressionTests
{
    private sealed class NeverLaunchedHost : IAuthenticatedBrowserHost
    {
        public Task<IAuthenticatedBrowserResources> LaunchAsync(Uri target, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Disabled runtime must not launch a browser.");
    }
    [Fact]
    public async Task DisabledRealSessionManager_PreservesRuntimeUnavailable()
    {
        await using var manager = new AuthenticatedBrowserSessionManager(
            new NeverLaunchedHost(), Options.Create(new AuthenticatedReviewOptions()),
            TimeProvider.System, NullLogger<AuthenticatedBrowserSessionManager>.Instance);
        var strategy = new InteractiveBrowserDetectionStrategy(manager, NullLogger<InteractiveBrowserDetectionStrategy>.Instance);
        var result = await strategy.ContinueDetectionAsync("https://m2lbdev.bufetat.no/", "detection-live", "development");
        Assert.Equal(AuthenticationFailureReason.RuntimeUnavailable, result.AuthenticationFailureReason);
        Assert.False(result.AuthenticationSucceeded);
    }

    [Theory]
    [InlineData("invalid_credentials", AuthenticationFailureReason.InvalidCredentials)]
    [InlineData("mfa_required", AuthenticationFailureReason.MfaRequired)]
    [InlineData("conditional_access_denied", AuthenticationFailureReason.ConditionalAccessDenied)]
    [InlineData("account_disabled", AuthenticationFailureReason.AccountDisabled)]
    [InlineData("navigation_timeout", AuthenticationFailureReason.NavigationTimeout)]
    [InlineData("authentication_navigation_failed", AuthenticationFailureReason.NavigationFailure)]
    [InlineData("browser_launch_failed", AuthenticationFailureReason.BrowserResourceFailure)]
    [InlineData("browser_resource_failure", AuthenticationFailureReason.BrowserResourceFailure)]
    [InlineData("browser_disconnected", AuthenticationFailureReason.BrowserResourceFailure)]
    [InlineData("page_closed", AuthenticationFailureReason.BrowserResourceFailure)]
    [InlineData("page_crashed", AuthenticationFailureReason.BrowserResourceFailure)]
    [InlineData("resources_null", AuthenticationFailureReason.BrowserResourceFailure)]
    [InlineData("unexpected_origin", AuthenticationFailureReason.UnexpectedOrigin)]
    [InlineData("unrecognized_future_category", AuthenticationFailureReason.GenericFailure)]
    [InlineData(null, AuthenticationFailureReason.GenericFailure)]
    public void CategoriesAreIntentionallyMapped(string? category, AuthenticationFailureReason expected)
    {
        var strategy = new InteractiveBrowserDetectionStrategy(Mock.Of<IAuthenticatedBrowserSessionManager>(), NullLogger<InteractiveBrowserDetectionStrategy>.Instance);
        Assert.Equal(expected, strategy.MapFailureCategory(category));
    }

    [Theory]
    [InlineData(AuthenticationFailureReason.RuntimeUnavailable)]
    [InlineData(AuthenticationFailureReason.NavigationTimeout)]
    public void WireContractHasExplicitStringReason(AuthenticationFailureReason reason)
    {
        var outcome = new TargetDetectionOutcome { AuthenticationFailureReason = reason, State = TargetDetectionState.Failed };
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(outcome, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(JsonValueKind.String, json.RootElement.GetProperty("authenticationFailureReason").ValueKind);
        Assert.Equal(reason.ToString(), json.RootElement.GetProperty("authenticationFailureReason").GetString());
    }
}
