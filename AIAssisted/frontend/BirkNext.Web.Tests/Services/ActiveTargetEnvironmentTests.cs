using System.Text.Json;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;
using Microsoft.JSInterop;
using Moq;

namespace BirkNext.Web.Tests.Services;

public sealed class ActiveTargetEnvironmentTests
{
    internal const string ProfilesJson = """
        {"activeProfileId":"dev","profiles":[
          {"id":"qa","name":"QA","environmentType":"QA","targetUrl":"https://example-qa.local"},
          {"id":"dev","name":"M2LB DEV","environmentType":"Development","targetUrl":"https://m2lbdev.bufetat.no/"}
        ]}
        """;

    internal sealed class Storage(string? json = ProfilesJson) : IJSRuntime
    {
        public string? Json { get; set; } = json;
        public bool FailWrites { get; set; }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            if (identifier == "birkNextStorage.getItem") return ValueTask.FromResult((TValue)(object?)Json!);
            if (identifier == "birkNextStorage.setItem")
            {
                if (FailWrites) throw new JSException("Storage unavailable");
                Json = (string)args![1]!;
            }
            return ValueTask.FromResult(default(TValue)!);
        }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<TValue>(identifier, args);
    }

    internal static FrontendAnalysisContextFactory Factory(FrontendAnalysisSettingsService settings, IJSRuntime js) =>
        new(settings, Mock.Of<IAuthenticatedBrowserSessionService>(), js);

    [Theory]
    [InlineData("dev", "M2LB DEV", "https://m2lbdev.bufetat.no/")]
    [InlineData("qa", "QA", "https://example-qa.local")]
    public async Task ExactActiveIdWinsRegardlessOfOrder(string id, string name, string url)
    {
        var js = new Storage();
        var settings = new FrontendAnalysisSettingsService();
        await settings.LoadAsync(js);
        settings.SelectActiveProfile(id);
        settings.Settings.Profiles.Reverse();
        var context = await Factory(settings, js).GetActiveContextAsync();
        context.ActiveProfile.Id.Should().Be(id);
        context.ActiveProfile.Name.Should().Be(name);
        context.TargetUrl.Should().Be(url);
        new TargetEnvironmentService(settings).Environments.Count(p => p.IsActive).Should().Be(1);
    }

    [Theory]
    [InlineData(null, "No active Target Environment")]
    [InlineData("deleted", "Active Target Environment is unavailable.")]
    public async Task MissingActiveNeverResolvesSample(string? activeId, string error)
    {
        var js = new Storage();
        var settings = new FrontendAnalysisSettingsService();
        await settings.LoadAsync(js);
        settings.Settings.ActiveProfileId = activeId;
        var context = await Factory(settings, js).GetActiveContextAsync();
        context.HasTargetUrl.Should().BeFalse();
        context.ActiveTargetError.Should().Be(error);
        context.ActiveProfile.Id.Should().BeEmpty();
    }

    [Fact]
    public async Task SetActivePersistsIntoFreshServiceAndFactoryWithSamplePresent()
    {
        var js = new Storage(ProfilesJson.Replace("\"activeProfileId\":\"dev\"", "\"activeProfileId\":\"qa\""));
        var settings = new FrontendAnalysisSettingsService();
        await settings.LoadAsync(js);
        settings.SelectActiveProfile("dev");
        await settings.SaveAsync(js);
        var restarted = new FrontendAnalysisSettingsService();
        var context = await Factory(restarted, js).GetActiveContextAsync();
        restarted.Settings.ActiveProfileId.Should().Be("dev");
        context.ActiveProfile.Name.Should().Be("M2LB DEV");
        context.TargetUrl.Should().Be("https://m2lbdev.bufetat.no/");
    }

    [Fact]
    public async Task DeleteActiveClearsActivityAndRestartDoesNotActivateQa()
    {
        var js = new Storage();
        var settings = new FrontendAnalysisSettingsService();
        await settings.LoadAsync(js);
        settings.DeleteProfile("dev");
        await settings.SaveAsync(js);
        var restarted = new FrontendAnalysisSettingsService();
        var context = await Factory(restarted, js).GetActiveContextAsync();
        restarted.Settings.ActiveProfileId.Should().BeNull();
        restarted.Settings.Profiles.Should().ContainSingle(p => p.Id == "qa");
        context.ActiveTargetError.Should().Be("No active Target Environment");
    }

    [Fact]
    public async Task DuplicateAndResetPreserveSourceActivity()
    {
        var js = new Storage();
        var settings = new FrontendAnalysisSettingsService();
        await settings.LoadAsync(js);
        var duplicate = settings.DuplicateProfile("dev");
        settings.ResetProfile("dev");
        settings.Settings.ActiveProfileId.Should().Be("dev");
        new TargetEnvironmentService(settings).Environments.Single(p => p.Id == duplicate.Id).IsActive.Should().BeFalse();
        await settings.SaveAsync(js);
        (await Factory(new(), js).GetActiveContextAsync()).ActiveProfile.Id.Should().Be("dev");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{\"profiles\":[],\"activeProfileId\":null}")]
    public async Task FirstLoadAndPersistedEmptyStoreNeverAutoActivate(string? json)
    {
        var js = new Storage(json);
        var settings = new FrontendAnalysisSettingsService();
        var context = await Factory(settings, js).GetActiveContextAsync();
        settings.ActiveProfile.Should().BeNull();
        context.HasTargetUrl.Should().BeFalse();
        if (json is not null) settings.Settings.Profiles.Should().BeEmpty();
    }

    [Fact]
    public async Task ConflictingLegacyFlagsRequireExplicitActivationAndRepairPersists()
    {
        var js = new Storage(ProfilesJson.Replace("\"id\":", "\"isActive\":true,\"id\":"));
        var settings = new FrontendAnalysisSettingsService();
        var context = await Factory(settings, js).GetActiveContextAsync();
        context.ActiveTargetError.Should().Contain("Multiple active");
        context.HasTargetUrl.Should().BeFalse();
        new TargetEnvironmentService(settings).Environments.Should().OnlyContain(p => !p.IsActive);
        await settings.SaveAsync(js); // unrelated saves must not silently repair conflicting legacy activity
        (await Factory(new(), js).GetActiveContextAsync()).ActiveTargetError.Should().Contain("Multiple active");
        settings.SelectActiveProfile("dev");
        await settings.SaveAsync(js);
        js.Json.Should().NotContain("isActive");
        (await Factory(new(), js).GetActiveContextAsync()).ActiveProfile.Id.Should().Be("dev");
    }

    [Fact]
    public async Task DuplicateStableIdsAreUnavailableRatherThanChosenByOrder()
    {
        var js = new Storage(ProfilesJson.Replace("\"id\":\"qa\"", "\"id\":\"dev\""));
        var context = await Factory(new(), js).GetActiveContextAsync();
        context.ActiveTargetError.Should().Be("Active Target Environment is unavailable.");
    }

    [Fact]
    public async Task MalformedPersistenceDoesNotLoadSeedOrOverwriteStorage()
    {
        var js = new Storage("broken JSON");
        var context = await Factory(new(), js).GetActiveContextAsync();
        context.ActiveTargetError.Should().Contain("could not be loaded");
        context.HasTargetUrl.Should().BeFalse();
        js.Json.Should().Be("broken JSON");
    }

    [Fact]
    public async Task SnapshotRetainsAllSettingsWhenSavedProfileAndActiveIdChange()
    {
        var js = new Storage();
        var settings = new FrontendAnalysisSettingsService();
        await settings.LoadAsync(js);
        var dev = settings.ActiveProfile!;
        dev.ApiAuth.BearerToken = "memory-only-token";
        dev.AllowedRestHosts.Add("dev-api.test");
        dev.Integrations.Add(new() { Id = "integration", Name = "Original" });
        var context = await Factory(settings, js).GetActiveContextAsync();
        var originalThreshold = context.PerformanceThresholds.MaxStartupRequests;
        dev.Name = "Edited";
        dev.TargetUrl = "https://changed.test/";
        dev.ApiAuth.BearerToken = "replacement";
        dev.Performance.MaxStartupRequests = 999;
        dev.AllowedRestHosts.Clear();
        dev.Integrations[0].Name = "Changed";
        settings.SelectActiveProfile("qa");
        context.ActiveProfile.Name.Should().Be("M2LB DEV");
        context.TargetUrl.Should().Be("https://m2lbdev.bufetat.no/");
        context.ApiAuth.BearerToken.Should().Be("memory-only-token");
        context.ActiveProfile.ApiAuth.BearerToken.Should().BeNull();
        context.PerformanceThresholds.MaxStartupRequests.Should().Be(originalThreshold);
        context.AllowedRestHosts.Should().ContainSingle("dev-api.test");
        context.Integrations[0].Name.Should().Be("Original");
        (await Factory(settings, js).GetActiveContextAsync()).ActiveProfile.Id.Should().Be("qa");
    }

    [Fact]
    public async Task SerializedReportAndExportKeepOriginalIdentityAfterActiveChanges()
    {
        var js = new Storage();
        var settings = new FrontendAnalysisSettingsService();
        var context = await Factory(settings, js).GetActiveContextAsync();
        var report = new FrontendQualityReviewReport
        {
            TargetUrl = context.TargetUrl,
            TargetEnvironment = new(context.ActiveProfile.Id, context.ActiveProfile.Name,
                context.ActiveProfile.EnvironmentType.ToString(), context.TargetUrl, DateTime.UtcNow)
        };
        settings.SelectActiveProfile("qa");
        var restored = JsonSerializer.Deserialize<FrontendQualityReviewReport>(JsonSerializer.Serialize(report))!;
        var html = new ReportExportService().ExportFrontendQualityReview(restored, "Test");
        restored.TargetEnvironment.Should().Be(report.TargetEnvironment);
        html.Should().Contain("M2LB DEV").And.Contain("https://m2lbdev.bufetat.no/").And.Contain("Development");
        html.Should().NotContain("example-qa.local");
    }
}
