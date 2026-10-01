using System.Text;
using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.SecurityClassification;
using BirkNext.Dependencies;
using BirkNext.Integrations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Tests.Services.SecurityClassification;

/// <summary>
/// Security Classification consumes Source Analysis snapshots: the archive is ingested once by Source Analysis (which captures the classification
/// observations of that snapshot), a review binds one primary plus explicitly included related snapshots by exact id and fingerprint, each
/// fact keeps its own snapshot, nothing is merged, and a newer snapshot never silently replaces the reviewed one. Generic repositories only.
/// </summary>
public sealed class SecurityClassificationSourceScopeTests
{
    private static AppDbContext Db() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static byte[] Zip(params (string Path, string Content)[] files)
    {
        using var stream = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, content) in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open(), Encoding.UTF8);
                writer.Write(content);
            }
        return stream.ToArray();
    }

    private static string Csproj(string packages = "", string extra = "") =>
        $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework>{extra}</PropertyGroup><ItemGroup>{packages}</ItemGroup></Project>";

    // ── Generic multi-repository fixture: AppRepo (CDC mapping + authorization consumer) and Shared.Security (classification contract + guard) ──

    private const string SecretSentinel = "SECRET_SENTINEL_6f1";

    /// <summary>The application: builds the shared CDC event (with a constant classification), maps the payload level, exposes access paths.</summary>
    public static byte[] AppRepo(bool referenceShared = true, bool externalProjectReference = false, bool constant = true) => Zip(
        ("AppRepo/AppRepo.sln", "Microsoft Visual Studio Solution File"),
        ("AppRepo/src/App.Adapter/App.Adapter.csproj", Csproj((referenceShared ? "<PackageReference Include=\"Shared.Security\" Version=\"1.4.0\" />" : "") + "<PackageReference Include=\"Shared.Common\" Version=\"2.0.0\" />")
            + (externalProjectReference ? "<!-- --><ItemGroup><ProjectReference Include=\"..\\..\\..\\Shared.Security\\src\\Shared.Security\\Shared.Security.csproj\" /></ItemGroup>" : "")),
        ("AppRepo/src/App.Adapter/appsettings.json", $"{{\"ConnectionStrings\":{{\"Default\":\"Server=db;Password={SecretSentinel}\"}}}}"),
        ("AppRepo/src/App.Adapter/CdcProcessorWorker.cs", $$"""
            public sealed class CdcProcessorWorker
            {
                private static CdcEvent? Deserialize(BinaryData body)
                {
                    using var doc = JsonDocument.Parse(body);
                    var envelope = doc.RootElement.GetProperty("payload");
                    var op = envelope.GetProperty("op").GetString() ?? string.Empty;
                    var recordField = op == "d" ? "before" : "after";
                    if (!envelope.TryGetProperty(recordField, out var record)) return null;
                    return new CdcEvent(
                        Operasjon: op,
                        Tabellnavn: "Barn",
                        Sikkerhetsnivaa: {{(constant ? "0, // not present in the envelope" : "record.GetProperty(\"Sikkerhetsnivå\").GetInt32(),")}}
                        Payload: record.Clone());
                }
            }
            """),
        ("AppRepo/src/App.Adapter/CdcRouter.cs", """
            public sealed class CdcRouter
            {
                public Task<RoutingResult> RouteAsync(CdcEvent cdcEvent, CancellationToken ct)
                {
                    if (guard.Evaluate(cdcEvent) == GuardResult.Rejected) return Task.FromResult(new RoutingResult(RoutingOutcome.Rejected));
                    var record = childMapper.Map(cdcEvent);
                    return Task.FromResult(new RoutingResult(RoutingOutcome.Delivered));
                }
            }
            """),
        ("AppRepo/src/App.Adapter/ChildMapper.cs", """
            public sealed class ChildMapper
            {
                public ChildRecord? Map(CdcEvent cdcEvent) => new ChildRecord(GetInt(cdcEvent.Payload, "Sikkerhetsnivå"));
            }
            """),
        ("AppRepo/src/App.Person/App.Person.csproj", Csproj()),
        ("AppRepo/src/App.Person/ProfileService.cs", """
            public class ProfileService
            {
                public async Task<Profil> HentBarnProfilAsync(Guid barnRegistreringId, Guid brukerId, CancellationToken ct = default)
                {
                    var barn = await _repository.Hent(barnRegistreringId, ct);
                    if (barn is null) throw new PersonNotFoundException(barnRegistreringId);
                    if (barn.KreverGradertTilgang && !await _authz.EvaluerOperasjon(brukerId, "Person:SeGradertBarn", barnRegistreringId)) throw new PersonNotFoundException(barnRegistreringId);
                    await _leseloggPublisher.PublishAsync(new LeseloggHendelse(), ct);
                    return Map(barn);
                }
            }
            """),
        ("AppRepo/src/App.Person/Resolvers.cs", """
            [ExtendObjectType("Query")]
            public class ProfileQuery
            {
                public async Task<ProfilGql?> HentBarnAsync(Guid barnRegistreringId, [Service] IAuth auth, [Service] ProfileService service, CancellationToken ct)
                {
                    await auth.KrevOperasjon("Person:SeBarnProfil", barnRegistreringId);
                    try { return Map(await service.HentBarnProfilAsync(barnRegistreringId, auth.Bruker(), ct)); }
                    catch (PersonNotFoundException) { return null; }
                }
            }
            """));

    /// <summary>The shared package: the classification contract (levels), the CDC event type and the graded-access guard.</summary>
    public static byte[] SharedSecurity() => Zip(
        ("Shared.Security.sln", "Microsoft Visual Studio Solution File"),
        ("Directory.Build.props", "<Project><PropertyGroup><PackageId>$(MSBuildProjectName)</PackageId><Version>1.4.0</Version></PropertyGroup></Project>"),
        ("src/Shared.Security/Shared.Security.csproj", Csproj()),
        ("src/Shared.Security/SikkerhetsnivaaType.cs", """
            /// <summary>Seeded classification levels — NEVER modify seeded Nivaa values.</summary>
            public class SikkerhetsnivaaType
            {
                public int Nivaa { get; set; }
                public string Verdi { get; set; } = string.Empty;
                public bool KreverGradertTilgang { get; set; }
            }
            public static class Levels
            {
                public static readonly SikkerhetsnivaaType[] All =
                [
                    new SikkerhetsnivaaType { Nivaa = 0, Verdi = "Ingen", KreverGradertTilgang = false },
                    new SikkerhetsnivaaType { Nivaa = 1, Verdi = "SkjultAdresse", KreverGradertTilgang = false },
                    new SikkerhetsnivaaType { Nivaa = 2, Verdi = "Kode7", BiRKKode = "Kode 7", KreverGradertTilgang = true },
                    new SikkerhetsnivaaType { Nivaa = 3, Verdi = "Kode6", BiRKKode = "Kode 6", KreverGradertTilgang = true },
                ];
            }
            """),
        ("src/Shared.Security/CdcEvent.cs", "public sealed record CdcEvent(string Operasjon, string Tabellnavn, int Sikkerhetsnivaa, JsonElement Payload);"),
        ("src/Shared.Security/SecurityClassificationGuard.cs", """
            public sealed class SecurityClassificationGuard
            {
                public GuardResult Evaluate(CdcEvent cdcEvent)
                {
                    if (cdcEvent.Sikkerhetsnivaa is not (2 or 3)) return GuardResult.Allowed;
                    return GuardResult.Rejected;
                }
            }
            """));

    /// <summary>A shared package the application references that has nothing to do with classification or authorization.</summary>
    public static byte[] SharedCommon() => Zip(
        ("Shared.Common.sln", "Microsoft Visual Studio Solution File"),
        ("Directory.Build.props", "<Project><PropertyGroup><PackageId>$(MSBuildProjectName)</PackageId><Version>2.0.0</Version></PropertyGroup></Project>"),
        ("src/Shared.Common/Shared.Common.csproj", Csproj()),
        ("src/Shared.Common/Text.cs", "public static class Text { public static string Trim(string s) => s.Trim(); }"));

    private static async Task<IqrSourceSnapshot> Upload(AppDbContext db, string name, byte[] bytes)
    {
        var (snapshot, error) = await new IqrSourceStore(db).AnalyzeAsync("dev", "source-analysis", name, bytes);
        error.Should().BeNull();
        return snapshot!;
    }

    private sealed class CountingProbe : IClassificationLiveProbe
    {
        public int Calls { get; private set; }
        public Task<ClassificationLiveEvidence> ProbeAsync(ClassificationTestContext context, ClassificationRunRequest request, IReadOnlyList<ClassificationLevel> levels, CancellationToken ct = default)
        { Calls++; return Task.FromResult(new ClassificationLiveEvidence { State = IntegrationEvidenceState.NotConfigured, Reason = "No approved DEV/QA test context." }); }
    }

    private static (ClassificationReviewService Service, CountingProbe Probe) Service(AppDbContext db)
    {
        var probe = new CountingProbe();
        return (new ClassificationReviewService(db, probe, new ClassificationTestContextStore(), NullLogger<ClassificationReviewService>.Instance), probe);
    }

    private static ClassificationSourceScopeRequest Scope(IqrSourceSnapshot primary, params IqrSourceSnapshot[] related) =>
        new() { PrimarySnapshotId = primary.Id, RelatedSnapshotIds = related.Select(r => r.Id).ToList() };

    // ── Source Analysis captures the observations; nothing here is a verdict ───────────────────────────────────────

    [Fact]
    public async Task SourceAnalysisCapturesClassificationObservationsWithoutVerdictsOrSecrets()
    {
        await using var db = Db();
        var app = await Upload(db, "AppRepo.zip", AppRepo());

        var stored = (await new IqrSourceStore(db).ListAsync("dev")).Single();
        stored.SecurityClassificationEvidence.Should().NotBeNull("captured with the snapshot, so Security Classification never needs the archive again");
        stored.SecurityClassificationEvidence!.Facts.Select(f => f.State).Should().NotContain(new[] { ClassificationState.Pass, ClassificationState.Fail, ClassificationState.Verified },
            "Source Analysis supplies evidence; it never declares secure, protected or passed");
        stored.SecurityClassificationEvidence.Cdc.DeserializerType.Should().Be("CdcProcessorWorker");
        stored.SecurityClassificationEvidence.ReferencedTypes.Should().Equal(new[] { "CdcEvent" }, "the event type is constructed here but declared in another source");
        db.IqrSourceSnapshots.Single().EvidenceJson.Should().NotContain(SecretSentinel);
        db.SecurityClassificationEvidence.Should().BeEmpty("Security Classification keeps no copy of the source");
        app.Archive.Sha256.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task SingleSnapshotReviewIsTheSameAnalysisWithSnapshotProvenance()
    {
        await using var db = Db();
        var bytes = Zip(("Repo/Repo.sln", ""), ("Repo/src/App/App.csproj", Csproj()), ("Repo/src/App/Model.cs", SharedSecurityModel), ("Repo/src/App/CdcEvent.cs", "public sealed record CdcEvent(string Operasjon, int Sikkerhetsnivaa, JsonElement Payload);"));
        var snapshot = await Upload(db, "Repo.zip", bytes);
        var (_, files, _) = BirkNext.Api.Services.Integrations.Scim.ScimSourceReader.Read("Repo.zip", bytes, _ => false);
        var direct = ClassificationSourceAnalyzer.Analyze("dev", [], files, DateTimeOffset.UtcNow);

        var (combined, error) = await new ClassificationSourceScopeService(new IqrSourceStore(db)).ResolveAsync("dev", Scope(snapshot));

        error.Should().BeNull();
        combined!.Facts.Select(f => (f.Id, f.State, f.Detail)).Should().Equal(direct.Facts.Select(f => (f.Id, f.State, f.Detail)), "the input refactor loses no evidence");
        combined.Levels.Should().BeEquivalentTo(direct.Levels);
        combined.Facts.Should().OnlyContain(f => f.Sources.Count == 1 && f.Sources[0].SnapshotId == snapshot.Id && f.Sources[0].Fingerprint == snapshot.Archive.Sha256);
        combined.Scope!.Primary.Should().Match<SourceScopeEntry>(e => e.SnapshotId == snapshot.Id && e.Fingerprint == snapshot.Archive.Sha256 && e.AnalyzedAt == snapshot.AnalyzedAt);
    }

    private const string SharedSecurityModel = """
        public class SikkerhetsnivaaType { public int Nivaa { get; set; } public bool KreverGradertTilgang { get; set; } }
        static class Seed { static readonly object[] All = [new SikkerhetsnivaaType { Nivaa = 0, Verdi = "Ingen", KreverGradertTilgang = false }, new SikkerhetsnivaaType { Nivaa = 2, Verdi = "Kode7", KreverGradertTilgang = true }]; }
        """;

    // ── Scope validation, exact binding, no silent rebind ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ScopeValidationNeverSubstitutes()
    {
        await using var db = Db();
        var app = await Upload(db, "AppRepo.zip", AppRepo());
        var appAgain = await Upload(db, "AppRepo.zip", AppRepo(constant: false));
        var all = await new IqrSourceStore(db).ListAsync("dev");

        ClassificationSourceScopeService.Validate(new ClassificationSourceScopeRequest(), all).Error.Should().Be("Choose a primary source snapshot.");
        ClassificationSourceScopeService.Validate(new() { PrimarySnapshotId = Guid.NewGuid() }, all).Error.Should().Contain("unavailable in Source Analysis").And.Contain("nothing is substituted");
        ClassificationSourceScopeService.Validate(new() { PrimarySnapshotId = app.Id, RelatedSnapshotIds = [app.Id] }, all).Error.Should().Contain("more than once");
        ClassificationSourceScopeService.Validate(Scope(app, appAgain), all).Error.Should().Contain("one snapshot per repository");
    }

    [Fact]
    public async Task LegacySnapshotWithoutClassificationEvidenceIsNotUsedAndSaysWhy()
    {
        await using var db = Db();
        var legacy = new IqrSourceSnapshot { Id = Guid.NewGuid(), IntegrationId = "source-analysis", Archive = new SourceArchive("Old.zip", new string('b', 64), 10), AnalyzedAt = DateTimeOffset.UtcNow.AddDays(-3) };
        db.IqrSourceSnapshots.Add(new IqrSourceSnapshotRecord { Id = legacy.Id, EnvironmentId = "dev", IntegrationId = "source-analysis", AnalyzedAt = legacy.AnalyzedAt,
            EvidenceJson = JsonSerializer.Serialize(legacy, new JsonSerializerOptions(JsonSerializerDefaults.Web)) });
        await db.SaveChangesAsync();
        var (service, _) = Service(db);

        var options = await service.SourceScopeAsync("dev", Scope(legacy));

        options.Snapshots.Single().HasClassificationEvidence.Should().BeFalse();
        options.Snapshots.Single().EvidenceNote.Should().Be(ClassificationSourceScopeService.NoEvidence);
        options.Error.Should().Contain("Analyze the archive again in Source Analysis");
        var run = () => service.RunAsync("dev", new ClassificationRunRequest { EnvironmentType = "Development", SourceScope = Scope(legacy) });
        await run.Should().ThrowAsync<InvalidSourceSelectionException>();
        db.SecurityClassificationEvidence.Should().BeEmpty("a rejected scope stores no run");
    }

    [Fact]
    public async Task RunsBindTheExactSnapshotAndANewerSnapshotIsOnlyOffered()
    {
        await using var db = Db();
        var (service, _) = Service(db);
        var a = await Upload(db, "AppRepo.zip", AppRepo());
        var shared = await Upload(db, "Shared.Security.zip", SharedSecurity());
        var first = await service.RunAsync("dev", new ClassificationRunRequest { EnvironmentType = "Development", SourceScope = Scope(a, shared) });
        var b = await Upload(db, "AppRepo.zip", AppRepo(constant: false));

        var options = await service.SourceScopeAsync("dev", Scope(a, shared));
        var again = await service.RunAsync("dev", new ClassificationRunRequest { EnvironmentType = "Development", SourceScope = Scope(a, shared) });

        options.Newer.Should().ContainSingle(n => n.SnapshotId == b.Id, "the newer snapshot of the same repository is shown");
        options.Scope!.Primary.SnapshotId.Should().Be(a.Id, "and never switched to");
        again.SourceScope!.Primary.SnapshotId.Should().Be(a.Id);
        again.Findings.Should().Contain(f => f.RuleId == "cdc-classification-constant", "snapshot A still has the constant guard input although B does not");
        (await service.GetRunAsync(first.RunId))!.SourceScope!.Primary.Should().Match<SourceScopeEntry>(e => e.SnapshotId == a.Id && e.Fingerprint == a.Archive.Sha256 && e.AnalyzedAt == a.AnalyzedAt);
        (await service.ReviewAsync("dev", "Development"))!.Findings.Should().Contain(f => f.RuleId == "cdc-classification-constant", "IQR reuses the latest run's exact scope, not the newest snapshot");
    }

    // ── Related sources: suggested from evidence, never included silently, never merged ────────────────────────────

    [Fact]
    public async Task SharedSecuritySourceIsSuggestedFromEvidenceAndAnUnrelatedSharedPackageIsNot()
    {
        await using var db = Db();
        var app = await Upload(db, "AppRepo.zip", AppRepo());
        var shared = await Upload(db, "Shared.Security.zip", SharedSecurity());
        await Upload(db, "Shared.Common.zip", SharedCommon());
        var (service, probe) = Service(db);
        var rowsBefore = db.IqrSourceSnapshots.Count();

        var options = await service.SourceScopeAsync("dev", Scope(app));

        var candidate = options.Candidates.Should().ContainSingle().Subject;
        candidate.Repository.Should().Be("Shared.Security");
        candidate.State.Should().Be(RelatedSourceState.SnapshotAvailable);
        candidate.Confidence.Should().Be("Suggested");
        candidate.MatchingSnapshotIds.Should().Equal(shared.Id);
        candidate.Evidence.Select(e => e.Kind).Should().BeEquivalentTo(["Type reference", "PackageReference"]);
        candidate.Evidence.Single(e => e.Kind == "Type reference").Detail.Should().Contain("CdcEvent");
        options.Candidates.Should().NotContain(c => c.Repository == "Shared.Common", "a referenced package without classification or authorization evidence is not suggested");
        options.Scope!.Related.Should().BeEmpty("a detected source is never included automatically");
        options.Scope.Limitations.Should().ContainSingle(l => l.Contains("Related source detected but not included") && l.Contains("Shared.Security"));
        options.Coverage.Single(c => c.Id == "model").State.Should().Be(ClassificationCoverageState.NotFound, "the model lives in the source that is not included");
        probe.Calls.Should().Be(0, "choosing snapshots never calls the runtime");
        db.IqrSourceSnapshots.Count().Should().Be(rowsBefore, "no synthetic merged snapshot is created");
        db.SecurityClassificationEvidence.Should().BeEmpty("reading the scope stores nothing");
    }

    [Fact]
    public async Task IncludedRelatedSourceSpansTheEvidenceChainWithPerSnapshotProvenance()
    {
        await using var db = Db();
        var app = await Upload(db, "AppRepo.zip", AppRepo());
        var shared = await Upload(db, "Shared.Security.zip", SharedSecurity());
        var (service, _) = Service(db);

        var run = await service.RunAsync("dev", new ClassificationRunRequest { EnvironmentType = "Development", SourceScope = Scope(app, shared) });

        run.SourceScope!.Primary.SnapshotId.Should().Be(app.Id);
        run.SourceScope.Related.Should().ContainSingle(r => r.SnapshotId == shared.Id && r.Fingerprint == shared.Archive.Sha256 && r.Repository == "Shared.Security");
        run.SourceScope.Primary.Fingerprint.Should().NotBe(run.SourceScope.Related[0].Fingerprint, "two sources, two fingerprints — never one merged snapshot");
        run.SourceScope.Limitations.Should().BeEmpty();
        run.Levels.Select(l => l.Nivaa).Should().Equal(0, 1, 2, 3);
        string SourceOf(string id) => run.Checks.Single(c => c.CheckId == id).Sources.Single().Repository;
        SourceOf("model-reference").Should().Be("Shared.Security");
        SourceOf("guard-logic").Should().Be("Shared.Security");
        SourceOf("cdc-deserializer").Should().Be("AppRepo");
        SourceOf("access-profile-antidisclosure").Should().Be("AppRepo");
        run.Checks.Single(c => c.CheckId == "cdc-mapper-guard-consistency").Sources.Select(s => s.Repository).Should().Equal("AppRepo");
        var finding = run.Findings.Single(f => f.RuleId == "cdc-classification-constant");
        finding.Sources.Should().ContainSingle(s => s.Repository == "AppRepo" && s.SnapshotId == app.Id, "the source finding is tied to the snapshot it was found in");
        run.Pipeline.Single(p => p.Stage == ClassificationPipelineStage.Guard).Source.Should().Be(ClassificationState.SourceVerified, "the guard from the shared source closes the chain");
        run.Pipeline.Single(p => p.Stage == ClassificationPipelineStage.GuardInput).Source.Should().Be(ClassificationState.IssueDetected);
        run.Pipeline.Should().OnlyContain(p => p.Runtime == ClassificationState.NotTested, "a source path is not runtime continuity");
        run.Live.Observations.Should().BeEmpty();
        run.Overall.Should().NotBe(ClassificationOverall.Verified, "a source rule never implies runtime protection");
        run.Checks.Single(c => c.CheckId == "metric-runtime").State.Should().Be(ClassificationState.NotAvailable);
        run.Limitations.Should().NotContain(l => l.Contains("not declared in the selected source scope"));
    }

    [Fact]
    public async Task ContinuingWithoutARelatedSourceRecordsTheLimitation()
    {
        await using var db = Db();
        var app = await Upload(db, "AppRepo.zip", AppRepo());
        await Upload(db, "Shared.Security.zip", SharedSecurity());
        var (service, _) = Service(db);

        var run = await service.RunAsync("dev", new ClassificationRunRequest { EnvironmentType = "Development", SourceScope = Scope(app) with { ExcludedSuggestions = ["Shared.Security"] } });

        run.SourceScope!.ExcludedSuggestions.Should().Equal("Shared.Security");
        run.Limitations.Should().Contain(l => l.StartsWith("Related source detected but not included in this review scope: Shared.Security"));
        run.Limitations.Should().Contain(l => l.Contains("CdcEvent") && l.Contains("not declared in the selected source scope"));
        run.Readiness.Single(r => r.Id == "source-model").Status.Should().Be(ClassificationReadiness.Missing);
        run.Overall.Should().NotBe(ClassificationOverall.IssueDetected, "no source snapshot or missing model is not a security failure");
    }

    [Fact]
    public async Task ReferencedSourceWithoutASnapshotPointsToSourceAnalysis()
    {
        await using var db = Db();
        var app = await Upload(db, "AppRepo.zip", AppRepo(referenceShared: false, externalProjectReference: true));
        var (service, _) = Service(db);

        var options = await service.SourceScopeAsync("dev", Scope(app));

        var candidate = options.Candidates.Should().ContainSingle().Subject;
        candidate.State.Should().Be(RelatedSourceState.SnapshotUnavailable);
        candidate.MatchingSnapshotIds.Should().BeEmpty("no snapshot is fabricated");
        candidate.Evidence.Single().Detail.Should().Contain("Shared.Security.csproj").And.Contain("CdcEvent");
        options.Scope!.Limitations.Should().ContainSingle(l => l.Contains("no analyzed snapshot"));
    }

    // ── Runs without source, legacy input, Partial snapshots ───────────────────────────────────────────────────────

    [Fact]
    public async Task NoSourceSnapshotRunsRuntimeOnlyAndNeverReadsLegacyUploads()
    {
        await using var db = Db();
        var legacy = ClassificationSourceAnalyzer.Analyze("dev", [new SourceArchive("M2LB.zip", new string('c', 64), 1)], new(
            [new("R/src/A/A.csproj", Csproj()), new("R/src/A/Model.cs", SharedSecurityModel)], [], []), DateTimeOffset.UtcNow.AddDays(-2));
        db.SecurityClassificationEvidence.Add(new SecurityClassificationEvidenceRecord { Id = Guid.NewGuid(), EnvironmentId = "dev", Kind = "source", CreatedAt = legacy.AnalyzedAt,
            Json = JsonSerializer.Serialize(legacy, new JsonSerializerOptions(JsonSerializerDefaults.Web)) });
        await db.SaveChangesAsync();
        var (service, probe) = Service(db);

        var overview = await service.OverviewAsync("dev");
        var run = await service.RunAsync("dev", new ClassificationRunRequest { EnvironmentType = "Development" });

        overview.Source.Should().BeNull("an uploaded archive is never the current source for new runs");
        run.SourceScope.Should().BeNull();
        run.SourceArchives.Should().BeEmpty("no legacy archive is attached to a new run");
        run.Checks.Should().NotContain(c => c.Provenance == IntegrationEvidenceSource.SourceCode, "source checks are not run without a snapshot");
        run.Readiness.Single(r => r.Id == "source").Status.Should().Be(ClassificationReadiness.Missing);
        run.MissingItems.Should().NotContain(i => i.Title.Contains("archive", StringComparison.OrdinalIgnoreCase));
        run.Overall.Should().Be(ClassificationOverall.NotTestable, "no source snapshot is not a security failure");
        probe.Calls.Should().Be(1, "runtime checks stay independently runnable");
        db.SecurityClassificationEvidence.Count(r => r.Kind == "source").Should().Be(1, "historical evidence is never deleted");
    }

    [Fact]
    public async Task PartialSourceAnalysisDoesNotBlockTheSecurityReview()
    {
        await using var db = Db();
        var app = await Upload(db, "AppRepo.zip", AppRepo());
        var shared = await Upload(db, "Shared.Security.zip", SharedSecurity());
        var (service, _) = Service(db);

        var options = await service.SourceScopeAsync("dev", Scope(app, shared));

        options.Snapshots.Select(s => s.SourceStatus).Should().OnlyContain(s => s.Length > 0, "the Source Analysis status is shown, whatever it is");
        options.Error.Should().BeNull("the extraction status of Architecture or Database does not decide this review");
        options.Coverage.Single(c => c.Id == "cdc").State.Should().Be(ClassificationCoverageState.Detected);
        options.Coverage.Single(c => c.Id == "guard").State.Should().Be(ClassificationCoverageState.Detected);
        options.Coverage.Single(c => c.Id == "runtime").State.Should().Be(ClassificationCoverageState.NotAssessed, "source coverage never covers runtime authorization");
    }

    // ── Current source shapes: declarative policies, positive guard sets, shared authorization packages (generic names) ──

    /// <summary>An application whose resolvers use declarative policies from a shared authorization package it imports.</summary>
    private static byte[] PolicyApp() => Zip(
        ("PolicyApp/PolicyApp.sln", ""),
        ("PolicyApp/src/App.Person/App.Person.csproj", Csproj()),
        ("PolicyApp/src/App.Person/Model.cs", SharedSecurityModel),
        ("PolicyApp/src/App.Person/Resolvers.cs", """
            using Shared.Auth.Policies;
            [ExtendObjectType("Query")]
            public class ChildQuery
            {
                [Authorize(Policy = "Person:SeBarnProfil")]
                public async Task<Child?> HentBarnAsync(Guid barnRegistreringId, [Service] IChildRepository repository, CancellationToken ct) => await repository.Hent(barnRegistreringId, ct);
                [Authorize]
                public async Task<Guid?> HentIdAsync(int eksternId, [Service] IChildRepository repository, CancellationToken ct) => await repository.FinnId(eksternId, ct);
            }
            """),
        ("PolicyApp/src/App.Person/ProfileService.cs", """
            public class ProfileService
            {
                public async Task<Profil> HentBarnProfilAsync(Guid barnRegistreringId, Guid brukerId, CancellationToken ct = default)
                {
                    var barn = await _repository.Hent(barnRegistreringId, ct);
                    if (barn is null) throw new PersonNotFoundException(barnRegistreringId);
                    if (barn.KreverGradertTilgang && !await _client.EvaluateAsync(brukerId, "Person:SeGradertBarn", barnRegistreringId, ct: ct)) throw new PersonNotFoundException(barnRegistreringId);
                    await _leseloggPublisher.PublishAsync(new LeseloggHendelse(), ct);
                    return Map(barn);
                }
            }
            """),
        ("PolicyApp/src/App.Adapter/App.Adapter.csproj", Csproj()),
        ("PolicyApp/src/App.Adapter/CdcEvent.cs", "public sealed record CdcEvent(string Tabellnavn, int? Sikkerhetsnivaa, JsonElement Payload);"),
        ("PolicyApp/src/App.Adapter/Guard.cs", """
            public sealed class ClassificationGuard
            {
                public GuardResult Evaluate(CdcEvent cdcEvent)
                {
                    var graded = cdcEvent.Sikkerhetsnivaa is null or 2 or 3;
                    return graded ? GuardResult.Rejected : GuardResult.Allowed;
                }
            }
            """));

    /// <summary>The shared authorization package: policy handler, real client, and an allow-all client for local development.</summary>
    private static byte[] SharedAuth() => Zip(
        ("Shared.Auth.sln", ""),
        ("src/Shared.Auth/Shared.Auth.csproj", Csproj()),
        ("src/Shared.Auth/Policies/PolicyHandler.cs", """
            namespace Shared.Auth.Policies;
            public sealed class PolicyHandler(IAuthClient client) : AuthorizationHandler<OperationRequirement>
            {
                protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, OperationRequirement requirement)
                {
                    bool allowed;
                    try { allowed = await client.EvaluateAsync(Guid.Empty, requirement.Operation); }
                    catch (AuthUnavailableException ex) { logger.LogError(ex, "Unavailable for {Operation}; denied", requirement.Operation); Fail(context); return; }
                    if (allowed) context.Succeed(requirement); else Fail(context);
                }
            }
            """),
        ("src/Shared.Auth/Clients.cs", """
            namespace Shared.Auth;
            internal sealed class RemoteAuthClient : IAuthClient
            {
                public async Task<bool> EvaluateAsync(Guid user, string operation, Guid? child = null)
                {
                    try { var answer = await Post(user, operation, child); return answer.Allowed; }
                    catch (Exception ex) { logger.LogError(ex, "Call failed for {Operation}", operation); throw new AuthUnavailableException("unavailable", ex); }
                }
            }
            public sealed class AllowAllAuthClient : IAuthClient
            {
                public Task<bool> EvaluateAsync(Guid user, string operation, Guid? child = null) => Task.FromResult(true);
            }
            public static class AuthExtensions
            {
                public static IServiceCollection AddAuthDev(this IServiceCollection services) { services.AddScoped<IAuthClient, AllowAllAuthClient>(); return services; }
            }
            """));

    [Fact]
    public async Task DeclarativePoliciesAndPositiveGuardSetsAreRecognisedButAuthenticationIsNotAuthorization()
    {
        await using var db = Db();
        var app = await Upload(db, "PolicyApp.zip", PolicyApp());
        var (evidence, _) = await new ClassificationSourceScopeService(new IqrSourceStore(db)).ResolveAsync("dev", Scope(app));

        ClassificationFact F(string id) => evidence!.Facts.Single(f => f.Id == id);
        F("graphql-hentBarn").State.Should().Be(ClassificationState.SourceVerified);
        F("graphql-hentBarn").Detail.Should().Contain("Person:SeBarnProfil").And.Contain("[Authorize] policy").And.Contain("not in this snapshot");
        F("graphql-hentId").State.Should().Be(ClassificationState.IssueDetected, "a plain [Authorize] is authentication only");
        F("graphql-hentId").Detail.Should().Contain("authenticated is not authorized");
        F("guard-logic").State.Should().Be(ClassificationState.SourceVerified);
        F("guard-logic").Detail.Should().Contain("2 or 3, or missing (fail-closed)");
        F("access-child-specific").State.Should().Be(ClassificationState.SourceVerified, "EvaluateAsync with the child's id is a per-child check");
        app.SecurityClassificationEvidence!.ReferencedNamespaces.Should().ContainSingle(n => n.Name == "Shared.Auth.Policies" && n.Files == 1);
    }

    [Fact]
    public async Task SharedAuthorizationPackageIsSuggestedByExactNamespaceAndItsFactsKeepTheirSource()
    {
        await using var db = Db();
        var app = await Upload(db, "PolicyApp.zip", PolicyApp());
        var auth = await Upload(db, "Shared.Auth.zip", SharedAuth());
        await Upload(db, "Shared.Common.zip", SharedCommon());
        var scopes = new ClassificationSourceScopeService(new IqrSourceStore(db));

        var options = await scopes.OptionsAsync("dev", Scope(app));
        var (evidence, _) = await scopes.ResolveAsync("dev", Scope(app, auth));

        var candidate = options.Candidates.Should().ContainSingle().Subject;
        candidate.Repository.Should().Be("Shared.Auth");
        candidate.Evidence.Single().Should().Match<RelatedSourceEvidence>(e => e.Kind == "Namespace reference" && e.Detail.Contains("Shared.Auth.Policies"));
        candidate.MatchingSnapshotIds.Should().Equal(auth.Id);
        options.Scope!.Related.Should().BeEmpty();
        var handler = evidence!.Facts.Single(f => f.Id == "access-policy-handler");
        handler.State.Should().Be(ClassificationState.SourceVerified);
        handler.Sources.Single().Repository.Should().Be("Shared.Auth");
        var client = evidence.Facts.Single(f => f.Id == "access-authz-fail-closed");
        client.Sources.Single().Repository.Should().Be("Shared.Auth");
        client.State.Should().Be(ClassificationState.SourceVerified, "a failure throws and nothing defaults to allowed — braces in a log template do not hide that");
        client.Detail.Should().Contain("RemoteAuthClient", "the allow-all client is reported on its own, never as the real client");
        var allowAll = evidence.Facts.Single(f => f.Id == "access-authz-allow-all-AllowAllAuthClient");
        allowAll.State.Should().Be(ClassificationState.Warning);
        allowAll.Detail.Should().Contain("AuthExtensions.AddAuthDev").And.Contain("not assessed from source");
        evidence.Facts.Single(f => f.Id == "graphql-hentBarn").Sources.Single().Repository.Should().Be("PolicyApp", "the resolver fact stays with its own snapshot");
    }
}
