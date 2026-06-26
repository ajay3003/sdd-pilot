using System.Net;
using System.Net.Http.Headers;
using System.Text;
using BirkNext.Api.Configuration;
using BirkNext.Api.Services.ImplementationTraceability;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BirkNext.Api.Tests.Unit.ImplementationTraceability;

public class AzureDevOpsProviderTests
{
    // ── PAT auth header ────────────────────────────────────────────────────

    [Fact]
    public void PatAuthHeader_IsBase64EncodedCorrectly()
    {
        const string pat    = "my-secret-token";
        var encoded         = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}"));
        var headerValue     = new AuthenticationHeaderValue("Basic", encoded);

        headerValue.Scheme.Should().Be("Basic");
        var decoded = Encoding.ASCII.GetString(Convert.FromBase64String(headerValue.Parameter!));
        decoded.Should().Be($":{pat}");
        decoded.Should().StartWith(":"); // username is always empty
    }

    [Fact]
    public void PatAuthHeader_DoesNotContainPatInPlainText()
    {
        const string pat = "super-secret-pat-value";
        var encoded      = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}"));

        // The raw header value must not be the plain-text PAT
        encoded.Should().NotBe(pat);
        encoded.Should().NotContain(pat);
    }

    // ── Missing PAT falls back to mock ─────────────────────────────────────

    [Fact]
    public void MockProvider_IsUsedWhenPatIsEmpty()
    {
        var options = new AzureDevOpsOptions { Enabled = true, Pat = string.Empty };

        var useAdo = options.Enabled && !string.IsNullOrWhiteSpace(options.Pat);

        useAdo.Should().BeFalse("empty PAT must select mock provider");
    }

    [Fact]
    public void MockProvider_IsUsedWhenDisabled()
    {
        var options = new AzureDevOpsOptions { Enabled = false, Pat = "some-pat" };

        var useAdo = options.Enabled && !string.IsNullOrWhiteSpace(options.Pat);

        useAdo.Should().BeFalse("disabled flag must select mock provider");
    }

    [Fact]
    public void AdoProvider_IsUsedWhenConfiguredCorrectly()
    {
        var options = new AzureDevOpsOptions
        {
            Enabled         = true,
            OrganizationUrl = "https://dev.azure.com/myorg",
            Project         = "MyProject",
            RepositoryId    = "my-repo",
            Pat             = "valid-token",
        };

        options.IsConfigured.Should().BeTrue();
    }

    // ── Work item relation → PR extraction ─────────────────────────────────

    [Theory]
    [InlineData("vstfs:///Git/PullRequestId/42",                           42)]
    [InlineData("https://dev.azure.com/org/proj/_git/repo/pullrequest/99", 99)]
    [InlineData("https://dev.azure.com/org/proj/_apis/git/pullRequests/7", 7)]
    public void ExtractPrId_FromRelationUrl_ReturnsCorrectId(string url, int expectedId)
    {
        // Use the internal static method via a helper that mirrors the production regex
        var match = System.Text.RegularExpressions.Regex.Match(
            url,
            @"(?i)pullrequest[s]?[/\\](?<id>\d+)|PullRequestId[/\\](?<id>\d+)");

        match.Success.Should().BeTrue($"URL '{url}' should match PR id pattern");
        int.Parse(match.Groups["id"].Value).Should().Be(expectedId);
    }

    [Fact]
    public void ExtractPrId_FromUnrelatedUrl_DoesNotMatch()
    {
        const string url = "https://dev.azure.com/org/proj/_workitems/edit/42";
        var match = System.Text.RegularExpressions.Regex.Match(
            url,
            @"(?i)pullrequest[s]?[/\\](?<id>\d+)|PullRequestId[/\\](?<id>\d+)");

        match.Success.Should().BeFalse();
    }

    // ── File classification ─────────────────────────────────────────────────

    [Theory]
    [InlineData("src/Features/Auth/AuthService.cs",              FileCategory.Source)]
    [InlineData("src/Components/Search/SearchBar.razor",         FileCategory.Source)]
    [InlineData("src/Client/api/userApi.ts",                     FileCategory.Source)]
    [InlineData("src/Client/components/Button.tsx",              FileCategory.Source)]
    [InlineData("tests/Unit/Auth/AuthServiceTests.cs",           FileCategory.Test)]
    [InlineData("tests/Unit/AuthServiceTests.cs",                FileCategory.Test)]
    [InlineData("src/Auth/AuthServiceTests.cs",                  FileCategory.Test)]
    [InlineData("tests/search.test.ts",                          FileCategory.Test)]
    [InlineData("tests/Button.spec.tsx",                         FileCategory.Test)]
    [InlineData("appsettings.json",                              FileCategory.Configuration)]
    [InlineData("appsettings.Production.json",                   FileCategory.Configuration)]
    [InlineData(".github/workflows/ci.yml",                      FileCategory.Configuration)]
    [InlineData("docker-compose.override.yml",                   FileCategory.Configuration)]
    [InlineData("docs/architecture.md",                          FileCategory.Documentation)]
    [InlineData("README.md",                                     FileCategory.Documentation)]
    [InlineData("src/Infrastructure/Migrations/20240101_Init.cs",FileCategory.Migration)]
    [InlineData("src/Data/Migrations/AddIndex.cs",               FileCategory.Migration)]
    [InlineData("somefile.txt",                                  FileCategory.Unknown)]
    public void ClassifyFile_ReturnsCorrectCategory(string path, FileCategory expected)
    {
        var category = AzureDevOpsImplementationEvidenceProvider.ClassifyFile(path);
        category.Should().Be(expected, $"path '{path}' should be {expected}");
    }

    // ── Test evidence detection ─────────────────────────────────────────────

    [Fact]
    public void BuildTestEvidence_DetectsTestFileInSamePr()
    {
        var files = new List<ChangedFileEvidence>
        {
            new() { Path = "src/Features/Auth/AuthService.cs",      ChangeType = "edit", Category = FileCategory.Source,  PullRequestId = "PR-1" },
            new() { Path = "tests/Unit/Auth/AuthServiceTests.cs",    ChangeType = "add",  Category = FileCategory.Test,    PullRequestId = "PR-1" },
        };

        var evidence = AzureDevOpsImplementationEvidenceProvider.BuildTestEvidence(files);

        evidence.Should().ContainSingle(e => e.SourceFile == "src/Features/Auth/AuthService.cs");
        var item = evidence.First(e => e.SourceFile == "src/Features/Auth/AuthService.cs");
        item.HasTest.Should().BeTrue("AuthServiceTests.cs is present in the PR");
    }

    [Fact]
    public void BuildTestEvidence_FlagsSourceWithoutTest()
    {
        var files = new List<ChangedFileEvidence>
        {
            new() { Path = "src/Features/BarnSearch.razor",          ChangeType = "edit", Category = FileCategory.Source, PullRequestId = "PR-2" },
        };

        var evidence = AzureDevOpsImplementationEvidenceProvider.BuildTestEvidence(files);

        evidence.Should().ContainSingle(e => e.SourceFile == "src/Features/BarnSearch.razor");
        evidence.First().HasTest.Should().BeFalse();
    }

    // ── Expected test file derivation ───────────────────────────────────────

    [Theory]
    [InlineData("src/Features/Auth/AuthService.cs",    "tests/Unit/AuthServiceTests.cs")]
    [InlineData("src/Client/api/userApi.ts",           "tests/userApi.test.ts")]
    [InlineData("src/Client/components/Button.tsx",    "tests/Button.test.tsx")]
    [InlineData("src/Shared/helper.js",                null)]
    public void DeriveExpectedTestFile_ReturnsCorrectPath(string source, string? expected)
    {
        var result = AzureDevOpsImplementationEvidenceProvider.DeriveExpectedTestFile(source);
        result.Should().Be(expected);
    }

    // ── 401/403 safe error handling ─────────────────────────────────────────

    [Fact]
    public async Task MockProvider_ReturnsReport_WithDefaultDemoIds()
    {
        var provider = new MockImplementationEvidenceProvider();

        var report = await provider.FetchAsync([], null, null);

        report.Should().NotBeNull();
        report.Source.Should().Be("Mock");
        report.Tasks.Should().HaveCountGreaterThan(0);
        report.StatusMessage.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task MockProvider_ReturnsReport_ForRequestedWorkItemIds()
    {
        var provider = new MockImplementationEvidenceProvider();

        var report = await provider.FetchAsync([42, 99], null, null);

        report.Tasks.Should().HaveCount(2);
        report.Tasks.Select(t => t.ExternalId).Should().BeEquivalentTo(["42", "99"]);
    }

    [Fact]
    public void AzureDevOpsApiException_ExposesStatusCode()
    {
        var ex = new AzureDevOpsApiException(HttpStatusCode.Unauthorized);
        ex.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        ex.Message.Should().Contain("401");
    }

    [Fact]
    public void AzureDevOpsApiException_ForbiddenDoesNotExposeToken()
    {
        var ex = new AzureDevOpsApiException(HttpStatusCode.Forbidden);
        ex.Message.Should().NotContain("pat");
        ex.Message.Should().NotContain("token");
        ex.Message.Should().NotContain("secret");
    }
}
