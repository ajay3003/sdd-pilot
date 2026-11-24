using System.Text.Json;
using BirkNext.Api.Controllers;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.TestPlanning;
using BirkNext.TestPlanning;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Tests.Controllers;

/// <summary>
/// GET discover/last is revalidated by an ETag over the exact JSON it sends (8 MB for the 1,752 M2LB scenarios, read by every Manual Testing
/// page): 304 while the result is byte-identical, the full result whenever anything in it changed.
/// </summary>
public sealed class LastDiscoveryRevalidationTests
{
    private readonly ScenarioDiscoveryCache _cache = new();
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private TestScenarioDiscoveryService Discovery() => new(new IqrSourceStore(null!), [], _cache);

    private static ScenarioDiscoveryResult Result(string title) => new()
    {
        DiscoveryId = "d1", ProjectKey = "project-a", SnapshotKey = "s1", DiscoveredAt = DateTimeOffset.UnixEpoch,
        Candidates = [new DiscoveredScenario { CandidateId = "c1", ProjectKey = "project-a", Title = title }],
    };

    private async Task<(IActionResult Result, HttpContext Http)> GetAsync(string? ifNoneMatch = null)
    {
        var http = new DefaultHttpContext();
        if (ifNoneMatch is not null) http.Request.Headers.IfNoneMatch = ifNoneMatch;
        var controller = new TestPlanSuggestionsController(null!, null!) { ControllerContext = new ControllerContext { HttpContext = http } };
        var result = await controller.LastDiscovery("project-a", null, Discovery(),
            Microsoft.Extensions.Options.Options.Create(new JsonOptions { JsonSerializerOptions = { PropertyNamingPolicy = JsonNamingPolicy.CamelCase } }), default);
        return (result, http);
    }

    [Fact]
    public async Task The_result_carries_an_etag_and_is_always_revalidated()
    {
        _cache.Store(Result("Søknad"));

        var (result, http) = await GetAsync();

        var file = result.Should().BeOfType<FileContentResult>().Subject;
        JsonSerializer.Deserialize<ScenarioDiscoveryResult>(file.FileContents, _json)!.Candidates.Single().Title.Should().Be("Søknad");
        http.Response.Headers.ETag.ToString().Should().MatchRegex("^\"[0-9A-F]{64}\"$");
        http.Response.Headers.CacheControl.ToString().Should().Be("no-cache");
    }

    [Fact]
    public async Task An_unchanged_result_is_not_sent_again()
    {
        _cache.Store(Result("Søknad"));
        var (_, first) = await GetAsync();
        var etag = first.Response.Headers.ETag.ToString();

        var (result, second) = await GetAsync(etag);

        result.Should().BeOfType<StatusCodeResult>().Which.StatusCode.Should().Be(StatusCodes.Status304NotModified);
        second.Response.Headers.ETag.ToString().Should().Be(etag);
        (await GetAsync($"\"other\", {etag}")).Result.Should().BeOfType<StatusCodeResult>("one of several validators matches");
    }

    [Fact]
    public async Task Any_change_in_the_result_is_sent_in_full()
    {
        _cache.Store(Result("Søknad"));
        var (_, first) = await GetAsync();
        var etag = first.Response.Headers.ETag.ToString();

        _cache.Store(Result("Søknad endret"));
        var (result, second) = await GetAsync(etag);

        result.Should().BeOfType<FileContentResult>();
        second.Response.Headers.ETag.ToString().Should().NotBe(etag);
    }

    [Fact]
    public async Task No_last_discovery_is_still_no_content()
    {
        (await GetAsync()).Result.Should().BeOfType<NoContentResult>();
    }
}
