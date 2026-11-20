using FluentAssertions;
using BirkNext.Api.Services.FrontendBrowserRuntime;
using BirkNext.Api.Services.FrontendLighthouse;
using Microsoft.Extensions.Logging.Abstractions;
using Cache = BirkNext.Api.Services.FrontendLighthouse.FrontendLighthouseReviewService.LighthouseReadinessCache;

namespace BirkNext.Api.Tests.Unit.FrontendLighthouse;

/// <summary>
/// Lighthouse readiness reuses a successful probe (Node + headless Chromium, ~15-20 s) for five minutes; the enablement and runner checks always
/// run, failures are never reused, and an updated runner file is probed again. No real Node is needed: an absent executable proves the failure
/// path, a seeded entry proves the reuse.
/// </summary>
public sealed class LighthouseReadinessCacheTests
{
    private static FrontendLighthouseReviewService Service(string runner, string node, bool enabled = true) => new(
        NullLogger<FrontendLighthouseReviewService>.Instance, new BrowserTargetValidator(), new LighthouseEvidenceSanitizer(new BrowserEvidenceSanitizer()), runner, node, enabled);

    private static readonly LighthouseReadinessResult Ready = new(LighthouseReadinessState.Ready, true, "12.0.0", "20.0.0", "Chromium", "130");

    private static string Runner()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lh-runner-{Guid.NewGuid():N}.mjs");
        File.WriteAllText(path, "// synthetic runner");
        return path;
    }

    [Fact]
    public async Task A_successful_probe_is_reused_but_a_disabled_engine_is_reported_at_once()
    {
        var runner = Runner();
        const string node = "birknext-no-such-node-for-cache-test";
        try
        {
            Cache.Set(Cache.Key(node, runner), Ready);

            (await Service(runner, node).CheckReadinessAsync()).Should().Be(Ready, "a recent successful probe is reused instead of starting Node and Chromium again");
            (await Service(runner, node, enabled: false).CheckReadinessAsync()).State.Should().Be(LighthouseReadinessState.Disabled, "the System Settings switch is always read");
        }
        finally { File.Delete(runner); }
    }

    [Fact]
    public async Task A_failed_probe_is_never_reused()
    {
        var runner = Runner();
        const string node = "birknext-no-such-node-for-failure-test";
        try
        {
            var first = await Service(runner, node).CheckReadinessAsync();
            first.Available.Should().BeFalse();
            first.State.Should().Be(LighthouseReadinessState.NodeUnavailable);
            Cache.TryGet(Cache.Key(node, runner)).Should().BeNull("a failure is probed again next time, so a fixed installation shows at once");
        }
        finally { File.Delete(runner); }
    }

    [Fact]
    public void An_entry_expires_after_five_minutes_and_an_updated_runner_is_probed_again()
    {
        var runner = Runner();
        try
        {
            var at = DateTimeOffset.UtcNow;
            var key = Cache.Key("node", runner);
            Cache.Set(key, Ready, at);
            Cache.TryGet(key, at.AddMinutes(4)).Should().Be(Ready);
            Cache.TryGet(key, at.AddMinutes(5)).Should().BeNull();

            File.SetLastWriteTimeUtc(runner, DateTime.UtcNow.AddMinutes(1));
            Cache.Key("node", runner).Should().NotBe(key, "a changed runner file is a different key");
        }
        finally { File.Delete(runner); }
    }
}
