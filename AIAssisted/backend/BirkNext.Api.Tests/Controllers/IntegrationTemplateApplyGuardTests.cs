using System.Net;
using System.Net.Http.Headers;
using BirkNext.Api.Services.Integrations;
using BirkNext.Api.Tests.Utilities;
using BirkNext.Integrations;
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
/// The M2LB DEV helper is refused by the SERVER for any environment that is not M2LB DEV (409, nothing written) — a frontend warning alone is
/// not the guard. A backend-owned trusted environment record wins over what the request claims about the environment.
/// </summary>
public sealed class IntegrationTemplateApplyGuardTests
{
    private const string Issuer = "https://login.microsoftonline.com/00000000-0000-0000-0000-000000000001/v2.0";
    private const string Audience = "api://birknext-test";
    private static readonly SymmetricSecurityKey Key = new(System.Text.Encoding.UTF8.GetBytes("birknext-test-signing-key-0123456789-abcdef-0123456789"));
    private const string DevUrl = "https://m2lbdev.bufetat.no/";

    private static WebApplicationFactory<Program> Host(RecordingCatalog catalog) => TestHostConfiguration.CreateDefaultHostWithEnginesDisabled()
        .WithWebHostBuilder(builder =>
        {
            // A backend-owned trusted record says "qa-env" is QA, whatever a request claims.
            builder.UseSetting("TargetEnvironments:Trusted:0:EnvironmentId", "qa-env");
            builder.UseSetting("TargetEnvironments:Trusted:0:DisplayName", "M2LB QA");
            builder.UseSetting("TargetEnvironments:Trusted:0:EnvironmentType", "QA");
            builder.UseSetting("TargetEnvironments:Trusted:0:TargetUrl", "https://m2lbqa.bufetat.no/");
            builder.ConfigureTestServices(services =>
            {
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    options.Authority = null;
                    options.RequireHttpsMetadata = false;
                    options.Configuration = new OpenIdConnectConfiguration();
                    options.TokenValidationParameters = new TokenValidationParameters { ValidIssuer = Issuer, ValidAudience = Audience, IssuerSigningKey = Key, ValidateLifetime = true };
                });
                services.AddSingleton(catalog);
                services.AddScoped<IIntegrationCatalogService>(sp => sp.GetRequiredService<RecordingCatalog>());
            });
        });

    private static string Token() => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Issuer = Issuer, Audience = Audience, Expires = DateTime.UtcNow.AddMinutes(10), SigningCredentials = new SigningCredentials(Key, SecurityAlgorithms.HmacSha256),
        Claims = new Dictionary<string, object> { ["oid"] = "11111111-1111-1111-1111-111111111111", ["tid"] = "00000000-0000-0000-0000-000000000001", ["ver"] = "2.0", ["scp"] = "IntegrationConfiguration.Write" },
    });

    private static async Task<HttpResponseMessage> Apply(HttpClient client, string environmentId, string? type, string? url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/integrations/templates/{M2lbDevIntegrationSeed.Name}/apply?environmentId={environmentId}&environmentType={type}&targetUrl={Uri.EscapeDataString(url ?? "")}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token());
        return await client.SendAsync(request);
    }

    [Theory]
    [InlineData("Production", DevUrl, "never applied to a Production")]
    [InlineData("QA", "https://m2lbqa.bufetat.no/", "only to a Development")]
    [InlineData("Development", "https://other.example.test/", "only to the M2LB DEV environment")]
    public async Task AnyOtherEnvironment_IsRefusedWith409(string type, string url, string reason)
    {
        var catalog = new RecordingCatalog();
        await using var factory = Host(catalog);
        using var client = factory.CreateClient();
        var response = await Apply(client, "some-env", type, url);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain(reason);
        catalog.Written.Should().BeFalse();
    }

    [Fact]
    public async Task TheM2lbDevEnvironment_IsApplied()
    {
        var catalog = new RecordingCatalog();
        await using var factory = Host(catalog);
        using var client = factory.CreateClient();
        (await Apply(client, "dev-env", "Development", DevUrl)).StatusCode.Should().Be(HttpStatusCode.OK);
        catalog.Written.Should().BeTrue();
    }

    [Fact]
    public async Task ATrustedEnvironmentRecord_WinsOverWhatTheRequestClaims()
    {
        var catalog = new RecordingCatalog();
        await using var factory = Host(catalog);
        using var client = factory.CreateClient();
        var response = await Apply(client, "qa-env", "Development", DevUrl);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict, "the backend knows qa-env is QA");
        catalog.SeenType.Should().Be("QA");
        catalog.Written.Should().BeFalse();
    }

    /// <summary>Applies the real guard (<see cref="M2lbDevIntegrationSeed.ApplyBlock"/>) and records whether anything would have been written.</summary>
    internal sealed class RecordingCatalog : IIntegrationCatalogService
    {
        public bool Written { get; private set; }
        public string? SeenType { get; private set; }
        public Task<IntegrationCatalog> GetAsync(string environmentId, string? environmentType, string? targetUrl, CancellationToken ct = default) => Task.FromResult(new IntegrationCatalog { EnvironmentId = environmentId });
        public Task<IntegrationTemplateApplyResult?> ApplyTemplateAsync(string environmentId, string templateId, string? environmentType, string? targetUrl, CancellationToken ct = default)
        {
            SeenType = environmentType;
            var blocked = M2lbDevIntegrationSeed.ApplyBlock(environmentType, targetUrl);
            Written = blocked is null;
            return Task.FromResult<IntegrationTemplateApplyResult?>(new(blocked is null ? new IntegrationCatalog { EnvironmentId = environmentId } : null, blocked));
        }
        public Task<IntegrationDefinition> CreateAsync(string environmentId, IntegrationDefinition definition, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IntegrationDefinition?> UpdateAsync(string environmentId, string id, IntegrationDefinition definition, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IntegrationDefinition?> SetEnabledAsync(string environmentId, string id, bool enabled, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(string environmentId, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IntegrationPlatform?> UpdatePlatformAsync(string environmentId, string id, IntegrationPlatform platform, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> ImportLegacyAsync(string environmentId, IReadOnlyList<BirkNext.Api.Services.IntegrationQuality.IntegrationConfigDto> legacy, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
