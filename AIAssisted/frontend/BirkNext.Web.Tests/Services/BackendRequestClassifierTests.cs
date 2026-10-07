using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using BirkNext.Web.Configuration;
using BirkNext.Web.Services;
using FluentAssertions;
using Xunit;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// One classification for every backend failure: any HTTP response proves the backend is reachable; only a failure before
/// an HTTP response may say "unreachable", and timeout, TLS and cancellation each stay distinguishable.
/// </summary>
public sealed class BackendRequestClassifierTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(200)]
    [InlineData(204)]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(409)]
    [InlineData(422)]
    [InlineData(500)]
    [InlineData(503)]
    public void AnyBackendHttpResponseProvesReachability(int status)
    {
        BackendRequestClassifier.ConnectivityFor(status).Should().Be(BackendConnectivity.Reachable);
    }

    [Theory]
    [InlineData(400, BackendRequestCategory.BadRequest, "rejected the request")]
    [InlineData(401, BackendRequestCategory.Unauthorized, "authentication required")]
    [InlineData(403, BackendRequestCategory.Forbidden, "access denied")]
    [InlineData(404, BackendRequestCategory.NotFound, "reachable, but this endpoint was not found")]
    [InlineData(409, BackendRequestCategory.Conflict, "current state")]
    [InlineData(422, BackendRequestCategory.ValidationError, "did not accept")]
    [InlineData(500, BackendRequestCategory.ServerError, "returned HTTP 500")]
    [InlineData(503, BackendRequestCategory.ServiceUnavailable, "temporarily unavailable")]
    public void HttpErrorsAreEndpointFailuresOnAReachableBackend(int status, BackendRequestCategory category, string phrase)
    {
        // The same error whether it arrives as a status or as the exception GetFromJsonAsync/EnsureSuccessStatusCode throw.
        var fromStatus = BackendRequestClassifier.FromStatus("Critical E2E overview", status, at: At);
        var fromException = BackendRequestClassifier.FromException("Critical E2E overview",
            new HttpRequestException("Response status code does not indicate success.", null, (HttpStatusCode)status), at: At);

        foreach (var error in new[] { fromStatus, fromException })
        {
            error.Connectivity.Should().Be(BackendConnectivity.Reachable);
            error.Category.Should().Be(category);
            error.HttpStatus.Should().Be(status);
            error.IsTransportFailure.Should().BeFalse();
            error.ConnectivityLabel.Should().Be("Backend reachable");
            error.UserMessage.Should().StartWith("Critical E2E overview: ").And.Contain(phrase).And.Contain($"HTTP {status}");
            error.UserMessage.Should().NotContainEquivalentOf("unreachable").And.NotContainEquivalentOf("not reachable").And.NotContain("local");
        }
    }

    [Theory]
    [InlineData(502)]
    [InlineData(504)]
    public void AGatewayAnsweringDoesNotProveTheBackendBehindItIsReachable(int status)
    {
        var error = BackendRequestClassifier.FromStatus("Security Classification reviews", status);
        error.Connectivity.Should().Be(BackendConnectivity.Unknown);
        error.Category.Should().Be(BackendRequestCategory.GatewayError);
        error.UserMessage.Should().Contain($"HTTP {status}").And.NotContainEquivalentOf("unreachable");
    }

    [Fact]
    public void TimeoutIsATimeoutNotAnUnreachableBackend()
    {
        // HttpClient.Timeout: TaskCanceledException with an inner TimeoutException, and the caller did not cancel.
        var error = BackendRequestClassifier.FromException("Critical E2E overview", new TaskCanceledException("timeout", new TimeoutException()));
        error.Category.Should().Be(BackendRequestCategory.Timeout);
        error.Connectivity.Should().Be(BackendConnectivity.Unknown);
        error.UserMessage.Should().Contain("timed out").And.NotContainEquivalentOf("unreachable");
        error.HttpStatus.Should().BeNull();
    }

    [Fact]
    public void CallerCancellationIsCancelledNotUnreachable()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var error = BackendRequestClassifier.FromException("Critical E2E run", new TaskCanceledException(), cancellationToken: cts.Token);
        error.Category.Should().Be(BackendRequestCategory.Cancelled);
        error.Connectivity.Should().Be(BackendConnectivity.Unknown);
        error.UserMessage.Should().Contain("cancelled").And.NotContainEquivalentOf("unreachable");
    }

    [Fact]
    public void ConnectionRefusedIsUnreachable()
    {
        var error = BackendRequestClassifier.FromException("Critical E2E overview",
            new HttpRequestException(HttpRequestError.ConnectionError, "refused", new SocketException((int)SocketError.ConnectionRefused)));
        error.Connectivity.Should().Be(BackendConnectivity.Unreachable);
        error.Category.Should().Be(BackendRequestCategory.ConnectionRefused);
        error.ConnectivityLabel.Should().Be("Backend unreachable");
        error.UserMessage.Should().Contain("connection refused").And.NotContain("local");
    }

    [Fact]
    public void DnsFailureIsUnreachableHostNotFound()
    {
        var error = BackendRequestClassifier.FromException("Critical E2E overview",
            new HttpRequestException(HttpRequestError.NameResolutionError, "No such host", new SocketException((int)SocketError.HostNotFound)));
        error.Connectivity.Should().Be(BackendConnectivity.Unreachable);
        error.Category.Should().Be(BackendRequestCategory.HostNotFound);
        error.UserMessage.Should().Contain("host not found");
    }

    [Fact]
    public void TlsFailureIsASecureConnectionFailureNotAnHttpError()
    {
        var error = BackendRequestClassifier.FromException("Critical E2E overview",
            new HttpRequestException(HttpRequestError.SecureConnectionError, "SSL", new AuthenticationException("handshake")));
        error.Category.Should().Be(BackendRequestCategory.TlsError);
        error.HttpStatus.Should().BeNull();
        error.CategoryLabel.Should().Be("Secure connection failed");
        error.UserMessage.Should().Contain("secure connection").And.NotContain("HTTP 5");
    }

    [Fact]
    public void BrowserFetchFailureIsUnreachableAndNamesTheCrossOriginPossibility()
    {
        // Blazor WebAssembly: fetch rejects with no status and no socket detail.
        var error = BackendRequestClassifier.FromException("Critical E2E overview", new HttpRequestException("TypeError: Failed to fetch"));
        error.Connectivity.Should().Be(BackendConnectivity.Unreachable);
        error.Category.Should().Be(BackendRequestCategory.NetworkError);
        error.UserMessage.Should().Contain("no HTTP response").And.Contain("cross-origin");
    }

    [Fact]
    public void AnUnreadableBodyMeansTheBackendAnswered()
    {
        var error = BackendRequestClassifier.FromException("Critical E2E overview", new JsonException("bad"));
        error.Connectivity.Should().Be(BackendConnectivity.Reachable);
        error.Category.Should().Be(BackendRequestCategory.InvalidResponse);
    }

    [Fact]
    public void TheErrorCarriesTheRouteTemplateOnly()
    {
        var error = BackendRequestClassifier.FromStatus("Security Classification reviews", 404, "GET api/security-classification", At);
        error.Endpoint.Should().Be("GET api/security-classification");
        error.Timestamp.Should().Be(At);
        error.TechnicalMessage.Should().Be("HTTP 404");
    }

    [Theory]
    [InlineData("http://localhost:5000", "http://localhost:5000/")]
    [InlineData("http://localhost:5000/", "http://localhost:5000/")]
    [InlineData("https://birknext.example/api-host", "https://birknext.example/api-host/")]
    public void TheBackendBaseAddressAlwaysEndsWithASlashSoApiPathsAppend(string configured, string expected)
    {
        var baseAddress = BackendUrlValidator.BaseAddress(configured);
        baseAddress.ToString().Should().Be(expected);
        new Uri(baseAddress, "api/critical-e2e/overview").AbsolutePath.Should().EndWith("/api/critical-e2e/overview").And.NotContain("/api/api/");
    }

    [Fact]
    public void EveryApiClientUsesTheOneConfiguredBackendAddress()
    {
        // A client with its own hard-coded host or port drifts from the rest — its calls fail while other pages work.
        var program = File.ReadAllText(Path.Combine(RepoFrontend(), "BirkNext.Web", "Program.cs"));
        program.Should().NotContain("new Uri(\"http://localhost").And.NotContain("new Uri(\"https://localhost").And.NotContain("new Uri(backendUrl)");
        program.Should().Contain("BackendUrlValidator.BaseAddress(backendUrl)");
        var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoFrontend(), "BirkNext.Web", "wwwroot", "appsettings.json")));
        settings.RootElement.GetProperty("BackendUrl").GetString().Should().Be("http://localhost:5000");
    }

    private static string RepoFrontend()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "BirkNext.Web"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("frontend root not found");
    }
}
