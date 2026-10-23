using BirkNext.Api.Services.FrontendBrowserRuntime;
using BirkNext.Api.Services.FrontendPassiveSecurity;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace BirkNext.Api.Tests.Unit.FrontendPassiveSecurity;

/// <summary>
/// The typed Passive Web Security trust decision: one source, distinct states (never one generic "unavailable"), per-origin, and always
/// in agreement with the scan's own authorization. Nothing here weakens a check: <see cref="PassiveSecurityTargetAuthorizer.Authorize"/>
/// still adds the target validator on top of a Trusted state.
/// </summary>
public class PassiveWebSecurityTrustTests
{
    private static PassiveSecurityTargetAuthorizer Authorizer(params (string Profile, string Url, string Type)[] registrations)
    {
        var values = new Dictionary<string, string?>();
        foreach (var (profile, url, type) in registrations)
        {
            values[$"FrontendPassiveSecurity:TrustedProfiles:{profile}:BaseUrl"] = url;
            values[$"FrontendPassiveSecurity:TrustedProfiles:{profile}:EnvironmentType"] = type;
        }
        return new(new BrowserTargetValidator(), new ConfigurationBuilder().AddInMemoryCollection(values).Build());
    }

    private static PassiveSecurityReviewRequest Request(string target, string profile = "dev") => new(target, profile, target, "Development");

    [Fact]
    public void RegisteredOrigin_IsTrusted_AndNamesTheOrigin()
    {
        var trust = Authorizer(("dev", "https://app.dev.example.test", "Development")).Trust(Request("https://app.dev.example.test/start"));
        trust.State.Should().Be(PassiveWebSecurityTrustState.Trusted);
        trust.RegisteredOrigin.Should().Be("https://app.dev.example.test");
    }

    [Fact]
    public void NoRegistration_IsNotRegistered_NotAGenericFailure() =>
        Authorizer().Trust(Request("https://app.dev.example.test/")).State.Should().Be(PassiveWebSecurityTrustState.NotRegistered);

    /// <summary>The frontend and API of one environment are different origins: trusting the API origin never trusts the frontend.</summary>
    [Fact]
    public void ApiOriginRegistered_FrontendOrigin_IsOriginNotTrusted_WithBothOrigins()
    {
        var trust = Authorizer(("dev", "https://api.dev.example.test", "Development")).Trust(Request("https://app.dev.example.test/"));
        trust.State.Should().Be(PassiveWebSecurityTrustState.OriginNotTrusted);
        (trust.RegisteredOrigin, trust.TargetOrigin).Should().Be(("https://api.dev.example.test", "https://app.dev.example.test"));
    }

    [Theory]
    [InlineData("https://sub.app.dev.example.test/")]
    [InlineData("https://app.dev.example.test:8443/")]
    [InlineData("http://app.dev.example.test/")]
    public void TrustNeverLeaksToSubdomainsPortsOrSchemes(string target) =>
        Authorizer(("dev", "https://app.dev.example.test", "Development")).Trust(Request(target)).State.Should().Be(PassiveWebSecurityTrustState.OriginNotTrusted);

    [Fact]
    public void Production_IsProductionBlocked_AndOtherTypes_AreNotPermitted()
    {
        Authorizer(("dev", "https://app.example.test", "Production")).Trust(Request("https://app.example.test/")).State.Should().Be(PassiveWebSecurityTrustState.ProductionBlocked);
        Authorizer(("dev", "https://app.example.test", "Public")).Trust(Request("https://app.example.test/")).State.Should().Be(PassiveWebSecurityTrustState.TypeNotPermitted);
    }

    /// <summary>The typed state and the scan's authorization always agree: nothing untrusted is ever authorized.</summary>
    [Theory]
    [InlineData("https://app.dev.example.test/", true)]
    [InlineData("https://api.dev.example.test/", false)]
    [InlineData("https://user:secret@app.dev.example.test/", false)]
    [InlineData("https://app.dev.example.test/#x", false)]
    public void TrustAndAuthorization_Agree(string target, bool expected)
    {
        var authorizer = Authorizer(("dev", "https://app.dev.example.test", "Development"));
        authorizer.Trust(Request(target)).IsTrusted.Should().Be(expected);
        authorizer.Authorize(Request(target)).IsValid.Should().Be(expected);
    }
}
