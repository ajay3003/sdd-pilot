using BirkNext.Api.Services.AuthenticatedReview;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace BirkNext.Api.Tests.Services.AuthenticatedReview;

public sealed class AuthenticationOriginPolicyTests
{
    private static readonly Uri App = new("https://m2lbdev.bufetat.no/path?secret=value");
    private static readonly Uri Entra = new("https://login.microsoftonline.com/tenant/oauth2/v2.0/authorize");

    [Fact]
    public void ApplicationOrigin_IgnoresPathQueryAndFragment() =>
        Policy().Classify(new("https://m2lbdev.bufetat.no/other?code=secret#fragment"), App, Entra, true, false, null)
            .Should().Be(AuthenticationOriginClass.Application);

    [Fact]
    public void ExactEntraAuthority_IsTemporaryIdentityProvider() =>
        Policy().Classify(new("https://login.microsoftonline.com/common/login"), App, Entra, true, false, null)
            .Should().Be(AuthenticationOriginClass.EntraAuthority);

    [Theory]
    [InlineData("https://microsoft.com")]
    [InlineData("https://login.microsoft.com")]
    [InlineData("http://login.microsoftonline.com")]
    public void ArbitraryMicrosoftOrigin_IsRejected(string candidate) =>
        Policy().Classify(new(candidate), App, Entra, true, false, null).Should().Be(AuthenticationOriginClass.Unexpected);

    [Fact]
    public void TargetCorrelatedMcas_AcceptedOnlyAfterEntraDuringActiveAttempt()
    {
        var candidate = new Uri("https://m2lbdev-bufetat-no.access.mcas.ms/notice?state=secret");
        Policy().Classify(candidate, App, Entra, true, true, null).Should().Be(AuthenticationOriginClass.McasIntermediary);
        Policy().Classify(candidate, App, Entra, true, false, null).Should().Be(AuthenticationOriginClass.Unexpected);
        Policy().Classify(candidate, App, Entra, false, true, null).Should().Be(AuthenticationOriginClass.Unexpected);
    }

    [Theory]
    [InlineData("https://unrelated.access.mcas.ms")]
    [InlineData("https://m2lbdev-bufetat-no.evil.example")]
    [InlineData("http://m2lbdev-bufetat-no.access.mcas.ms")]
    public void BroadOrUncorrelatedMcas_IsRejected(string candidate) =>
        Policy().Classify(new(candidate), App, Entra, true, true, null).Should().Be(AuthenticationOriginClass.Unexpected);

    [Fact]
    public void SyntheticOrigins_AreDisabledByDefault()
    {
        var synthetic = new Uri("http://127.0.0.1:5102");
        Policy().Classify(synthetic, App, Entra, true, true, synthetic).Should().Be(AuthenticationOriginClass.Unexpected);
    }

    private static AuthenticationOriginPolicy Policy() =>
        new(Options.Create(new AuthenticatedReviewOptions()));
}
