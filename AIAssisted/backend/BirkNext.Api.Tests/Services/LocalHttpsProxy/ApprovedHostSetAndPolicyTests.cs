using BirkNext.Api.Services.LocalHttpsProxy;
using BirkNext.LocalHttpsProxy;

namespace BirkNext.Api.Tests.Services.LocalHttpsProxy;

/// <summary>Target allowlist (tests D-H) and the environment gate (test B) fail closed.</summary>
public sealed class ApprovedHostSetAndPolicyTests
{
    private const string Target = "https://m2lbdev.bufetat.no/";

    [Fact]
    public void ConfiguredTargetIsAccepted()
    {
        var set = ApprovedHostSet.Create(Target, []);
        Assert.True(set.Contains("m2lbdev.bufetat.no", 443));
        Assert.True(set.Contains("M2LBDEV.bufetat.no.", 443));
        Assert.Equal("m2lbdev.bufetat.no", set.TargetHost);
        Assert.Equal(["m2lbdev.bufetat.no:443"], set.Authorities);
    }

    [Fact]
    public void ApprovedRestAndGraphQlHostsAreAccepted()
    {
        var set = ApprovedHostSet.Create(Target, ["api-dev.bufetat.no", "https://graphql-dev.bufetat.no/graphql", "gateway.example.test:8443"]);
        Assert.True(set.Contains("api-dev.bufetat.no", 443));
        Assert.True(set.Contains("graphql-dev.bufetat.no", 443));
        Assert.True(set.Contains("gateway.example.test", 8443));
        Assert.False(set.Contains("gateway.example.test", 443));
        Assert.True(set.ContainsUri(new Uri("https://api-dev.bufetat.no/v1/status")));
        Assert.False(set.ContainsUri(new Uri("http://api-dev.bufetat.no/v1/status")));
    }

    [Theory]
    [InlineData("www.bing.com")]
    [InlineData("mail.google.com")]
    [InlineData("outlook.office.com")]
    [InlineData("bufetat.no")]
    [InlineData("evil-m2lbdev.bufetat.no")]
    [InlineData("m2lbdev.bufetat.no.evil.test")]
    public void UnrelatedAndArbitraryHostsAreNotIntercepted(string host)
    {
        var set = ApprovedHostSet.Create(Target, ["api-dev.bufetat.no"]);
        Assert.False(set.Contains(host, 443));
        Assert.False(set.ContainsUri(new Uri($"https://{host}/")));
    }

    [Theory]
    [InlineData("login.microsoftonline.com")]
    [InlineData("m2lbdev-bufetat-no.access.mcas.ms")]
    [InlineData("anything.access.mcas.ms")]
    [InlineData("aadcdn.msftauth.net")]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("*.bufetat.no")]
    [InlineData("api.local")]
    [InlineData("intranet")]
    public void IdentityIntermediaryLoopbackAndWildcardHostsCanNeverBeConfiguredForInterception(string host)
    {
        Assert.False(ApprovedHostSet.IsInterceptableHost(host));
        Assert.Throws<ArgumentException>(() => ApprovedHostSet.Create(Target, [host]));
    }

    [Theory]
    [InlineData("http://m2lbdev.bufetat.no/")]
    [InlineData("https://user:pw@m2lbdev.bufetat.no/")]
    [InlineData("not a url")]
    [InlineData("https://localhost/")]
    [InlineData("https://login.microsoftonline.com/")]
    public void UnsafeTargetsRejected(string target) => Assert.Throws<ArgumentException>(() => ApprovedHostSet.Create(target, []));

    [Theory]
    [InlineData("api.test/path")]
    [InlineData("api.test?x=1")]
    [InlineData("user@api.test")]
    [InlineData("api.test:0")]
    [InlineData("api.test:70000")]
    [InlineData("http://api.test/")]
    public void MalformedEntriesRejected(string entry) => Assert.Throws<ArgumentException>(() => ApprovedHostSet.NormalizeAuthority(entry));

    [Fact]
    public void HostCountIsBounded()
    {
        var hosts = Enumerable.Range(0, ApprovedHostSet.MaxHosts + 1).Select(i => $"h{i}.example.test").ToList();
        Assert.Throws<ArgumentException>(() => ApprovedHostSet.Create(Target, hosts));
    }

    [Theory]
    [InlineData("Local", true)]
    [InlineData("Development", true)]
    [InlineData("QA", true)]
    [InlineData("Test", true)]
    [InlineData("RC", true)]
    [InlineData("Production", false)]
    [InlineData("production", false)]
    [InlineData("Custom", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void EnvironmentGateAllowsOnlyNonProductionTypes(string? environment, bool expected) =>
        Assert.Equal(expected, LocalHttpsProxyEnvironmentPolicy.IsAllowed(environment));
}
