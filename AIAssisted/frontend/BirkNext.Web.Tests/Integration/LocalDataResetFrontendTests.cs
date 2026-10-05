using System.Net;
using System.Text;
using BirkNext.Integrations;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Moq;
using Xunit;

namespace BirkNext.Web.Tests.Integration;

/// <summary>
/// The frontend half of a local data reset: every project store is cleared, Target Environments go back to the generic seed, the project
/// localStorage keys are removed, and a write refused by the backend as stale (another tab reset local data) is reported.
/// </summary>
public sealed class LocalDataResetFrontendTests
{
    private sealed class RecordingJs : IJSRuntime
    {
        public List<(string Identifier, object?[]? Args)> Calls { get; } = [];
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) { Calls.Add((identifier, args)); return ValueTask.FromResult(default(TValue)!); }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<TValue>(identifier, args);
    }

    private static (ServiceProvider Provider, RecordingJs Js) Build()
    {
        var js = new RecordingJs();
        var services = new ServiceCollection();
        services.AddSingleton<WorkspaceArtifactRepository>();
        services.AddSingleton<IWorkspaceArtifactRepository>(sp => sp.GetRequiredService<WorkspaceArtifactRepository>());
        services.AddSingleton<IWorkspaceSessionService>(sp => sp.GetRequiredService<WorkspaceArtifactRepository>());
        services.AddSingleton<IWorkspaceStateManager, WorkspaceStateManager>();
        services.AddSingleton<IDashboardSnapshotService, DashboardSnapshotService>();
        services.AddSingleton<QualityReviewSessionService>();
        services.AddSingleton<RuntimeReviewSessionService>();
        services.AddSingleton(Mock.Of<IExtractionSessionService>(s => s.ClearAsync() == Task.CompletedTask));
        services.AddSingleton<FrontendAnalysisSettingsService>();
        services.AddSingleton<IFrontendAnalysisSettingsService>(sp => sp.GetRequiredService<FrontendAnalysisSettingsService>());
        services.AddSingleton<IntegrationMappingEvidenceSession>();
        services.AddSingleton<IJSRuntime>(js);
        services.AddSingleton(sp => new ApplicationRuntimeResetService(
            sp.GetRequiredService<IWorkspaceSessionService>(), sp.GetRequiredService<IWorkspaceStateManager>(),
            sp.GetRequiredService<QualityReviewSessionService>(), sp.GetRequiredService<IDashboardSnapshotService>(),
            sp.GetRequiredService<RuntimeReviewSessionService>(), sp.GetRequiredService<IExtractionSessionService>(),
            services: sp, js: js));
        return (services.BuildServiceProvider(), js);
    }

    [Fact]
    public async Task Reset_ReturnsTargetEnvironmentsToTheGenericSeed()
    {
        var (provider, _) = Build();
        var settings = provider.GetRequiredService<FrontendAnalysisSettingsService>();
        settings.Settings.Profiles.Add(new FrontendAnalysisProfile { Id = "m2lb-dev", Name = "M2LB DEV", TargetUrl = "https://m2lb-dev.example.no" });
        settings.Settings.ActiveProfileId = "m2lb-dev";

        await provider.GetRequiredService<ApplicationRuntimeResetService>().ClearFrontendRuntimeStateAsync();

        settings.IsLoaded.Should().BeTrue();
        settings.Settings.ActiveProfileId.Should().BeNull();
        settings.Settings.Profiles.Select(p => p.Name).Should().Equal("Local", "Development", "QA", "Production");
        settings.Settings.Profiles.Should().OnlyContain(p => !p.TargetUrl.Contains("m2lb", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Reset_RemovesEveryProjectStorageKey_AndSavesTheSeed()
    {
        var (provider, js) = Build();

        await provider.GetRequiredService<ApplicationRuntimeResetService>().ClearFrontendRuntimeStateAsync();

        var remove = js.Calls.Single(c => c.Identifier == "birkNextStorage.removeProjectState");
        ((string[])remove.Args![0]!).Should().Contain(["birknext:frontend-analysis-settings", "birknext:endpoint-discovery", "birknext:api-review",
            "birknext:integration-target-registry", "birknext:detection-snapshots", "birknext:extraction:session"]);
        ((string[])remove.Args![1]!).Should().Contain(["architecture-layout:", "database-layout:"]);
        js.Calls.Should().Contain(c => c.Identifier == "birkNextStorage.setItem" && (string)c.Args![0]! == "birknext:frontend-analysis-settings",
            "the seed is written after the old settings key is removed, so a reload shows the seed, not an empty list");
        js.Calls.FindIndex(c => c.Identifier == "birkNextStorage.removeProjectState")
            .Should().BeLessThan(js.Calls.FindLastIndex(c => c.Identifier == "birkNextStorage.setItem"));
    }

    [Fact]
    public async Task Reset_ClearsWorkspaceAndSessionStores()
    {
        var (provider, _) = Build();
        var workspace = provider.GetRequiredService<IWorkspaceArtifactRepository>();
        workspace.CurrentProject = "m2lb";
        workspace.Set(WorkspaceArtifactType.Specification, "spec");
        provider.GetRequiredService<IWorkspaceStateManager>().NotifyWorkspaceChanged(Guid.NewGuid());
        var mapping = provider.GetRequiredService<IntegrationMappingEvidenceSession>();
        mapping.Record("m2lb-dev", new IntegrationMappingEvidenceCheck { IntegrationId = "person" });
        var reset = provider.GetRequiredService<ApplicationRuntimeResetService>();

        await reset.ClearFrontendRuntimeStateAsync();

        workspace.CurrentProject.Should().BeNull();
        workspace.GetAllArtifacts().Should().BeEmpty();
        provider.GetRequiredService<IWorkspaceStateManager>().CurrentWorkspaceId.Should().BeNull();
        mapping.For("m2lb-dev").Should().BeEmpty();
        reset.LastWarnings.Should().BeEmpty();
    }

    [Fact]
    public async Task Reset_IsIdempotent()
    {
        var (provider, _) = Build();
        var reset = provider.GetRequiredService<ApplicationRuntimeResetService>();
        await reset.ClearFrontendRuntimeStateAsync();
        await reset.ClearFrontendRuntimeStateAsync();
        reset.LastWarnings.Should().BeEmpty();
    }

    private sealed class ConflictHandler(string body) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null) Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task AutoSave_RefusedAsStale_SendsTheEpoch_AndNotifiesTheLayout()
    {
        var handler = new ConflictHandler("""{"code":"stale-reset-epoch","resetEpoch":3}""");
        var workspace = new WorkspaceArtifactRepository();
        var epoch = new LocalDataResetEpoch();
        epoch.Observe(2);
        var notified = new TaskCompletionSource<int>();
        epoch.StaleDetected += e => { notified.TrySetResult(e); return Task.CompletedTask; };
        var service = new WorkspacePersistenceApiService(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") },
            NullLogger<WorkspacePersistenceApiService>.Instance, workspace, epoch);
        workspace.CurrentProject = "m2lb";

        var saved = await service.AutoSaveAsync();

        saved.Should().BeNull();
        handler.Bodies.Single().Should().Contain("\"resetEpoch\":2");
        (await notified.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(3);
        epoch.Value.Should().Be(3);
    }

    [Fact]
    public async Task AnUnrelatedConflict_IsNotTreatedAsAReset()
    {
        var epoch = new LocalDataResetEpoch();
        var raised = false;
        epoch.StaleDetected += _ => { raised = true; return Task.CompletedTask; };
        var service = new WorkspacePersistenceApiService(new HttpClient(new ConflictHandler("""{"error":"name exists"}""")) { BaseAddress = new Uri("http://localhost/") },
            NullLogger<WorkspacePersistenceApiService>.Instance, new WorkspaceArtifactRepository(), epoch);

        await service.AutoSaveAsync();
        await Task.Delay(50);

        raised.Should().BeFalse();
    }
}
