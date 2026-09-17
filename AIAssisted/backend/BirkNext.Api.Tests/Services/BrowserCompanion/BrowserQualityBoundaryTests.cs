using BirkNext.Api.Services.BrowserCompanion;
using BirkNext.Api.Services.FrontendBrowserRuntime;
using BirkNext.Api.Services.LocalHttpsProxy;
using BirkNext.BrowserCompanion;
using BirkNext.LocalHttpsProxy;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Tests.Services.BrowserCompanion;

public sealed class BrowserQualityBoundaryTests
{
    [Theory]
    [InlineData("access.mcas.ms")]
    [InlineData("samhandlingsrom-onmicrosoft-com.access.mcas.ms")]
    [InlineData("login.microsoftonline.com")]
    [InlineData("login.live.com")]
    public void InfrastructureHtmlAndReferrersNeverCorrelateToApplicationPages(string host)
    {
        var direct = NetworkTrafficClassifier.Classify(new NetworkRequestMetadata
        { Host = host, Port = 443, Method = "GET", Target = "/", ResponseStatus = 200, ResponseContentType = "text/html" }, DateTimeOffset.UtcNow);
        Assert.Equal(ObservedTrafficCategory.Authentication, direct.Category); Assert.Null(direct.PageOrigin);
        var referred = NetworkTrafficClassifier.Classify(new NetworkRequestMetadata
        { Host = "api.bufetat.no", Port = 443, Method = "GET", Target = "/api", ResponseStatus = 200, ResponseContentType = "application/json", Referer = $"https://{host}/" }, DateTimeOffset.UtcNow);
        Assert.Null(referred.PageOrigin);
    }

    [Fact]
    public void RedirectDoesNotCreatePageAndFinalApplicationDocumentOwnsEvidence()
    {
        var request = new NetworkRequestMetadata { Host = "m2lbdev.bufetat.no", Port = 443, Method = "GET", Target = "/dashboard",
            ResponseStatus = 302, ResponseContentType = "text/html", Referer = "https://access.mcas.ms/" };
        Assert.Null(NetworkTrafficClassifier.Classify(request, DateTimeOffset.UtcNow).PageOrigin);
        var final = NetworkTrafficClassifier.Classify(request with { ResponseStatus = 200 }, DateTimeOffset.UtcNow);
        Assert.Equal("https://m2lbdev.bufetat.no", final.PageOrigin); Assert.Equal("/dashboard", final.PagePath);
    }

    [Fact]
    public void ThirdPartyResourceIsNotItsOwnPage()
    {
        var asset = NetworkTrafficClassifier.Classify(new NetworkRequestMetadata { Host = "cdn.example.org", Port = 443,
            Method = "GET", Target = "/app.js", ResponseContentType = "text/javascript", ResponseStatus = 200,
            Referer = "https://m2lbdev.bufetat.no/dashboard" }, DateTimeOffset.UtcNow);
        Assert.Equal(ObservedTrafficCategory.StaticAsset, asset.Category);
        Assert.Equal("https://m2lbdev.bufetat.no", asset.PageOrigin);
    }

    [Fact]
    public void PairingCannotAuthorizeInfrastructure()
    {
        var service = new BrowserCompanionService(new(new BrowserEvidenceSanitizer()), TimeProvider.System, NullLogger<BrowserCompanionService>.Instance);
        Assert.Throws<ArgumentException>(() => service.StartPairing(new("dev", "Dev", "Development", ["https://access.mcas.ms"])));
    }

    [Fact]
    public void AxeEvidenceIsBoundedSanitizedAndTyped()
    {
        var sanitizer = new BrowserCompanionEvidenceSanitizer(new BrowserEvidenceSanitizer());
        var page = sanitizer.Sanitize(new() { Accessibility = new() { Axe = new() { State = "Completed", Version = AxeRuleCatalog.Version, EvidenceVersion = AxeRuleCatalog.Version,
            Rules = Enumerable.Range(0, 400).Select(_ => new BrowserAxeRule { RuleId = "image-alt", Outcome = "Compliant", Count = -1,
                CriterionIds = ["1.1.1", "<html>", "1.1.1", "Bearer secret"] }).ToList() } } });
        var axe = page.Accessibility!.Axe!;
        Assert.Equal(300, axe.Rules.Count); Assert.Equal(AxeRuleCatalog.Version, axe.Version); Assert.DoesNotContain("secret", axe.EvidenceVersion);
        Assert.All(axe.Rules, rule => { Assert.Equal("NotTested", rule.Outcome); Assert.Equal(0, rule.Count); Assert.Equal("1.1.1", Assert.Single(rule.CriterionIds)); });
    }
}
