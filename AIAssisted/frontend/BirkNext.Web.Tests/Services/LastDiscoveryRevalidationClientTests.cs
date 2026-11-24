using System.Net;
using System.Text;
using System.Text.Json;
using BirkNext.TestPlanning;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// The last discovery (8 MB for 1,752 M2LB scenarios) is read by every Manual Testing page. The client revalidates it by ETag and reuses the
/// result it already parsed while the backend answers 304; any change is read again, and nothing stale is kept.
/// </summary>
public sealed class LastDiscoveryRevalidationClientTests
{

    private sealed class DiscoveryServer(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string?> IfNoneMatch { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            IfNoneMatch.Add(request.Headers.IfNoneMatch.FirstOrDefault()?.Tag);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Ok(ScenarioDiscoveryResult result, string etag)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json"),
        };
        response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
        return response;
    }

    private static ScenarioDiscoveryResult Result(string id) => new() { DiscoveryId = id, ProjectKey = "p", Candidates = [new DiscoveredScenario { CandidateId = id + "-1", Title = "Søknad" }] };

    [Fact]
    public async Task An_unchanged_last_discovery_is_revalidated_and_reused_not_parsed_again()
    {
        var server = new DiscoveryServer(r => r.Headers.IfNoneMatch.Any(t => t.Tag == "\"v1\"") ? new HttpResponseMessage(HttpStatusCode.NotModified) : Ok(Result("d1"), "\"v1\""));
        var cache = new LastDiscoveryCache();
        ITestPlanSuggestionsApiService api = new TestPlanSuggestionsApiService(new HttpClient(server) { BaseAddress = new Uri("http://localhost:5000/") }, cache);

        var (first, error) = await api.LastDiscoveryAsync("p", null);
        var (second, _) = await api.LastDiscoveryAsync("p", null);

        error.Should().BeNull();
        first!.DiscoveryId.Should().Be("d1");
        second.Should().BeSameAs(first, "304 Not Modified: the parsed result is reused");
        server.IfNoneMatch.Should().Equal(null, "\"v1\"");
    }

    [Fact]
    public async Task A_changed_last_discovery_is_read_again()
    {
        var version = 1;
        var server = new DiscoveryServer(r => r.Headers.IfNoneMatch.Any(t => t.Tag == $"\"v{version}\"")
            ? new HttpResponseMessage(HttpStatusCode.NotModified) : Ok(Result($"d{version}"), $"\"v{version}\""));
        ITestPlanSuggestionsApiService api = new TestPlanSuggestionsApiService(new HttpClient(server) { BaseAddress = new Uri("http://localhost:5000/") }, new LastDiscoveryCache());

        (await api.LastDiscoveryAsync("p", null)).Result!.DiscoveryId.Should().Be("d1");
        version = 2;
        (await api.LastDiscoveryAsync("p", null)).Result!.DiscoveryId.Should().Be("d2");
        (await api.LastDiscoveryAsync("p", null)).Result!.DiscoveryId.Should().Be("d2");
    }

    [Fact]
    public async Task No_last_discovery_or_a_failed_read_keeps_nothing_stale()
    {
        var status = HttpStatusCode.OK;
        var server = new DiscoveryServer(r => status == HttpStatusCode.OK ? Ok(Result("d1"), "\"v1\"") : new HttpResponseMessage(status));
        var cache = new LastDiscoveryCache();
        ITestPlanSuggestionsApiService api = new TestPlanSuggestionsApiService(new HttpClient(server) { BaseAddress = new Uri("http://localhost:5000/") }, cache);
        await api.LastDiscoveryAsync("p", null);
        cache.TryGet(LastDiscoveryCache.KeyOf("p", null), out _, out _).Should().BeTrue();

        status = HttpStatusCode.NoContent;
        (await api.LastDiscoveryAsync("p", null)).Result.Should().BeNull("the project has no last discovery any more");
        cache.TryGet(LastDiscoveryCache.KeyOf("p", null), out _, out _).Should().BeFalse();

        status = HttpStatusCode.OK;
        await api.LastDiscoveryAsync("p", null);
        status = HttpStatusCode.InternalServerError;
        (await api.LastDiscoveryAsync("p", null)).Error.Should().NotBeNull();
        cache.TryGet(LastDiscoveryCache.KeyOf("p", null), out _, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Projects_and_snapshots_are_kept_apart_and_the_cache_is_bounded()
    {
        var server = new DiscoveryServer(r => Ok(Result(r.RequestUri!.Query), "\"" + r.RequestUri!.Query.GetHashCode() + "\""));
        var cache = new LastDiscoveryCache();
        ITestPlanSuggestionsApiService api = new TestPlanSuggestionsApiService(new HttpClient(server) { BaseAddress = new Uri("http://localhost:5000/") }, cache);
        var snapshot = Guid.NewGuid();

        var a = (await api.LastDiscoveryAsync("a", null)).Result!;
        var b = (await api.LastDiscoveryAsync("a", snapshot)).Result!;
        var c = (await api.LastDiscoveryAsync("c", null)).Result!;
        new[] { a.DiscoveryId, b.DiscoveryId, c.DiscoveryId }.Should().OnlyHaveUniqueItems();

        for (var i = 0; i < LastDiscoveryCache.Capacity + 3; i++) await api.LastDiscoveryAsync($"x{i}", null);
        new[] { "a", "c" }.Concat(Enumerable.Range(0, LastDiscoveryCache.Capacity + 3).Select(i => $"x{i}"))
            .Count(k => cache.TryGet(LastDiscoveryCache.KeyOf(k, null), out _, out _)).Should().Be(LastDiscoveryCache.Capacity);
    }

    [Fact]
    public async Task Without_a_cache_every_read_is_a_plain_read()
    {
        var server = new DiscoveryServer(_ => Ok(Result("d1"), "\"v1\""));
        ITestPlanSuggestionsApiService api = new TestPlanSuggestionsApiService(new HttpClient(server) { BaseAddress = new Uri("http://localhost:5000/") });

        await api.LastDiscoveryAsync("p", null);
        await api.LastDiscoveryAsync("p", null);

        server.IfNoneMatch.Should().Equal(null, null);
    }
}
