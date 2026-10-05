using BirkNext.Api.Data;
using BirkNext.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BirkNext.Api.Tests.Services;

public sealed class AdminServiceResetPolicyTests
{
    private static AdminService Create(string environment, string? mode, string? host, bool allowed = true)
    {
        var values = new Dictionary<string, string?>
        {
            ["DatabaseSettings:Mode"] = mode,
            ["DatabaseSettings:Host"] = host,
            ["ConnectionStrings:Default"] = host is null ? null : $"Host={host};Database=test;Username=test;Password=test",
            ["AdminSettings:AllowLocalDatabaseReset"] = allowed.ToString()
        };
        var env = new Mock<IWebHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns(environment);
        env.SetupGet(e => e.ContentRootPath).Returns(Path.GetTempPath());
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        return new AdminService(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), env.Object, db, NullLogger<AdminService>.Instance);
    }

    [Fact]
    public void DevelopmentLocalLoopbackReportsResetReady()
    {
        var settings = Create("Development", "Local", "localhost").BuildSettings();

        Assert.True(settings.Maintenance.ResetAllowed);
        Assert.Equal("Local", settings.Maintenance.DatabaseMode);
    }

    [Theory]
    [InlineData("Production", "Local", "localhost")]
    [InlineData("Development", null, "localhost")]
    [InlineData("Development", "Local", "prod-db.example")]
    [InlineData("Development", "Shared", "localhost")]
    public async Task UnsafeOrUnknownConfigurationRejectsReset(string environment, string? mode, string? host)
    {
        var service = Create(environment, mode, host);

        var result = await service.ResetLocalDatabaseAsync();

        Assert.False(result.Success);
    }

    [Fact]
    public void DisabledPolicyDoesNotReportResetAsAllowed()
    {
        Assert.False(Create("Development", "Local", "localhost", allowed: false)
            .BuildSettings().Maintenance.ResetAllowed);
    }
}
