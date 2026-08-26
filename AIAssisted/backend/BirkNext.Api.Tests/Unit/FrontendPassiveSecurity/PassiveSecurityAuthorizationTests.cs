using BirkNext.Api.Services.FrontendBrowserRuntime;
using BirkNext.Api.Services.FrontendPassiveSecurity;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace BirkNext.Api.Tests.Unit.FrontendPassiveSecurity;

public class PassiveSecurityAuthorizationTests
{
    private readonly PassiveSecurityTargetAuthorizer _sut = new(new BrowserTargetValidator(), Configuration());
    private static IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
    { ["FrontendPassiveSecurity:TrustedProfiles:qa:BaseUrl"]="https://qa.example.test", ["FrontendPassiveSecurity:TrustedProfiles:qa:EnvironmentType"]="Public",
      ["FrontendPassiveSecurity:TrustedProfiles:internal:BaseUrl"]="http://10.1.2.3", ["FrontendPassiveSecurity:TrustedProfiles:internal:EnvironmentType"]="Internal",
      ["FrontendPassiveSecurity:TrustedProfiles:metadata:BaseUrl"]="http://169.254.169.254", ["FrontendPassiveSecurity:TrustedProfiles:metadata:EnvironmentType"]="Internal" }).Build();
    private static PassiveSecurityReviewRequest Request(string target="https://qa.example.test/page", string profile="qa", string configured="https://qa.example.test", string type="Public") => new(target,profile,configured,type);

    [Fact] public void Matching_profile_and_origin_is_allowed() => _sut.Authorize(Request()).IsValid.Should().BeTrue();
    [Fact] public void Raw_url_without_profile_identity_is_blocked() => _sut.Authorize(Request(profile:"")).IsValid.Should().BeFalse();
    [Fact] public void Unknown_profile_identity_is_blocked() => _sut.Authorize(Request(profile:"unknown")).IsValid.Should().BeFalse();
    [Fact] public void Caller_claimed_profile_url_does_not_override_server_profile() => _sut.Authorize(Request(target:"https://other.example.test",configured:"https://other.example.test")).IsValid.Should().BeFalse();
    [Theory] [InlineData("file:///tmp/a")] [InlineData("javascript:alert(1)")] [InlineData("data:text/plain,x")]
    public void Unsafe_schemes_are_blocked(string url) => _sut.Authorize(Request(target:url)).IsValid.Should().BeFalse();
    [Fact] public void Metadata_is_always_blocked() => _sut.Authorize(Request("http://169.254.169.254/","metadata","ignored","Public")).IsValid.Should().BeFalse();
    [Fact] public void Private_target_requires_server_registered_internal_profile() { _sut.Authorize(Request("http://10.1.2.3/","qa","ignored","Internal")).IsValid.Should().BeFalse(); _sut.Authorize(Request("http://10.1.2.3/","internal","ignored","Public")).IsValid.Should().BeTrue(); }
    [Fact] public void Same_origin_redirect_is_allowed() => _sut.AuthorizeRedirect(Request(),"https://qa.example.test/next").IsValid.Should().BeTrue();
    [Fact] public void Cross_origin_redirect_is_blocked() => _sut.AuthorizeRedirect(Request(),"https://evil.example.test/").IsValid.Should().BeFalse();
    [Fact] public void Redirect_to_metadata_is_blocked() => _sut.AuthorizeRedirect(Request(),"http://169.254.169.254/").IsValid.Should().BeFalse();
}
