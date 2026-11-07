using System.Net;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// RD-GAP-1: OpenAPI interface discovery is an integration-configuration write. The backend grants it only to an Entra-issued
/// IntegrationConfiguration.Write permission or, in a Development backend from a loopback client, Authorization:LocalConfigurationWrites — never to
/// local trusted execution. A refused discovery must say what actually grants it, so nobody enables the wrong setting.
/// </summary>
public sealed class IntegrationTopologyApiServiceTests
{
    private sealed class Status(HttpStatusCode code) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(new HttpResponseMessage(code));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task A_refused_discovery_names_what_grants_it_and_never_points_to_local_trusted_execution(HttpStatusCode code)
    {
        var service = new IntegrationTopologyApiService(new HttpClient(new Status(code)) { BaseAddress = new Uri("http://localhost:5000/") });

        var (view, error) = await service.DiscoverAsync("dev", []);

        view.Should().BeNull();
        error.Should().Contain("IntegrationConfiguration.Write").And.Contain("local configuration writes")
            .And.NotContain("trusted execution", "local trusted execution does not grant configuration writes");
    }
}
