using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Services.LocalHttpsProxy;
using BirkNext.LocalHttpsProxy;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BirkNext.Api.Tests.Services.LocalHttpsProxy;

/// <summary>
/// Endpoint Discovery history: runs per Target Environment + host, operations with first/last-seen and counts, states relative to the latest
/// run (New / Observed again / Previously observed — never "removed"), a never-run vs ran-with-nothing distinction, a baseline import that
/// invents no delta, an in-progress capture merged without persisting, and safe normalized identities only. In-memory database; no network.
/// </summary>
public sealed class EndpointObservationHistoryServiceTests : IDisposable
{
    private const string Dev = "dev-env", Qa = "qa-env", Host = "m2lbdev.bufetat.no";
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"eoh-{Guid.NewGuid()}").Options);
    private EndpointObservationHistoryService Service => new(_db, TimeProvider.System);
    public void Dispose() => _db.Dispose();

    private static ObservedNetworkEndpoint Rest(string method, string path, int minutes = 0, int count = 1, string host = Host) => new()
    {
        Category = ObservedTrafficCategory.Rest, Host = host, Port = 443, Method = method, Path = path, Count = count, LastStatus = 200,
        FirstObservedAt = T0.AddMinutes(minutes), LastObservedAt = T0.AddMinutes(minutes + 1), AuthObserved = true,
    };

    private static ObservedNetworkEndpoint GraphQl(string name, GraphQlOperationType type = GraphQlOperationType.Query) => new()
    {
        Category = ObservedTrafficCategory.GraphQl, Host = Host, Port = 443, Method = "POST", Path = "/graphql", OperationName = name, OperationType = type,
        FirstObservedAt = T0, LastObservedAt = T0.AddMinutes(1),
    };

    private Task<EndpointDiscoveryRunSummary> Run(string id, int minutes, string env = Dev, string host = Host, params ObservedNetworkEndpoint[] observed) =>
        Service.RecordRunAsync(env, host, id, T0.AddMinutes(minutes), T0.AddMinutes(minutes + 5), observed, CancellationToken.None);

    private Task<EndpointObservationHistoryView> View(string env = Dev, string host = Host, IReadOnlyList<ObservedNetworkEndpoint>? live = null) =>
        Service.ViewAsync(env, host, live, CancellationToken.None);

    [Fact]
    public async Task Never_run_and_ran_with_zero_operations_are_different_states()
    {
        (await View()).Should().Match<EndpointObservationHistoryView>(v => !v.EverRun && v.LatestRun == null && v.Operations.Count == 0);

        await Run("run-1", 0);
        var empty = await View();
        empty.EverRun.Should().BeTrue();
        empty.LatestRun!.Should().Match<EndpointDiscoveryRunSummary>(r => r.Observed == 0 && r.Status == EndpointDiscoveryRunSummary.Completed);
    }

    [Fact]
    public async Task Later_runs_keep_history_and_mark_new_observed_again_and_previously_observed()
    {
        await Run("run-1", 0, observed: [Rest("GET", "/api/children/123"), Rest("GET", "/api/person/12")]);
        var second = await Run("run-2", 60, observed: [Rest("GET", "/api/person/45", 60, count: 3), Rest("GET", "/api/status", 60)]);
        (second.Observed, second.New, second.ObservedAgain, second.NotSeen).Should().Be((2, 1, 1, 1), "computed from normalized identities");

        var view = await View();
        var byIdentity = view.Operations.ToDictionary(o => o.Identity);
        byIdentity.Keys.Should().BeEquivalentTo(["GET /api/children/{id}", "GET /api/person/{id}", "GET /api/status"], "observed identifiers are templated, never stored");
        byIdentity["GET /api/person/{id}"].Should().Match<ObservedOperationHistory>(o => o.State == ObservedOperationState.ObservedAgain && o.ObservationCount == 4
            && o.FirstRunId == "run-1" && o.LastRunId == "run-2" && o.FirstObservedAt == T0 && o.Current);
        byIdentity["GET /api/status"].State.Should().Be(ObservedOperationState.New);
        byIdentity["GET /api/children/{id}"].Should().Match<ObservedOperationHistory>(o => o.State == ObservedOperationState.PreviouslyObserved && !o.Current,
            "not seen in the latest run is not removed — it stays in history");
        (view.LatestRun!.RunId, view.PreviousRun!.RunId).Should().Be(("run-2", "run-1"));
    }

    [Fact]
    public async Task History_is_scoped_by_environment_and_host_and_a_run_is_recorded_once()
    {
        await Run("dev-run", 0, observed: [Rest("GET", "/api/person/1")]);
        await Run("qa-run", 0, env: Qa, observed: [Rest("GET", "/api/qa-only")]);
        await Run("old-host", 0, host: "old.example.org", observed: [Rest("GET", "/api/legacy", host: "old.example.org")]);
        (await View()).Operations.Select(o => o.Identity).Should().Equal("GET /api/person/{id}");
        (await View(Qa)).Operations.Select(o => o.Identity).Should().Equal("GET /api/qa-only");
        (await View(host: "old.example.org")).Operations.Should().ContainSingle("the previous host keeps its own history; it never becomes current for the new host");

        await Run("dev-run", 0, observed: [Rest("GET", "/api/person/1")]);
        (await View()).Operations.Single().ObservationCount.Should().Be(1, "recording the same run again changes nothing");
        (await View()).Runs.Should().ContainSingle();
    }

    [Fact]
    public async Task GraphQL_identity_is_the_operation_not_the_body_and_other_hosts_and_assets_are_ignored()
    {
        await Run("run-1", 0, observed:
        [
            GraphQl("GetChildren"), GraphQl("SaveReport", GraphQlOperationType.Mutation),
            Rest("GET", "/api/x", host: "login.microsoftonline.com"),
            new() { Category = ObservedTrafficCategory.StaticAsset, Host = Host, Method = "GET", Path = "/app.js", FirstObservedAt = T0, LastObservedAt = T0 },
        ]);
        (await View()).Operations.Select(o => (o.Identity, o.Protocol)).Should().BeEquivalentTo(new[] { ("graphql query GetChildren", "GraphQL"), ("graphql mutation SaveReport", "GraphQL") });
    }

    [Fact]
    public async Task A_baseline_from_earlier_browser_history_is_imported_once_without_inventing_deltas()
    {
        var baseline = await Service.ImportBaselineAsync(Dev, Host, [Rest("GET", "/api/person/9", -600)], CancellationToken.None);
        baseline!.Should().Match<EndpointDiscoveryRunSummary>(r => r.Status == EndpointDiscoveryRunSummary.Baseline && r.Observed == 1 && r.New == 0 && r.NotSeen == 0);
        var view = await View();
        view.Operations.Single().Should().Match<ObservedOperationHistory>(o => o.State == ObservedOperationState.Observed && o.FirstObservedAt == T0.AddMinutes(-600),
            "its own timestamps are kept; no 'New' is invented");
        (await Service.ImportBaselineAsync(Dev, Host, [Rest("GET", "/api/other")], CancellationToken.None)).Should().BeNull("only once, only before any run");
    }

    [Fact]
    public async Task A_capture_in_progress_is_the_latest_run_without_being_persisted()
    {
        await Run("run-1", 0, observed: [Rest("GET", "/api/person/1"), Rest("GET", "/api/children")]);
        var view = await View(live: [Rest("GET", "/api/person/7", 30), Rest("POST", "/api/report", 30)]);

        view.LatestRun!.Should().Match<EndpointDiscoveryRunSummary>(r => r.IsInProgress && r.Observed == 2 && r.New == 1 && r.ObservedAgain == 1 && r.NotSeen == 1);
        view.Operations.Single(o => o.Identity == "POST /api/report").State.Should().Be(ObservedOperationState.New);
        view.Operations.Single(o => o.Identity == "GET /api/person/{id}").State.Should().Be(ObservedOperationState.ObservedAgain);
        view.Operations.Single(o => o.Identity == "GET /api/children").State.Should().Be(ObservedOperationState.PreviouslyObserved);
        (await _db.ObservedOperations.CountAsync()).Should().Be(2, "a live view never writes history");
    }

    [Fact]
    public async Task Only_safe_metadata_is_persisted()
    {
        await Run("run-1", 0, observed: [Rest("GET", "/api/person/12345678901?token=abc&ssn=010101"), GraphQl("GetChildren")]);
        var stored = JsonSerializer.Serialize(await _db.ObservedOperations.ToListAsync());
        stored.Should().NotContain("12345678901").And.NotContain("token").And.NotContain("abc").And.NotContain("Bearer").And.NotContain("ssn");
        (await _db.ObservedOperations.SingleAsync(o => o.Protocol == "REST")).Path.Should().Be("/api/person/{id}");
    }
}
