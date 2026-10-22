using BirkNext.Integrations;
using BirkNext.TestData;
using BirkNext.Web.Components;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Components;

/// <summary>Shared Test Data in the browser: candidates load only for a snapshot the reader chooses and are never shown as approved; readiness
/// shows aliases and states (never values) and never reuses another environment's asset.</summary>
public sealed class TestDataLifecycleUiTests : BunitContext
{
    private static readonly Guid Older = Guid.Parse("7d000000-0000-0000-0000-000000000001"), Newer = Guid.Parse("7d000000-0000-0000-0000-000000000002");

    private static BirkNext.Web.Tests.Integration.FakeIntegrationCatalogApi Snapshots() => new()
    {
        SourceSnapshots =
        [
            new() { Id = Newer, Archive = new("receiver.zip", "b", 1), AnalyzedAt = DateTimeOffset.Parse("2026-10-22T08:00:00Z") },
            new() { Id = Older, Archive = new("receiver.zip", "a", 1), AnalyzedAt = DateTimeOffset.Parse("2026-10-01T08:00:00Z") },
        ],
    };

    private sealed class Api(params TestDataAssetSummary[] assets) : ITestDataApiService
    {
        public List<Guid> Requested { get; } = [];
        public Task<(TestDataCatalogView? Catalog, string? Error)> CatalogAsync(CancellationToken ct = default) => Task.FromResult<(TestDataCatalogView?, string?)>((new([.. assets], [], []), null));
        public Task<(TestDataAssetDetail? Detail, string? Error)> AssetAsync(string id, CancellationToken ct = default) => Task.FromResult<(TestDataAssetDetail?, string?)>((null, "n/a"));
        public Task<IReadOnlyList<TestDataCandidate>?> CandidatesAsync(Guid snapshotId, CancellationToken ct = default)
        {
            Requested.Add(snapshotId);
            return Task.FromResult<IReadOnlyList<TestDataCandidate>?>(
            [
                new("contract:x:valid", TestDataCandidateOrigin.ContractEvidence, "Report — valid structure", "application/xml", TestDataIntent.Positive, null,
                    ["Report/Fodselsnummer = " + TestDataLifecycle.ControlledReference], ["Report/Id: required"], "schemas/report.xsd", "Accepted"),
                new("contract:x:missing:Report/Id", TestDataCandidateOrigin.ContractEvidence, "Report — missing Report/Id", "application/xml", TestDataIntent.Negative, null,
                    [], ["Report/Id: omitted on purpose"], "schemas/report.xsd", "Rejected (schema validation) — a rejection is the expected, passing outcome"),
            ]);
        }
    }

    private static TestDataAssetSummary Asset(string alias, TestDataKind kind, string? environment) => new()
    {
        Id = alias, Alias = alias, Kind = kind, Environment = environment, Approval = TestDataApproval.Approved, Source = "Configured", DisplayValue = "SECRET-VALUE",
    };

    [Fact]
    public void Candidates_load_only_for_the_chosen_snapshot_and_are_never_shown_as_approved()
    {
        var api = new Api();
        Services.AddSingleton<ITestDataApiService>(api);
        Services.AddSingleton<IIntegrationCatalogApiService>(Snapshots());
        var cut = Render<TestDataCandidatesPanel>();

        api.Requested.Should().BeEmpty("the newest snapshot is never selected automatically");
        cut.FindAll("[data-testid=td-candidate]").Should().BeEmpty();

        cut.Find("[data-testid=td-candidates-snapshot]").Change(Older.ToString());

        api.Requested.Should().Equal(Older);
        cut.FindAll("[data-testid=td-candidate]").Should().HaveCount(2);
        cut.FindAll("[data-testid=td-candidate-state]").Should().OnlyContain(s => s.TextContent == "Candidate — not approved");
        cut.Find("[data-testid=td-candidates-note]").TextContent.Should().Contain("never approved automatically").And.Contain("never generated");
        cut.Markup.Should().Contain("a rejection is the expected, passing outcome");
    }

    [Fact]
    public void Readiness_lists_kinds_with_aliases_only_and_flags_another_environment()
    {
        Services.AddSingleton<ITestDataApiService>(new Api(Asset("payload-ok", TestDataKind.Structured, null), Asset("prod-only", TestDataKind.Message, "Production")));
        var cut = Render<TestDataReadinessSummary>(p => p.Add(x => x.Environment, "Test"));

        var rows = cut.FindAll("[data-testid=td-readiness-row]");
        rows.Select(r => (r.GetAttribute("data-kind"), r.GetAttribute("data-state"))).Should().BeEquivalentTo([("Structured", "Ready"), ("Message", "EnvironmentMismatch")]);
        cut.Markup.Should().Contain("payload-ok").And.Contain("never reused in Test").And.NotContain("SECRET-VALUE");
    }

    [Fact]
    public void Readiness_respects_the_consumer_kinds()
    {
        Services.AddSingleton<ITestDataApiService>(new Api(Asset("payload", TestDataKind.Structured, null), Asset("file", TestDataKind.File, null)));
        var cut = Render<TestDataReadinessSummary>(p => p.Add(x => x.Kinds, [TestDataKind.File]));
        cut.FindAll("[data-testid=td-readiness-row]").Should().ContainSingle().Which.GetAttribute("data-kind").Should().Be("File");
    }
}
