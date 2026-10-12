using System.Net;
using System.Net.Http.Headers;
using BirkNext.Api.Services.Integrations;
using BirkNext.Api.Tests.Services.IntegrationJourneys;
using BirkNext.Api.Tests.Utilities;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace BirkNext.Api.Tests.Controllers;

/// <summary>
/// Altinn testing endpoints on the real host pipeline: the contract is readable like other readiness metadata; refreshing it needs the
/// Integration Quality Review write permission; sign-in needs ActiveEventExecute; the ID-porten redirect target is anonymous but only
/// redirects to the configured frontend and never echoes the code. No request reaches a network: the configuration is not ready.
/// </summary>
public sealed class AltinnTestingAuthorizationTests
{
    private const string Issuer = "https://login.microsoftonline.com/00000000-0000-0000-0000-000000000001/v2.0";
    private const string Audience = "api://birknext-test";
    private static readonly SymmetricSecurityKey Key = new(System.Text.Encoding.UTF8.GetBytes("birknext-test-signing-key-0123456789-abcdef-0123456789"));

    private static WebApplicationFactory<Program> Host() => TestHostConfiguration.CreateDefaultHostWithEnginesDisabled()
        .WithWebHostBuilder(builder =>
        {
            builder.UseSetting("FRONTEND_ORIGIN", "http://localhost:5173");
            builder.ConfigureTestServices(services =>
            {
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    options.Authority = null;
                    options.RequireHttpsMetadata = false;
                    options.Configuration = new OpenIdConnectConfiguration();
                    options.TokenValidationParameters = new TokenValidationParameters { ValidIssuer = Issuer, ValidAudience = Audience, IssuerSigningKey = Key, ValidateLifetime = true };
                });
                services.AddScoped<IIntegrationMessageFlowStore, IntegrationJourneyHarness.FakeMessageFlows>();
            });
        });

    private static string Token(string scp) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Issuer = Issuer, Audience = Audience, Expires = DateTime.UtcNow.AddMinutes(10), SigningCredentials = new SigningCredentials(Key, SecurityAlgorithms.HmacSha256),
        Claims = new Dictionary<string, object> { ["oid"] = "11111111-1111-1111-1111-111111111111", ["scp"] = scp, ["ver"] = "2.0" },
    });

    private static async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string url, string? token)
    {
        using var request = new HttpRequestMessage(method, url);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task ContractIsReadable_RefreshNeedsConfigurationWrite_SignInNeedsActiveEventExecute()
    {
        await using var factory = Host();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        (await Send(client, HttpMethod.Get, "/api/altinn-testing/contract?environmentId=dev-env", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await Send(client, HttpMethod.Post, "/api/altinn-testing/contract/public-metadata?environmentId=dev-env", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Send(client, HttpMethod.Post, "/api/altinn-testing/contract/public-metadata?environmentId=dev-env", Token("ActiveEventExecute"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var refused = await Send(client, HttpMethod.Post, "/api/altinn-testing/contract/public-metadata?environmentId=dev-env", Token("IntegrationConfiguration.Write"));
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest, "no app id is configured, so nothing is fetched");
        (await refused.Content.ReadAsStringAsync()).Should().Contain("No Altinn app id is configured");
        (await Send(client, HttpMethod.Post, "/api/altinn-testing/contract/source-analysis?environmentId=dev-env", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Send(client, HttpMethod.Post, "/api/altinn-testing/contract/source-analysis?environmentId=dev-env", Token("ActiveEventExecute"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Send(client, HttpMethod.Post, "/api/altinn-testing/contract/upload?environmentId=dev-env", Token("IntegrationConfiguration.Write"))).StatusCode
            .Should().BeOneOf([HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed], "app source is uploaded only through Source Analysis");

        (await Send(client, HttpMethod.Get, "/api/altinn-testing/sign-in/status?environmentId=dev-env", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Send(client, HttpMethod.Get, "/api/altinn-testing/sign-in/status?environmentId=dev-env", Token("IntegrationConfiguration.Write"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Send(client, HttpMethod.Get, "/api/altinn-testing/sign-in/status?environmentId=dev-env", Token("ActiveEventExecute"))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Send(client, HttpMethod.Post, "/api/altinn-testing/sign-in/start?environmentId=dev-env", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var start = await Send(client, HttpMethod.Post, "/api/altinn-testing/sign-in/start?environmentId=dev-env", Token("ActiveEventExecute"));
        start.StatusCode.Should().Be(HttpStatusCode.BadRequest, "ID-porten TEST sign-in is not configured");
        (await start.Content.ReadAsStringAsync()).Should().Contain("not ID-porten");
    }

    [Fact]
    public async Task TheRedirectTarget_IsAnonymous_GoesOnlyToTheConfiguredFrontend_AndNeverEchoesTheCode()
    {
        await using var factory = Host();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await Send(client, HttpMethod.Get, "/api/altinn-testing/sign-in/callback?code=secret-authorization-code&state=never-issued", null);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Be("http://localhost:5173/extensions/skolenaervaer?altinn-sign-in=failed#snt-rapport");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        (await response.Content.ReadAsStringAsync()).Should().NotContain("secret-authorization-code");
    }
}
