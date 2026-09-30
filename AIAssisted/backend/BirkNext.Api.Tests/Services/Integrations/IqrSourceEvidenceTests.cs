using System.IO.Compression;
using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Integrations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Tests.Services.Integrations;

public sealed class IqrSourceEvidenceTests
{
    private const string Project = "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>";
    private const string TestProject = "<Project><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup><ItemGroup><PackageReference Include=\"xunit\" /></ItemGroup></Project>";
    private const string Mapper = """
        public static class PersonMapper {
            public static object? Map(JsonElement data) {
                if (!data.TryGetProperty("PersonPK", out var key)) { return null; }
                var name = data.TryGetProperty("Navn", out var n) ? n.GetString() : null;
                var read = data.GetProperty("Fornavn");
                var password = "SECRET_SENTINEL_123";
                return new object();
            }
        }
        """;
    private const string UnitTest = """
        public class PersonMapperTests {
            [Fact] public void MissingKeyReturnsNull() {
                var data = CreateData(); data.Remove("PersonPK");
                var result = PersonMapper.Map(data); Assert.Null(result);
            }
            [Fact] public void SimilarNameWithoutAssertion() { PersonMapper.Map(CreateData()); }
        }
        """;
    internal static byte[] Zip(params (string Path, string Content)[] files)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (var file in files) { using var writer = new StreamWriter(zip.CreateEntry(file.Path).Open()); writer.Write(file.Content); }
        return stream.ToArray();
    }
    private static IqrSourceSnapshot Analyze(params (string Path, string Content)[] extra)
    {
        var bytes = Zip([("src/Adapter.csproj", Project), ("src/PersonMapper.cs", Mapper), ("tests/Tests.csproj", TestProject), ("tests/PersonMapperTests.cs", UnitTest), .. extra]);
        var (workspace, error) = IqrSourceArchiveReader.Read("M2LB-fixture.zip", bytes);
        error.Should().BeNull();
        return IqrSourceAnalyzer.Analyze("person", workspace!, DateTimeOffset.UtcNow);
    }

    [Theory]
    [InlineData("../../escape.cs")]
    [InlineData("/absolute.cs")]
    [InlineData("C:/escape.cs")]
    [InlineData("safe/../escape.cs")]
    [InlineData("safe\\..\\escape.cs")]
    public void RejectsUnsafePathsEvenWhenIgnored(string path)
    {
        IqrSourceArchiveReader.Read("source.zip", Zip((path, Mapper))).Error.Should().Contain("unsafe");
    }
    [Fact] public void RejectsInvalidArchiveAndWrongExtension()
    {
        IqrSourceArchiveReader.Read("source.zip", [1, 2, 3]).Error.Should().Contain("invalid");
        IqrSourceArchiveReader.Read("source.exe", Zip(("a.cs", Mapper))).Error.Should().Contain(".zip");
    }
    [Fact] public void RejectsSymlinksAndDuplicatePaths()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true)) { var entry = zip.CreateEntry("link.cs"); entry.ExternalAttributes = unchecked((int)0xA1FF0000); }
        IqrSourceArchiveReader.Read("source.zip", stream.ToArray()).Error.Should().Contain("symbolic link");
        IqrSourceArchiveReader.Read("source.zip", Zip(("a.cs", Mapper), ("a.cs", Mapper))).Error.Should().Contain("duplicate");
    }
    [Fact] public void FingerprintIsDeterministicAndChangesWithArchiveBytes()
    {
        var bytes = Zip(("a.cs", Mapper));
        var first = IqrSourceArchiveReader.Read("source.zip", bytes).Workspace!.Archive.Sha256;
        IqrSourceArchiveReader.Read("renamed.zip", bytes).Workspace!.Archive.Sha256.Should().Be(first);
        IqrSourceArchiveReader.Read("source.zip", Zip(("a.cs", Mapper + " // changed"))).Workspace!.Archive.Sha256.Should().NotBe(first);
    }
    [Fact] public void MissingCommitIsUnknownAndOnlyLiteralRevisionIsAccepted()
    {
        Analyze().Commit.Should().Be("Unknown");
        var sha = new string('a', 40);
        Analyze(("other/Other.csproj", $"<Project><PropertyGroup><SourceRevisionId>{sha}</SourceRevisionId></PropertyGroup></Project>")).Commit.Should().Be(sha);
    }
    [Fact] public void FieldReadDoesNotBecomeRequiredAndFallbackIsSeparate()
    {
        var snapshot = Analyze();
        snapshot.Rules.Single(r => r.Field == "PersonPK").Requirement.Should().Be("Required for mapper output");
        snapshot.Rules.Single(r => r.Field == "Navn").Fallback.Should().Be("Missing → null");
        snapshot.Rules.Single(r => r.Field == "Fornavn").Requirement.Should().Be("Read; requiredness not resolved");
    }
    [Fact] public void ExistingUnitTestSuppressesSameLayerDuplicateButLeavesRuntimeAndE2eGaps()
    {
        var snapshot = Analyze();
        var rule = snapshot.Rules.Single(r => r.Field == "PersonPK");
        var coverage = snapshot.Coverage.Single(c => c.RuleId == rule.Id);
        coverage.DeveloperTestIds.Should().HaveCount(1);
        coverage.Statuses.Should().Contain(SourceCoverageStatus.DeveloperUnitCovered).And.Contain(SourceCoverageStatus.RuntimeGap).And.Contain(SourceCoverageStatus.E2EGap);
        coverage.Action.Should().Contain("no same-layer duplicate");
        snapshot.Tests.Should().OnlyContain(t => t.ExecutionResult.Contains("execution result unavailable"));
    }
    [Fact] public void IntegrationCoverageRequiresHostEvidenceAndRetainsRuntimeGap()
    {
        var snapshot = Analyze(("tests/Hosted.cs", UnitTest.Replace("PersonMapperTests", "HostedTests").Replace("var data = CreateData();", "var host = new WebApplicationFactory<App>(); var client = host.CreateClient(); var data = CreateData();")));
        snapshot.Tests.Should().Contain(t => t.Class == "HostedTests" && t.Layer == DeveloperTestLayer.Integration);
        var covered = snapshot.Coverage.Single(c => c.RuleId == snapshot.Rules.Single(r => r.Field == "PersonPK").Id);
        covered.Statuses.Should().Contain(SourceCoverageStatus.DeveloperIntegrationCovered).And.Contain(SourceCoverageStatus.RuntimeGap);
    }
    [Fact] public void SourceOnlyAndAmbiguousTestsDoNotCreateExecutableRecommendations()
    {
        var snapshot = Analyze();
        snapshot.Coverage.Single(c => c.RuleId == snapshot.Rules.Single(r => r.Field == "Fornavn").Id).Action.Should().Contain("no executable test");
        snapshot.Tests.Single(t => t.Method == "SimilarNameWithoutAssertion").AssertionIntent.Should().Be("Not resolved");
    }
    [Fact] public void MissingKeyOnUnrelatedInputIsNotEquivalentCoverage()
    {
        var bytes = Zip(("src/Adapter.csproj", Project), ("src/Mapper.cs", Mapper), ("tests/Tests.csproj", TestProject),
            ("tests/Test.cs", UnitTest.Replace("data.Remove(\"PersonPK\")", "other.Remove(\"PersonPK\")")));
        var snapshot = IqrSourceAnalyzer.Analyze("person", IqrSourceArchiveReader.Read("source.zip", bytes).Workspace!, DateTimeOffset.UtcNow);
        snapshot.Coverage.Should().OnlyContain(c => c.DeveloperTestIds.Count == 0);
    }
    [Fact] public void NunitMstestAndUnknownLayersAreInventoriedWithoutExecutionClaims()
    {
        var snapshot = Analyze(("tests/Other.cs", "public class Other { [Test] public void Nunit() { Assert.That(true); } [TestMethod] public void Mstest() { Assert.IsTrue(true); } }"));
        snapshot.Tests.Should().Contain(t => t.Framework == "NUnit" && t.Layer == DeveloperTestLayer.Unknown).And.Contain(t => t.Framework == "MSTest");
    }
    [Theory]
    [InlineData("var result = PersonMapper.Map(data); data.Remove(\"PersonPK\"); Assert.Null(result);")]
    [InlineData("data.Remove(\"PersonPK\"); data[\"PersonPK\"] = CreateValue(); var result = PersonMapper.Map(data); Assert.Null(result);")]
    public void MissingConditionMustPrecedeProductionCallAndNotBeOverwritten(string body)
    {
        var bytes = Zip(("src/Adapter.csproj", Project), ("src/Mapper.cs", Mapper), ("tests/Tests.csproj", TestProject),
            ("tests/Test.cs", "public class Tests { [Fact] public void Check() { var data = CreateData(); " + body + " } }"));
        var snapshot = IqrSourceAnalyzer.Analyze("person", IqrSourceArchiveReader.Read("source.zip", bytes).Workspace!, DateTimeOffset.UtcNow);
        snapshot.Coverage.Should().OnlyContain(c => c.DeveloperTestIds.Count == 0);
    }
    [Fact] public void AttributedRecordEnvelopeAndAmbiguousOverloadsStayConservative()
    {
        var snapshot = Analyze(("src/Record.cs", "public record Envelope([property:JsonPropertyName(\"payload\")] Payload? Payload = null);"),
            ("src/Overload.cs", "public static class PersonMapper { public static object Map(string data) => new object(); }"));
        snapshot.Rules.Single(r => r.Kind == "Envelope/model" && r.Field == "payload").TargetType.Should().Be("Payload?");
        snapshot.Coverage.Single(c => c.RuleId == snapshot.Rules.Single(r => r.Field == "PersonPK").Id).DeveloperTestIds.Should().BeEmpty();
    }
    [Theory]
    [InlineData("result.Should().BeNull();")]
    [InlineData("Assert.IsNull(result);")]
    [InlineData("Assert.That(result, Is.Null);")]
    public void EquivalentNullAssertionsAreResolvedWithoutExecutingTests(string assertion)
    {
        var bytes = Zip(("src/Adapter.csproj", Project), ("src/Mapper.cs", Mapper), ("tests/Tests.csproj", TestProject),
            ("tests/Test.cs", UnitTest.Replace("Assert.Null(result);", assertion)));
        var snapshot = IqrSourceAnalyzer.Analyze("person", IqrSourceArchiveReader.Read("source.zip", bytes).Workspace!, DateTimeOffset.UtcNow);
        snapshot.Coverage.Single(c => c.RuleId == snapshot.Rules.Single(r => r.Field == "PersonPK").Id).DeveloperTestIds.Should().HaveCount(1);
    }
    [Fact] public void ConfigValuesAndSentinelsNeverLeaveTheReader()
    {
        var snapshot = Analyze(("src/appsettings.json", "{\"ConnectionStrings\":{\"Database\":\"SECRET_SENTINEL_123\"},\"Authorization\":\"Bearer SECRET_SENTINEL_123\"}"),
            ("pipeline.yml", "clientSecret: SECRET_SENTINEL_123"));
        snapshot.Configurations.Should().Contain(c => c.Keys.Contains("ConnectionStrings:Database"));
        JsonSerializer.Serialize(snapshot).Should().NotContain("SECRET_SENTINEL_123").And.NotContain("Bearer");
    }
    [Fact] public void OperationsRoutesEnvelopeAndBarnRulesComeFromActualSyntax()
    {
        var source = """
            public class CdcEvent { [JsonPropertyName("payload")] public Payload Payload { get; set; } }
            public class Worker {
                public void Process(CdcEvent e) {
                    switch (e.Payload.Op) { case "c": Read(e.Payload.After); break; case "d": Read(e.Payload.Before); return; default: return; }
                    switch (e.Payload.Source.Table) { case "Person": PersonMapper.Map(e.Payload.After); break; case "Barn": ChildMapper.Map(e.Payload.After); break; default: return; }
                }
            }
            public static class ChildMapper { public static object? Map(JsonElement data) {
                if (!data.TryGetProperty("BarnPK", out var key) || key.ValueKind != JsonValueKind.Number) return null;
                var status = data.TryGetProperty("BarnStatusTypeFK", out var s) ? s.GetInt32() : 0; return new object();
            } }
            """;
        var snapshot = Analyze(("src/Worker.cs", source));
        snapshot.Rules.Where(r => r.Kind == "Operation").Select(r => r.Field).Should().BeEquivalentTo("c", "d", "default");
        snapshot.Rules.Single(r => r.Kind == "Operation" && r.Field == "d").Behavior.Should().Contain("Before");
        snapshot.Rules.Where(r => r.Kind == "Route" && r.Table is not null).Select(r => r.Table).Should().BeEquivalentTo("Person", "Barn");
        snapshot.Rules.Single(r => r.Field == "payload").Requirement.Should().Contain("not resolved");
        snapshot.Rules.Single(r => r.Field == "BarnPK").Requirement.Should().Be("Required for mapper output");
        snapshot.Rules.Single(r => r.Field == "BarnStatusTypeFK").Fallback.Should().Contain("Numeric constant");
    }
    [Fact] public void ConstantSecurityAssignmentRemainsCrossLayerGapDespiteGuardUnitTest()
    {
        var snapshot = Analyze(("src/Security.cs", """
            public class Deserializer { public Cdc Parse() { return new Cdc { SecurityLevel = 0 }; } }
            public class Guard { public static bool Reject(int value) => value > 1; }
            public class Adapter { private Guard securityGuard; public void Process(Cdc e) { var reject = securityGuard.Reject(e.SecurityLevel); } }
            """), ("tests/GuardTests.cs", "public class GuardTests { [Fact] public void Rejects() { var result = Guard.Reject(2); Assert.True(result); } }"));
        snapshot.Tests.Should().Contain(t => t.Class == "GuardTests" && t.Layer == DeveloperTestLayer.Unit);
        snapshot.Dataflows.Should().ContainSingle(f => f.Gap.Contains("Cross-layer gap") && f.Confidence == SourceConfidence.Partial);
        snapshot.Dataflows.Single().DeveloperTestIds.Should().HaveCount(1);
    }
    [Fact] public void InvalidBarnEnumWarningAndSymbolicFallbackAreDistinctFromRejection()
    {
        var snapshot = Analyze(("src/Child.cs", """
            public class ChildRegistrationMapper { public object Map(JsonElement data) {
                var status = data.TryGetProperty("BarnStatusTypeFK", out var s) ? s.GetInt32() : 0;
                if (!Enum.IsDefined(typeof(BarnStatus), status)) { logger.LogWarning("SECRET_SENTINEL_123"); status = BarnStatus.Active; }
                return new object();
            } }
            """));
        var validation = snapshot.Rules.Single(r => r.Kind == "Validation");
        validation.Field.Should().Be("BarnStatusTypeFK");
        validation.InvalidBehavior.Should().Be("Invalid enum → warning + fallback");
        validation.Fallback.Should().Be("Symbolic fallback: BarnStatus.Active");
        JsonSerializer.Serialize(snapshot).Should().NotContain("SECRET_SENTINEL_123");
    }
    [Fact] public void CheckpointAndErrorEvidenceIsSourceDefinedNeverRuntimeProof()
    {
        var snapshot = Analyze(("src/Worker.cs", "public class Worker { public void Process() { try { processor.UpdateCheckpointAsync(); } catch(JsonException ex) { logger.LogError(ex, \"SECRET_SENTINEL_123\"); } } }"));
        snapshot.Rules.Should().Contain(r => r.Kind == "Checkpoint" && r.Behavior.Contains("Runtime advance/withholding not verified"));
        snapshot.Rules.Should().Contain(r => r.Kind == "ErrorHandling" && r.Field == "JsonException");
        JsonSerializer.Serialize(snapshot).Should().NotContain("SECRET_SENTINEL_123");
    }
    [Fact] public void PartialProjectFailurePreservesUsefulEvidenceAndIgnoredAreasStayOut()
    {
        var snapshot = Analyze(("broken/Broken.csproj", "<Project>"), ("src/bin/Generated.cs", "public class Leak {}"), ("other/app.ts", "token = 'SECRET_SENTINEL_123'"));
        snapshot.Status.Should().Be(SourceAnalysisStatus.Partial);
        snapshot.Rules.Should().NotBeEmpty();
        snapshot.Limitations.Should().Contain(l => l.Contains("Project not parsed")).And.Contain(l => l.Contains(".ts"));
    }
    [Fact] public async Task StorePersistsOnlyEvidenceAndOldRunRetainsSnapshotAAfterB()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var store = new IqrSourceStore(db);
        var bytes = Zip(("src/Adapter.csproj", Project), ("src/Mapper.cs", Mapper), ("src/appsettings.json", "{\"Password\":\"SECRET_SENTINEL_123\"}"));
        var (a, _) = await store.AnalyzeAsync("dev", "person", "source.zip", bytes);
        var run = IqrSourceReview.Augment(new IntegrationReviewResult(), [a!]);
        db.IntegrationReviewRuns.Add(new IntegrationReviewRunRecord { Id = Guid.NewGuid(), EnvironmentId = "dev", ResultJson = JsonSerializer.Serialize(run) });
        await db.SaveChangesAsync();
        var (b, _) = await store.AnalyzeAsync("dev", "person", "source.zip", Zip(("src/Adapter.csproj", Project), ("src/Mapper.cs", Mapper + " // change")));
        (await store.ListAsync("dev")).Should().HaveCount(2);
        var historical = JsonSerializer.Deserialize<IntegrationReviewResult>((await db.IntegrationReviewRuns.SingleAsync()).ResultJson)!;
        historical.SourceSnapshots.Single().Id.Should().Be(a!.Id).And.NotBe(b!.Id);
        (await db.IqrSourceSnapshots.ToListAsync()).Should().OnlyContain(r => !r.EvidenceJson.Contains("SECRET_SENTINEL_123") && !r.EvidenceJson.Contains("public static class"));
        (await store.GetAsync("other", "person", a.Id)).Should().BeNull();
        (await store.GetAsync("dev", "other", a.Id)).Should().BeNull();
        historical.SourceSnapshots.Single().DeploymentCorrelation.Should().Be("Deployment/source correlation not established");
    }
    [Fact] public void IqrDomainsUseImplementationEvidenceWithoutFormalOrRuntimeOverclaim()
    {
        var snapshot = Analyze(("src/Worker.cs", "public class Worker { public void Process() { securityGuard.Check(); processor.UpdateCheckpointAsync(); logger.LogError(\"error\"); switch(table) { case \"Person\": Map(); break; } } }"));
        var domains = new[] { IntegrationReviewDomain.Contract, IntegrationReviewDomain.MessageFlow, IntegrationReviewDomain.Reliability, IntegrationReviewDomain.ErrorHandling, IntegrationReviewDomain.Security, IntegrationReviewDomain.DataQuality };
        var result = IqrSourceReview.Augment(new IntegrationReviewResult { ConfigurationSnapshot = new() { Integrations = [new() { Id = "person", SourceResource = "dbo.Person" }] }, Domains = domains.Select(d => new IntegrationDomainResult { Domain = d, StateLabel = "Not assessed" }).ToList() }, [snapshot]);
        result.Domains.Should().OnlyContain(d => d.StateLabel == "Partially assessed" && d.ChecksAssessed == 0);
        result.Domains.Single(d => d.Domain == IntegrationReviewDomain.Contract).Missing.Should().Contain(l => l.Contains("Formal schema"));
        result.WhatWasTested.Should().Contain(l => l.Contains("not executed"));
        result.Domains.Single(d => d.Domain == IntegrationReviewDomain.MessageFlow).Observed.Should().Contain(l => l.Contains("matching source branch") && l.Contains("Runtime application processing"));
    }
}
