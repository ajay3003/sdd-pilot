using BirkNext.Api.Services.LocalHttpsProxy;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.LocalHttpsProxy;

/// <summary>Dedicated Edge's Companion and proxy bypass follow the port Kestrel bound: 5000 in development, the launcher's port in the tester package.</summary>
public sealed class BirkNextBackendAddressTests
{
    [Theory]
    [InlineData(5480, "http://localhost:5480")]
    [InlineData(5000, "http://127.0.0.1:5000", "http://[::1]:5001")]
    [InlineData(5481, "https://localhost:7001", "http://[::1]:5481")]
    [InlineData(null, "http://+:5480", "http://0.0.0.0:5000", "https://127.0.0.1:7000")]
    [InlineData(null)]
    public void The_first_plain_http_loopback_listener_names_the_backend_port(int? expected, params string[] addresses) =>
        BirkNextBackendAddress.LoopbackPort(addresses).Should().Be(expected);

    [Fact]
    public void The_proxy_bypass_names_the_backend_port_and_keeps_loopback_proxied()
    {
        BirkNextBackendAddress.ProxyBypassList(5480).Should().Be("<-loopback>;127.0.0.1:5480;localhost:5480");
        BirkNextBackendAddress.DevelopmentPort.Should().Be(5000);
    }
}
