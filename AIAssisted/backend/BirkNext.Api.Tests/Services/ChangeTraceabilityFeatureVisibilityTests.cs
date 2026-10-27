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
/// Change Traceability (stable key ImplementationTraceability) depends on the optional Azure DevOps integration, so it is disabled by default:
/// no explicit value → disabled; an explicit true or false always wins. The feature, its key and its listing are unchanged.
/// </summary>
public sealed class ChangeTraceabilityFeatureVisibilityTests : IDisposable
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
    public void Without_an_explicit_value_it_is_disabled_and_explicit_values_win()
    {
        Service().BuildFeatureVisibility().ImplementationTraceability.Should().BeFalse();
        Service(new() { ["FeatureVisibility:ImplementationTraceability"] = "true" }).BuildFeatureVisibility().ImplementationTraceability.Should().BeTrue();
        Service(new() { ["FeatureVisibility:ImplementationTraceability"] = "false" }).BuildFeatureVisibility().ImplementationTraceability.Should().BeFalse();
    }

    [Fact]
    public void Feature_visibility_lists_it_disabled_by_default_and_other_core_features_keep_their_default()
    {
        var core = Service().BuildEditableSettings().FeatureVisibility.Core;
        var entry = core.Single(e => e.Key == "ImplementationTraceability");
        entry.Label.Should().Be("Change Traceability");
        entry.Value.Should().BeFalse();
        core.Where(e => e.Key != "ImplementationTraceability").Should().OnlyContain(e => e.Value, "only Change Traceability changed its default");
        Service(new() { ["FeatureVisibility:ImplementationTraceability"] = "true" }).BuildEditableSettings().FeatureVisibility.Core
            .Single(e => e.Key == "ImplementationTraceability").Value.Should().BeTrue();
    }

    [Fact]
    public void The_product_default_configuration_disables_it()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var configuration = new ConfigurationBuilder().AddJsonFile(path, optional: false).Build();
        configuration.GetValue<bool?>("FeatureVisibility:ImplementationTraceability").Should().BeFalse();
    }
}
