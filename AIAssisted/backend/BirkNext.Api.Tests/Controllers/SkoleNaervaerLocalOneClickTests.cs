using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.AltinnTesting;
using BirkNext.Api.Services.Integrations;
using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Api.Services.IntegrationJourneys.Packs.SkoleNaervaer;
using BirkNext.Api.Tests.Services.ActiveEventTesting;
using BirkNext.Api.Tests.Services.AltinnTesting;
using BirkNext.Api.Tests.Services.IntegrationJourneys;
using BirkNext.Api.Tests.Utilities;
using BirkNext.Integrations;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Api.Tests.Controllers;

/// <summary>
/// The one-click Skolenærvær run through the real HTTP pipeline without Entra: a frontend-origin request on a loopback Development backend with
/// local trusted execution → execution-access check → POST /api/integration-journeys/runs → the real journey service, runner, pack, Altinn
/// executor and unattended ID-porten TEST provider. Only the HTTP handler behind the guarded Altinn client is a fake (ID-porten TEST and
/// Altinn TT02 stand-ins in process); nothing leaves the test.
/// </summary>
public sealed class SkoleNaervaerLocalOneClickTests
{
    private const string Frontend = "http://localhost:5173";

    private static (WebApplicationFactory<Program> Factory, IntegrationJourneyHarness Harness) Host(AltinnTestKit kit)
    {
        var harness = new IntegrationJourneyHarness();
        harness.Active.Catalog.Extra.Add(new IntegrationDefinition
        {
            Id = "dev:http:altinn-skolenaervaer", EnvironmentId = ActiveEventTestHarness.Env, DisplayName = "Skolenærvær – Altinn report intake", Kind = IntegrationKind.HttpApi,
            Enabled = true, Producer = "Altinn (TT02)", Consumer = new IntegrationConsumer { DisplayName = "MU" },
        });
        harness.Enroll("dev:http:altinn-skolenaervaer");
        var scopes = new ServiceCollection()
            .AddSingleton<IIntegrationMessageFlowStore>(harness.MessageFlows)
            .AddTransient(_ => kit.Policy)
            .AddTransient(_ => kit.Contracts(harness.MessageFlows, harness.Sources))
            .BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        harness.Executors.Add(new SkoleNaervaerAltinnExecutor(scopes, kit.Authenticator(harness.Tokens), new AltinnAppApiClient(kit.Factory), TimeProvider.System));
        var service = harness.Service(new SkoleNaervaerJourneyPack(harness.MessageFlows, kit.Policy, kit.Contracts(harness.MessageFlows, harness.Sources)));

        var factory = TestHostConfiguration.CreateDefaultHostWithEnginesDisabled().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting(LocalTrustedExecution.Setting, "true");
            builder.UseSetting("FRONTEND_ORIGIN", Frontend);
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IStartupFilter>(new LoopbackPeer());
                services.AddScoped<IIntegrationJourneyService>(_ => service);
            });
        });
        return (factory, harness);
    }

    private static HttpRequestMessage FromFrontend(HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Add("Origin", Frontend);
        return request;
    }

    [Fact]
    public async Task OneClick_ThroughTheController_WithLocalTrustedExecution_SubmitsInAltinnOnly_AndEndsPartial()
    {
        var kit = new AltinnTestKit().ConfigureUnattended();
        var (factory, harness) = Host(kit);
        await using var _ = factory;
        await using var __ = harness;
        await kit.ConfigureReadyAsync(harness);
        kit.Http.Calls.Clear();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost") });
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var access = await (await client.SendAsync(FromFrontend(HttpMethod.Get, "/api/execution-access"))).Content.ReadFromJsonAsync<ExecutionAccessView>(json);
        access!.CanExecute.Should().BeTrue();
        access.Mode.Should().Be(ExecutionAccessMode.LocalTrusted);

        var child = SyntheticPersonIdentifier.Generate();
        var response = await client.SendAsync(FromFrontend(HttpMethod.Post, "/api/integration-journeys/runs", new IntegrationJourneyRunRequest
        {
            EnvironmentId = ActiveEventTestHarness.Env, PackId = "skolenaervaer", JourneyId = "rapportmottak", ScenarioId = "valid-report", Confirmed = true,
            Inputs = new Dictionary<string, string>
            {
                ["reporter"] = AltinnTestKit.ReporterAlias, ["identitySource"] = "registered", ["childIdentifier"] = child,
                ["periodFrom"] = "2026-10-01", ["periodTo"] = "2026-10-31", ["plannedHours"] = "100", ["actualHours"] = "92",
            },
        }));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var run = (await response.Content.ReadFromJsonAsync<IntegrationJourneyRun>(json))!;

        run.Steps.Take(6).Select(step => (step.StepId, step.State)).Should().Equal(
            ("altinn-auth", JourneyStepState.Verified), ("altinn-instance", JourneyStepState.Verified), ("altinn-data", JourneyStepState.Verified),
            ("altinn-validate", JourneyStepState.Verified), ("altinn-submit", JourneyStepState.Verified), ("altinn-receipt", JourneyStepState.Verified));
        run.Steps.Single(step => step.StepId == "altinn-submit").Facts.Select(fact => fact.Label).Should().Contain(["Process advanced", "Submitted"]);
        run.Steps.Skip(6).Should().OnlyContain(step => step.State == JourneyStepState.NotAssessed);
        run.OverallState.Should().Be(JourneyRunState.Partial);
        run.InitiatedBy.Should().Be("local-session:loopback", "the BirkNext execution actor, not the Altinn reporter");

        var stored = await client.SendAsync(FromFrontend(HttpMethod.Get, $"/api/integration-journeys/runs/{run.RunId}"));
        stored.StatusCode.Should().Be(HttpStatusCode.OK);
        var history = await stored.Content.ReadAsStringAsync();
        history.Should().Contain("local-session:loopback").And.NotContainAny(AltinnTestKit.AltinnToken, AltinnTestKit.IdPortenToken, AltinnTestKit.HeadlessCode,
            AltinnTestKit.ClientSecret, AltinnTestKit.ReporterIdentifier, child, "testid:");
        kit.Http.Calls.Should().OnlyContain(call => call.Uri.Host == "test.idporten.no" || call.Uri.Host == "login.test.idporten.no"
            || call.Uri.Host == "platform.tt02.altinn.no" || call.Uri.Host == "buf.apps.tt02.altinn.no", "only the in-process TEST/TT02 stand-ins were addressed");
    }

    [Fact]
    public async Task WithoutTheAppPolicyConfirmation_TheSameOneClickIsBlocked_AndNothingIsSent()
    {
        var kit = new AltinnTestKit().ConfigureUnattended();
        var (factory, harness) = Host(kit);
        await using var _ = factory;
        await using var __ = harness;
        await kit.ConfigureReadyAsync(harness);
        kit.Options.ConfirmedAppPolicies.Clear();
        kit.Http.Calls.Clear();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost") });

        var response = await client.SendAsync(FromFrontend(HttpMethod.Post, "/api/integration-journeys/runs", new IntegrationJourneyRunRequest
        {
            EnvironmentId = ActiveEventTestHarness.Env, PackId = "skolenaervaer", JourneyId = "rapportmottak", ScenarioId = "valid-report", Confirmed = true,
            Inputs = new Dictionary<string, string>
            {
                ["reporter"] = AltinnTestKit.ReporterAlias, ["identitySource"] = "registered", ["childIdentifier"] = SyntheticPersonIdentifier.Generate(),
                ["periodFrom"] = "2026-10-01", ["periodTo"] = "2026-10-31", ["plannedHours"] = "100", ["actualHours"] = "92",
            },
        }));
        var run = (await response.Content.ReadFromJsonAsync<IntegrationJourneyRun>(new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
        run.OverallState.Should().Be(JourneyRunState.Blocked);
        run.StateReason.Should().Contain("App authorization policy not confirmed");
        kit.Http.Calls.Should().BeEmpty();
    }

    private sealed class LoopbackPeer : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Loopback;
                context.Connection.LocalIpAddress = IPAddress.Loopback;
                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
