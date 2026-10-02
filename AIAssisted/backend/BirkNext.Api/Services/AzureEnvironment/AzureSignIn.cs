using System.Diagnostics;
using Path = System.IO.Path;
using BirkNext.Api.Services.AuthenticatedReview;
using BirkNext.Api.Services.ManagedEdge;
using BirkNext.AzureEnvironment;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;

namespace BirkNext.Api.Services.AzureEnvironment;

/// <summary>A token as the broker hands it over. Lives only in this process's memory; never persisted, logged or returned to the browser.</summary>
public sealed record AzureToken(string AccessToken, DateTimeOffset ExpiresOn, string? Account, string? TenantId);

/// <summary>The identity-platform calls (MSAL in production, a fake in tests). Interactive and device-code sign-in are the person's own:
/// MFA, Conditional Access and PIM-activated roles apply as usual. There is no username/password or client-secret path.</summary>
public interface IAzureTokenBroker
{
    Task<AzureToken> InteractiveAsync(Func<Uri, Task> openBrowser, CancellationToken ct);
    Task<AzureToken> DeviceCodeAsync(Func<AzureDeviceCodePrompt, Task> prompt, CancellationToken ct);
    /// <summary>A token from the in-memory cache; <paramref name="forceRefresh"/> asks Entra for a new one (after a PIM activation). Null when sign-in is needed again.</summary>
    Task<AzureToken?> SilentAsync(bool forceRefresh, CancellationToken ct);
    Task SignOutAsync();
}

public sealed class MsalAzureTokenBroker : IAzureTokenBroker
{
    private static readonly string[] Scopes = [AzureEnvironmentOptions.ManagementScope];
    private readonly IPublicClientApplication? _app;

    public MsalAzureTokenBroker(IOptions<AzureEnvironmentOptions> options)
    {
        var o = options.Value;
        if (!o.Configured) return;
        // No token-cache serialization is registered: MSAL keeps tokens in memory for this process only.
        _app = PublicClientApplicationBuilder.Create(o.ClientId!.Trim())
            .WithAuthority($"{AzureEnvironmentOptions.AuthorityHost}/{Uri.EscapeDataString(o.Tenant)}")
            .WithRedirectUri("http://localhost")
            .Build();
    }

    private IPublicClientApplication App => _app ?? throw new InvalidOperationException("Azure Environment Analysis is not configured.");

    public async Task<AzureToken> InteractiveAsync(Func<Uri, Task> openBrowser, CancellationToken ct)
    {
        var result = await App.AcquireTokenInteractive(Scopes)
            .WithPrompt(Prompt.SelectAccount)
            .WithUseEmbeddedWebView(false)
            .WithSystemWebViewOptions(new SystemWebViewOptions
            {
                OpenBrowserAsync = openBrowser,
                HtmlMessageSuccess = "Signed in to BirkNext Azure Environment Analysis. You can close this tab and return to BirkNext.",
                HtmlMessageError = "Sign-in did not complete. Return to BirkNext for details.",
            })
            .ExecuteAsync(ct);
        return From(result);
    }

    public async Task<AzureToken> DeviceCodeAsync(Func<AzureDeviceCodePrompt, Task> prompt, CancellationToken ct)
    {
        var result = await App.AcquireTokenWithDeviceCode(Scopes, code => prompt(new AzureDeviceCodePrompt(code.UserCode, code.VerificationUrl, code.ExpiresOn))).ExecuteAsync(ct);
        return From(result);
    }

    public async Task<AzureToken?> SilentAsync(bool forceRefresh, CancellationToken ct)
    {
        var account = (await App.GetAccountsAsync()).FirstOrDefault();
        if (account is null) return null;
        try { return From(await App.AcquireTokenSilent(Scopes, account).WithForceRefresh(forceRefresh).ExecuteAsync(ct)); }
        catch (MsalUiRequiredException) { return null; }
    }

    public async Task SignOutAsync()
    {
        if (_app is null) return;
        foreach (var account in await _app.GetAccountsAsync()) await _app.RemoveAsync(account);
    }

    private static AzureToken From(AuthenticationResult r) => new(r.AccessToken, r.ExpiresOn, r.Account?.Username, r.TenantId);
}

/// <summary>Opens the sign-in page in a DEDICATED Microsoft Edge profile (its own user-data directory): never the person's normal Edge
/// profile, never the Companion, managed-Edge, proxy or diagnostic profiles, no remote debugging, not headless. The profile keeps its Entra
/// browser session between sign-ins, so MFA is asked as Entra policy decides.</summary>
public interface IAzureSignInBrowser
{
    bool Available { get; }
    string? UnavailableReason { get; }
    Task OpenAsync(Uri signInUri);
}

public static class AzureSignInProfile
{
    public const string DirectoryName = "AzureSignInEdgeProfile";

    public static string DefaultDirectory() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BirkNext", DirectoryName);

    /// <summary>The other BirkNext browser profiles: the Azure profile is never one of them (separate cookies, separate purpose).</summary>
    public static IReadOnlyList<string> OtherBirkNextProfiles() =>
    [
        ManagedEdgePreflightService.DefaultProfileDirectory(),
        BirkNext.Api.Services.HeadlessAuthDiagnostic.HeadlessDiagnosticPolicy.ProfileRoot,
        BirkNext.Api.Services.LocalHttpsProxy.DedicatedCompanionProvisioner.ManagedRoot,
        BirkNext.Api.Services.LocalHttpsProxy.LocalHttpsProxyService.DefaultEdgeProfileDirectory(),
    ];

    /// <summary>The validated profile directory, or an exception: fully qualified, not a normal Edge profile, not another BirkNext profile.</summary>
    public static string Resolve(string? configured)
    {
        var directory = string.IsNullOrWhiteSpace(configured) ? DefaultDirectory() : configured.Trim();
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("The Azure sign-in profile directory must be a fully qualified path.");
        if (ManagedEdgePreflightService.IsNormalEdgeProfile(directory)) throw new ArgumentException("The normal Microsoft Edge profile is never used for Azure sign-in.");
        var full = Path.GetFullPath(directory).TrimEnd('\\', '/');
        if (OtherBirkNextProfiles().Any(p => full.StartsWith(Path.GetFullPath(p).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("The Azure sign-in profile must be separate from the other BirkNext browser profiles.");
        return full;
    }

    /// <summary>Only the profile directory, first-run suppression, a new window and the identity-platform URL. No remote-debugging port.</summary>
    public static IReadOnlyList<string> LaunchArguments(string profileDirectory, Uri signInUri)
    {
        if (!string.Equals(signInUri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
            || !string.Equals(signInUri.Host, new Uri(AzureEnvironmentOptions.AuthorityHost).Host, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only the Microsoft identity platform sign-in page is opened in the Azure sign-in profile.");
        return [$"--user-data-dir={profileDirectory}", "--no-first-run", "--no-default-browser-check", "--new-window", signInUri.AbsoluteUri];
    }
}

public sealed class DedicatedEdgeSignInBrowser(IEdgeInstallationLocator locator, IOptions<AzureEnvironmentOptions> options, IOptions<AuthenticatedReviewOptions> runtime,
    ILogger<DedicatedEdgeSignInBrowser> logger) : IAzureSignInBrowser
{
    public bool Available => UnavailableReason is null;

    public string? UnavailableReason =>
        !runtime.Value.IsLocalWorkstation ? "BirkNext.Api is not running as a local workstation runtime, so it cannot open a browser on your PC. Use device-code sign-in."
        : locator.Locate() is null ? ManagedEdgePreflightService.EdgeMissingReason
        : null;

    public Task OpenAsync(Uri signInUri)
    {
        var edge = locator.Locate() ?? throw new InvalidOperationException(ManagedEdgePreflightService.EdgeMissingReason);
        var profile = AzureSignInProfile.Resolve(options.Value.ProfileDirectory);
        Directory.CreateDirectory(profile);
        var start = new ProcessStartInfo(edge.ExecutablePath) { UseShellExecute = false };
        foreach (var argument in AzureSignInProfile.LaunchArguments(profile, signInUri)) start.ArgumentList.Add(argument);
        using var process = Process.Start(start);
        // The sign-in URL (state, PKCE challenge) is not logged.
        logger.LogInformation("Opened the Azure sign-in page in the dedicated BirkNext Azure Edge profile.");
        return Task.CompletedTask;
    }
}

/// <summary>
/// The Azure session of this BirkNext instance: one signed-in person at a time, token in memory only (lost on restart, removed on sign-out).
/// Sign-in runs in the background; the UI reads <see cref="Status"/>. Never activates PIM: "Refresh access" asks for a new token after the
/// person activated a role themselves.
/// </summary>
public interface IAzureSignInService
{
    AzureConnectionStatus Status();
    AzureConnectionStatus StartInteractive();
    AzureConnectionStatus StartDeviceCode();
    Task<AzureConnectionStatus> RefreshAsync(CancellationToken ct);
    Task<AzureConnectionStatus> SignOutAsync();
    /// <summary>A management-plane access token, refreshed silently when needed; null when not signed in or sign-in expired.</summary>
    Task<string?> AccessTokenAsync(bool forceRefresh, CancellationToken ct);
    void MarkExpired(string reason);
    AzureSignInMethod Method { get; }
}

public sealed class AzureSignInService(IOptions<AzureEnvironmentOptions> options, IAzureTokenBroker broker, IAzureSignInBrowser browser, ILogger<AzureSignInService> logger,
    TimeProvider? clock = null) : IAzureSignInService
{
    private static readonly TimeSpan InteractiveTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(3);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private AzureConnectionState _state = AzureConnectionState.SignedOut;
    private AzureSignInMethod _method;
    private AzureToken? _token;
    private DateTimeOffset? _signedInAt;
    private string _message = "";
    private AzureDeviceCodePrompt? _deviceCode;
    private CancellationTokenSource? _pending;

    public AzureSignInMethod Method { get { lock (_gate) return _method; } }

    public AzureConnectionStatus Status()
    {
        var o = options.Value;
        if (!o.Configured)
            return new()
            {
                State = AzureConnectionState.NotConfigured, Requirements = [.. AzureEnvironmentOptions.Requirements()],
                Message = o.Enabled ? "Azure Environment Analysis needs an app registration (AzureEnvironment:ClientId)." : "Azure Environment Analysis is not enabled for this BirkNext instance.",
            };
        lock (_gate)
        {
            var state = _state == AzureConnectionState.SignedIn && _token is { } t && t.ExpiresOn <= _clock.GetUtcNow() ? AzureConnectionState.Expired : _state;
            return new()
            {
                State = state, Method = _method, Account = state is AzureConnectionState.SignedIn or AzureConnectionState.Expired ? _token?.Account : null,
                TenantId = _token?.TenantId, SignedInAt = _signedInAt, ExpiresOn = _token?.ExpiresOn, DeviceCode = state == AzureConnectionState.AwaitingDeviceCode ? _deviceCode : null,
                Message = state == AzureConnectionState.Expired && _state == AzureConnectionState.SignedIn ? "The Azure token expired. Refresh access or sign in again." : _message,
                DedicatedEdgeAvailable = browser.Available, DeviceCodeAllowed = o.AllowDeviceCode,
            };
        }
    }

    public AzureConnectionStatus StartInteractive()
    {
        if (!options.Value.Configured) return Status();
        if (!browser.Available) { Set(AzureConnectionState.Failed, AzureSignInMethod.DedicatedEdgeProfile, browser.UnavailableReason ?? "The dedicated Edge profile is unavailable."); return Status(); }
        Begin(AzureConnectionState.SigningIn, AzureSignInMethod.DedicatedEdgeProfile, "Complete sign-in in the Microsoft Edge window that opened (dedicated BirkNext Azure profile).",
            ct => broker.InteractiveAsync(browser.OpenAsync, ct));
        return Status();
    }

    public AzureConnectionStatus StartDeviceCode()
    {
        if (!options.Value.Configured) return Status();
        if (!options.Value.AllowDeviceCode) { Set(AzureConnectionState.Failed, AzureSignInMethod.DeviceCode, "Device-code sign-in is disabled (AzureEnvironment:AllowDeviceCode)."); return Status(); }
        Begin(AzureConnectionState.SigningIn, AzureSignInMethod.DeviceCode, "Requesting a device code…", ct => broker.DeviceCodeAsync(prompt =>
        {
            lock (_gate) { _deviceCode = prompt; _state = AzureConnectionState.AwaitingDeviceCode; _message = "Open the verification page in a browser of your choice and enter the code."; }
            return Task.CompletedTask;
        }, ct));
        return Status();
    }

    private void Begin(AzureConnectionState state, AzureSignInMethod method, string message, Func<CancellationToken, Task<AzureToken>> acquire)
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            _pending?.Cancel();
            _pending = cts = new CancellationTokenSource(InteractiveTimeout);
            _state = state; _method = method; _message = message; _deviceCode = null;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var token = await acquire(cts.Token);
                lock (_gate)
                {
                    if (!ReferenceEquals(_pending, cts)) return;
                    _token = token; _signedInAt = _clock.GetUtcNow(); _state = AzureConnectionState.SignedIn; _deviceCode = null;
                    _message = "Signed in. BirkNext reads Azure with your permissions and never changes anything.";
                }
                logger.LogInformation("Azure sign-in completed ({Method}).", method);
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    if (!ReferenceEquals(_pending, cts)) return;
                    _state = AzureConnectionState.Failed; _deviceCode = null; _message = Describe(ex, cts.IsCancellationRequested);
                }
                logger.LogInformation("Azure sign-in did not complete ({Method}): {Error}.", method, ErrorCode(ex));
            }
        });
    }

    public async Task<AzureConnectionStatus> RefreshAsync(CancellationToken ct)
    {
        if (!options.Value.Configured) return Status();
        var token = await AccessTokenAsync(forceRefresh: true, ct);
        if (token is not null) lock (_gate) _message = "Access refreshed: a new token was issued, so newly activated roles are included. " + AzureEnvironmentText.PimGuidance.Split(". ")[0] + ".";
        return Status();
    }

    public async Task<AzureConnectionStatus> SignOutAsync()
    {
        lock (_gate) { _pending?.Cancel(); _pending = null; _token = null; _signedInAt = null; _deviceCode = null; _state = AzureConnectionState.SignedOut; _method = AzureSignInMethod.None;
            _message = "Signed out. The token was removed from memory. The dedicated Edge profile may still hold your Entra browser session."; }
        try { await broker.SignOutAsync(); } catch (Exception ex) { logger.LogInformation("Azure sign-out cache removal: {Error}.", ErrorCode(ex)); }
        return Status();
    }

    public async Task<string?> AccessTokenAsync(bool forceRefresh, CancellationToken ct)
    {
        AzureToken? current;
        lock (_gate)
        {
            if (_state is not (AzureConnectionState.SignedIn or AzureConnectionState.Expired)) return null;
            current = _token;
        }
        if (!forceRefresh && current is not null && current.ExpiresOn - RefreshMargin > _clock.GetUtcNow()) return current.AccessToken;
        try
        {
            var refreshed = await broker.SilentAsync(forceRefresh, ct);
            if (refreshed is null) { MarkExpired("Your Azure sign-in has expired. Sign in again."); return null; }
            lock (_gate) { _token = refreshed; _state = AzureConnectionState.SignedIn; }
            return refreshed.AccessToken;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogInformation("Azure access refresh failed: {Error}.", ErrorCode(ex));
            MarkExpired("The Azure token could not be refreshed. Sign in again.");
            return null;
        }
    }

    public void MarkExpired(string reason)
    {
        lock (_gate) { if (_state == AzureConnectionState.SignedIn) { _state = AzureConnectionState.Expired; _message = reason; } }
    }

    private void Set(AzureConnectionState state, AzureSignInMethod method, string message)
    {
        lock (_gate) { _state = state; _method = method; _message = message; }
    }

    /// <summary>An error code only — never a message that could echo a token, claim or URL.</summary>
    internal static string ErrorCode(Exception ex) => ex is MsalException m ? $"{ex.GetType().Name} ({m.ErrorCode})" : ex.GetType().Name;

    private static string Describe(Exception ex, bool timedOut) => ex switch
    {
        OperationCanceledException when timedOut => "Sign-in was not completed within five minutes.",
        OperationCanceledException => "Sign-in was cancelled.",
        MsalClientException { ErrorCode: "authentication_canceled" } => "Sign-in was cancelled in the browser.",
        MsalServiceException { ErrorCode: "invalid_client" or "unauthorized_client" } m => $"The app registration rejected the sign-in ({m.ErrorCode}). Check that it is a public client with redirect URI http://localhost.",
        MsalServiceException m when m.ErrorCode.Contains("consent", StringComparison.OrdinalIgnoreCase) => $"Consent is required for the app registration ({m.ErrorCode}). Ask an administrator.",
        MsalException m => $"Sign-in did not complete ({m.ErrorCode}).",
        _ => $"Sign-in did not complete ({ex.GetType().Name}).",
    };
}
