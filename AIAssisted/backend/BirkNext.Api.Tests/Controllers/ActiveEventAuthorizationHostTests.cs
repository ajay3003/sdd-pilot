using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using BirkNext.Api.Tests.Utilities;

namespace BirkNext.Api.Tests.Controllers;

public sealed class ActiveEventAuthorizationHostTests
{
    [Fact]
    public async Task ActiveEventEndpoints_UseTheHostAuthenticationAndPermissionPolicy()
    {
        await using var factory = TestHostConfiguration.CreateDefaultHostWithEnginesDisabled()
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            {
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = "ActiveEventTest";
                    options.DefaultChallengeScheme = "ActiveEventTest";
                    options.DefaultScheme = "ActiveEventTest";
                }).AddScheme<AuthenticationSchemeOptions, ActiveEventTestAuthenticationHandler>("ActiveEventTest", _ => { });
            }));
        using var client = factory.CreateClient();

        var uri = "/api/active-cdc-tests/scenarios?environmentId=&integrationId=";
        var anonymous = await client.GetAsync(uri);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, anonymous.StatusCode);

        client.DefaultRequestHeaders.Add("X-Test-Authenticated", "true");
        var authenticatedWithoutPermission = await client.GetAsync(uri);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, authenticatedWithoutPermission.StatusCode);

        client.DefaultRequestHeaders.Add("X-Test-Permission", "ActiveEventExecute");
        var authorized = await client.GetAsync(uri);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, authorized.StatusCode);
    }

    private sealed class ActiveEventTestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey("X-Test-Authenticated")) return Task.FromResult(AuthenticateResult.NoResult());
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "test-user") };
            if (Request.Headers.TryGetValue("X-Test-Permission", out var permission))
                claims.Add(new("permission", permission.ToString()));
            var identity = new ClaimsIdentity(claims, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
