using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;
using Microsoft.JSInterop;
using Moq;
using Xunit;

namespace BirkNext.Web.Tests.Services;

public sealed class FrontendAnalysisContextFactoryTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (FrontendAnalysisSettingsService Settings, Mock<IJSRuntime> Js) CreateSettings()
    {
        var mockJs = new Mock<IJSRuntime>();
        // Simulate empty localStorage so LoadAsync falls through to seed data or returns empty
        mockJs
            .Setup(j => j.InvokeAsync<string?>("birkNextStorage.getItem", It.IsAny<object[]>()))
            .ReturnsAsync((string?)null);
        return (new FrontendAnalysisSettingsService(), mockJs);
    }

    private static Mock<IFrontendAnalysisSettingsService> MockSettings(
        FrontendAnalysisProfile? activeProfile,
        ProfileValidationResult? validation = null)
    {
        var mock = new Mock<IFrontendAnalysisSettingsService>();
        mock.Setup(s => s.ActiveProfile).Returns(activeProfile);
        mock.Setup(s => s.Settings).Returns(new FrontendAnalysisSettings
        {
            Profiles        = activeProfile is not null ? [activeProfile] : [],
            ActiveProfileId = activeProfile?.Id
        });
        mock.Setup(s => s.ValidateProfile(It.IsAny<FrontendAnalysisProfile>()))
            .Returns(validation ?? new ProfileValidationResult());
        mock.Setup(s => s.LoadAsync(It.IsAny<IJSRuntime>()))
            .Returns(Task.CompletedTask);
        return mock;
    }

    private static PlaceholderAuthenticatedBrowserSessionService CreateSessionService(
        IFrontendAnalysisSettingsService settings) =>
        new(settings);

    private static FrontendAnalysisContextFactory CreateFactory(
        IFrontendAnalysisSettingsService settings,
        IAuthenticatedBrowserSessionService sessionService,
        IJSRuntime? js = null) =>
        new(settings, sessionService, js ?? new Mock<IJSRuntime>().Object);

    // ── Factory uses active profile ───────────────────────────────────────────

    [Fact]
    public async Task GetActiveContextAsync_UsesActiveProfileTargetUrl()
    {
        var profile = new FrontendAnalysisProfile
        {
            Id = "p1", Name = "QA", EnvironmentType = FrontendEnvironmentType.QA,
            TargetUrl = "https://example-qa.local",
            Performance = new(), CoreWebVitals = new(), Security = new(), Features = new()
        };

        var settings    = MockSettings(profile);
        var sessionSvc  = CreateSessionService(settings.Object);
        var factory     = CreateFactory(settings.Object, sessionSvc);

        var ctx = await factory.GetActiveContextAsync();

        ctx.TargetUrl.Should().Be("https://example-qa.local");
        ctx.ActiveProfile.Name.Should().Be("QA");
    }

    [Fact]
    public async Task GetActiveContextAsync_MapsAllowedHosts()
    {
        var profile = new FrontendAnalysisProfile
        {
            Id = "p1", Name = "P", EnvironmentType = FrontendEnvironmentType.QA,
            AllowedRestHosts = ["api.example.com"],
            Performance = new(), CoreWebVitals = new(),
            Security = new() { AllowedBackendDomains = ["backend.example.com"] },
            Features = new()
        };

        var settings   = MockSettings(profile);
        var sessionSvc = CreateSessionService(settings.Object);
        var factory    = CreateFactory(settings.Object, sessionSvc);

        var ctx = await factory.GetActiveContextAsync();

        ctx.AllowedRestHosts.Should().Contain("api.example.com");
        ctx.AllowedBackendDomains.Should().Contain("backend.example.com");
    }

    [Fact]
    public async Task GetActiveContextAsync_MapsPerformanceThresholds()
    {
        var profile = new FrontendAnalysisProfile
        {
            Id = "p1", Name = "P", EnvironmentType = FrontendEnvironmentType.QA,
            Performance = new FrontendPerformanceThresholds { MaxStartupRequests = 42 },
            CoreWebVitals = new(), Security = new(), Features = new()
        };

        var settings   = MockSettings(profile);
        var sessionSvc = CreateSessionService(settings.Object);
        var factory    = CreateFactory(settings.Object, sessionSvc);

        var ctx = await factory.GetActiveContextAsync();

        ctx.PerformanceThresholds.MaxStartupRequests.Should().Be(42);
    }

    [Fact]
    public async Task GetActiveContextAsync_MapsAuthenticationType()
    {
        var profile = new FrontendAnalysisProfile
        {
            Id = "p1", Name = "P", EnvironmentType = FrontendEnvironmentType.QA,
            Authentication = new FrontendAuthenticationSettings
            {
                AuthenticationType     = FrontendAuthenticationType.MicrosoftEntraId,
                RequiresAuthentication = true
            },
            Performance = new(), CoreWebVitals = new(), Security = new(), Features = new()
        };

        var settings   = MockSettings(profile);
        var sessionSvc = CreateSessionService(settings.Object);
        var factory    = CreateFactory(settings.Object, sessionSvc);

        var ctx = await factory.GetActiveContextAsync();

        ctx.AuthenticationType.Should().Be(FrontendAuthenticationType.MicrosoftEntraId);
        ctx.RequiresAuthentication.Should().BeTrue();
    }

    [Fact]
    public async Task GetActiveContextAsync_MapsFeatureToggles()
    {
        var profile = new FrontendAnalysisProfile
        {
            Id = "p1", Name = "P", EnvironmentType = FrontendEnvironmentType.QA,
            Features = new FrontendAnalysisFeatureToggles { AssetDiscovery = false },
            Performance = new(), CoreWebVitals = new(), Security = new()
        };

        var settings   = MockSettings(profile);
        var sessionSvc = CreateSessionService(settings.Object);
        var factory    = CreateFactory(settings.Object, sessionSvc);

        var ctx = await factory.GetActiveContextAsync();

        ctx.FeatureToggles.AssetDiscovery.Should().BeFalse();
    }

    // ── Missing active profile: fallback ─────────────────────────────────────

    [Fact]
    public async Task GetActiveContextAsync_WhenNoActiveProfile_ReturnsFallbackWithWarning()
    {
        var settings = MockSettings(null);
        var sessionSvc = CreateSessionService(settings.Object);
        var factory    = CreateFactory(settings.Object, sessionSvc);

        var ctx = await factory.GetActiveContextAsync();

        ctx.ValidationWarnings.Should().ContainSingle(w => w.Contains("No active"));
    }

    [Fact]
    public async Task GetActiveContextAsync_WhenNoActiveProfile_TargetUrlIsEmpty()
    {
        var settings   = MockSettings(null);
        var sessionSvc = CreateSessionService(settings.Object);
        var factory    = CreateFactory(settings.Object, sessionSvc);

        var ctx = await factory.GetActiveContextAsync();

        ctx.HasTargetUrl.Should().BeFalse();
    }

    [Fact]
    public async Task GetActiveContextAsync_WhenNoActiveProfileButFallbackExists_UsesFallbackProfile()
    {
        var fallback = new FrontendAnalysisProfile
        {
            Id = "fb", Name = "Fallback", EnvironmentType = FrontendEnvironmentType.Local,
            TargetUrl = "https://localhost:5001",
            Performance = new(), CoreWebVitals = new(), Security = new(), Features = new()
        };

        // Active profile is null but Settings.Profiles has one entry
        var mock = new Mock<IFrontendAnalysisSettingsService>();
        mock.Setup(s => s.ActiveProfile).Returns((FrontendAnalysisProfile?)null);
        mock.Setup(s => s.Settings).Returns(new FrontendAnalysisSettings
        {
            Profiles        = [fallback],
            ActiveProfileId = null
        });
        mock.Setup(s => s.ValidateProfile(It.IsAny<FrontendAnalysisProfile>()))
            .Returns(new ProfileValidationResult());
        mock.Setup(s => s.LoadAsync(It.IsAny<IJSRuntime>()))
            .Returns(Task.CompletedTask);

        var sessionSvc = CreateSessionService(mock.Object);
        var factory    = CreateFactory(mock.Object, sessionSvc);

        var ctx = await factory.GetActiveContextAsync();

        ctx.TargetUrl.Should().Be("https://localhost:5001");
        ctx.ActiveProfile.Name.Should().Be("Fallback");
    }

    // ── Validation errors/warnings passed through ─────────────────────────────

    [Fact]
    public async Task GetActiveContextAsync_ForwardsValidationErrors()
    {
        var profile = new FrontendAnalysisProfile
        {
            Id = "p1", Name = "Bad", EnvironmentType = FrontendEnvironmentType.QA,
            TargetUrl = "not-a-url",
            Performance = new(), CoreWebVitals = new(), Security = new(), Features = new()
        };

        var validation = new ProfileValidationResult
        {
            Errors   = ["Target URL is not valid."],
            Warnings = []
        };

        var settings   = MockSettings(profile, validation);
        var sessionSvc = CreateSessionService(settings.Object);
        var factory    = CreateFactory(settings.Object, sessionSvc);

        var ctx = await factory.GetActiveContextAsync();

        ctx.HasValidationErrors.Should().BeTrue();
        ctx.ValidationErrors.Should().Contain("Target URL is not valid.");
    }

    [Fact]
    public async Task GetActiveContextAsync_ForwardsValidationWarnings()
    {
        var profile = new FrontendAnalysisProfile
        {
            Id = "p1", Name = "Prod-local", EnvironmentType = FrontendEnvironmentType.Production,
            TargetUrl = "https://localhost:5001",
            Performance = new(), CoreWebVitals = new(), Security = new(), Features = new()
        };

        var validation = new ProfileValidationResult
        {
            Errors   = [],
            Warnings = ["Production profile is targeting a localhost URL."]
        };

        var settings   = MockSettings(profile, validation);
        var sessionSvc = CreateSessionService(settings.Object);
        var factory    = CreateFactory(settings.Object, sessionSvc);

        var ctx = await factory.GetActiveContextAsync();

        ctx.HasValidationWarnings.Should().BeTrue();
        ctx.ValidationWarnings.Should().Contain(w => w.Contains("localhost"));
    }

    // ── Authentication required but session unavailable ────────────────────────

    [Fact]
    public async Task GetActiveContextAsync_AuthRequired_SessionUnavailable()
    {
        var profile = new FrontendAnalysisProfile
        {
            Id = "p1", Name = "P", EnvironmentType = FrontendEnvironmentType.Production,
            Authentication = new FrontendAuthenticationSettings
            {
                AuthenticationType     = FrontendAuthenticationType.MicrosoftEntraId,
                RequiresAuthentication = true
            },
            Performance = new(), CoreWebVitals = new(), Security = new(), Features = new()
        };

        var settings   = MockSettings(profile);
        var sessionSvc = CreateSessionService(settings.Object);
        var factory    = CreateFactory(settings.Object, sessionSvc);

        var ctx = await factory.GetActiveContextAsync();

        ctx.IsAuthenticatedSessionAvailable.Should().BeFalse();
        ctx.AuthRequiredButUnavailable.Should().BeTrue();
    }

    [Fact]
    public async Task GetActiveContextAsync_AuthNotRequired_SessionStatusIsNotRequired()
    {
        var profile = new FrontendAnalysisProfile
        {
            Id = "p1", Name = "P", EnvironmentType = FrontendEnvironmentType.QA,
            Authentication = new FrontendAuthenticationSettings { RequiresAuthentication = false },
            Performance = new(), CoreWebVitals = new(), Security = new(), Features = new()
        };

        var settings   = MockSettings(profile);
        var sessionSvc = CreateSessionService(settings.Object);
        var factory    = CreateFactory(settings.Object, sessionSvc);

        var ctx = await factory.GetActiveContextAsync();

        ctx.AuthRequiredButUnavailable.Should().BeFalse();
    }

    // ── Session service status ────────────────────────────────────────────────

    [Fact]
    public async Task SessionService_AuthNotRequired_ReturnsNotRequired()
    {
        var svc     = new FrontendAnalysisSettingsService();
        var profile = svc.CreateProfile("P", FrontendEnvironmentType.QA);
        profile.Authentication.RequiresAuthentication = false;
        svc.SelectActiveProfile(profile.Id);

        var sessionSvc = new PlaceholderAuthenticatedBrowserSessionService(svc);
        var status = await sessionSvc.GetStatusAsync();

        status.Should().Be(AuthenticatedBrowserSessionStatus.NotRequired);
    }

    [Fact]
    public async Task SessionService_AuthRequired_ReturnsRequiredButNotAvailable()
    {
        var svc     = new FrontendAnalysisSettingsService();
        var profile = svc.CreateProfile("P", FrontendEnvironmentType.Production);
        profile.Authentication.RequiresAuthentication = true;
        svc.SelectActiveProfile(profile.Id);

        var sessionSvc = new PlaceholderAuthenticatedBrowserSessionService(svc);
        var status = await sessionSvc.GetStatusAsync();

        status.Should().Be(AuthenticatedBrowserSessionStatus.RequiredButNotAvailable);
    }

    [Fact]
    public async Task SessionService_GetOrCreateSession_AuthNotRequired_ReturnsSafeMessage()
    {
        var svc = new FrontendAnalysisSettingsService();
        svc.CreateProfile("P", FrontendEnvironmentType.QA);

        var sessionSvc = new PlaceholderAuthenticatedBrowserSessionService(svc);
        var ctx = new FrontendAnalysisContext { RequiresAuthentication = false };

        var session = await sessionSvc.GetOrCreateSessionAsync(ctx);

        session.StatusMessage.Should().NotBeNullOrEmpty();
        session.StatusMessage.Should().NotContain("password");
        session.StatusMessage.Should().NotContain("token");
        session.StatusMessage.Should().NotContain("secret");
    }

    [Fact]
    public async Task SessionService_GetOrCreateSession_AuthRequired_ReturnsPlaceholderMessage()
    {
        var svc = new FrontendAnalysisSettingsService();

        var sessionSvc = new PlaceholderAuthenticatedBrowserSessionService(svc);
        var ctx = new FrontendAnalysisContext
        {
            RequiresAuthentication = true,
            TargetUrl              = "https://example.com",
            AuthenticationType     = FrontendAuthenticationType.MicrosoftEntraId
        };

        var session = await sessionSvc.GetOrCreateSessionAsync(ctx);

        session.StatusMessage.Should().Contain("not implemented");
        session.IsAuthenticated.Should().BeFalse();
    }

    // ── Safe diagnostics: no secrets ─────────────────────────────────────────

    [Fact]
    public async Task GetActiveContextAsync_DiagnosticsDoNotExposeSecrets()
    {
        var profile = new FrontendAnalysisProfile
        {
            Id = "p1", Name = "P", EnvironmentType = FrontendEnvironmentType.Production,
            TargetUrl = "https://example.com",
            Authentication = new FrontendAuthenticationSettings
            {
                ExpectedClientId = "super-secret-client-id",
                ExpectedTenant   = "my-tenant-id",
                RequiresAuthentication = true
            },
            Performance = new(), CoreWebVitals = new(), Security = new(), Features = new()
        };

        var settings   = MockSettings(profile);
        var sessionSvc = CreateSessionService(settings.Object);
        var factory    = CreateFactory(settings.Object, sessionSvc);

        var ctx = await factory.GetActiveContextAsync();

        // Context exposes safe fields only — never raw client IDs, tenant IDs, or secrets
        ctx.TargetUrl.Should().Be("https://example.com");

        // The context model does NOT have a ClientId or TenantId property on the root level
        // Security settings are intentionally NOT flattened to the context root
        var ctxStr = System.Text.Json.JsonSerializer.Serialize(ctx);
        ctxStr.Should().NotContain("super-secret-client-id");
    }

    [Fact]
    public async Task SessionService_SafeDiagnostics_DoNotContainSecrets()
    {
        var svc = new FrontendAnalysisSettingsService();
        var sessionSvc = new PlaceholderAuthenticatedBrowserSessionService(svc);

        var ctx = new FrontendAnalysisContext
        {
            RequiresAuthentication = true,
            TargetUrl              = "https://example.com",
            AuthenticationType     = FrontendAuthenticationType.MicrosoftEntraId
        };

        var session = await sessionSvc.GetOrCreateSessionAsync(ctx);

        foreach (var diag in session.SafeDiagnostics)
        {
            diag.Should().NotContain("password");
            diag.Should().NotContain("token");
            diag.Should().NotContain("secret");
        }
    }

    // ── Security Review context consumption ───────────────────────────────────

    [Fact]
    public async Task ContextFactory_SecurityReview_UsesContextTargetUrl()
    {
        var profile = new FrontendAnalysisProfile
        {
            Id = "p1", Name = "P", EnvironmentType = FrontendEnvironmentType.QA,
            TargetUrl = "https://example-qa.local",
            Security = new FrontendSecuritySettings
            {
                ExpectedAuthority = "https://login.microsoftonline.com/tenant"
            },
            Performance = new(), CoreWebVitals = new(), Features = new()
        };

        var settings   = MockSettings(profile);
        var sessionSvc = CreateSessionService(settings.Object);
        var factory    = CreateFactory(settings.Object, sessionSvc);

        var ctx = await factory.GetActiveContextAsync();

        // Security review should use these values (not its own local config)
        ctx.TargetUrl.Should().Be("https://example-qa.local");
        ctx.SecuritySettings.ExpectedAuthority.Should().Be("https://login.microsoftonline.com/tenant");
    }

    // ── Performance Review context consumption ────────────────────────────────

    [Fact]
    public async Task ContextFactory_PerformanceReview_UsesContextThresholds()
    {
        var profile = new FrontendAnalysisProfile
        {
            Id = "p1", Name = "P", EnvironmentType = FrontendEnvironmentType.QA,
            TargetUrl = "https://example-qa.local",
            Performance = new FrontendPerformanceThresholds
            {
                Mode                 = FrontendThresholdMode.Strict,
                MaxStartupSizeBytes  = 5L * 1024 * 1024,
                MaxStartupRequests   = 20
            },
            CoreWebVitals = new(), Security = new(), Features = new()
        };

        var settings   = MockSettings(profile);
        var sessionSvc = CreateSessionService(settings.Object);
        var factory    = CreateFactory(settings.Object, sessionSvc);

        var ctx = await factory.GetActiveContextAsync();

        // Performance review should read thresholds from the context
        ctx.PerformanceThresholds.Mode.Should().Be(FrontendThresholdMode.Strict);
        ctx.PerformanceThresholds.MaxStartupSizeBytes.Should().Be(5L * 1024 * 1024);
        ctx.PerformanceThresholds.MaxStartupRequests.Should().Be(20);
    }

    [Fact]
    public async Task ContextFactory_PerformanceReview_UsesContextCoreWebVitals()
    {
        var profile = new FrontendAnalysisProfile
        {
            Id = "p1", Name = "P", EnvironmentType = FrontendEnvironmentType.QA,
            CoreWebVitals = new CoreWebVitalsThresholds { LcpGoodMs = 2000, LcpPoorMs = 3500 },
            Performance = new(), Security = new(), Features = new()
        };

        var settings   = MockSettings(profile);
        var sessionSvc = CreateSessionService(settings.Object);
        var factory    = CreateFactory(settings.Object, sessionSvc);

        var ctx = await factory.GetActiveContextAsync();

        ctx.CoreWebVitalsThresholds.LcpGoodMs.Should().Be(2000);
        ctx.CoreWebVitalsThresholds.LcpPoorMs.Should().Be(3500);
    }

    // ── Feature toggles passed to both reviews ────────────────────────────────

    [Fact]
    public async Task ContextFactory_FeatureToggles_PassedToSecurityContext()
    {
        var profile = new FrontendAnalysisProfile
        {
            Id = "p1", Name = "P", EnvironmentType = FrontendEnvironmentType.QA,
            Features = new FrontendAnalysisFeatureToggles
            {
                SecurityHeaderReview = false,
                ConfigurationExposureReview = false
            },
            Performance = new(), CoreWebVitals = new(), Security = new()
        };

        var settings   = MockSettings(profile);
        var sessionSvc = CreateSessionService(settings.Object);
        var factory    = CreateFactory(settings.Object, sessionSvc);

        var ctx = await factory.GetActiveContextAsync();

        ctx.FeatureToggles.SecurityHeaderReview.Should().BeFalse();
        ctx.FeatureToggles.ConfigurationExposureReview.Should().BeFalse();
    }

    [Fact]
    public async Task ContextFactory_FeatureToggles_PassedToPerformanceContext()
    {
        var profile = new FrontendAnalysisProfile
        {
            Id = "p1", Name = "P", EnvironmentType = FrontendEnvironmentType.QA,
            Features = new FrontendAnalysisFeatureToggles
            {
                StartupAnalysis = false,
                CachingReview   = false
            },
            Performance = new(), CoreWebVitals = new(), Security = new()
        };

        var settings   = MockSettings(profile);
        var sessionSvc = CreateSessionService(settings.Object);
        var factory    = CreateFactory(settings.Object, sessionSvc);

        var ctx = await factory.GetActiveContextAsync();

        ctx.FeatureToggles.StartupAnalysis.Should().BeFalse();
        ctx.FeatureToggles.CachingReview.Should().BeFalse();
    }
}
