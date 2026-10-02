using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Services.Integrations;
using BirkNext.Integrations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Tests.Services.Integrations;

/// <summary>
/// The verified M2LB DEV Event Hub runtime-evidence defaults (seed v4): exact identifiers, <c>$Default</c> as a configured assumption,
/// checkpoint endpoint + container from the Person Adapter's configuration, Application Insights without a Log Analytics workspace, no
/// secret anywhere, and a seed upgrade that fills only what is missing and never overwrites a person's edit.
/// </summary>
public sealed class EventHubRuntimeDefaultsTests
{
    private const string DevId = "0b13cd886b4b441896f931ba2ef13907";
    private const string DevUrl = "https://m2lbdev.bufetat.no/";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static AppDbContext Db() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static IntegrationCatalogService Service(AppDbContext db) => new(db, NullLogger<IntegrationCatalogService>.Instance);
    private static IIntegrationAzureCredential Azure(bool enabled) =>
        new IntegrationAzureCredential(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["IntegrationReview:Azure:Enabled"] = enabled.ToString() }).Build());

    private static async Task<IntegrationPlatform> SeededPlatform(AppDbContext? db = null) =>
        (await Service(db ?? Db()).GetWithM2lbTemplateAsync(DevId, "Development", DevUrl)).Platforms.Single(p => p.Id == M2lbDevIntegrationSeed.PlatformId);

    [Fact]
    public async Task TheSeedCarriesTheVerifiedDefaults()
    {
        var platform = await SeededPlatform();
        platform.Name.Should().Be("M2LB DEV Event Hubs");
        platform.ResourceGroup.Should().Be("rg-m2lb-dev-integration-nwe");
        platform.Namespace.Should().Be("evhns-m2lb-dev-nwe-001");
        platform.NamespaceFqdn.Should().Be("evhns-m2lb-dev-nwe-001.servicebus.windows.net");
        platform.Region.Should().Be("Norway East");
        platform.ProducerTechnology.Should().Be("Debezium SQL Server CDC");
        platform.DefaultConsumerAuthentication.Should().Be(IntegrationAuthMechanism.ManagedIdentity);
        var r = platform.RuntimeEvidence!;
        r.EventHubMetadata.Should().BeTrue();
        r.SubscriptionName.Should().Be("m2lb-samhandling-dev");
        r.SubscriptionId.Should().Be("2fcdb9d0-22eb-43b0-b95e-7cbba08c34b0");
        (r.NamespaceSku, r.NamespaceCapacity).Should().Be(("Premium", 1));
        r.ExpectedConsumerGroup.Should().Be("$Default");
        r.ExpectedConsumerGroupProvenance.Should().Be(IntegrationValueProvenance.ConfiguredAssumption);
        IntegrationRuntimeEvidenceSettings.ProvenanceLabel(r.ExpectedConsumerGroupProvenance).Should().Be("Configured assumption");
        r.ExpectedConsumerGroupNote.Should().Contain("m2lb-cdc-dev.birkm2lb.dbo.barntype").And.Contain("Active, 1 partition, 168 h retention, 1 consumer group");
        r.CheckpointBlobEndpoint.Should().Be("https://stm2bbirkdevnwe001.blob.core.windows.net/");
        r.CheckpointContainerName.Should().Be("person-adapter");
        r.ResolvedCheckpointContainerUrl().Should().Be("https://stm2bbirkdevnwe001.blob.core.windows.net/person-adapter");
        r.CheckpointProvenance.Should().Be(IntegrationValueProvenance.SourceConfigurationVerified);
        IntegrationRuntimeEvidenceSettings.ProvenanceLabel(r.CheckpointProvenance).Should().Be("From source configuration (audited, not deployment-verified)");
        r.CheckpointSourceNote.Should().Contain("EventHub__FQDN").And.Contain("Storage__BlobEndpoint").And.Contain("Storage__ContainerName");
        r.ApplicationInsightsResourceName.Should().Be("appi-m2lb-dev-nwe-001");
        r.ApplicationInsightsResourceGroup.Should().Be("rg-m2lb-dev-shared-nwe");
        r.ApplicationInsightsConfigured.Should().BeTrue();
        r.ApplicationInsightsResourceId().Should().Be("/subscriptions/2fcdb9d0-22eb-43b0-b95e-7cbba08c34b0/resourceGroups/rg-m2lb-dev-shared-nwe/providers/Microsoft.Insights/components/appi-m2lb-dev-nwe-001");
        r.TelemetryWorkspaceId.Should().BeNull("the Container Apps environment has no Log Analytics workspace; none is required");
        r.ContainerAppsLogDestination.Should().Be("azure-monitor");
        r.TelemetryConfigured.Should().BeTrue();
        (r.ConsumerApplicationName, r.ConsumerApplicationResourceGroup).Should().Be(("ca-m2lb-person-adp-dev-nwe-001", "rg-m2lb-dev-apps-nwe"));
        r.ReviewWindowHours.Should().Be(24);
        r.MaxConsumerLagEvents.Should().BeNull("no threshold is invented");
        r.MaxCheckpointAgeMinutes.Should().BeNull();
        r.Validate().Should().BeNull();
    }

    [Fact]
    public async Task IntegrationsKeepNoOwnConsumerGroup_TheAssumptionLivesOnThePlatformOnly()
    {
        var catalog = await Service(Db()).GetWithM2lbTemplateAsync(DevId, "Development", DevUrl);
        catalog.Integrations.Should().OnlyContain(i => i.ConsumerGroup == null);
        catalog.Integrations.Single(i => i.Id.EndsWith("dbo.Person")).Consumer.ContainerApp.Should().Be("ca-m2lb-person-adp-dev-nwe-001");
    }

    [Fact]
    public async Task NoSecretIsSeeded()
    {
        var json = JsonSerializer.Serialize(await SeededPlatform(), Json);
        json.Should().NotContainAny("InstrumentationKey", "APPLICATIONINSIGHTS_CONNECTION_STRING", "ConnectionString", "AccountKey", "SharedAccessKey", "sig=", "?sv=", "eyJ");
    }

    [Fact]
    public async Task TheDefaultsApplyOnlyToM2lbDev()
    {
        var qa = await Service(Db()).GetAsync("qa", "Test", "https://m2lbqa.bufetat.no/");
        qa.Platforms.Should().BeEmpty();
        var otherDev = await Service(Db()).GetAsync("other", "Development", "https://example.dev/");
        otherDev.Platforms.Should().BeEmpty();
    }

    /// <summary>Rewinds a DB to a v3 environment whose Event Hub platform has the given document.</summary>
    private static async Task AsVersion3(AppDbContext db, Func<IntegrationPlatform, IntegrationPlatform> platform, bool userModified)
    {
        var record = await db.IntegrationPlatforms.SingleAsync(p => p.Id == M2lbDevIntegrationSeed.PlatformId);
        var current = IntegrationCatalogService.FromRecord(record);
        record.DocumentJson = JsonSerializer.Serialize(platform(current) with { UserModified = userModified }, Json);
        record.UserModified = userModified;
        (await db.IntegrationEnvironmentStates.SingleAsync()).SeedVersion = 3;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task UpgradeFromV3_FillsTheDefaultsIntoAnUneditedPlatform()
    {
        await using var db = Db();
        await SeededPlatform(db);
        await AsVersion3(db, p => p with { RuntimeEvidence = null, Region = "nwe" }, userModified: false);
        var upgraded = await SeededPlatform(db);
        upgraded.RuntimeEvidence.Should().BeEquivalentTo(M2lbDevIntegrationSeed.RuntimeDefaults());
        upgraded.Region.Should().Be("Norway East");
        upgraded.UserModified.Should().BeFalse("a seed upgrade is not a person's edit");
        (await db.IntegrationEnvironmentStates.SingleAsync()).SeedVersion.Should().Be(M2lbDevIntegrationSeed.Version);
    }

    [Fact]
    public async Task UpgradeFromV3_NeverOverwritesAValue_AndFillsOnlyMissingFields()
    {
        await using var db = Db();
        await SeededPlatform(db);
        await AsVersion3(db, p => p with { RuntimeEvidence = new() { ReviewWindowHours = 12, SubscriptionName = "renamed" } }, userModified: false);
        var r = (await SeededPlatform(db)).RuntimeEvidence!;
        r.ReviewWindowHours.Should().Be(12);
        r.SubscriptionName.Should().Be("renamed");
        r.SubscriptionId.Should().Be("2fcdb9d0-22eb-43b0-b95e-7cbba08c34b0");
        r.ExpectedConsumerGroup.Should().Be("$Default");
        r.EventHubMetadata.Should().BeFalse("a stored choice is kept");
    }

    [Fact]
    public async Task UpgradeFromV3_LeavesPersonEditedRuntimeSettingsExactlyAsTheyAre()
    {
        await using var db = Db();
        await SeededPlatform(db);
        var edited = new IntegrationRuntimeEvidenceSettings { SubscriptionId = "11111111-1111-1111-1111-111111111111", CheckpointContainerUrl = "https://other.blob.core.windows.net/cp" };
        await AsVersion3(db, p => p with { RuntimeEvidence = edited, Region = "custom region", MonitoringUrl = "https://dash" }, userModified: true);
        var platform = await SeededPlatform(db);
        platform.RuntimeEvidence.Should().BeEquivalentTo(edited);
        platform.Region.Should().Be("custom region");
        platform.MonitoringUrl.Should().Be("https://dash");
    }

    [Fact]
    public async Task UpgradeFromV3_ADeletedPlatformIsNotBroughtBack()
    {
        await using var db = Db();
        await SeededPlatform(db);
        db.IntegrationPlatforms.Remove(await db.IntegrationPlatforms.SingleAsync(p => p.Id == M2lbDevIntegrationSeed.PlatformId));
        (await db.IntegrationEnvironmentStates.SingleAsync()).SeedVersion = 3;
        await db.SaveChangesAsync();
        var catalog = await Service(db).GetWithM2lbTemplateAsync(DevId, "Development", DevUrl);
        catalog.Platforms.Should().NotContain(p => p.Id == M2lbDevIntegrationSeed.PlatformId);
    }

    [Fact]
    public async Task AzureDisabled_SourcesAreConfiguredButNothingRuns()
    {
        var platform = await SeededPlatform();
        var off = Azure(false);
        new AzureEventHubMetadataSource(off, NullLogger<AzureEventHubMetadataSource>.Instance).Describe(platform).Reason.Should().Be(IntegrationAzureCredential.DisabledMessage);
        new BlobCheckpointEvidenceSource(off, NullLogger<BlobCheckpointEvidenceSource>.Instance).Describe(platform).State.Should().Be(IntegrationEvidenceState.NotConfigured);
        new LogAnalyticsTelemetrySource(off, NullLogger<LogAnalyticsTelemetrySource>.Instance).Describe(platform).Reason.Should().Be(IntegrationAzureCredential.DisabledMessage);
    }

    [Fact]
    public async Task AzureEnabled_TheSeededSourcesAreReady_WithoutALogAnalyticsWorkspace()
    {
        var platform = await SeededPlatform();
        var on = Azure(true);
        new BlobCheckpointEvidenceSource(on, NullLogger<BlobCheckpointEvidenceSource>.Instance).Describe(platform).State.Should().Be(IntegrationEvidenceState.Available);
        var telemetry = new LogAnalyticsTelemetrySource(on, NullLogger<LogAnalyticsTelemetrySource>.Instance).Describe(platform);
        telemetry.State.Should().Be(IntegrationEvidenceState.Available);
        telemetry.Reason.Should().Contain("appi-m2lb-dev-nwe-001").And.Contain("No Log Analytics workspace is required");
        new ArmConsumerGroupSource(on, new HttpClient(), NullLogger<ArmConsumerGroupSource>.Instance).Describe(platform).State.Should().Be(IntegrationEvidenceState.Available);
    }

    [Fact]
    public void WithoutAnyTelemetrySource_TheReasonNoLongerDemandsAWorkspace()
    {
        var platform = M2lbDevIntegrationSeed.Platform("dev", DateTimeOffset.UtcNow) with { RuntimeEvidence = new() };
        var reason = new LogAnalyticsTelemetrySource(Azure(true), NullLogger<LogAnalyticsTelemetrySource>.Instance).Describe(platform).Reason;
        reason.Should().Contain("Application Insights resource").And.NotContain("Log Analytics workspace");
    }

    [Fact]
    public void ResourceQueriesUseTheClassicSchema_AggregateOnly()
    {
        foreach (var query in new[] { LogAnalyticsTelemetrySource.ExceptionsQuery("r", classic: true), LogAnalyticsTelemetrySource.TracesQuery("r", classic: true), LogAnalyticsTelemetrySource.DependenciesQuery("r", classic: true) })
        {
            query.Should().Contain("cloud_RoleName == \"r\"").And.Contain("summarize").And.Contain("timestamp");
            query.Should().NotContainAny("AppExceptions", "AppTraces", "AppDependencies", "AppRoleName", "TimeGenerated", "project ", "take ");
        }
        LogAnalyticsTelemetrySource.ExceptionsQuery("r").Should().StartWith("AppExceptions", "the workspace schema is unchanged");
    }

    [Theory]
    [InlineData("https://stm2bbirkdevnwe001.blob.core.windows.net/?sv=2024-01-01&sig=abc", "never a SAS URL")]
    [InlineData("https://user:pw@stm2bbirkdevnwe001.blob.core.windows.net/", "credentials")]
    [InlineData("https://stm2bbirkdevnwe001.blob.core.windows.net/?token=abc", "token")]
    [InlineData("http://stm2bbirkdevnwe001.blob.core.windows.net/", "https")]
    [InlineData("https://stm2bbirkdevnwe001.blob.core.windows.net/person-adapter", "container is a separate field")]
    public void TheCheckpointEndpointIsAPlainAccountEndpoint(string endpoint, string reason) =>
        (M2lbDevIntegrationSeed.RuntimeDefaults() with { CheckpointBlobEndpoint = endpoint }).Validate().Should().Contain(reason);

    [Theory]
    [InlineData("DefaultEndpointsProtocol=https;AccountName=a;AccountKey=abc==;EndpointSuffix=core.windows.net")]
    [InlineData("InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=https://x/")]
    [InlineData("Endpoint=sb://x.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=v")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.sig")]
    [InlineData("Bearer abc.def")]
    public void AnyCredentialInAnyFieldIsRejected(string secret)
    {
        var d = M2lbDevIntegrationSeed.RuntimeDefaults();
        foreach (var settings in new[]
                 {
                     d with { ApplicationInsightsResourceName = secret }, d with { ApplicationInsightsResourceGroup = secret }, d with { SubscriptionName = secret },
                     d with { CheckpointSourceNote = secret }, d with { ExpectedConsumerGroupNote = secret }, d with { CheckpointContainerName = secret },
                     d with { ExpectedConsumerGroup = secret }, d with { ConsumerApplicationName = secret }, d with { ContainerAppsLogDestination = secret },
                 })
            settings.Validate().Should().NotBeNull($"\"{secret}\" is a credential, not an identifier");
    }

    [Theory]
    [InlineData("Person-Adapter")]
    [InlineData("ab")]
    [InlineData("double--hyphen")]
    public void TheContainerNameFollowsAzureRules(string container) =>
        (M2lbDevIntegrationSeed.RuntimeDefaults() with { CheckpointContainerName = container }).Validate().Should().Contain("container name");

    [Fact]
    public void EndpointAndContainerAreAPair()
    {
        (M2lbDevIntegrationSeed.RuntimeDefaults() with { CheckpointContainerName = null }).Validate().Should().Contain("both");
        new IntegrationRuntimeEvidenceSettings { ExpectedConsumerGroup = "my group!" }.Validate().Should().Contain("consumer-group name");
        new IntegrationRuntimeEvidenceSettings { ExpectedConsumerGroup = "$Default" }.Validate().Should().BeNull();
        new IntegrationRuntimeEvidenceSettings { NamespaceCapacity = 0 }.Validate().Should().NotBeNull();
    }

    [Fact]
    public async Task TheCatalogEndpointReportsAzureExecutionSeparately()
    {
        await using var db = Db();
        var controller = new BirkNext.Api.Controllers.IntegrationsController(Service(db));
        await Service(db).ApplyTemplateAsync(DevId, M2lbDevIntegrationSeed.Name);
        var off = (IntegrationCatalog)((Microsoft.AspNetCore.Mvc.OkObjectResult)(await controller.Get(DevId, "Development", DevUrl, Azure(false), CancellationToken.None)).Result!).Value!;
        off.AzureRuntimeEnabled.Should().BeFalse();
        off.Platforms.Single(p => p.Id == M2lbDevIntegrationSeed.PlatformId).RuntimeEvidence!.ExpectedConsumerGroup.Should().Be("$Default", "configured sources exist whether or not Azure runs");
        var on = (IntegrationCatalog)((Microsoft.AspNetCore.Mvc.OkObjectResult)(await controller.Get(DevId, "Development", DevUrl, Azure(true), CancellationToken.None)).Result!).Value!;
        on.AzureRuntimeEnabled.Should().BeTrue();
    }

    [Fact]
    public void TheEvidenceAdaptersNeverWrite()
    {
        var root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "BirkNext.Api"))) root = Path.GetDirectoryName(root)!;
        // Code only: doc comments may name the SDK types being avoided.
        var source = string.Join('\n', File.ReadAllLines(Path.Combine(root, "BirkNext.Api", "Services", "Integrations", "AzureIntegrationEvidence.cs")).Where(l => !l.TrimStart().StartsWith("//")));
        source.Should().Contain("BlobContainerClient", "the file under guard is the evidence adapter file");
        var forbidden = new System.Text.RegularExpressions.Regex(
            @"\b(EventHubProducerClient|EventProcessorClient|BlobClient|BlobLeaseClient|ReadEventsAsync|ReadEventsFromPartitionAsync|SendAsync\(\s*new\s+EventData|UploadAsync|SetMetadataAsync|DeleteAsync|DeleteIfExistsAsync|CreateIfNotExistsAsync|AcquireAsync|CreateOrUpdate)\b|HttpMethod\.(Put|Post|Delete|Patch)\b");
        forbidden.IsMatch("await blob.UploadAsync(x)").Should().BeTrue("the guard itself must match a write");
        forbidden.IsMatch(source).Should().BeFalse("runtime evidence is read-only: no send, receive, checkpoint write, lease, blob write or ARM write");
    }
}
