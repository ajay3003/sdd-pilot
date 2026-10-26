using BirkNext.Api.Data;
using BirkNext.Api.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BirkNext.Api.Tests.Services;

/// <summary>
/// Generated Documentation Health is a project Analysis feature (it moved out of System Settings → Developer). Its visibility key is new, so a
/// saved configuration without it keeps the page visible; it is listed with the other Analysis features.
/// </summary>
public sealed class GeneratedDocumentationHealthFeatureVisibilityTests : IDisposable
{
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    public void Dispose() => _db.Dispose();

    private AdminService Service(Dictionary<string, string?>? values = null)
    {
        var env = new Mock<IWebHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns("Development");
        env.SetupGet(e => e.ContentRootPath).Returns(Path.GetTempPath());
        return new AdminService(new ConfigurationBuilder().AddInMemoryCollection(values ?? []).Build(), env.Object, _db, NullLogger<AdminService>.Instance);
    }

    [Fact]
    public void Older_configuration_without_the_key_keeps_it_visible()
    {
        Service().BuildFeatureVisibility().GeneratedDocumentationHealth.Should().BeTrue();
        Service(new() { ["FeatureVisibility:GeneratedDocumentationHealth"] = "false" }).BuildFeatureVisibility().GeneratedDocumentationHealth.Should().BeFalse();
    }

    [Fact]
    public void Is_listed_with_the_analysis_features_after_environment_analysis()
    {
        var core = Service().BuildEditableSettings().FeatureVisibility.Core;
        var keys = core.Select(e => e.Key).ToList();

        keys.IndexOf("GeneratedDocumentationHealth").Should().Be(keys.IndexOf("AzureEnvironmentAnalysis") + 1);
        core.Single(e => e.Key == "GeneratedDocumentationHealth").Label.Should().Be("Generated Documentation Health");
    }
}
