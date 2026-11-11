using System.IO.Compression;
using System.Text;
using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Integrations;
using BirkNext.SourceBaselines;
using BirkNext.SourceEvidence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Tests.Services;

/// <summary>
/// The shared source context: snapshots of one logical source (repository identity) are versions; one is analysed (the person's analysis baseline,
/// else the newest import, flagged NotSelected when contents differ); alternatives are never combined; a vanished baseline is never substituted;
/// decisions are append-only and identical re-selection records nothing. Expectations are written from the rules, not read back from the code.
/// Synthetic fixtures (multi-branch task A–Z where they apply).
/// </summary>
public sealed class SourceBaselineServiceTests : IDisposable
{
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    public void Dispose() => _db.Dispose();

    private static readonly DateTimeOffset T0 = new(2026, 11, 11, 10, 0, 0, TimeSpan.Zero);

    private static IqrSourceSnapshot Snap(string archive, string content, int minutes, string repoKey = "m2lb", string repoName = "M2LB", string basis = "Archive file name",
        string? branch = null, string commit = "Unknown", params string[] projects) => new()
    {
        Id = Guid.NewGuid(), IntegrationId = IqrSourceStore.SourceAnalysisOwner, Archive = new SourceArchive(archive, Convert.ToHexString(Encoding.UTF8.GetBytes(archive)).ToLowerInvariant().PadRight(64, '0')[..64], 10, content),
        AnalyzedAt = T0.AddMinutes(minutes), Status = SourceAnalysisStatus.Ready, Repository = new SourceRepositoryIdentity(repoKey, repoName, basis), Branch = branch, Commit = commit,
        Projects = [.. projects.Select(p => new SourceProject(p, $"{p}/src/{p}.csproj", false, "Service", SourceConfidence.Confirmed))],
    };

    private async Task<IqrSourceSnapshot> Store(IqrSourceSnapshot s)
    {
        _db.IqrSourceSnapshots.Add(new IqrSourceSnapshotRecord { Id = s.Id, EnvironmentId = "", IntegrationId = IqrSourceStore.SourceAnalysisOwner, AnalyzedAt = s.AnalyzedAt,
            EvidenceJson = JsonSerializer.Serialize(s, new JsonSerializerOptions(JsonSerializerDefaults.Web)) });
        await _db.SaveChangesAsync();
        return s;
    }

    private SourceBaselineService Service() => new(_db, new IqrSourceStore(_db));

    [Fact] // B, J: five alternative snapshots; the analysis uses exactly one per logical source, never a union
    public void Resolve_returns_one_snapshot_per_logical_source_baseline_first_else_newest()
    {
        var v3 = Snap("M2LB (3).zip", "c3", 1); var v4 = Snap("M2LB (4).zip", "c4", 2); var v7 = Snap("M2LB (7).zip", "c7", 5);
        var common = Snap("M2LB.Common.zip", "cc", 0, repoKey: "m2lb.common", repoName: "M2LB.Common");
        SourceBaselineService.Resolve([v3, v4, v7, common], new Dictionary<string, Guid>()).Select(s => s.Id).Should().BeEquivalentTo([v7.Id, common.Id], "newest per source without a baseline");
        SourceBaselineService.Resolve([v3, v4, v7, common], new Dictionary<string, Guid> { ["m2lb"] = v3.Id }).Select(s => s.Id).Should().BeEquivalentTo([v3.Id, common.Id],
            "the selected baseline wins over newer imports; the supporting source keeps its own version");
    }

    [Fact] // W/Z: the selected baseline is gone → that source is left out, never substituted
    public void A_selected_baseline_that_is_unavailable_is_never_substituted()
    {
        var v4 = Snap("M2LB (4).zip", "c4", 2); var v7 = Snap("M2LB (7).zip", "c7", 5);
        SourceBaselineService.Resolve([v4, v7], new Dictionary<string, Guid> { ["m2lb"] = Guid.NewGuid() }).Should().BeEmpty();
    }

    [Fact] // E, I, A: identical content, single snapshot and unresolved alternatives are distinguished; branch is honest
    public async Task The_context_states_are_derived_from_content_identity_and_decisions()
    {
        await Store(Snap("Solo.zip", "s1", 0, repoKey: "solo", repoName: "Solo"));
        await Store(Snap("Same (1).zip", "same", 1, repoKey: "same", repoName: "Same")); await Store(Snap("Same (2).zip", "same", 2, repoKey: "same", repoName: "Same"));
        var a = await Store(Snap("M2LB (5).zip", "c5", 3, projects: ["Meldingsutvekslermottak", "Skole"]));
        var b = await Store(Snap("M2LB (7).zip", "c7", 4, projects: ["MeldingsutvekslerReceiver", "SkoleService"]));

        var context = await Service().ContextAsync(default);
        context.LogicalSources.Should().Be(3);
        context.Snapshots.Should().Be(5);
        context.Sources.Single(s => s.RepositoryKey == "solo").State.Should().Be(SourceBaselineState.SingleSnapshot);
        var same = context.Sources.Single(s => s.RepositoryKey == "same");
        same.State.Should().Be(SourceBaselineState.IdenticalContent);
        same.Snapshots.Should().OnlyContain(v => v.SameContentAs == 1);
        var m2lb = context.Sources.Single(s => s.RepositoryKey == "m2lb");
        m2lb.State.Should().Be(SourceBaselineState.NotSelected);
        m2lb.DistinctContents.Should().Be(2);
        m2lb.UsedSnapshotId.Should().Be(b.Id, "without a baseline the newest import is used, and that is said");
        m2lb.StateDetail.Should().Contain("no analysis baseline");
        m2lb.Snapshots.Should().OnlyContain(v => v.BranchKind == SourceBranchKind.Unknown && v.Branch == null && v.Commit == null, "no Git metadata: branch unknown, nothing invented");
        m2lb.Snapshots.Single(v => v.SnapshotId == a.Id).Components.Should().Equal("Meldingsutvekslermottak", "Skole");
        context.NeedsBaseline.Should().Be(1);
    }

    [Fact] // J, history: decisions are append-only; identical re-selection records nothing; wrong source refused
    public async Task Selecting_a_baseline_is_recorded_once_and_wrong_sources_are_refused()
    {
        var a = await Store(Snap("M2LB (5).zip", "c5", 3)); var b = await Store(Snap("M2LB (7).zip", "c7", 4));
        var other = await Store(Snap("Other.zip", "o", 1, repoKey: "other", repoName: "Other"));
        var service = Service();
        var before = (await service.ContextAsync(default)).ContextFingerprint;

        (await service.SelectBaselineAsync("m2lb", a.Id, "local-session:loopback", "Reviewing the Norwegian generation", default)).Changed.Should().BeTrue();
        (await service.SelectBaselineAsync("m2lb", a.Id, "local-session:loopback", null, default)).Changed.Should().BeFalse("the same choice again is not a new revision");
        (await service.SelectBaselineAsync("m2lb", other.Id, "local-session:loopback", null, default)).Message.Should().Contain("belongs to Other");
        _db.SourceDecisions.Count().Should().Be(1);

        var context = await service.ContextAsync(default);
        var m2lb = context.Sources.Single(s => s.RepositoryKey == "m2lb");
        m2lb.State.Should().Be(SourceBaselineState.Selected);
        m2lb.UsedSnapshotId.Should().Be(a.Id, "the older version is analysed because a person chose it");
        context.ContextFingerprint.Should().NotBe(before, "the context fingerprint changes when a used snapshot changes");

        await service.SelectBaselineAsync("m2lb", b.Id, "local-session:loopback", null, default);
        await service.SelectBaselineAsync("m2lb", null, "local-session:loopback", null, default);
        var history = await service.HistoryAsync("m2lb", default);
        history.Select(h => h.SnapshotId).Should().Equal(null, b.Id, a.Id);
        history.Should().OnlyContain(h => h.DecidedBy == "local-session:loopback");
        history.Last().Reason.Should().Be("Reviewing the Norwegian generation");
        (await service.ContextAsync(default)).Sources.Single(s => s.RepositoryKey == "m2lb").State.Should().Be(SourceBaselineState.NotSelected, "a cleared baseline is not a choice");
    }

    [Fact] // H/I: declared branch labels are statements, verified branches come only from Git
    public async Task Declared_branches_are_labelled_as_declared_and_Git_branches_as_verified()
    {
        var zip = await Store(Snap("M2LB (5).zip", "c5", 3));
        var git = await Store(Snap("m2lb-git", "cg", 4, branch: "feature/period-lock", commit: "0123456789abcdef0123456789abcdef01234567"));
        var service = Service();
        (await service.DeclareBranchAsync(zip.Id, "feature/norsk; DROP", "local-session:loopback", default)).Changed.Should().BeFalse("unsafe characters are refused");
        (await service.DeclareBranchAsync(zip.Id, "feature/norsk-ruter", "local-session:loopback", default)).Changed.Should().BeTrue();
        (await service.DeclareBranchAsync(zip.Id, "feature/norsk-ruter", "local-session:loopback", default)).Changed.Should().BeFalse();
        var views = (await service.ContextAsync(default)).Sources.Single().Snapshots;
        views.Single(v => v.SnapshotId == zip.Id).Should().Match<SourceSnapshotView>(v => v.BranchKind == SourceBranchKind.Declared && v.Branch == "feature/norsk-ruter" && v.Commit == null);
        views.Single(v => v.SnapshotId == git.Id).Should().Match<SourceSnapshotView>(v => v.BranchKind == SourceBranchKind.Verified && v.Branch == "feature/period-lock" && v.Commit!.StartsWith("01234567"));
    }

    [Fact] // P: a baseline older than the loaded window is still used exactly (fetched), not replaced by the newest
    public async Task ResolveAsync_fetches_a_baseline_outside_the_loaded_window()
    {
        var old = await Store(Snap("M2LB (3).zip", "c3", 0)); var newest = await Store(Snap("M2LB (7).zip", "c7", 9));
        var service = Service();
        await service.SelectBaselineAsync("m2lb", old.Id, "x", null, default);
        (await service.ResolveAsync([newest], default)).Select(s => s.Id).Should().Equal(old.Id);
    }
}

/// <summary>Canonical content fingerprint: content identity independent of archive name, entry order, compression and root folder; any byte change counts.</summary>
public sealed class SourceContentFingerprintTests
{
    private static byte[] Zip(IEnumerable<(string Path, string Text)> entries, CompressionLevel level = CompressionLevel.Optimal)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (path, text) in entries)
            {
                var e = zip.CreateEntry(path, level);
                using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
                w.Write(text);
            }
        return ms.ToArray();
    }

    private static readonly (string, string)[] Files = [("src/App/App.csproj", "<Project />"), ("src/App/Program.cs", "class P {}\n"), ("Skjema/a.xsd", "<xs:schema/>")];

    [Fact] // D, E, F and root folder rename: identical content → identical fingerprint; archive hashes differ
    public void Same_files_give_the_same_fingerprint_whatever_the_archive()
    {
        var a = Zip(Files);
        var reordered = Zip(Files.Reverse());
        var stored = Zip(Files, CompressionLevel.NoCompression);
        var rooted = Zip(Files.Select(f => ("M2LB-main/" + f.Item1, f.Item2)));
        var fp = IqrSourceArchiveReader.ContentFingerprint(a);
        IqrSourceArchiveReader.ContentFingerprint(reordered).Should().Be(fp);
        IqrSourceArchiveReader.ContentFingerprint(stored).Should().Be(fp);
        IqrSourceArchiveReader.ContentFingerprint(rooted).Should().Be(fp, "a single common root folder is not content");
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stored)).Should().NotBe(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(a)), "binary identity still differs");
    }

    [Fact] // G and line endings: any byte change is a different version
    public void A_changed_file_or_line_ending_changes_the_fingerprint()
    {
        var fp = IqrSourceArchiveReader.ContentFingerprint(Zip(Files));
        IqrSourceArchiveReader.ContentFingerprint(Zip(Files.Select(f => f.Item1.EndsWith(".xsd") ? (f.Item1, "<xs:schema version=\"2.0.1\"/>") : f))).Should().NotBe(fp);
        IqrSourceArchiveReader.ContentFingerprint(Zip(Files.Select(f => (f.Item1, f.Item2.Replace("\n", "\r\n"))))).Should().NotBe(fp, "line endings are not normalised");
        IqrSourceArchiveReader.ContentFingerprint(Zip(Files.Append(("README.md", "x")))).Should().NotBe(fp);
    }

    [Fact]
    public void The_reader_records_both_identities()
    {
        var (workspace, error) = IqrSourceArchiveReader.Read("M2LB (3).zip", Zip(Files));
        error.Should().BeNull();
        workspace!.Archive.ContentSha256.Should().Be(IqrSourceArchiveReader.ContentFingerprint(Zip(Files)));
        workspace.Archive.Sha256.Should().NotBe(workspace.Archive.ContentSha256);
    }
}
