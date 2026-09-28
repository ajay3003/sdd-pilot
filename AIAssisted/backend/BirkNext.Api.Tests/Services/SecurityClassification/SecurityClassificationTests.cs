using System.Net;
using System.Text;
using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Services.Integrations.ApplicationMessaging;
using BirkNext.Api.Services.Integrations.Scim;
using BirkNext.Api.Services.SecurityClassification;
using BirkNext.Integrations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Tests.Services.SecurityClassification;

/// <summary>
/// Security Classification / Gradert tilgang review. The model comes from source; the production CDC path is followed as written (a guard
/// that exists is not a guard that receives the level); unit tests with constructed events are not production-path coverage; live checks are
/// fixed queries for configured synthetic test children only; counts are timestamped consistency evidence, never authorization.
/// </summary>
public sealed class SecurityClassificationTests
{
    // ── Synthetic fixture (adapter + Person module), no real identities ──────────────────────────────────────────────

    private sealed record Variant
    {
        public bool ConstantGuardInput { get; init; } = true;
        public bool UnicodeField { get; init; } = true;
        public bool ProfileQueryExcludesGraded { get; init; } = true;
        public bool UnguardedResolver { get; init; } = true;
        public bool ReadLogCaught { get; init; }
        public bool CountBeforeFilter { get; init; }
        public bool RawDeserializerTest { get; init; }
    }

    private static List<SourceFile> Fixture(Variant? v = null)
    {
        v ??= new Variant();
        var field = v.UnicodeField ? "Sikkerhetsnivå" : "Sikkerhetsnivaa";
        var files = new List<SourceFile>
        {
            new("M2LB/PersonAdapter/src/Adapter/M2LB.PersonAdapter.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>"),
            new("M2LB/Person/src/Person/M2LB.Person.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>"),
            new("M2LB/Person/src/Person/SikkerhetsnivaaType.cs", """
                /// <summary>Fixed seed rows — NEVER modify seeded Nivaa values.</summary>
                public class SikkerhetsnivaaType
                {
                    public Guid SikkerhetsnivaaTypeId { get; set; }
                    public int Nivaa { get; set; }
                    public string Verdi { get; set; } = string.Empty;
                    public bool KreverGradertTilgang { get; set; }
                }
                """),
            new("M2LB/Person/src/Person/PersonDbContext.cs", """
                public class PersonDbContext : DbContext
                {
                    protected override void OnModelCreating(ModelBuilder mb)
                    {
                        mb.Entity<SikkerhetsnivaaType>(e => { e.HasIndex(s => s.Nivaa).IsUnique(); });
                        mb.Entity<SikkerhetsnivaaType>().HasData(
                            new SikkerhetsnivaaType { Nivaa = 0, Verdi = "Ingen", Beskrivelse = "Ingen", KreverGradertTilgang = false },
                            new SikkerhetsnivaaType { Nivaa = 1, Verdi = "SkjultAdresse", Beskrivelse = "Skjult adresse", KreverGradertTilgang = false },
                            new SikkerhetsnivaaType { Nivaa = 2, Verdi = "Kode7", BiRKKode = "Kode 7", ElementsKode = "K1", Beskrivelse = "Kode 7", KreverGradertTilgang = true },
                            new SikkerhetsnivaaType { Nivaa = 3, Verdi = "Kode6", BiRKKode = "Kode 6", ElementsKode = "K2", Beskrivelse = "Kode 6", KreverGradertTilgang = true });
                    }
                }
                """),
            new("M2LB/PersonAdapter/src/Adapter/CdcEvent.cs", "public sealed record CdcEvent(string Operasjon, string Tabellnavn, int Sikkerhetsnivaa, JsonElement Payload);"),
            new("M2LB/PersonAdapter/src/Adapter/CdcProcessorWorker.cs", $$"""
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
                            Sikkerhetsnivaa: {{(v.ConstantGuardInput ? "0, // not present in Debezium envelope" : "record.TryGetProperty(\"Sikkerhetsnivå\", out var level) ? level.GetInt32() : -1,")}}
                            Payload: record.Clone());
                    }
                }
                """),
            new("M2LB/PersonAdapter/src/Adapter/SecurityClassificationGuard.cs", """
                public sealed class SecurityClassificationGuard
                {
                    private static readonly Counter<long> _kode67Counter = _meter.CreateCounter<long>("birk.kode67.rejections");
                    public GuardResult Evaluate(CdcEvent cdcEvent)
                    {
                        if (cdcEvent.Sikkerhetsnivaa is not (2 or 3)) return GuardResult.Allowed;
                        _logger.LogCritical("Kode 6/7 record rejected — tabellnavn: {Tabellnavn}", cdcEvent.Tabellnavn);
                        _alerts.RaiseKode67Alert(cdcEvent.Tabellnavn, DateTimeOffset.UtcNow);
                        _kode67Counter.Add(1);
                        return GuardResult.Rejected;
                    }
                }
                """),
            new("M2LB/PersonAdapter/src/Adapter/CdcRouter.cs", """
                public sealed class CdcRouter
                {
                    public Task<RoutingResult> RouteAsync(CdcEvent cdcEvent, CancellationToken ct)
                    {
                        if (guard.Evaluate(cdcEvent) == GuardResult.Rejected)
                            return Task.FromResult(new RoutingResult(RoutingOutcome.Rejected));
                        if (cdcEvent.OperationType == OperationType.Delete)
                            return Task.FromResult(new RoutingResult(RoutingOutcome.Discarded));
                        var record = childMapper.Map(cdcEvent);
                        return Task.FromResult(new RoutingResult(RoutingOutcome.Delivered));
                    }
                }
                """),
            new("M2LB/PersonAdapter/src/Adapter/ChildRegistrationMapper.cs", $$"""
                public sealed partial class ChildRegistrationMapper
                {
                    public ChildRegistrationRecord? Map(CdcEvent cdcEvent)
                    {
                        var sikkerhetsnivaa = GetInt(cdcEvent.Payload, "{{field}}");
                        logger.LogInformation("Barn mapping — birkId={BirkId} Sikkerhetsnivå={Sikkerhetsnivaa}", birkId, sikkerhetsnivaa);
                        return new ChildRegistrationRecord(MapSikkerhetsnivaa(sikkerhetsnivaa));
                    }
                    private static Guid MapSikkerhetsnivaa(int? nivaa) => nivaa switch
                    {
                        0 => new Guid("d1000000-0000-0000-0000-000000000000"), // Ingen
                        2 => new Guid("d1000000-0000-0000-0000-000000000002"), // Kode7
                        _ => new Guid("d1000000-0000-0000-0000-000000000000"), // default Ingen
                    };
                }
                """),
            new("M2LB/Person/src/Person/BarnProfileService.cs", $$"""
                public class BarnProfileService
                {
                    public async Task<BarnProfil> HentBarnProfilAsync(Guid barnRegistreringId, Guid brukerId, CancellationToken ct = default)
                    {
                        var barn = await _repository.HentBarnProfilAsync(barnRegistreringId, ct);
                        if (barn is null) throw new PersonNotFoundException(barnRegistreringId);
                        if (barn.SikkerhetsnivaaType?.KreverGradertTilgang == true)
                        {
                            var harGradertTilgang = await _autorisasjonClient.EvaluerOperasjon(brukerId, "Person:SeGradertBarn", barnRegistreringId);
                            if (!harGradertTilgang) throw new PersonNotFoundException(barnRegistreringId);
                        }
                        {{(v.ReadLogCaught ? "try { await _leseloggPublisher.PublishAsync(new LeseloggHendelse(), ct); } catch (Exception) { }" : "await _leseloggPublisher.PublishAsync(new LeseloggHendelse(), ct);")}}
                        _logger.LogInformation("BarnProfil hentet: barnRegistreringId={BarnRegistreringId} nivaa={Nivaa}", barnRegistreringId, 0);
                        return MapTilProfil(barn);
                    }
                }
                """),
            new("M2LB/Person/src/Person/PersonRepository.cs", $$"""
                public class PersonRepository
                {
                    public async Task<(IReadOnlyList<BarnSoekResultat> Resultater, int TotaltAntall)> SoekBarnAsync(BarnSoekKriterier kriterier, IReadOnlyList<Guid> grantedChildIds, CancellationToken ct = default)
                    {
                        var query = _db.Persons.Join(_db.SikkerhetsnivaaTyper, pb => pb.b.SikkerhetsnivaaTypeId, s => s.SikkerhetsnivaaTypeId, (pb, s) => new { pb.p, pb.b, s });
                        {{(v.CountBeforeFilter ? "var totaltAntall = await query.CountAsync(ct);\n        query = query.Where(x => !x.s.KreverGradertTilgang || grantedChildIds.Contains(x.b.BarnRegistreringId));" : "query = query.Where(x => !x.s.KreverGradertTilgang || grantedChildIds.Contains(x.b.BarnRegistreringId));\n        var totaltAntall = await query.CountAsync(ct);")}}
                        return (await query.Skip(0).Take(20).ToListAsync(ct), totaltAntall);
                    }
                    public async Task<Barn?> HentBarnProfilAsync(Guid barnRegistreringId, CancellationToken ct = default)
                        => await _db.BarnRegistreringer.Include(b => b.SikkerhetsnivaaType)
                            {{(v.ProfileQueryExcludesGraded ? ".Where(b => b.SikkerhetsnivaaType != null && !b.SikkerhetsnivaaType.KreverGradertTilgang)" : "")}}
                            .FirstOrDefaultAsync(b => b.BarnRegistreringId == barnRegistreringId, ct);
                    public async Task<Guid?> HentBarnRegistreringIdFraEksternIdAsync(int eksternId, CancellationToken ct = default)
                        => await _db.BarnRegistreringer.Where(b => b.EksternId == eksternId).Select(b => (Guid?)b.BarnRegistreringId).FirstOrDefaultAsync(ct);
                }
                """),
            new("M2LB/Person/src/Person/AutorisasjonClient.cs", """
                public class AutorisasjonClient : IAutorisasjonClient
                {
                    public async Task<bool> EvaluerOperasjon(Guid brukerId, string operasjonId, Guid? barnRegistreringId = null)
                    {
                        try { var response = await Post(); return response?.Tillatt ?? false; }
                        catch (Exception ex) { throw new AutorisasjonException("utilgjengelig", ex); }
                    }
                    public async Task<IReadOnlyList<Guid>> HentGradertBarntilganger(Guid brukerId)
                    {
                        var response = await Get();
                        return response?.Barn.Select(b => b.BarnId).ToList() ?? [];
                    }
                }
                """),
            new("M2LB/Person/src/Person/Resolvers.cs", $$"""
                [ExtendObjectType("Query")]
                public class BarnProfilQueryResolver
                {
                    public async Task<BarnProfilGql?> HentBarnAsync(Guid barnRegistreringId, [Service] IGraphQLAuthMiddleware auth, [Service] BarnProfileService profileService, CancellationToken ct)
                    {
                        await auth.KrevOperasjon("Person:SeBarnProfil", barnRegistreringId);
                        try { return Map(await profileService.HentBarnProfilAsync(barnRegistreringId, auth.HentBrukerId(), ct)); }
                        catch (PersonNotFoundException) { return null; }
                    }
                }
                {{(v.UnguardedResolver ? """
                [ExtendObjectType("Query")]
                public class BirkIdQueryResolver
                {
                    public async Task<Guid?> HentBarnRegistreringIdFraEksternIdAsync(int eksternId, [Service] IPersonRepository repository, CancellationToken ct)
                    {
                        // no auth call here
                        return await repository.HentBarnRegistreringIdFraEksternIdAsync(eksternId, ct);
                    }
                }
                """ : "")}}
                """),
            new("M2LB/Person/src/Person/Program.cs", "var app = builder.Build();\napp.MapGraphQL();\napp.Run();"),
            new("M2LB/Person/src/Person/GradertBarntilgangService.cs", """
                public class GradertBarntilgangService
                {
                    // public async Task<TildelGradertBarntilgangResultat> TildelGradertBarntilgangAsync(Guid a, Guid b)
                    // {
                    // }
                }
                """),
            new("M2LB/PersonAdapter/tests/Adapter.Tests/Adapter.Tests.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" /></ItemGroup></Project>"),
            new("M2LB/PersonAdapter/tests/Adapter.Tests/SecurityClassificationGuardTests.cs", """
                public class SecurityClassificationGuardTests
                {
                    private static CdcEvent MakeEvent(int level) => new("c", "Barn", level, JsonDocument.Parse("{}").RootElement);
                    [Theory] public void Evaluate_LevelTwoOrThree_ReturnsRejected(int level) { }
                    [Fact] public async Task Kode6Event_IsRejected() { var result = await router.RouteAsync(MakeEvent(level: 2), CancellationToken.None); }
                }
                """),
        };
        if (v.RawDeserializerTest)
            files.Add(new("M2LB/PersonAdapter/tests/Adapter.Tests/RawDebeziumTests.cs", """
                public class RawDebeziumTests
                {
                    [Fact] public void RawDebeziumLevel2_ReachesSecurityClassificationGuard() { var e = Deserialize(BinaryData.FromString("{\"Sikkerhetsnivå\":2}")); }
                }
                """));
        return files;
    }

    private static readonly List<SourceFile> Docs = [new("M2LB/PersonAdapter/specs/001/spec.md", "**Given** a CDC record with security level 2 (Kode 6), **When** …\nSome text about nødtilgang.")];

    private static ClassificationSourceEvidence Analyze(Variant? v = null, List<SourceFile>? code = null) =>
        ClassificationSourceAnalyzer.Analyze("dev", [new SourceArchive("fixture.zip", new string('a', 64), 1)], new ScimSourceSet(code ?? Fixture(v), Docs, []), DateTimeOffset.UtcNow);

    private static ClassificationFact Fact(ClassificationSourceEvidence e, string id) => e.Facts.Single(f => f.Id == id);

    private static ClassificationReviewResult Evaluate(ClassificationSourceEvidence? source, ClassificationLiveEvidence? live = null, ClassificationCountEvidence? s = null, ClassificationCountEvidence? t = null) =>
        ClassificationEvaluator.Evaluate("dev", source, Context(), live ?? new ClassificationLiveEvidence { State = IntegrationEvidenceState.NotConfigured, Reason = "No context." }, s, t, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    // ── 65 Model ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ClassificationModelComesFromSource()
    {
        var evidence = Analyze();

        evidence.Detected.Should().BeTrue();
        evidence.Levels.Select(l => (l.Nivaa, l.Verdi, l.BiRKKode, l.ElementsKode, l.KreverGradertTilgang)).Should().Equal(
            (0, "Ingen", null, null, false), (1, "SkjultAdresse", null, null, false), (2, "Kode7", "Kode 7", "K1", true), (3, "Kode6", "Kode 6", "K2", true));
        Fact(evidence, "model-graded-levels").Detail.Should().Contain("level(s) 2, 3").And.Contain("no separate, higher permission");
        Fact(evidence, "model-unique").State.Should().Be(ClassificationState.SourceVerified);
        Fact(evidence, "model-immutable").State.Should().Be(ClassificationState.SourceVerified);
        Fact(evidence, "model-terminology").Detail.Should().Contain("level 2 (Kode 6)").And.Contain("Kode6Event");
    }

    [Fact]
    public void WithoutReferenceDataNothingIsAssumed()
    {
        var evidence = Analyze(code: Fixture().Where(f => !f.Path.EndsWith("PersonDbContext.cs") && !f.Path.EndsWith("SikkerhetsnivaaType.cs")).ToList());

        evidence.Detected.Should().BeFalse();
        evidence.Levels.Should().BeEmpty();
    }

    // ── 66–67 / 58 Raw Debezium production path ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void RawDebeziumLevel2_ConstantGuardInputIsAHighFinding()
    {
        var evidence = Analyze();
        var result = Evaluate(evidence);

        var deserializer = Fact(evidence, "cdc-deserializer");
        deserializer.State.Should().Be(ClassificationState.IssueDetected);
        deserializer.Detail.Should().Contain("Sikkerhetsnivaa: 0").And.Contain("not present in Debezium envelope").And.Contain("populated with a constant/default value");
        deserializer.Locations.Single().File.Should().EndWith("CdcProcessorWorker.cs");
        evidence.Pipeline.Single(s => s.Stage == ClassificationPipelineStage.GuardInput).Source.Should().Be(ClassificationState.IssueDetected);
        evidence.Pipeline.Single(s => s.Stage == ClassificationPipelineStage.Guard).Source.Should().Be(ClassificationState.SourceVerified);
        result.Findings.Should().Contain(f => f.RuleId == "cdc-classification-constant" && f.Severity == ClassificationSeverity.High);
        result.Overall.Should().Be(ClassificationOverall.IssueDetected);
    }

    [Fact]
    public void RawDebeziumLevel3_FixedDeserializerIsSourceVerified()
    {
        var evidence = Analyze(new Variant { ConstantGuardInput = false });

        Fact(evidence, "cdc-deserializer").State.Should().Be(ClassificationState.SourceVerified);
        Fact(evidence, "cdc-mapper-guard-consistency").State.Should().Be(ClassificationState.SourceVerified);
        Evaluate(evidence).Findings.Should().NotContain(f => f.RuleId == "cdc-classification-constant");
    }

    [Fact]
    public void ProposedRegressionTestsTargetTheProductionDeserializer()
    {
        var proposed = Analyze().ProposedTests;

        proposed.Select(p => p.Name).Should().Contain(["RawDebeziumLevel2_ReachesSecurityClassificationGuard", "RawDebeziumLevel3_ReachesSecurityClassificationGuard", "RawDebeziumDeleteLevel3_ReadsClassificationFromBefore"]);
        var level2 = proposed.Single(p => p.Name == "RawDebeziumLevel2_ReachesSecurityClassificationGuard").Code;
        level2.Should().Contain("typeof(CdcProcessorWorker).GetMethod(\"Deserialize\"").And.Contain("Sikkerhetsnivå\\\":2").And.Contain("Assert.Equal(2, cdcEvent.Sikkerhetsnivaa)").And.Contain("GuardResult.Rejected");
        level2.Should().NotContain("new CdcEvent(");
        proposed.Single(p => p.Name.StartsWith("RawDebeziumDelete")).Code.Should().Contain("\"op\\\":\\\"d").And.Contain("\"before\\\":\" + record");
    }

    // ── 68 Create/update/delete ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EnvelopeUsesBeforeForDeletesAndDeletesAreDiscardedAfterTheGuard()
    {
        var evidence = Analyze();

        Fact(evidence, "cdc-envelope").Detail.Should().Contain("\"before\" for deletes");
        Fact(evidence, "cdc-router").Detail.Should().Contain("before any mapper").And.Contain("Delete events are discarded after the guard");
    }

    // ── 69 Unit tests are not production-path coverage ──────────────────────────────────────────────────────────────

    [Fact]
    public void DirectCdcEventUnitTestDoesNotCountAsProductionCoverage()
    {
        var coverage = Analyze().TestCoverage;

        coverage.Single(c => c.Scenario.StartsWith("SecurityClassificationGuard")).State.Should().Be(RepositoryTestCoverageState.UnitOnly);
        coverage.Single(c => c.Scenario.StartsWith("SecurityClassificationGuard")).Note.Should().Contain("not that the production deserializer delivers the level");
        coverage.Single(c => c.Scenario.StartsWith("Router")).State.Should().Be(RepositoryTestCoverageState.UnitOnly);
        coverage.Single(c => c.Scenario.StartsWith("Raw Debezium")).State.Should().Be(RepositoryTestCoverageState.Missing);
        Analyze(new Variant { RawDeserializerTest = true }).TestCoverage.Single(c => c.Scenario.StartsWith("Raw Debezium")).State.Should().Be(RepositoryTestCoverageState.Present);
    }

    // ── 10 / 59 / 60 Field name, mapper consistency, unknown levels ─────────────────────────────────────────────────

    [Fact]
    public void UnicodeFieldNameIsReportedAndATransliterationIsFlagged()
    {
        Fact(Analyze(), "cdc-field-name").Detail.Should().Contain("\"Sikkerhetsnivå\"").And.Contain("U+00E5");
        Fact(Analyze(new Variant { UnicodeField = false }), "cdc-field-name").State.Should().Be(ClassificationState.Warning);
    }

    [Fact]
    public void MapperAndGuardInconsistencyIsDetected()
    {
        var fact = Fact(Analyze(), "cdc-mapper-guard-consistency");

        fact.State.Should().Be(ClassificationState.IssueDetected);
        fact.Detail.Should().Contain("the two values diverge");
    }

    [Fact]
    public void UnknownLevelNeedsADecisionAndIsNotPassed()
    {
        var fact = Fact(Analyze(), "cdc-unknown-level");

        fact.State.Should().Be(ClassificationState.NeedsDecision);
        fact.Detail.Should().Contain("\"Ingen\"").And.Contain("does not assume fail-closed");
        Evaluate(Analyze()).Findings.Should().Contain(f => f.RuleId == "unknown-level-default");
    }

    // ── 70–72 Direct profile ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AntiDisclosureNotFoundIsTheExpectedResult()
    {
        var evidence = Analyze();

        Fact(evidence, "access-profile-antidisclosure").State.Should().Be(ClassificationState.SourceVerified);
        Fact(evidence, "access-profile-antidisclosure").Detail.Should().Contain("404-not-403");
        Fact(evidence, "access-child-specific").State.Should().Be(ClassificationState.SourceVerified);
    }

    [Fact]
    public void ProfileQueryExcludingGradedChildrenNeedsADecision()
    {
        var evidence = Analyze();

        Fact(evidence, "access-profile-query-excludes").State.Should().Be(ClassificationState.NeedsDecision);
        Fact(evidence, "access-profile-query-excludes").Detail.Should().Contain("authorized positive control cannot pass");
        Evaluate(evidence).Findings.Should().Contain(f => f.RuleId == "profile-graded-unreachable" && f.Severity == ClassificationSeverity.Medium);
        Analyze(new Variant { ProfileQueryExcludesGraded = false }).Facts.Should().NotContain(f => f.Id == "access-profile-query-excludes");
    }

    // ── 73–75 Search ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SearchFilterAndCountOrderAreDerived()
    {
        Fact(Analyze(), "search-filter").State.Should().Be(ClassificationState.SourceVerified);
        Fact(Analyze(), "search-count").State.Should().Be(ClassificationState.SourceVerified);
        Fact(Analyze(new Variant { CountBeforeFilter = true }), "search-count").State.Should().Be(ClassificationState.IssueDetected);
        Fact(Analyze(), "search-grant-source").State.Should().Be(ClassificationState.Warning);
    }

    // ── 76–77 GraphQL (source) ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ResolverWithoutAuthorizationIsAnExistenceOracle()
    {
        var evidence = Analyze();

        Fact(evidence, "graphql-hentBarnRegistreringIdFraEksternId").State.Should().Be(ClassificationState.IssueDetected);
        Fact(evidence, "graphql-hentBarn").Detail.Should().Contain("Person:SeBarnProfil").And.Contain("anti-disclosure");
        Fact(evidence, "graphql-endpoint-auth").State.Should().Be(ClassificationState.Warning);
        Evaluate(evidence).Findings.Should().Contain(f => f.RuleId == "graphql-unguarded" && f.Severity == ClassificationSeverity.High);
        Analyze(new Variant { UnguardedResolver = false }).Facts.Should().NotContain(f => f.State == ClassificationState.IssueDetected && f.Area == ClassificationArea.GraphQL);
    }

    // ── 78 Leselogg ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ReadLoggingFailClosedIsDerivedFromThePublishPath()
    {
        Fact(Analyze(), "readlog-fail-closed").State.Should().Be(ClassificationState.SourceVerified);
        Fact(Analyze(new Variant { ReadLogCaught = true }), "readlog-fail-closed").State.Should().Be(ClassificationState.IssueDetected);
    }

    // ── 36–37 Grants / emergency access ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CommentedOutGrantIsDocumentedOnlyAndEmergencyAccessIsNotAssumed()
    {
        var evidence = Analyze();

        Fact(evidence, "grant-implementation").State.Should().Be(ClassificationState.DocumentedOnly);
        Fact(evidence, "revoke-implementation").State.Should().Be(ClassificationState.NotFound);
        Fact(evidence, "emergency-access").State.Should().Be(ClassificationState.DocumentedOnly);
    }

    // ── 79–80 Level change ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LevelChangeIsNotTestedWithoutMutation()
    {
        var result = Evaluate(Analyze());

        result.Checks.Single(c => c.CheckId == "level-change").State.Should().Be(ClassificationState.NotTested);
        result.Checks.Single(c => c.CheckId == "browser-storage").State.Should().Be(ClassificationState.NotTested);
    }

    // ── 83 Log privacy ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LoggingThatTiesChildIdToLevelIsFlaggedAndNoPiiIsStored()
    {
        var evidence = Analyze();
        var json = JsonSerializer.Serialize(Evaluate(evidence)) + JsonSerializer.Serialize(evidence);

        Fact(evidence, "privacy-logging").State.Should().Be(ClassificationState.Warning);
        Fact(evidence, "privacy-logging").Detail.Should().Contain("tie a child identifier to its classification level");
        json.Should().NotContain("synthetic.person@example.test");
    }

    // ── 84–86 Counts ────────────────────────────────────────────────────────────────────────────────────────────────

    private static ClassificationCountEvidence Counts(string system, DateTimeOffset at, long l2, long l3) =>
        new() { System = system, CapturedAt = at, Provenance = "approved read-only count query", Counts = new() { [2] = l2, [3] = l3 } };

    [Fact]
    public void CountMatchIsDataConsistencyNotAuthorization()
    {
        var at = DateTimeOffset.UtcNow;
        var result = Evaluate(Analyze(new Variant { ConstantGuardInput = false, UnguardedResolver = false }), null, Counts("BiRK", at, 11, 57), Counts("M2LB", at.AddMinutes(5), 11, 57));

        result.CountComparisons.Should().OnlyContain(c => c.State == CountComparisonState.Match);
        result.CountComparisons.First().Detail.Should().Contain("says nothing about authorization");
        result.Summary.Single(r => r.Area == ClassificationArea.Search).State.Should().NotBe(ClassificationState.Pass);
        result.Summary.Single(r => r.Area == ClassificationArea.DirectAccess).State.Should().NotBe(ClassificationState.Pass);
    }

    [Fact]
    public void CountMismatchIsNotABreach()
    {
        var at = DateTimeOffset.UtcNow;
        var result = Evaluate(Analyze(), null, Counts("BiRK", at, 11, 57), Counts("M2LB", at, 10, 57));

        result.CountComparisons.Single(c => c.Nivaa == 2).State.Should().Be(CountComparisonState.Mismatch);
        result.CountComparisons.Single(c => c.Nivaa == 2).Detail.Should().Contain("not in itself a security breach");
        result.Findings.Should().NotContain(f => f.Title.Contains("count", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CountsCapturedAtDifferentTimesAreNotComparable()
    {
        var at = DateTimeOffset.UtcNow;
        ClassificationEvaluator.CompareCounts(Counts("BiRK", at, 11, 57), Counts("M2LB", at.AddDays(3), 10, 57), ClassificationReviewService.CountAlignment)
            .Should().OnlyContain(c => c.State == CountComparisonState.NotComparable);
        var windowed = Counts("BiRK", at, 11, 57) with { WindowStart = at.AddHours(-2), WindowEnd = at.AddHours(-1) };
        ClassificationEvaluator.CompareCounts(windowed, Counts("M2LB", at, 11, 57) with { WindowStart = at.AddMinutes(-5), WindowEnd = at }, TimeSpan.FromDays(1))
            .Should().OnlyContain(c => c.State == CountComparisonState.NotComparable);
        ClassificationEvaluator.CompareCounts(null, null, ClassificationReviewService.CountAlignment).Single().State.Should().Be(CountComparisonState.NotAvailable);
    }

    // ── 87 Metric ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MetricDefinedIsNotRuntimeZero()
    {
        var result = Evaluate(Analyze());

        Fact(Analyze(), "guard-metric").State.Should().Be(ClassificationState.Configured);
        result.Checks.Single(c => c.CheckId == "metric-runtime").State.Should().Be(ClassificationState.NotAvailable);
        result.Checks.Single(c => c.CheckId == "metric-runtime").Detail.Should().Contain("not 0 rejections");
    }

    // ── Live checks: fake Person GraphQL endpoint ───────────────────────────────────────────────────────────────────

    private static readonly Guid Graded = Guid.Parse("11111111-2222-3333-4444-000000000002");
    private static readonly Guid Ungraded = Guid.Parse("11111111-2222-3333-4444-000000000000");

    private static ClassificationTestContext Context() => new()
    {
        Environment = "DEV", GraphQlEndpoint = "https://person.dev.example.test/graphql", ApprovedByTestLead = true, UnauthorizedIdentityLabel = "Saksbehandler uten gradert tilgang", AuthorizedIdentityLabel = "Saksbehandler med gradert tilgang",
        TestChildren = [new() { Nivaa = 0, BarnRegistreringId = Ungraded, BirkId = "B999-0000" }, new() { Nivaa = 2, BarnRegistreringId = Graded, BirkId = "B999-0002" }],
    };

    private sealed class FakePerson(bool leakProfile = false, bool leakCount = false, bool distinguishable = false, bool authorizedBlocked = false) : HttpMessageHandler
    {
        public List<(string Query, JsonElement Variables, string? Auth)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var query = doc.RootElement.GetProperty("query").GetString()!;
            var variables = doc.RootElement.GetProperty("variables").Clone();
            var auth = request.Headers.Authorization?.Parameter;
            Requests.Add((query, variables, auth));
            var authorized = auth == "tok-auth" && !authorizedBlocked;
            string body;
            if (query.Contains("hentBarn("))
            {
                var id = variables.GetProperty("id").GetGuid();
                var visible = id == Ungraded || id == Graded && (authorized || leakProfile);
                body = visible ? $"{{\"data\":{{\"hentBarn\":{{\"barnRegistreringId\":\"{id}\",\"sikkerhetsnivaaKode\":{(id == Graded ? 2 : 0)}}}}}}}"
                    : id == Graded && distinguishable ? "{\"data\":{\"hentBarn\":null},\"errors\":[{\"message\":\"forbidden\",\"extensions\":{\"code\":\"AUTH_NOT_AUTHORIZED\"}}]}"
                    : "{\"data\":{\"hentBarn\":null}}";
            }
            else if (query.Contains("soekBarn("))
            {
                var birk = variables.GetProperty("birkId").GetString();
                var id = birk == "B999-0002" ? Graded : Ungraded;
                var visible = id == Ungraded || authorized;
                body = $"{{\"data\":{{\"soekBarn\":{{\"totaltAntall\":{(visible || leakCount ? 1 : 0)},\"resultater\":[{(visible ? $"{{\"barnRegistreringId\":\"{id}\"}}" : "")}]}}}}}}";
            }
            else
            {
                var id = variables.GetProperty("id").GetGuid();
                body = id == Graded && !authorized ? "{\"data\":{\"hentRevisjonslogg\":null}}" : "{\"data\":{\"hentRevisjonslogg\":{\"totaltAntall\":3}}}";
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static readonly List<ClassificationLevel> Levels = Analyze().Levels;

    private static async Task<(ClassificationLiveEvidence Live, FakePerson Server)> Live(FakePerson server, ClassificationRunRequest? request = null, ClassificationTestContext? context = null)
    {
        var probe = new GraphQlClassificationProbe(new HttpClient(server), NullLogger<GraphQlClassificationProbe>.Instance);
        var live = await probe.ProbeAsync(context ?? Context(), request ?? new ClassificationRunRequest { EnvironmentType = "Development", UnauthorizedToken = "tok-unauth", AuthorizedToken = "tok-auth" }, Levels);
        return (live, server);
    }

    private static ClassificationObservation Obs(ClassificationLiveEvidence live, int level, ClassificationIdentity identity, ClassificationSurface surface) =>
        live.Observations.Single(o => o.Nivaa == level && o.Identity == identity && o.Surface == surface);

    [Fact]
    public async Task UnauthorizedGradedChildIsNotDisclosedAndAuthorizedPositiveControlPasses()
    {
        var (live, _) = await Live(new FakePerson());

        Obs(live, 2, ClassificationIdentity.Unauthorized, ClassificationSurface.DirectProfile).State.Should().Be(ClassificationState.Pass);
        Obs(live, 2, ClassificationIdentity.Unauthorized, ClassificationSurface.DirectProfile).Detail.Should().Contain("Anti-disclosure behavior verified");
        Obs(live, 2, ClassificationIdentity.Unauthorized, ClassificationSurface.NonexistentComparison).State.Should().Be(ClassificationState.Pass);
        Obs(live, 2, ClassificationIdentity.Authorized, ClassificationSurface.DirectProfile).State.Should().Be(ClassificationState.Pass);
        Obs(live, 2, ClassificationIdentity.Unauthorized, ClassificationSurface.Search).State.Should().Be(ClassificationState.Pass);
        Obs(live, 2, ClassificationIdentity.Unauthorized, ClassificationSurface.SearchTotalCount).State.Should().Be(ClassificationState.Pass);
        Obs(live, 2, ClassificationIdentity.Authorized, ClassificationSurface.Search).State.Should().Be(ClassificationState.Pass);
        Obs(live, 2, ClassificationIdentity.Unauthorized, ClassificationSurface.AuditLog).State.Should().Be(ClassificationState.Pass);
        Obs(live, 0, ClassificationIdentity.Unauthorized, ClassificationSurface.DirectProfile).State.Should().Be(ClassificationState.Pass);
        Obs(live, 2, ClassificationIdentity.Unauthorized, ClassificationSurface.DirectProfile).TestType.Should().Be(ClassificationTestType.Negative);
        Obs(live, 2, ClassificationIdentity.Authorized, ClassificationSurface.DirectProfile).TestType.Should().Be(ClassificationTestType.Functional);
    }

    [Fact]
    public async Task ProfileLeakIsACriticalFailure()
    {
        var (live, _) = await Live(new FakePerson(leakProfile: true));

        Obs(live, 2, ClassificationIdentity.Unauthorized, ClassificationSurface.DirectProfile).State.Should().Be(ClassificationState.Fail);
        var result = Evaluate(Analyze(new Variant { ConstantGuardInput = false, UnguardedResolver = false }), live);
        result.Findings.Should().Contain(f => f.RuleId == "live-disclosure" && f.Severity == ClassificationSeverity.Critical);
        result.Overall.Should().Be(ClassificationOverall.IssueDetected);
    }

    [Fact]
    public async Task TotalCountLeakFails()
    {
        var (live, _) = await Live(new FakePerson(leakCount: true));

        Obs(live, 2, ClassificationIdentity.Unauthorized, ClassificationSurface.SearchTotalCount).State.Should().Be(ClassificationState.Fail);
        Obs(live, 2, ClassificationIdentity.Unauthorized, ClassificationSurface.Search).State.Should().Be(ClassificationState.Pass, "the item is hidden even though the count leaks");
    }

    [Fact]
    public async Task DistinguishableUnauthorizedAndNonexistentResponsesFail()
    {
        var (live, _) = await Live(new FakePerson(distinguishable: true));

        Obs(live, 2, ClassificationIdentity.Unauthorized, ClassificationSurface.NonexistentComparison).State.Should().Be(ClassificationState.Fail);
        Obs(live, 2, ClassificationIdentity.Unauthorized, ClassificationSurface.DirectProfile).State.Should().Be(ClassificationState.Pass, "no data was returned — only the response shape differs");
    }

    [Fact]
    public async Task FailedPositiveControlIsReportedNotHidden()
    {
        var (live, _) = await Live(new FakePerson(authorizedBlocked: true));

        Obs(live, 2, ClassificationIdentity.Authorized, ClassificationSurface.DirectProfile).State.Should().Be(ClassificationState.Fail);
        Evaluate(Analyze(), live).Findings.Should().Contain(f => f.RuleId == "live-positive-control" && f.Severity == ClassificationSeverity.Medium);
    }

    // ── 88–89 Safety ────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Production")]
    [InlineData("QA")]
    [InlineData(null)]
    public async Task ProductionMismatchedOrUnknownEnvironmentsAreNeverContacted(string? environmentType)
    {
        var (live, server) = await Live(new FakePerson(), new ClassificationRunRequest { EnvironmentType = environmentType, UnauthorizedToken = "tok-unauth" });

        live.State.Should().Be(IntegrationEvidenceState.NotSupported);
        server.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task UnapprovedContextOrMissingTokensSendNothing()
    {
        var (unapproved, s1) = await Live(new FakePerson(), context: Context() with { ApprovedByTestLead = false });
        var (noTokens, s2) = await Live(new FakePerson(), new ClassificationRunRequest { EnvironmentType = "Development" });

        unapproved.State.Should().Be(IntegrationEvidenceState.NotConfigured);
        noTokens.State.Should().Be(IntegrationEvidenceState.NotConfigured);
        (s1.Requests.Count + s2.Requests.Count).Should().Be(0);
    }

    [Fact]
    public async Task OnlyFixedQueriesForConfiguredTestChildrenAreSent()
    {
        var (_, server) = await Live(new FakePerson());

        server.Requests.Should().OnlyContain(r => GraphQlClassificationProbe.AllowedDocuments.Contains(r.Query));
        server.Requests.Should().NotContain(r => r.Query.Contains("mutation", StringComparison.OrdinalIgnoreCase));
        var birkIds = server.Requests.Where(r => r.Variables.TryGetProperty("birkId", out _)).Select(r => r.Variables.GetProperty("birkId").GetString()).Distinct();
        birkIds.Should().BeEquivalentTo(["B999-0000", "B999-0002"], "search only ever looks for a configured test child by its exact BiRK id");
        var ids = server.Requests.Where(r => r.Variables.TryGetProperty("id", out _)).Select(r => r.Variables.GetProperty("id").GetGuid()).Distinct().ToList();
        ids.Except([Graded, Ungraded]).Should().HaveCount(1, "one random nonexistent id per run for the anti-disclosure comparison");
        GraphQlClassificationProbe.AllowedDocuments.Should().OnlyContain(d => !d.Contains("navn", StringComparison.OrdinalIgnoreCase) && !d.Contains("foedselsnummer", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ContextValidationRefusesProductionAndPersonalLabels()
    {
        (Context() with { Environment = "PROD" }).Validate().Should().Contain("DEV or QA");
        (Context() with { UnauthorizedIdentityLabel = "ola.nordmann@bufdir.no" }).Validate().Should().Contain("no e-mail");
        (Context() with { GraphQlEndpoint = "https://x.test/graphql?token=abc" }).Validate().Should().Contain("query string");
        (Context() with { TestChildren = [new() { Nivaa = 2 }, new() { Nivaa = 2 }] }).Validate().Should().Contain("at most one");
        Context().LiveMutationTests.Should().BeFalse();
    }

    // ── Store, tokens, IQR ──────────────────────────────────────────────────────────────────────────────────────────

    private static AppDbContext Db() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static byte[] Zip(IEnumerable<SourceFile> files)
    {
        using var stream = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            foreach (var file in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(file.Path).Open(), Encoding.UTF8);
                writer.Write(file.Content);
            }
        return stream.ToArray();
    }

    [Fact]
    public async Task RunsAreImmutableSnapshotsAndTokensAreNeverStored()
    {
        await using var db = Db();
        var server = new FakePerson();
        var service = new ClassificationReviewService(db, new GraphQlClassificationProbe(new HttpClient(server), NullLogger<GraphQlClassificationProbe>.Instance), NullLogger<ClassificationReviewService>.Instance);
        (await service.AnalyzeAsync("dev", [("M2LB.zip", Zip(Fixture().Concat(Docs)))])).Error.Should().BeNull();
        (await service.SaveContextAsync("dev", Context())).Error.Should().BeNull();

        var run = await service.RunAsync("dev", new ClassificationRunRequest { EnvironmentType = "Development", UnauthorizedToken = "tok-unauth-SECRET", AuthorizedToken = "tok-auth" });

        run.Live.State.Should().Be(IntegrationEvidenceState.Available);
        db.SecurityClassificationEvidence.Select(r => r.Json).ToList().Should().OnlyContain(j => !j.Contains("tok-unauth-SECRET") && !j.Contains("tok-auth"));
        (await service.GetRunAsync(run.RunId))!.Findings.Count.Should().Be(run.Findings.Count);
        var overview = await service.OverviewAsync("dev");
        overview.History.Should().ContainSingle();
        overview.Context.TestChildren.Should().HaveCount(2);
        (await service.SaveContextAsync("dev", Context() with { Environment = "PROD" })).Error.Should().NotBeNull();
    }

    [Fact]
    public void IqrContributionKeepsSourceFactsOutOfPass()
    {
        var (checks, findings) = ClassificationEvaluator.ReviewChecks(Evaluate(Analyze()));

        checks.Where(c => c.Provenance == IntegrationEvidenceSource.SourceCode).Should().NotContain(c => c.Status == IntegrationCheckStatus.Pass);
        checks.Single(c => c.CheckId == "classification-message-flow").Status.Should().Be(IntegrationCheckStatus.NotAssessed);
        checks.Single(c => c.CheckId == "classification-cdc-deserializer").Domain.Should().Be(IntegrationReviewDomain.DataQuality);
        checks.Single(c => c.CheckId == "classification-model-reference").Domain.Should().Be(IntegrationReviewDomain.Configuration);
        checks.Single(c => c.CheckId == "classification-search-filter").Domain.Should().Be(IntegrationReviewDomain.Security);
        findings.Should().Contain(f => f.Severity == IntegrationFindingSeverityV2.High && f.Domain == IntegrationReviewDomain.DataQuality);
    }

    // ── Real read-only acceptance (runs only where the developer's archive exists) ─────────────────────────────────

    [Fact]
    public void RealM2lbSourceAcceptance()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "M2LB (1).zip");
        if (!File.Exists(path)) return;
        var (archive, files, error) = ScimSourceReader.Read("M2LB (1).zip", File.ReadAllBytes(path), p => p.Contains("/docs/") || p.Contains("/specs/"));
        error.Should().BeNull();

        var evidence = ClassificationSourceAnalyzer.Analyze("dev", [archive!], files, DateTimeOffset.UtcNow);
        var result = Evaluate(evidence);

        evidence.Levels.Select(l => $"{l.Nivaa}={l.Verdi}/{l.BiRKKode}/{l.ElementsKode}").Should().Equal("0=Ingen//", "1=SkjultAdresse//", "2=Kode7/Kode 7/K1", "3=Kode6/Kode 6/K2");
        Fact(evidence, "cdc-deserializer").State.Should().Be(ClassificationState.IssueDetected);
        Fact(evidence, "cdc-deserializer").Locations.Single().Should().Be(new SourceLocation("PersonAdapter/src/M2LB.PersonBiRKAdapter.Worker/Workers/CdcProcessorWorker.cs", 301));
        Fact(evidence, "graphql-hentBarnRegistreringIdFraEksternId").State.Should().Be(ClassificationState.IssueDetected);
        Fact(evidence, "grant-implementation").State.Should().Be(ClassificationState.DocumentedOnly);
        evidence.TestCoverage.Single(c => c.Scenario.StartsWith("Raw Debezium")).State.Should().Be(RepositoryTestCoverageState.Missing);
        result.Findings.Should().Contain(f => f.RuleId == "cdc-classification-constant");
        JsonSerializer.Serialize(evidence).Should().NotContain("@example.com");
    }
}
