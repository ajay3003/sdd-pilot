using BirkNext.Api.Data;
using BirkNext.Api.Models.Admin;
using BirkNext.Api.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BirkNext.Api.Tests.Services;

/// <summary>
/// Feature visibility has one default per key. The sidebar flags (api/admin/feature-visibility) and the values System Settings shows and counts
/// (api/admin/system-settings/editable) are read from the same configuration with the same default, so a key the configuration omits can no longer
/// be counted Hidden in Settings while the sidebar shows its page (Documentation Drift, Impact Analysis, Code Traceability, Traceability
/// Suggestions were split before). Only listed keys can be saved.
/// </summary>
public sealed class FeatureVisibilityDefaultsTests : IDisposable
{
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly string _root = Directory.CreateTempSubdirectory("bn-fv-").FullName;

    public void Dispose() { _db.Dispose(); try { Directory.Delete(_root, true); } catch { /* temp */ } }

    private AdminService Service(Dictionary<string, string?>? values = null)
    {
        var env = new Mock<IWebHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns("Development");
        env.SetupGet(e => e.ContentRootPath).Returns(_root);
        return new AdminService(new ConfigurationBuilder().AddInMemoryCollection(values ?? []).Build(), env.Object, _db, NullLogger<AdminService>.Instance);
    }

    private static bool Flag(FeatureVisibilityInfo info, string key) => (bool)typeof(FeatureVisibilityInfo).GetProperty(key)!.GetValue(info)!;

    [Fact]
    public void Without_configuration_the_sidebar_and_System_Settings_agree_on_every_listed_key()
    {
        var service = Service();
        var sidebar = service.BuildFeatureVisibility();
        var settings = service.BuildEditableSettings().FeatureVisibility;

        foreach (var entry in settings.Core.Concat(settings.Advanced).Concat(settings.Platform))
            Flag(sidebar, entry.Key).Should().Be(entry.Value, $"{entry.Key} has one default");
    }

    [Theory]
    [InlineData("SpecDrift", true)]               // implemented document review
    [InlineData("ImpactAnalysis", false)]         // optional, off by default
    [InlineData("CodeTraceability", false)]       // legacy
    [InlineData("TraceabilitySuggestions", false)] // legacy
    [InlineData("AiChangeReview", false)]
    [InlineData("ImplementationTraceability", false)]
    [InlineData("SourceAnalysis", true)]
    public void A_key_the_configuration_omits_takes_its_single_default(string key, bool expected)
    {
        Flag(Service().BuildFeatureVisibility(), key).Should().Be(expected);
        var settings = Service().BuildEditableSettings().FeatureVisibility;
        settings.Core.Concat(settings.Advanced).Single(e => e.Key == key).Value.Should().Be(expected);
    }

    [Fact]
    public void An_explicit_value_always_wins_over_the_default()
    {
        var service = Service(new() { ["FeatureVisibility:SpecDrift"] = "false", ["FeatureVisibility:ImpactAnalysis"] = "true" });
        service.BuildFeatureVisibility().SpecDrift.Should().BeFalse();
        service.BuildFeatureVisibility().ImpactAnalysis.Should().BeTrue();
        service.BuildEditableSettings().FeatureVisibility.Advanced.Single(e => e.Key == "ImpactAnalysis").Value.Should().BeTrue();
    }

    [Fact]
    public async Task An_unknown_feature_key_is_rejected_and_nothing_is_written()
    {
        var service = Service();
        var request = new SaveSettingsRequest { FeatureVisibility = new() { ["MadeUpFeature"] = true } };

        service.ValidateSettingsUpdate(request).Valid.Should().BeFalse();
        var (saved, message) = await service.SaveSettingsAsync(request);

        saved.Should().BeFalse();
        message.Should().Contain("Unknown feature key 'MadeUpFeature'");
        File.Exists(Path.Combine(_root, "appsettings.Local.json")).Should().BeFalse();
    }

    [Fact]
    public void Listed_keys_and_their_case_variants_are_accepted_and_platform_keys_still_cannot_be_disabled()
    {
        var service = Service();
        service.ValidateSettingsUpdate(new SaveSettingsRequest { FeatureVisibility = new() { ["specdrift"] = false, ["ImpactAnalysis"] = true } }).Valid.Should().BeTrue();
        service.ValidateSettingsUpdate(new SaveSettingsRequest { FeatureVisibility = new() { ["Dashboard"] = false } }).Valid.Should().BeFalse();
    }

    [Fact]
    public void The_product_configuration_holds_no_key_that_no_code_reads()
    {
        var configuration = new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false).Build();
        var keys = configuration.GetSection("FeatureVisibility").GetChildren().Select(c => c.Key).ToList();
        keys.Should().NotContain(["CreateTestScenario", "QaReadiness"], "orphaned keys were removed");
        var dtoKeys = typeof(FeatureVisibilityInfo).GetProperties().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        keys.Should().OnlyContain(k => dtoKeys.Contains(k), "every configured key is read by the feature-visibility flags");
    }
}
