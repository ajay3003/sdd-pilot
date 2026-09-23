using BirkNext.Api.Services.BrowserAutomationDiagnostic;
using BirkNext.Api.Services.HeadlessAuthDiagnostic;
using BirkNext.HeadlessAuthDiagnostic;
using FluentAssertions;
using Microsoft.Playwright;
using Xunit;

namespace BirkNext.Api.Tests.Unit.BrowserAutomationDiagnostic;

/// <summary>
/// The safe browser-error extraction and its derived category. The messages below are the shape Playwright actually
/// produces against Microsoft Edge (captured 2026-09-23): the error code first, then " at " and the full requested URL
/// — query included — then a call log that repeats the URL.
/// </summary>
public sealed class BrowserNavigationFailureClassifierTests
{
    private static readonly TimeSpan Timeout = BrowserAutomationDiagnosticPolicy.TargetNavigationTimeout;

    private static string Real(string code, string url = "https://example-qa.local/") =>
        $"page.goto: {code} at {url}\nCall log:\n\u001b[2m  - navigating to \"{url}\", waiting until \"commit\"\u001b[22m\n";

    private static BrowserNavigationFailureEvidence Describe(Exception ex) =>
        BrowserNavigationFailureClassifier.Describe(ex, BrowserAutomationDiagnosticStage.TargetNavigation, Timeout);

    // §27 DNS
    [Fact]
    public void Dns_TheCodeIsExtractedAndCategorized()
    {
        var failure = Describe(new PlaywrightException(Real("net::ERR_NAME_NOT_RESOLVED")));

        failure.ExceptionType.Should().Be("PlaywrightException");
        failure.BrowserErrorCode.Should().Be("net::ERR_NAME_NOT_RESOLVED");
        failure.Category.Should().Be(BrowserNavigationFailureCategory.Dns);
        failure.CategoryLabel.Should().Be("DNS");
        failure.Interpretation.Should().Be("The browser could not resolve the target hostname.");
        failure.ObservedAtStage.Should().Be(BrowserAutomationDiagnosticStage.TargetNavigation);
        failure.NavigationTimeoutMs.Should().Be((long)Timeout.TotalMilliseconds);
    }

    // §28 / §21 Certificate: the specific code is kept and explained, not collapsed.
    [Fact]
    public void CertificateAuthorityInvalid_IsTlsWithASpecificInterpretation()
    {
        var failure = Describe(new PlaywrightException(Real("net::ERR_CERT_AUTHORITY_INVALID")));

        failure.BrowserErrorCode.Should().Be("net::ERR_CERT_AUTHORITY_INVALID");
        failure.Category.Should().Be(BrowserNavigationFailureCategory.TlsCertificate);
        failure.CategoryLabel.Should().Be("TLS / certificate");
        failure.Interpretation.Should().Be("The browser did not trust the certificate chain the target presented.");
    }

    // §29 Connection refused
    [Fact]
    public void ConnectionRefused_IsItsOwnCategory()
    {
        var failure = Describe(new PlaywrightException(Real("net::ERR_CONNECTION_REFUSED")));
        failure.Category.Should().Be(BrowserNavigationFailureCategory.ConnectionRefused);
        failure.Interpretation.Should().Be("The target actively refused the connection.");
    }

    // §30 / §20 A browser-reported connection timeout and a Playwright navigation timeout are different evidence.
    [Fact]
    public void BrowserConnectionTimeout_IsConnectionTimeout()
    {
        var failure = Describe(new PlaywrightException(Real("net::ERR_CONNECTION_TIMED_OUT")));
        failure.BrowserErrorCode.Should().Be("net::ERR_CONNECTION_TIMED_OUT");
        failure.Category.Should().Be(BrowserNavigationFailureCategory.ConnectionTimeout);
    }

    [Fact]
    public void PlaywrightTimeout_IsNavigationTimeout_WithNoBrowserCode()
    {
        // Playwright for .NET (1.48) raises System.TimeoutException for a timeout; it has no public timeout type.
        var failure = Describe(new System.TimeoutException(
            "Timeout 25000ms exceeded.\nCall log:\n  - navigating to \"https://example-qa.local/?code=abc\", waiting until \"commit\""));

        failure.ExceptionType.Should().Be("TimeoutException");
        failure.BrowserErrorCode.Should().BeNull();
        failure.Category.Should().Be(BrowserNavigationFailureCategory.NavigationTimeout);
        failure.Category.Should().NotBe(BrowserNavigationFailureCategory.ConnectionTimeout);
        failure.Interpretation.Should().Contain("25 s").And.Contain("not a diagnosed network failure");
    }

    // §31 Proxy — no policy, no root cause.
    [Theory]
    [InlineData("net::ERR_PROXY_CONNECTION_FAILED", BrowserNavigationFailureCategory.Proxy)]
    [InlineData("net::ERR_PROXY_CERTIFICATE_INVALID", BrowserNavigationFailureCategory.Proxy)]
    [InlineData("net::ERR_TUNNEL_CONNECTION_FAILED", BrowserNavigationFailureCategory.Tunnel)]
    public void Proxy_IsProxyOrTunnel_AndNamesNoCause(string code, BrowserNavigationFailureCategory expected)
    {
        var failure = Describe(new PlaywrightException(Real(code)));

        failure.Category.Should().Be(expected);
        failure.Interpretation.Should().NotContainAny("broken", "misconfigured", "corporate", "policy", "IT ");
    }

    // §7 The mapping table, including the families decided by prefix.
    [Theory]
    [InlineData("ERR_NAME_NOT_RESOLVED", BrowserNavigationFailureCategory.Dns)]
    [InlineData("ERR_DNS_TIMED_OUT", BrowserNavigationFailureCategory.Dns)]
    [InlineData("ERR_CERT_COMMON_NAME_INVALID", BrowserNavigationFailureCategory.TlsCertificate)]
    [InlineData("ERR_CERT_DATE_INVALID", BrowserNavigationFailureCategory.TlsCertificate)]
    [InlineData("ERR_SSL_PROTOCOL_ERROR", BrowserNavigationFailureCategory.TlsCertificate)]
    [InlineData("ERR_TIMED_OUT", BrowserNavigationFailureCategory.ConnectionTimeout)]
    [InlineData("ERR_CONNECTION_RESET", BrowserNavigationFailureCategory.ConnectionReset)]
    [InlineData("ERR_NETWORK_CHANGED", BrowserNavigationFailureCategory.NetworkUnavailable)]
    [InlineData("ERR_INTERNET_DISCONNECTED", BrowserNavigationFailureCategory.NetworkUnavailable)]
    [InlineData("ERR_ADDRESS_UNREACHABLE", BrowserNavigationFailureCategory.AddressUnreachable)]
    [InlineData("ERR_HTTP2_PROTOCOL_ERROR", BrowserNavigationFailureCategory.Protocol)]
    [InlineData("ERR_BLOCKED_BY_ADMINISTRATOR", BrowserNavigationFailureCategory.BrowserPolicyOrRestriction)]
    [InlineData("ERR_UNSAFE_PORT", BrowserNavigationFailureCategory.BrowserPolicyOrRestriction)]
    [InlineData("ERR_ABORTED", BrowserNavigationFailureCategory.Unknown)]
    [InlineData("ERR_SOMETHING_NEW", BrowserNavigationFailureCategory.Unknown)]
    public void KnownCodesMap_AndUnknownCodesStayUnknownWithTheirCode(string code, BrowserNavigationFailureCategory expected)
    {
        var failure = Describe(new PlaywrightException(Real("net::" + code)));

        failure.Category.Should().Be(expected);
        failure.BrowserErrorCode.Should().Be("net::" + code, "the original safe code is preserved whatever the category");
    }

    // §32 / §8 TargetClosed stays distinct and carries no fabricated network error.
    [Fact]
    public void TargetClosed_IsTargetClosed_WithNoNetworkCode()
    {
        var failure = Describe(new PlaywrightException("Target page, context or browser has been closed"));

        failure.ExceptionType.Should().Be("TargetClosedException");
        failure.BrowserErrorCode.Should().BeNull();
        failure.Category.Should().Be(BrowserNavigationFailureCategory.TargetClosed);
        failure.Category.Should().NotBe(BrowserNavigationFailureCategory.BrowserPolicyOrRestriction);
        failure.Interpretation.Should().Be("The browser page or context closed while the target navigation was in progress.");
    }

    // §33 / §9 A PlaywrightException with no code is Unknown — never a policy guess.
    [Fact]
    public void PlaywrightExceptionWithoutACode_IsUnknown_AndNoMessageIsEchoed()
    {
        const string message = "Something internal went wrong at C:\\Users\\someone\\profile with secret=hunter2";
        var failure = Describe(new PlaywrightException(message));

        failure.BrowserErrorCode.Should().BeNull();
        failure.Category.Should().Be(BrowserNavigationFailureCategory.Unknown);
        failure.Category.Should().NotBe(BrowserNavigationFailureCategory.BrowserPolicyOrRestriction);
        failure.Interpretation.Should().Contain("did not expose a recognised network/navigation error code");
        Serialized(failure).Should().NotContainAny("Something internal", "someone", "hunter2", "secret");
    }

    // §34 Sensitive message: only the type, the code and the derived values survive.
    [Fact]
    public void ASensitiveMessage_LeavesOnlyTheSafeFields()
    {
        var url = "https://example-qa.local/callback?code=0.AAAA-secret-code&state=s3cr3t-state&access_token=eyJhbGciOi.tok";
        var failure = Describe(new PlaywrightException(Real("net::ERR_NAME_NOT_RESOLVED", url)));

        failure.BrowserErrorCode.Should().Be("net::ERR_NAME_NOT_RESOLVED");
        Serialized(failure).Should().NotContainAny("secret-code", "s3cr3t-state", "eyJhbGciOi", "access_token", "callback",
            "example-qa.local", "Call log", "navigating to");
    }

    // §35 Code-like text anywhere but the start of the line — the URL, a query value, page-derived text — is ignored.
    [Theory]
    [InlineData("page.goto: Navigation failed at https://host/?error=net::ERR_NAME_NOT_RESOLVED")]
    [InlineData("Error text from page: ERR_NAME_NOT_RESOLVED")]
    [InlineData("page.goto: something happened\nnet::ERR_NAME_NOT_RESOLVED on a later line")]
    [InlineData("xnet::ERR_NAME_NOT_RESOLVED")]
    [InlineData("net::ERR_NAME_NOT_RESOLVEDX-and-more")]
    public void CodeTextOutsideTheAnchoredPosition_IsNotExtracted(string message) =>
        BrowserNavigationFailureClassifier.TryExtractBrowserErrorCode(message).Should().BeNull();

    // §36 Prefix and case forms normalize to one display.
    [Theory]
    [InlineData("net::ERR_NAME_NOT_RESOLVED at https://h/")]
    [InlineData("ERR_NAME_NOT_RESOLVED at https://h/")]
    [InlineData("page.goto: net::ERR_NAME_NOT_RESOLVED at https://h/")]
    [InlineData("Page.GotoAsync: ERR_NAME_NOT_RESOLVED")]
    [InlineData("NET::ERR_NAME_NOT_RESOLVED")]
    [InlineData("  net::ERR_NAME_NOT_RESOLVED\r\nCall log:")]
    public void PrefixForms_NormalizeToTheCanonicalCode(string message) =>
        BrowserNavigationFailureClassifier.TryExtractBrowserErrorCode(message).Should().Be("net::ERR_NAME_NOT_RESOLVED");

    [Fact]
    public void LowerCaseCodes_AreNotTreatedAsBrowserCodes() =>
        BrowserNavigationFailureClassifier.TryExtractBrowserErrorCode("net::err_name_not_resolved").Should().BeNull();

    // §19 The inner exception is inspected, to a bounded depth.
    [Fact]
    public void TheCodeIsFoundInAnInnerException()
    {
        var ex = new InvalidOperationException("wrapper", new PlaywrightException(Real("net::ERR_CONNECTION_RESET")));
        BrowserNavigationFailureClassifier.TryExtractBrowserErrorCode(ex).Should().Be("net::ERR_CONNECTION_RESET");
    }

    [Fact]
    public void TheExceptionChainIsBounded()
    {
        Exception ex = new PlaywrightException(Real("net::ERR_CONNECTION_RESET"));
        for (var i = 0; i < BrowserNavigationFailureClassifier.MaxExceptionChainDepth; i++)
            ex = new InvalidOperationException($"wrapper {i}", ex);

        BrowserNavigationFailureClassifier.TryExtractBrowserErrorCode(ex).Should().BeNull("the code sits beyond the inspected depth");
    }

    [Fact]
    public void AnOverlongLineIsTruncatedBeforeMatching()
    {
        var message = new string(' ', 300) + "net::ERR_NAME_NOT_RESOLVED";
        // Leading whitespace is trimmed, so this is found; a code beyond the bound after other text is not.
        BrowserNavigationFailureClassifier.TryExtractBrowserErrorCode(message).Should().Be("net::ERR_NAME_NOT_RESOLVED");
        BrowserNavigationFailureClassifier.TryExtractBrowserErrorCode("page.goto: " + new string('x', 400) + " net::ERR_NAME_NOT_RESOLVED")
            .Should().BeNull();
    }

    // §11 No interpretation claims a cause or an owner.
    [Fact]
    public void NoInterpretationNamesACauseOrAnOwner()
    {
        foreach (var category in Enum.GetValues<BrowserNavigationFailureCategory>())
        foreach (var code in new string?[] { null, "net::ERR_X" })
            BrowserNavigationFailureClassifier.Interpretation(category, code, Timeout)
                .Should().NotContainAny("misconfigured", "broken", "PKI", "team", "Conditional Access", "MCAS", "should");
    }

    // §24 / §37 The auth prerequisite says what the first blocker was, safely, and stays blocked.
    [Fact]
    public void AHeadlessNavigationFailure_KeepsTheAuthDiagnosticBlocked_AndNamesTheBrowserError()
    {
        var store = new BrowserAutomationEvidenceStore();
        store.Record(FailedReport(Describe(new PlaywrightException(Real("net::ERR_NAME_NOT_RESOLVED",
            "https://example-qa.local/?code=leak-me")))));

        var prerequisite = store.Check(new HeadlessDiagnosticRequest
        {
            TargetEnvironmentId = "qa", TargetUrl = "https://example-qa.local/", EnvironmentType = "QA",
        });

        prerequisite.Available.Should().BeFalse();
        prerequisite.Reason.Should().Contain("Headless browser navigation failed before authentication could be observed")
            .And.Contain("net::ERR_NAME_NOT_RESOLVED (DNS)")
            .And.Contain("MFA, Conditional Access and session control were not assessed");
        prerequisite.Reason.Should().NotContain("leak-me");
    }

    [Fact]
    public void AHeadlessTargetClose_AddsNoNavigationFailureSentence()
    {
        var store = new BrowserAutomationEvidenceStore();
        store.Record(FailedReport(Describe(new PlaywrightException("Target page, context or browser has been closed"))));

        var prerequisite = store.Check(new HeadlessDiagnosticRequest
        {
            TargetEnvironmentId = "qa", TargetUrl = "https://example-qa.local/", EnvironmentType = "QA",
        });

        prerequisite.Available.Should().BeFalse();
        prerequisite.Reason.Should().NotContain("navigation failed");
    }

    private static BrowserAutomationDiagnosticReport FailedReport(BrowserNavigationFailureEvidence failure) => new()
    {
        DiagnosticId = "abc123",
        TargetEnvironmentId = "qa", TargetEnvironmentType = "QA",
        TargetUrl = "https://example-qa.local/", CorrelationTargetUrl = "https://example-qa.local/",
        Modes =
        [
            new() { Mode = BrowserAutomationDiagnosticMode.Headed, Result = BrowserAutomationDiagnosticModeResult.Failed,
                Target = new() { NavigationFailure = failure } },
            new() { Mode = BrowserAutomationDiagnosticMode.Headless, Headless = true, Result = BrowserAutomationDiagnosticModeResult.Failed,
                Target = new() { NavigationFailure = failure } },
        ],
    };

    private static string Serialized(object value) => System.Text.Json.JsonSerializer.Serialize(value);
}
