using BirkNext.Api.Services.BrowserCompanion;
using BirkNext.Api.Services.FrontendBrowserRuntime;
using BirkNext.BrowserCompanion;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Tests.Services.BrowserCompanion;

public sealed class WcagEvidenceTests
{
    [Fact]
    public void CheckEvidenceIsBoundedAndSensitiveSelectorsAreRejected()
    {
        var sanitizer = new BrowserCompanionEvidenceSanitizer(new BrowserEvidenceSanitizer());
        var result = sanitizer.Sanitize(new BrowserPageEvidence
        {
            Accessibility = new()
            {
                VideoCount = -1, Checks = Enumerable.Range(0, 100).Select(_ => new BrowserWcagCheck
                {
                    CheckId = "text-contrast", Outcome = "Compliant", Tested = -1, Failed = -1,
                    Selectors = ["input[value='secret']", "div[data-token='Bearer abc']", "p:nth-of-type(2)"],
                }).ToList(),
                NavigationStructure = Enumerable.Repeat(-1, 200).ToList(),
            },
        });
        Assert.Equal(80, result.Accessibility!.Checks.Count);
        Assert.Equal(100, result.Accessibility.NavigationStructure.Count);
        Assert.All(result.Accessibility.Checks, c =>
        {
            Assert.Equal("NotTested", c.Outcome); Assert.Equal(0, c.Tested); Assert.Equal(0, c.Failed);
            Assert.Equal("p:nth-of-type(2)", Assert.Single(c.Selectors));
        });
    }

    [Theory]
    [InlineData("Production")][InlineData("Development")][InlineData(null)]
    public void PairedEnvironmentPolicyIsReturnedFromTheBoundSession(string? environment)
    {
        var service = new BrowserCompanionService(new BrowserCompanionEvidenceSanitizer(new BrowserEvidenceSanitizer()),
            TimeProvider.System, NullLogger<BrowserCompanionService>.Instance);
        var code = service.StartPairing(new("dev", "Test", environment, ["https://app.test"]));
        var pair = service.CompletePairing(new(code.PairingCode, "test"), "chrome-extension://abcdefghijklmnopabcdefghijklmnop");
        Assert.True(pair.Accepted);
        Assert.Equal(environment, pair.EnvironmentType);
        Assert.Equal(environment, service.ValidateSession(pair.SessionId!, "dev", "chrome-extension://abcdefghijklmnopabcdefghijklmnop").EnvironmentType);
    }
}
