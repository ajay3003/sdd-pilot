using System.Reflection;
using System.Text.Json;
using BirkNext.Api.Controllers;
using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.BrowserAuthentication;
using BirkNext.BrowserAuthentication;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BirkNext.Api.Tests.BrowserAuthentication;

/// <summary>
/// Unattended browser login configuration: states, invalidation, enable/disable, clear, protected credential storage and that the secret
/// never leaves the backend. No browser is started here.
/// </summary>
public sealed class UnattendedLoginConfigurationTests : IDisposable
{
    internal const string Secret = "S3cret!-unattended-pw-7731";
    internal const string LoginName = "automation.reporter@example.test";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "birknext-unattended-" + Guid.NewGuid().ToString("N"));
    private readonly UnattendedLoginStore _store;
    private readonly UnattendedLoginService _service;
    private readonly ListLogger<UnattendedLoginService> _log = new();

    public UnattendedLoginConfigurationTests()
    {
        _store = new UnattendedLoginStore(_dir);
        var runner = new UnattendedLoginRunner(new UnusableLauncher(), NullLogger<UnattendedLoginRunner>.Instance);
        _service = new UnattendedLoginService(_store, runner, TimeProvider.System, _log);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    internal static SaveUnattendedLoginRequest Request(string? loginName = LoginName, string? password = Secret, string identity = "skolenaervaer-unattended-reporter",
        string loginUrl = "https://login.example.test/sign-in", bool enabled = true) => new()
    {
        IdentityAlias = identity, LoginName = loginName, Password = password, LoginUrl = loginUrl, SuccessUrlContains = "/app", SuccessSelector = "#signed-in", Enabled = enabled,
    };

    /// <summary>Marks the current configuration as validated, as a successful validation would.</summary>
    private void MarkValidated(string outcome = nameof(UnattendedLoginOutcome.Succeeded))
    {
        var settings = _store.Load()!;
        _store.Save(settings with { LastValidation = new StoredValidation(outcome, "fake", DateTimeOffset.UtcNow, UnattendedLoginService.Fingerprint(settings), 10) });
    }

    [Fact]
    public void AbsentConfiguration_IsNotConfigured_AndAutomatedExecutionIsUnavailable()
    {
        var view = _service.View();
        view.State.Should().Be(UnattendedLoginState.NotConfigured);
        view.StateLabel.Should().Be("Not configured");
        view.CredentialConfigured.Should().BeFalse();

        var automated = _service.AutomatedReadiness();
        automated.Available.Should().BeFalse();
        automated.Status.Should().Be("Automated browser execution unavailable");
        automated.Reason.Should().Be("Unattended browser authentication not configured");
    }

    [Fact]
    public void Save_CreatesConfiguration_NotVerified_WithCredentialConfiguredFlag()
    {
        var outcome = _service.Save(Request());

        outcome.Errors.Should().BeEmpty();
        var view = outcome.View!;
        view.State.Should().Be(UnattendedLoginState.NotVerified);
        view.IdentityAlias.Should().Be("skolenaervaer-unattended-reporter");
        view.CredentialConfigured.Should().BeTrue();
        view.LoginNameHint.Should().Be("a•••@e•••.test");
        view.CredentialStorage.Should().Contain("Encrypted at rest");
        view.Requirements.Single(r => r.Key == "validated").Met.Should().BeFalse();
        view.Audit.Select(a => a.Event).Should().Contain(["Configuration created", "Credential set"]);
    }

    [Fact]
    public void Validated_IsReady_AndEachRelevantChangeReturnsToNotVerified()
    {
        _service.Save(Request());
        MarkValidated();
        _service.View().State.Should().Be(UnattendedLoginState.Ready);
        _service.AutomatedReadiness().Available.Should().BeTrue();

        // Identity change invalidates.
        _service.Save(Request(loginName: null, password: null, identity: "another-automation-identity"));
        _service.View().State.Should().Be(UnattendedLoginState.NotVerified);

        // Credential replacement invalidates.
        MarkValidated();
        _service.Save(Request(identity: "another-automation-identity", password: Secret + "-rotated"));
        _service.View().State.Should().Be(UnattendedLoginState.NotVerified);
        _service.View().Audit.Select(a => a.Event).Should().Contain("Credential replaced");

        // Login target change invalidates.
        MarkValidated();
        _service.Save(Request(loginName: null, password: null, identity: "another-automation-identity", loginUrl: "https://login.example.test/other"));
        _service.View().State.Should().Be(UnattendedLoginState.NotVerified);

        // A save that changes nothing relevant keeps Ready.
        MarkValidated();
        _service.Save(Request(loginName: null, password: null, identity: "another-automation-identity", loginUrl: "https://login.example.test/other"));
        _service.View().State.Should().Be(UnattendedLoginState.Ready);
    }

    [Fact]
    public void FailedValidation_IsValidationFailed_AndNotAvailableForAutomation()
    {
        _service.Save(Request());
        MarkValidated(nameof(UnattendedLoginOutcome.InteractiveChallengeDetected));

        var view = _service.View();
        view.State.Should().Be(UnattendedLoginState.ValidationFailed);
        view.LastValidation!.Outcome.Should().Be(UnattendedLoginOutcome.InteractiveChallengeDetected);
        _service.AutomatedReadiness().Reason.Should().Contain("MFA or interactive challenge detected");
    }

    [Fact]
    public async Task Disabled_KeepsConfiguration_AndIsUnavailableForExecution()
    {
        _service.Save(Request());
        MarkValidated();

        var view = _service.SetEnabled(false)!;

        view.State.Should().Be(UnattendedLoginState.Disabled);
        view.StateLabel.Should().Be("Configured · Disabled");
        view.IdentityAlias.Should().Be("skolenaervaer-unattended-reporter");
        view.CredentialConfigured.Should().BeTrue();
        _service.AutomatedReadiness().Available.Should().BeFalse();
        var run = await _service.RunAuthenticatedAsync((_, _) => Task.FromResult(true), default);
        run.Outcome.Should().Be(UnattendedLoginOutcome.Disabled);
        run.Result.Should().BeFalse("the consumer never ran");

        _service.SetEnabled(true)!.State.Should().Be(UnattendedLoginState.Ready, "re-enabling an unchanged, validated configuration is Ready again");
    }

    [Fact]
    public async Task Clear_RemovesConfigurationAndCredential()
    {
        _service.Save(Request());
        _service.Clear();

        _service.View().State.Should().Be(UnattendedLoginState.NotConfigured);
        _store.ReadCredential().Should().BeNull();
        Directory.GetFiles(_dir).Should().BeEmpty();
        (await _service.RunAuthenticatedAsync((_, _) => Task.FromResult(true), default)).Outcome.Should().Be(UnattendedLoginOutcome.NotConfigured);
    }

    [Fact]
    public async Task CredentialRemoved_IsCredentialMissing_NotReady()
    {
        _service.Save(Request());
        MarkValidated();
        File.Delete(Path.Combine(_dir, "unattended-login.credential"));

        var view = _service.View();
        view.CredentialConfigured.Should().BeFalse();
        view.State.Should().NotBe(UnattendedLoginState.Ready);
        view.Requirements.Single(r => r.Key == "credential").Met.Should().BeFalse();
        (await _service.ValidateAsync(default)).Outcome.Should().Be(UnattendedLoginOutcome.CredentialMissing);
    }

    [Fact]
    public void Save_Validation_RequiresIdentityTargetSuccessDetectionAndBothCredentialParts()
    {
        _service.Save(new SaveUnattendedLoginRequest()).Errors.Should().Contain(e => e.Contains("automation identity"))
            .And.Contain(e => e.Contains("login URL")).And.Contain(e => e.Contains("successful login")).And.Contain(e => e.Contains("login name and password"));
        _service.Save(Request(password: null)).Errors.Should().ContainSingle(e => e.Contains("both the login name and the password"));
        _service.Save(Request(loginUrl: "http://login.example.test/")).Errors.Should().ContainSingle(e => e.Contains("Use https"));
        _service.Save(Request(loginUrl: "https://user:pw@login.example.test/")).Errors.Should().ContainSingle(e => e.Contains("must not contain"));
        _service.Save(Request(loginUrl: "http://localhost:5000/login")).Errors.Should().BeEmpty("a local loopback login page may use http");
    }

    [Fact]
    public void Secret_NeverAppearsInDtoFilesOrLogs()
    {
        _service.Save(Request());
        var json = JsonSerializer.Serialize(_service.View(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        json.Should().NotContain(Secret).And.NotContain(LoginName).And.Contain("\"credentialConfigured\":true");
        JsonSerializer.Serialize(_service.AutomatedReadiness()).Should().NotContain(Secret).And.NotContain(LoginName);

        foreach (var file in Directory.GetFiles(_dir, "*", SearchOption.AllDirectories))
            File.ReadAllText(file).Should().NotContain(Secret).And.NotContain(LoginName, $"{Path.GetFileName(file)} is never clear text");
        _log.Messages.Should().NotContain(m => m.Contains(Secret) || m.Contains(LoginName));
        _store.ReadCredential()!.ToString().Should().NotContain(Secret);
    }

    [Fact]
    public void Credential_RoundTripsOnlyThroughDataProtection()
    {
        _service.Save(Request());
        var credential = _store.ReadCredential()!;
        credential.LoginName.Should().Be(LoginName);
        credential.Password.Should().Be(Secret);

        File.WriteAllText(Path.Combine(_dir, "unattended-login.credential"), "tampered");
        _store.ReadCredential().Should().BeNull("an unreadable credential is treated as missing, never as a secret");
    }

    [Fact]
    public void ExistingInstallations_LoadWithoutUnattendedFiles_AsNotConfigured()
    {
        Directory.GetFiles(_dir).Should().BeEmpty();
        new UnattendedLoginStore(_dir).Load().Should().BeNull();
        _service.View().State.Should().Be(UnattendedLoginState.NotConfigured);
    }

    [Fact]
    public void EveryMutatingEndpoint_UsesTheConfigurationWritePolicy_AndNoEndpointReturnsTheSecret()
    {
        var methods = typeof(BrowserAuthenticationController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        var mutating = methods.Where(m => m.GetCustomAttributes().Any(a => a is HttpPutAttribute or HttpPostAttribute or HttpDeleteAttribute)).ToList();
        mutating.Should().HaveCount(4);
        mutating.Should().OnlyContain(m => m.GetCustomAttribute<AuthorizeAttribute>()!.Policy == BirkNextPermissions.IntegrationConfigurationWritePolicy);
        typeof(UnattendedLoginView).GetProperties().Select(p => p.Name).Should().NotContain(["Password", "LoginName", "Secret", "Token", "Cookie"]);
    }

    [Fact]
    public void DefaultBrowserAuthenticationMode_IsAttended()
    {
        default(BrowserAuthenticationMode).Should().Be(BrowserAuthenticationMode.AttendedBrowser);
        new BirkNext.Api.Services.FrontendBrowserRuntime.BrowserRuntimeExecutionRequest("https://app.example.test/").ExecutionMode
            .Should().Be(BirkNext.Api.Services.FrontendBrowserRuntime.BrowserRuntimeExecutionMode.AnonymousOwnedBrowser, "existing defaults are unchanged");
        ((int)BirkNext.Api.Services.FrontendBrowserRuntime.BrowserRuntimeExecutionMode.AuthenticatedSessionPage).Should().Be(1, "existing serialized values keep their numbers");
    }

    /// <summary>A launcher that must never be used by these tests.</summary>
    private sealed class UnusableLauncher : IUnattendedLoginBrowserLauncher
    {
        public Task<(Microsoft.Playwright.IPlaywright Playwright, Microsoft.Playwright.IBrowser Browser)> LaunchAsync(CancellationToken ct) =>
            throw new InvalidOperationException("No browser in configuration tests.");
    }
}

internal sealed class ListLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
{
    public List<string> Messages { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Messages) Messages.Add(formatter(state, exception) + (exception is null ? "" : " " + exception));
    }
}
