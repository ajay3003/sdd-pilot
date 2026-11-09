using BirkNext.DeliveryReporting;
using BirkNext.Maintenance;
using BirkNext.Web.Components;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Maintenance → one "Reset data…" entry point: three explicit scopes with no default, the backend plan before anything is deleted, typed
/// RESET EVERYTHING only for the scope that overrides Test Data protection, and per-domain results that never claim success the backend did not report.
/// </summary>
public sealed class DataResetPanelTests : BunitContext
{
    private readonly Mock<AdminApiService> _api = new(new HttpClient()) { CallBase = false };
    private readonly DeliveryReportingSession _delivery = new();

    public DataResetPanelTests()
    {
        Services.AddSingleton(_api.Object);
        Services.AddSingleton(_delivery);
        foreach (var mode in Enum.GetValues<DataResetMode>())
            _api.Setup(a => a.GetDataResetPlanAsync(mode)).ReturnsAsync(Plan(mode));
    }

    private static DataResetPlan Plan(DataResetMode mode, bool allowed = true) => new()
    {
        Mode = mode, Title = mode.ToString(), Summary = $"{mode} summary", Removed = [$"{mode} removed"], Preserved = [$"{mode} preserved"],
        Notes = mode == DataResetMode.FullIncludingProtected ? ["Reset everything means all resettable local application data — not uninstalling BirkNext."] : [],
        Counts = new() { ["Delivery snapshots"] = 3 },
        ConfirmationPhrase = mode == DataResetMode.FullIncludingProtected ? "RESET EVERYTHING" : null,
        Allowed = allowed, NotAllowedReason = allowed ? null : "Reset is available only for a Local database on a non-production backend.",
    };

    private IRenderedComponent<DataResetPanel> RenderPanel(bool allowed = true) =>
        Render<DataResetPanel>(p => p.Add(x => x.Allowed, allowed).Add(x => x.NotAllowedReason, allowed ? null : "Reset is disabled by local administrator policy."));

    private static bool RunEnabled(IRenderedComponent<DataResetPanel> cut) => !cut.Find("[data-testid=reset-run]").HasAttribute("disabled");

    [Fact]
    public void One_entry_point_opens_three_scopes_with_nothing_selected()
    {
        var cut = RenderPanel();
        cut.FindAll("button").Should().ContainSingle(b => b.TextContent.Contains("Reset"), "Reset Local Data and Reset Test Data are replaced by one action");
        cut.Find("[data-testid=reset-open]").TextContent.Should().Be("Reset data…");

        cut.Find("[data-testid=reset-open]").Click();

        cut.FindAll("[data-testid^=reset-scope-]").Select(e => e.TextContent).Should().SatisfyRespectively(
            first => first.Should().Contain("Reset everything").And.Contain("including protected Test Data"),
            second => second.Should().Contain("Reset everything except protected data"),
            third => third.Should().Contain("Reset Delivery Management only"));
        cut.FindAll("input[type=radio]").Should().OnlyContain(r => !r.HasAttribute("checked"), "no scope is selected by default");
        cut.FindAll("[data-testid=reset-plan]").Should().BeEmpty();
        RunEnabled(cut).Should().BeFalse();
        cut.Find("[data-testid=reset-scope-FullIncludingProtected]").GetAttribute("data-severity").Should().Be("critical");
        cut.Find("[data-testid=reset-scope-DeliveryManagement]").GetAttribute("data-severity").Should().Be("moderate");
    }

    [Fact]
    public void Reset_everything_requires_the_exact_typed_phrase_and_lands_on_the_dashboard()
    {
        _api.Setup(a => a.ResetDataAsync(DataResetMode.FullIncludingProtected, "RESET EVERYTHING"))
            .ReturnsAsync(new DataResetResult { Mode = DataResetMode.FullIncludingProtected, Status = "Completed", ResetEpoch = 4, Message = "done" });
        var cut = RenderPanel();
        cut.Find("[data-testid=reset-open]").Click();
        cut.Find("[data-testid=reset-scope-FullIncludingProtected] input").Change(true);

        cut.WaitForAssertion(() => cut.Find("[data-testid=reset-removed]").TextContent.Should().Contain("FullIncludingProtected removed"));
        cut.Find("[data-testid=reset-preserved]").TextContent.Should().Contain("FullIncludingProtected preserved");
        cut.Find("[data-testid=reset-note]").TextContent.Should().Contain("not uninstalling BirkNext");
        cut.Find("[data-testid=reset-counts]").TextContent.Should().Contain("Delivery snapshots: 3");
        cut.FindAll("[data-testid=reset-confirm-ack]").Should().BeEmpty("the full scope is confirmed by typing, not a checkbox");
        RunEnabled(cut).Should().BeFalse();
        cut.Find("[data-testid=reset-confirm-text]").Input("reset everything");
        RunEnabled(cut).Should().BeFalse("the phrase is case-sensitive");
        cut.Find("[data-testid=reset-confirm-text]").Input("RESET EVERYTHING");
        RunEnabled(cut).Should().BeTrue();

        cut.Find("[data-testid=reset-run]").Click();

        cut.WaitForAssertion(() => Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith("dashboard?reset=done"));
        _api.Verify(a => a.ResetDataAsync(DataResetMode.FullIncludingProtected, "RESET EVERYTHING"), Times.Once);
    }

    [Fact]
    public void Delivery_only_is_acknowledged_shows_per_domain_results_and_clears_only_delivery_state()
    {
        var project = Guid.NewGuid();
        _delivery.SetPending(project, new DeliveryImportPreview(), null);
        _api.Setup(a => a.ResetDataAsync(DataResetMode.DeliveryManagement, "CONFIRM")).ReturnsAsync(new DataResetResult
        {
            Mode = DataResetMode.DeliveryManagement, Status = "Completed", Message = "Delivery Management reset completed: 42 records removed.",
            Domains = [new("Delivery Management", "Removed", "42 records")],
        });
        var cut = RenderPanel();
        cut.Find("[data-testid=reset-open]").Click();
        cut.Find("[data-testid=reset-scope-DeliveryManagement] input").Change(true);
        cut.WaitForAssertion(() => cut.Find("[data-testid=reset-plan]").GetAttribute("data-severity").Should().Be("moderate"));
        cut.FindAll("[data-testid=reset-confirm-text]").Should().BeEmpty();
        RunEnabled(cut).Should().BeFalse();
        cut.Find("[data-testid=reset-confirm-ack]").Change(true);

        cut.Find("[data-testid=reset-run]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=reset-result]").TextContent.Should().Contain("Reset completed"));
        cut.Find("[data-testid=reset-domains]").TextContent.Should().Contain("Removed").And.Contain("Delivery Management").And.Contain("42 records");
        Services.GetRequiredService<NavigationManager>().Uri.Should().NotContain("dashboard", "a delivery-only reset keeps the user here");
        _delivery.Pending(project).Should().BeNull("the staged preview no longer exists on the backend");
        cut.Find("[data-testid=reset-close]").Click();
        cut.Find("[data-testid=reset-delivery-done]").TextContent.Should().Contain("42 records removed");
    }

    [Theory]
    [InlineData("Failed", "Reset failed")]
    [InlineData("Blocked", "Reset blocked")]
    [InlineData("PartiallyCompleted", null)]
    public void A_reset_that_did_not_complete_is_never_reported_as_success(string status, string? title)
    {
        _api.Setup(a => a.ResetDataAsync(DataResetMode.FullPreserveProtected, "CONFIRM")).ReturnsAsync(new DataResetResult
        {
            Mode = DataResetMode.FullPreserveProtected, Status = status, Message = "Reset failed. See server logs.", ResetEpoch = 2,
            Domains = [new("Application database", "Removed"), new("Delivery Management", "Failed", "Delivery Management data could not be cleared.")],
        });
        var cut = RenderPanel();
        cut.Find("[data-testid=reset-open]").Click();
        cut.Find("[data-testid=reset-scope-FullPreserveProtected] input").Change(true);
        cut.WaitForAssertion(() => cut.Find("[data-testid=reset-confirm-ack]"));
        cut.Find("[data-testid=reset-confirm-ack]").Change(true);

        cut.Find("[data-testid=reset-run]").Click();

        var nav = Services.GetRequiredService<NavigationManager>();
        if (title is null)
        {
            cut.WaitForAssertion(() => nav.Uri.Should().EndWith("dashboard?reset=warnings", "a partial reset lands on the warnings banner, not on done"));
            return;
        }
        cut.WaitForAssertion(() => cut.Find("[data-testid=reset-result]").TextContent.Should().Contain(title));
        cut.Find("[data-testid=reset-result]").GetAttribute("role").Should().Be("alert");
        nav.Uri.Should().NotContain("reset=done");
    }

    [Fact]
    public void A_not_allowed_backend_disables_the_entry_point_and_a_refused_plan_cannot_run()
    {
        var blocked = RenderPanel(allowed: false);
        blocked.Find("[data-testid=reset-open]").HasAttribute("disabled").Should().BeTrue();
        blocked.Find("[data-testid=reset-not-allowed]").TextContent.Should().Contain("administrator policy");

        _api.Setup(a => a.GetDataResetPlanAsync(DataResetMode.DeliveryManagement)).ReturnsAsync(Plan(DataResetMode.DeliveryManagement, allowed: false));
        var cut = RenderPanel();
        cut.Find("[data-testid=reset-open]").Click();
        cut.Find("[data-testid=reset-scope-DeliveryManagement] input").Change(true);
        cut.WaitForAssertion(() => cut.Find("[data-testid=reset-plan-refused]").TextContent.Should().Contain("Local database"));
        cut.Find("[data-testid=reset-confirm-ack]").Change(true);
        RunEnabled(cut).Should().BeFalse();
    }

    [Fact]
    public void An_unreachable_backend_plan_is_an_error_never_an_empty_plan()
    {
        _api.Setup(a => a.GetDataResetPlanAsync(DataResetMode.FullPreserveProtected)).ReturnsAsync((DataResetPlan?)null);
        var cut = RenderPanel();
        cut.Find("[data-testid=reset-open]").Click();
        cut.Find("[data-testid=reset-scope-FullPreserveProtected] input").Change(true);
        cut.WaitForAssertion(() => cut.Find("[data-testid=reset-plan-error]").TextContent.Should().Contain("nothing was reset"));
        RunEnabled(cut).Should().BeFalse();
    }

    [Fact]
    public void A_schema_blocked_reset_says_what_was_requested_that_nothing_changed_and_what_to_do_next()
    {
        _api.Setup(a => a.ResetDataAsync(DataResetMode.FullPreserveProtected, "CONFIRM")).ReturnsAsync(new DataResetResult
        {
            Mode = DataResetMode.FullPreserveProtected, Status = "Blocked",
            Message = "Reset blocked: the database schema does not match this BirkNext build (1 table(s) missing in the database: integration_suites). Nothing was deleted.",
        });
        var cut = RenderPanel();
        cut.Find("[data-testid=reset-open]").Click();
        cut.Find("[data-testid=reset-scope-FullPreserveProtected] input").Change(true);
        cut.WaitForAssertion(() => cut.Find("[data-testid=reset-confirm-ack]"));
        cut.Find("[data-testid=reset-confirm-ack]").Change(true);
        cut.Find("[data-testid=reset-run]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=reset-result]").TextContent.Should().Contain("Reset blocked"));
        cut.Find("[data-testid=reset-requested]").TextContent.Should().Contain("Reset everything except protected data");
        cut.Find("[data-testid=reset-message]").TextContent.Should().Contain("integration_suites").And.NotContain("See server logs");
        cut.Find("[data-testid=reset-changed]").TextContent.Should().Contain("nothing");
        cut.Find("[data-testid=reset-next]").TextContent.Should().Contain("restart BirkNext");
        Services.GetRequiredService<NavigationManager>().Uri.Should().NotContain("reset=");
    }

    private sealed class StubHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond());
    }

    private static AdminApiService Client(Func<HttpResponseMessage> respond) => new(new HttpClient(new StubHandler(respond)) { BaseAddress = new Uri("http://localhost:5000/") });

    [Theory]
    [InlineData(System.Net.HttpStatusCode.Forbidden)]
    [InlineData(System.Net.HttpStatusCode.Unauthorized)]
    public async Task Permission_denied_is_reported_as_insufficient_permissions_with_nothing_changed(System.Net.HttpStatusCode status)
    {
        var result = await Client(() => new HttpResponseMessage(status)).ResetDataAsync(DataResetMode.FullPreserveProtected, "CONFIRM");
        result.Status.Should().Be("Refused");
        result.Success.Should().BeFalse();
        result.Message.Should().StartWith("Reset blocked: insufficient permissions").And.Contain("Nothing was changed");
    }

    [Fact]
    public async Task A_backend_failure_result_is_passed_through_and_a_non_result_response_never_leaks_internals()
    {
        var failed = await Client(() => new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
        {
            Content = System.Net.Http.Json.JsonContent.Create(new DataResetResult { Status = "Failed", Message = "Reset failed: the database operation could not be completed. Nothing was deleted (the change was rolled back). Reference: 0123abcd (server log)." }),
        }).ResetDataAsync(DataResetMode.FullPreserveProtected, "CONFIRM");
        failed.Status.Should().Be("Failed");
        failed.Message.Should().Contain("Reference: 0123abcd");

        var proxy = await Client(() => new HttpResponseMessage(System.Net.HttpStatusCode.BadGateway) { Content = new StringContent("<html>Npgsql stack trace</html>") })
            .ResetDataAsync(DataResetMode.FullPreserveProtected, "CONFIRM");
        proxy.Status.Should().Be("Failed");
        proxy.Message.Should().Contain("HTTP 502").And.NotContain("Npgsql").And.NotContain("html");

        var down = await Client(() => throw new HttpRequestException("No connection could be made 127.0.0.1:5000 secret-detail")).ResetDataAsync(DataResetMode.FullPreserveProtected, "CONFIRM");
        down.Status.Should().Be("Failed");
        down.Message.Should().Contain("could not be reached").And.NotContain("secret-detail");
    }
}
