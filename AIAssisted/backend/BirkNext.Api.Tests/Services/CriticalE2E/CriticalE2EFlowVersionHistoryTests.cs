using System.Text.Json;
using BirkNext.Api.Services.CriticalE2E;
using BirkNext.CriticalE2E;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BirkNext.Api.Tests.Services.CriticalE2E;

/// <summary>
/// Flow definition history: every new version is recorded once with its exact definition and fingerprint; an identical save records nothing;
/// flows from before history existed get their current version backfilled (marked so); deleting a flow keeps its history for the runs that
/// executed it; Local Data Reset clears it. A run carries the fingerprint of the definition it executed, and a run whose fingerprint differs
/// from the current definition is stale even when the version number matches (for example re-keyed by an import).
/// </summary>
public sealed class CriticalE2EFlowVersionHistoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "birknext-e2e-versions-" + Guid.NewGuid().ToString("N")[..8]);
    private CriticalE2EStore Store() => new(_dir, NullLogger<CriticalE2EStore>.Instance);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static CriticalE2EFlowDefinition Flow(string name) => new()
    {
        Name = name, EnvironmentId = "dev", Module = "Barn", Steps = [new() { StepId = "open", Description = "Open the start page" }],
    };

    [Fact]
    public void Each_new_version_is_recorded_once_with_its_exact_definition_and_identical_saves_add_nothing()
    {
        var store = Store();
        var v1 = store.Save(Flow("Register child"));
        store.Save(v1).Version.Should().Be(1);
        var v2 = store.Save(v1 with { Name = "Register a child" });
        v2.Version.Should().Be(2);

        var versions = store.FlowVersions(v1.Id);
        versions.Select(v => v.Version).Should().Equal(2, 1);
        versions.Single(v => v.Version == 1).Definition.Name.Should().Be("Register child", "version 1 keeps the definition it had");
        versions.Should().OnlyContain(v => !v.Backfilled && v.Fingerprint.Length == 64);
        versions[0].Fingerprint.Should().Be(CriticalE2EStore.Fingerprint(v2)).And.NotBe(versions[1].Fingerprint);

        // Persisted: a new store reads the same history; deleting the flow keeps it for the runs that executed it.
        var reopened = Store();
        reopened.FlowVersions(v1.Id).Select(v => v.Version).Should().Equal(2, 1);
        reopened.Delete(v1.Id).Should().BeTrue();
        reopened.FlowVersions(v1.Id).Should().HaveCount(2);
        reopened.ClearAll();
        reopened.FlowVersions(v1.Id).Should().BeEmpty("Local Data Reset clears the history with the flows");
        Store().FlowVersions(v1.Id).Should().BeEmpty();
    }

    [Fact]
    public void A_flow_from_before_history_existed_gets_its_current_version_backfilled_once()
    {
        var saved = Store().Save(Flow("Old flow"));
        File.Delete(Path.Combine(_dir, "flow-versions.json"));
        var store = Store();
        store.FlowVersions(saved.Id).Should().ContainSingle().Which.Should().Match<CriticalE2EFlowVersionRecord>(v => v.Backfilled && v.Version == saved.Version);
        Store().FlowVersions(saved.Id).Should().ContainSingle("backfilling happens once");
        JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "flow-versions.json"))).RootElement.GetArrayLength().Should().Be(1);
    }

    [Fact]
    public void A_run_of_different_content_under_the_same_version_number_is_stale()
    {
        var flow = Store().Save(Flow("Register child"));
        CriticalE2ERunResult Run(string? fingerprint) => new()
        {
            RunId = Guid.NewGuid().ToString("N"), FlowId = flow.Id, FlowName = flow.Name, EnvironmentId = "dev", StartedAt = DateTimeOffset.UtcNow,
            Status = CriticalE2EStatus.Passed, FlowVersion = flow.Version, FlowFingerprint = fingerprint,
        };
        CriticalE2ECoverage.Summarize(flow, [Run(CriticalE2EStore.Fingerprint(flow))], null).LastRunStale.Should().BeFalse();
        CriticalE2ECoverage.Summarize(flow, [Run(null)], null).LastRunStale.Should().BeFalse("runs from before fingerprints are judged by version only");
        CriticalE2ECoverage.Summarize(flow, [Run(new string('0', 64))], null).LastRunStale.Should().BeTrue();
    }
}
