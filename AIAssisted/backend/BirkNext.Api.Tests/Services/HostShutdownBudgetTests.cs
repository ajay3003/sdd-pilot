using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BirkNext.Api.Tests.Services;

/// <summary>
/// The host stops hosted services in reverse registration order and cancels its shutdown token once ShutdownTimeout elapses. When one service
/// is slow to stop (under load: a background pass still in a store call), every service stopped after it receives an already-cancelled token.
/// Those services must still release their resources and must not fail host shutdown (BQA-012: the proxy threw from its gate wait).
/// </summary>
public sealed class HostShutdownBudgetTests
{
    [Fact]
    public void HostShutdown_WhenAnEarlierServiceSpendsTheShutdownBudget_CompletesWithoutThrowing()
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromMilliseconds(200));
            services.AddHostedService<SlowToStopService>(); // registered last, so stopped first
        }));
        _ = factory.Services; // starts the host

        var shutdown = () => factory.Dispose();

        shutdown.Should().NotThrow();
    }

    private sealed class SlowToStopService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None);
    }
}
