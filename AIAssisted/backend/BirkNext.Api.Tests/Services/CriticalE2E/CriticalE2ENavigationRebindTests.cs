using BirkNext.Api.Services.BrowserCompanion;
using BirkNext.Api.Services.CriticalE2E;
using BirkNext.Api.Services.FrontendBrowserRuntime;
using BirkNext.BrowserCompanion;
using BirkNext.CriticalE2E;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BirkNext.Api.Tests.Services.CriticalE2E;

/// <summary>
/// Found on real M2LB DEV: a Navigate-by-route step is a full page load, so the tab's content script is replaced and the
/// run's page binding ended — every later step was Blocked as "no longer open". The run now follows ITS OWN navigation in
/// the same tab. Any other reload, and any other tab, still counts as a different page.
/// </summary>
public sealed class CriticalE2ENavigationRebindTests
{
    private const string Extension = "chrome-extension://abcdefghijklmnopabcdefghijklmnop";
    private const string Origin = "https://m2lbdev.bufetat.no";
    private readonly BrowserCompanionService _companion = new(new BrowserCompanionEvidenceSanitizer(new BrowserEvidenceSanitizer()), TimeProvider.System, NullLogger<BrowserCompanionService>.Instance);
    private readonly CompanionBrowserStepExecutor _executor;
    private string _sessionId = "";

    public CriticalE2ENavigationRebindTests()
    {
        _executor = new CompanionBrowserStepExecutor(_companion, TimeProvider.System, NullLogger<CompanionBrowserStepExecutor>.Instance);
        var challenge = _companion.StartPairing(new BrowserCompanionPairingStartRequest("dev", "M2LB DEV", "Development", [Origin]));
        _sessionId = _companion.CompletePairing(new BrowserCompanionPairRequest(challenge.PairingCode, "0.1.0"), Extension).SessionId!;
    }

    private BrowserCompanionAcceptResult Beat(params string[] pageIds) =>
        _companion.Heartbeat(new BrowserCompanionHeartbeat(_sessionId, "dev", null, null, "0.1.0",
            pageIds.Select(id => new BrowserCompanionLivePageReport(id, Origin, "/admin/general-roles", id.Split('-')[1])).ToList(),
            [CompanionCapabilities.ElementPick]), Extension);

    private void Complete(CompanionAutomationCommand command, CriticalE2EStatus status = CriticalE2EStatus.Passed) =>
        _companion.CompleteCommand(new CompanionAutomationResultEnvelope
        {
            SessionId = _sessionId, ProfileId = "dev", ExtensionVersion = "0.1.0",
            Result = new CompanionAutomationResult { CommandId = command.CommandId, Status = status, ObservedRoute = "/admin/general-roles" },
        }, Extension);

    private static CriticalE2ERunContext Context(string pageId) => new()
    {
        Flow = new CriticalE2EFlowDefinition { Id = "f", ProfileId = "dev", EnvironmentId = "dev" },
        RunId = "run-" + Guid.NewGuid().ToString("N")[..6], CorrelationId = "c", StartedAt = DateTimeOffset.UtcNow,
        TargetOrigin = Origin, PageId = pageId,
    };

    private static CriticalE2EStepDefinition Navigate() => new()
    {
        StepId = "nav", BrowserAction = CompanionActionKind.Navigate, Value = "/admin/general-roles", TimeoutMs = 2_000,
    };

    /// <summary>Runs one step while playing the companion: claim, report the outcome, then report the tab's pages.</summary>
    private async Task<CriticalE2EStepResult> Execute(CriticalE2EStepDefinition step, CriticalE2ERunContext context, string[] pagesAfter)
    {
        var running = _executor.ExecuteAsync(step, context, CancellationToken.None);
        var claimed = Beat(context.PageId!).PendingCommand!;
        claimed.PageId.Should().Be(context.PageId);
        Complete(claimed);
        Beat(pagesAfter);
        return await running;
    }

    [Fact]
    public async Task ARunFollowsItsOwnNavigationToTheSameTabsNewPage()
    {
        Beat("t7-first");
        var context = Context("t7-first");

        var result = await Execute(Navigate(), context, ["t7-second"]);

        result.Status.Should().Be(CriticalE2EStatus.Passed);
        context.PageId.Should().Be("t7-second");
    }

    [Fact]
    public async Task ADifferentTabIsNeverAdopted()
    {
        Beat("t7-first");
        var context = Context("t7-first");

        var result = await Execute(Navigate(), context, ["t9-other"]);

        result.Status.Should().Be(CriticalE2EStatus.Blocked);
        result.SanitizedError.Should().Contain("did not come back after navigating");
        context.PageId.Should().Be("t7-first", "another tab is another page");
    }

    [Fact]
    public async Task OnlyANavigateStepRebinds_AClickThatReloadedStaysStale()
    {
        Beat("t7-first");
        var context = Context("t7-first");
        var click = new CriticalE2EStepDefinition { StepId = "c", BrowserAction = CompanionActionKind.Click,
            Selector = new CompanionSelector { Kind = CompanionSelectorKind.TestId, Value = "x" } };

        (await Execute(click, context, ["t7-second"])).Status.Should().Be(CriticalE2EStatus.Passed);

        context.PageId.Should().Be("t7-first");
    }

    [Fact]
    public async Task TheNextStepGoesToTheReloadedPage()
    {
        Beat("t7-first");
        var context = Context("t7-first");
        await Execute(Navigate(), context, ["t7-second"]);

        var next = _executor.ExecuteAsync(new CriticalE2EStepDefinition { StepId = "a", BrowserAction = CompanionActionKind.AssertRoute, Expected = "/admin/general-roles" },
            context, CancellationToken.None);
        var claimed = Beat("t7-second").PendingCommand!;
        claimed.PageId.Should().Be("t7-second");
        claimed.ContentScriptInstanceId.Should().Be("second");
        Complete(claimed);
        (await next).Status.Should().Be(CriticalE2EStatus.Passed);
    }

    [Fact]
    public void TheTabKeyIsThePageIdWithoutItsInstance()
    {
        new BrowserCompanionLivePage { PageId = "t1164473519-6zd9lwfso7k9" }.TabKey.Should().Be("t1164473519");
        new BrowserCompanionLivePage { PageId = "current" }.TabKey.Should().Be("current");
    }
}
