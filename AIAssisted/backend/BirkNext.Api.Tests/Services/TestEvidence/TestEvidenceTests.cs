using System.Text;
using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.SourceAnalysis.Technology;
using BirkNext.Api.Services.TestEvidence;
using BirkNext.Api.Tests.Services.Integrations;
using BirkNext.Integrations;
using BirkNext.Sdd;
using BirkNext.Technology;
using BirkNext.TestEvidence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;
using F = BirkNext.Api.Tests.Services.TestEvidence.TestEvidenceFixtures;

namespace BirkNext.Api.Tests.Services.TestEvidence;

public sealed class TestEvidenceTests
{
    private static readonly Guid SnapshotId = Guid.Parse("0f0f0f0f-0000-0000-0000-000000000001");
    private static readonly TrxTestExecutionEvidenceProvider Trx = new(TestEvidenceOptions.Default);

    private static IqrSourceArchiveReader.Workspace Workspace(params (string Path, string Content)[] files)
    {
        var (workspace, error) = IqrSourceArchiveReader.Read("contoso.zip", IqrSourceEvidenceTests.Zip(files));
        error.Should().BeNull();
        return workspace!;
    }

    private static SourceTestInventory Discover(params (string Path, string Content)[] files) =>
        DotNetXunitTestDiscoveryProvider.Discover(SnapshotId, "abc123", "Contoso", Workspace(files.Length == 0 ? F.Solution() : files));

    private static SourceTestDefinition Def(SourceTestInventory inventory, string method, string project = "Contoso.Orders.Unit.Tests") =>
        inventory.Definitions.Single(d => d.MethodName == method && d.Project == project);

    private static TestResultArtifactPreview Read(string trx, string name = "Unit-test-results.trx") => Trx.Read(name, Encoding.UTF8.GetBytes(trx));

    // ── Source discovery: projects ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Discovery_DetectsTestProjectsFromProjectMetadata_NotFolderNames()
    {
        var inventory = Discover();
        inventory.ProviderId.Should().Be("test.discovery.dotnet.xunit");
        inventory.Projects.Select(p => p.Name).Should().BeEquivalentTo("Contoso.Orders.Unit.Tests", "Contoso.Orders.Integration.Tests", "Contoso.Billing.Integration.Tests",
            "Contoso.Orders.ContractTests", "Contoso.Billing.ContractTests", "Contoso.Web.Tests", "Contoso.Legacy.Tests");
        inventory.Projects.Should().NotContain(p => p.Name == "TestHelpers", "a project under tests/ without test packages is not a test project");
        inventory.Definitions.Should().NotContain(d => d.MethodName == "Fact", "a helper method named Fact is not an xUnit test");
        var unit = inventory.Projects.Single(p => p.Name == "Contoso.Orders.Unit.Tests");
        unit.Framework.Should().Be("xUnit");
        unit.FrameworkPackage.Should().Be("xunit.v3");
        unit.TrxReportConfigured.Should().BeTrue();
        unit.Packages.Should().Contain(["Microsoft.NET.Test.Sdk", "Microsoft.Testing.Extensions.TrxReport", "xunit.v3", "coverlet.collector"]);
        inventory.RepositoryName.Should().Be("Contoso");
        inventory.SnapshotId.Should().Be(SnapshotId);
    }

    [Fact]
    public void Discovery_ClassifiesKindsConservatively_AndKeepsBUnitAsComponentTests()
    {
        var inventory = Discover();
        Kind(inventory, "Contoso.Orders.Unit.Tests").Should().Be((TestKind.Unit, TestEvidenceConfidence.StronglySupported));
        Kind(inventory, "Contoso.Orders.Integration.Tests").Should().Be((TestKind.Integration, TestEvidenceConfidence.StronglySupported));
        Kind(inventory, "Contoso.Orders.ContractTests").Should().Be((TestKind.Contract, TestEvidenceConfidence.StronglySupported));
        var web = inventory.Projects.Single(p => p.Name == "Contoso.Web.Tests");
        web.Kind.Should().Be(TestKind.FrontendComponent);
        web.KindBasis.Should().Contain("not browser E2E");
        // Declared category outranks the project-name token.
        Def(inventory, "Discount_NeverNegative").KindConfidence.Should().Be(TestEvidenceConfidence.Confirmed);
        Def(inventory, "Discount_NeverNegative").KindBasis.Should().Be("Declared category 'Unit'");
        // Testcontainers alone does not make a project a runtime-environment test: the kind comes from its name token only.
        inventory.Projects.Single(p => p.Name == "Contoso.Orders.Integration.Tests").Packages.Should().Contain("Testcontainers.MsSql");
        Discover(("x/Some.Api.Tests/Some.Api.Tests.csproj", F.IntegrationProject), ("x/Some.Api.Tests/A.cs", F.HealthTests("A"))).Projects.Single().Kind.Should().Be(TestKind.Unknown);

        static (TestKind, TestEvidenceConfidence) Kind(SourceTestInventory i, string name) { var p = i.Projects.Single(x => x.Name == name); return (p.Kind, p.KindConfidence); }
    }

    [Fact]
    public void Discovery_ReportsNUnitAsDetectedButUnsupported()
    {
        var inventory = Discover();
        var legacy = inventory.Projects.Single(p => p.Name == "Contoso.Legacy.Tests");
        legacy.Framework.Should().Be("NUnit");
        legacy.DiscoverySupported.Should().BeFalse();
        legacy.DefinitionCount.Should().Be(0);
        inventory.Limitations.Should().Contain(l => l.StartsWith("NUnit source test discovery is not implemented"));
        inventory.Status.Should().Be(SourceTestDiscoveryStatus.Partial);
        Discover(("a/L.Tests/L.Tests.csproj", F.NUnitProject), ("a/L.Tests/T.cs", F.NUnitTests)).Status.Should().Be(SourceTestDiscoveryStatus.Unsupported);
        Discover(("a/App/App.csproj", F.HelperProject), ("a/App/A.cs", "class A {}")).Status.Should().Be(SourceTestDiscoveryStatus.NoTestProjects);
    }

    // ── Source discovery: [Fact] / [Theory] ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Discovery_FindsFactsAndTheories_WithRunnerIdentity()
    {
        var inventory = Discover();
        var unit = inventory.Definitions.Where(d => d.Project == "Contoso.Orders.Unit.Tests").ToList();
        unit.Select(d => d.MethodName).Should().BeEquivalentTo("Discount_NeverNegative", "Rounding_IsBankers", "Tax_FailsOnPurpose", "Currency_Skipped", "Square",
            "Shipping_Free", "FR023_NameLooksLikeARequirement", "Dynamic", "Inner_Passes");
        var square = Def(inventory, "Square");
        square.DefinitionKind.Should().Be(TestDefinitionKind.Theory);
        square.InlineDataRows.Should().Be(3);
        square.FullyQualifiedName.Should().Be("Contoso.Orders.Unit.Tests.OrderPricingTests.Square");
        square.ClassName.Should().Be("Contoso.Orders.Unit.Tests.OrderPricingTests");
        square.Namespace.Should().Be("Contoso.Orders.Unit.Tests");
        square.FilePath.Should().Be("Contoso/tests/Contoso.Orders.Unit.Tests/OrderTests.cs");
        square.Line.Should().BeGreaterThan(1);
        Def(inventory, "Dynamic").HasDynamicData.Should().BeTrue();
        Def(inventory, "Inner_Passes").FullyQualifiedName.Should().Be("Contoso.Orders.Unit.Tests.OrderPricingTests+Nested.Inner_Passes", "nested classes use '+' like the runners");
        Def(inventory, "Shipping_Free").DisplayName.Should().Be("Shipping is free above threshold");
        Def(inventory, "Currency_Skipped").SkipDeclared.Should().BeTrue();
        Def(inventory, "Discount_NeverNegative").DefinitionKind.Should().Be(TestDefinitionKind.Fact);
        Def(inventory, "Discount_NeverNegative").SourceFingerprint.Should().HaveLength(64);
    }

    [Fact]
    public void Discovery_KeepsMethodAndClassTraits_AndCategories()
    {
        var square = Def(Discover(), "Square");
        square.Traits.Should().Contain(new SourceTestTrait("Requirement", "JIRA-123", "Method"));
        square.Traits.Should().Contain(new SourceTestTrait("Category", "Unit", "Class"));
        square.Categories.Should().Equal("Unit");
    }

    [Fact]
    public void Discovery_DuplicateNamesAcrossProjectsDoNotCollide()
    {
        var inventory = Discover();
        var health = inventory.Definitions.Where(d => d.MethodName == "Health_ReturnsOk").ToList();
        health.Should().HaveCount(4);
        health.Select(d => d.TestDefinitionId).Should().OnlyHaveUniqueItems();
        health.Select(d => d.StableIdentity).Should().OnlyHaveUniqueItems().And.OnlyContain(id => id.StartsWith("Contoso::"));
        health.Count(d => d.FullyQualifiedName == "Shared.Contract.HealthEndpointTests.Health_ReturnsOk").Should().Be(2, "same FQN in two contract-test projects");
    }

    [Fact]
    public void Discovery_IdentityIsStableAcrossSnapshots_AndFingerprintTracksTestChanges()
    {
        var first = Def(Discover(), "Discount_NeverNegative");
        var again = Def(DotNetXunitTestDiscoveryProvider.Discover(Guid.NewGuid(), "other", "Contoso", Workspace(F.Solution())), "Discount_NeverNegative");
        again.TestDefinitionId.Should().Be(first.TestDefinitionId);
        again.SourceFingerprint.Should().Be(first.SourceFingerprint);
        var changed = F.Solution().Select(f => f.Path.EndsWith("OrderTests.cs") ? (f.Path, f.Content.Replace("10 - 20", "10 - 30")) : f).ToArray();
        var edited = Def(DotNetXunitTestDiscoveryProvider.Discover(Guid.NewGuid(), "other", "Contoso", Workspace(changed)), "Discount_NeverNegative");
        edited.TestDefinitionId.Should().Be(first.TestDefinitionId);
        edited.SourceFingerprint.Should().NotBe(first.SourceFingerprint);
    }

    [Fact]
    public void Discovery_SurvivesCSharp12CollectionExpressions_WithoutLosingTests()
    {
        DotNetXunitTestDiscoveryProvider.NeutralizeCollectionExpressions("""var r = await db.FindAsync(["1406"], ct); x = [1, 2]; return [a]; var y = items[0];""")
            .Should().Be("""var r = await db.FindAsync(new[]{"1406"}, ct); x = new[]{1, 2}; return new[]{a}; var y = items[0];""");
        DotNetXunitTestDiscoveryProvider.NeutralizeCollectionExpressions("    [Fact]\n    [InlineData(1)]\n    [Trait(\"Category\", \"Unit\")]").Should().Be("    [Fact]\n    [InlineData(1)]\n    [Trait(\"Category\", \"Unit\")]");
        var code = """
            namespace N;
            public class SyncTests
            {
                [Fact] public async Task A() { var row = await Find(["1406"]); }
                [Fact] public void B() { }
                [Fact] public void C() { }
                private Task<int> Find(string[] keys) => Task.FromResult(1);
            }
            """;
        var inventory = Discover(("s/N.Unit/N.Unit.csproj", F.ContractProject), ("s/N.Unit/SyncTests.cs", code));
        inventory.Definitions.Select(d => d.MethodName).Should().Equal("A", "B", "C");
        inventory.Definitions.Single(d => d.MethodName == "B").Line.Should().Be(5, "line numbers are preserved");
    }

    // ── Source discovery: explicit requirement references ─────────────────────────────────────────────────────────────

    [Fact]
    public void References_ConfidenceFollowsWhereTheIdentifierIs()
    {
        var inventory = Discover();
        Ref(inventory, "Discount_NeverNegative", "FR-023").Should().Be((TestEvidenceConfidence.StronglySupported, "Structured comment on the test method"));
        Ref(inventory, "Rounding_IsBankers", "FR-026").Should().Be((TestEvidenceConfidence.Confirmed, "[Trait(\"Requirement\")] on the test method"));
        Ref(inventory, "Square", "FR-031").Should().Be((TestEvidenceConfidence.StronglySupported, "Structured comment in the test body"));
        Ref(inventory, "Tax_FailsOnPurpose", "SC-004").Should().Be((TestEvidenceConfidence.Inferred, "Mentioned in a comment in the test body"));
        Ref(inventory, "Shipping_Free", "FR-040").Item1.Should().Be(TestEvidenceConfidence.Inferred, "assertion-message text is a candidate, not a link");
        Ref(inventory, "Discount_NeverNegative", "FR-090").Should().Be((TestEvidenceConfidence.Inferred, "Mentioned in the test class comment (applies to the class, not established per test)"));
    }

    [Fact]
    public void References_CustomIdsAndMultipleIdsArePreserved_ByTheSharedParser()
    {
        var square = Def(Discover(), "Square");
        square.References.Where(r => r.Confidence == TestEvidenceConfidence.Confirmed).Select(r => r.Id).Should().BeEquivalentTo("JIRA-123", "US-A1", "AC-007");
        square.References.Single(r => r.Id == "AC-007").Kind.Should().Be(TestReferenceKind.AcceptanceCriterion);
        square.References.Single(r => r.Id == "US-A1").Kind.Should().Be(TestReferenceKind.Requirement);
        RequirementReferenceParser.Extract("covers FR1 and JIRA-123, US-A1").Should().Equal("FR-001", "JIRA-123", "US-A1");
    }

    [Fact]
    public void References_AreNotInferredFromNamesHelpersOrOtherMethods()
    {
        var inventory = Discover();
        Def(inventory, "FR023_NameLooksLikeARequirement").References.Where(r => r.Confidence != TestEvidenceConfidence.Inferred || r.Id != "FR-090")
            .Should().BeEmpty("a method name resembling an ID is not a reference");
        inventory.Definitions.SelectMany(d => d.References).Should().NotContain(r => r.Id == "FR-099", "a comment on a helper method links no test");
        inventory.Definitions.SelectMany(d => d.References).Should().NotContain(r => r.Id == "FR-077", "a fixture comment in a non-test project links no test");
        Def(inventory, "Inner_Passes").References.Should().BeEmpty("the outer class comment is not the nested class's comment");
        Def(inventory, "Currency_Skipped").References.Should().NotContain(r => r.Id == "JIRA-123", "a skip reason is not a requirement reference");
        Def(inventory, "Health_ReturnsOk", "Contoso.Orders.Integration.Tests").References.Should().BeEmpty("a test without references is valid without links");
    }

    [Fact]
    public void Discovery_RecordsTrxConfigurationAsConfigurationEvidenceOnly()
    {
        var evidence = Discover().TrxConfiguration;
        evidence.Should().Contain(e => e.Kind == "TrxGeneration" && e.File.EndsWith("runtests.yml"));
        evidence.Should().Contain(e => e.Kind == "TrxPublication" && e.Detail.Contains("publishes TRX"));
        evidence.Should().Contain(e => e.Kind == "TrxReportPackage" && e.File.EndsWith("Contoso.Orders.Unit.Tests.csproj"));
    }

    [Fact]
    public async Task SourceAnalysisUpload_AttachesTheTestInventoryToTheSnapshot()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var store = new IqrSourceStore(db);
        var (snapshot, error) = await store.AnalyzeAsync("dev", IqrSourceStore.SourceAnalysisOwner, "contoso.zip", IqrSourceEvidenceTests.Zip(F.Solution()));
        error.Should().BeNull();
        snapshot!.TestInventory.Should().NotBeNull();
        snapshot.TestInventory!.SnapshotId.Should().Be(snapshot.Id);
        snapshot.TestInventory.SnapshotFingerprint.Should().Be(snapshot.Archive.Sha256);
        var stored = await store.FindSourceAnalysisAsync(snapshot.Id);
        stored!.TestInventory!.Definitions.Should().HaveCount(snapshot.TestInventory.Definitions.Count);
        snapshot.TechnologyCoverage!.Technologies.Select(t => t.TechnologyId).Should().Contain(["test.xunit", "test.nunit", "test.trx"]);
    }

    // ── TRX provider ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Trx_MicrosoftTestingPlatform_ParsesRunAndEveryExecution()
    {
        var preview = Read(F.MtpTrx());
        preview.Status.Should().Be(TestResultImportStatus.Valid);
        preview.ProviderId.Should().Be("test.execution.trx");
        preview.Fingerprint.Should().HaveLength(64);
        preview.Run!.ProviderRunId.Should().Be("7abfec54-72a5-4096-99bc-60bcde8bed85");
        preview.Run.StartedAt.Should().Be(DateTimeOffset.Parse("2026-09-30T08:00:00Z"));
        preview.Run.DurationMs.Should().Be(2000);
        preview.Run.RunState.Should().Be("Completed");
        preview.Run.ProviderOutcome.Should().Be("Failed");
        preview.Run.Counts.Should().BeEquivalentTo(new TestResultCounts { Total = 9, Passed = 6, Failed = 2, NotExecuted = 1 });
        var rows = preview.Executions;
        rows.Single(e => e.TestName.EndsWith("Inner_Passes")).FullyQualifiedName.Should().Be("Contoso.Orders.Unit.Tests.OrderPricingTests+Nested.Inner_Passes");
        var square = rows.Where(e => e.FullyQualifiedName == "Contoso.Orders.Unit.Tests.OrderPricingTests.Square").ToList();
        square.Should().HaveCount(3, "each theory data row is its own execution");
        square.Select(e => e.DataRowLabel).Should().BeEquivalentTo("value: 1, expected: 1", "value: 2, expected: 4", "value: 3, expected: 10");
        square.Select(e => e.Result).Should().BeEquivalentTo("Passed", "Passed", "Failed");
        rows.Single(e => e.TestName == "Shipping is free above threshold").FullyQualifiedName.Should().BeNull("a display name is not a fully-qualified name");
        rows.Single(e => e.TestName.EndsWith("Tax_FailsOnPurpose")).Should().Match<NormalizedTestExecution>(e => e.ExecutionState == "Completed" && e.Result == "Failed" && e.DurationMs == 1.2);
        rows.Should().OnlyContain(e => e.AssemblyName == "Contoso.Orders.Unit.Tests.dll");
    }

    [Fact]
    public void Trx_VsTestLogger_BareMethodNames_AndCountsComeFromResultsNotCounters()
    {
        var preview = Read(F.VsTestTrx());
        preview.Executions.Single(e => e.ProviderExecutionId == "e1").FullyQualifiedName.Should().Be("Contoso.Orders.Unit.Tests.OrderPricingTests.Square");
        preview.Executions.Single(e => e.ProviderExecutionId == "e1").DataRowLabel.Should().Be("value: 1, expected: 1");
        preview.Executions.Single(e => e.ProviderExecutionId == "e2").FullyQualifiedName.Should().Be("Contoso.Orders.Unit.Tests.OrderPricingTests.Shipping_Free");
        var skipped = preview.Executions.Single(e => e.ProviderExecutionId == "e3");
        (skipped.ExecutionState, skipped.Result, skipped.ProviderOutcome, skipped.ErrorMessage).Should().Be(("NotExecuted", "Unknown", "NotExecuted", "Pending JIRA-123"));
        preview.Run!.Counts.NotExecuted.Should().Be(1);
        preview.Run.DeclaredCounts!.NotExecuted.Should().Be(0);
        preview.Warnings.Should().Contain(w => w.Contains("counters differ"));
        preview.Run.StartedAt.Should().Be(DateTimeOffset.Parse("2026-09-30T06:10:00Z"));
    }

    [Theory]
    [InlineData("Passed", "Completed", "Passed")]
    [InlineData("Failed", "Completed", "Failed")]
    [InlineData("Inconclusive", "Completed", "Inconclusive")]
    [InlineData("Warning", "Completed", "Inconclusive")]
    [InlineData("PassedButRunAborted", "Completed", "Passed")]
    [InlineData("NotExecuted", "NotExecuted", "Unknown")]
    [InlineData("NotRunnable", "NotExecuted", "Unknown")]
    [InlineData("Timeout", "TimedOut", "Unknown")]
    [InlineData("Aborted", "Aborted", "Unknown")]
    [InlineData("Error", "ExecutionFailed", "Unknown")]
    [InlineData("Disconnected", "ExecutionFailed", "Unknown")]
    [InlineData("InProgress", "Running", "Unknown")]
    [InlineData("Pending", "Queued", "Unknown")]
    [InlineData("SomethingNew", "Unknown", "Unknown")]
    public void Trx_OutcomeNormalization_NeverCollapsesNonPassIntoFailed(string outcome, string state, string result) =>
        TrxTestExecutionEvidenceProvider.Normalize(outcome).Should().Be((state, result));

    [Fact]
    public void Trx_AbortedRun_KeepsExactStates_AndInventsNoFailures()
    {
        var preview = Read(F.AbortedTrx);
        preview.Run!.RunState.Should().Be("Aborted");
        preview.Executions.Select(e => (e.ExecutionState, e.Result)).Should().Equal(("Completed", "Passed"), ("TimedOut", "Unknown"), ("ExecutionFailed", "Unknown"));
        preview.Run.Counts.Failed.Should().Be(0);
        preview.Run.Counts.TimedOut.Should().Be(1);
        preview.Run.Counts.ExecutionFailed.Should().Be(1);
        preview.Limitations.Should().Contain(l => l.Contains("aborted") && l.Contains("not Failed"));
        preview.Run.Messages.Should().Equal("Test host process crashed");
    }

    [Fact]
    public void Trx_MachineUserAndPathDetail_NeverLeaveTheProvider()
    {
        var json = JsonSerializer.Serialize(Read(F.MtpTrx())) + JsonSerializer.Serialize(Read(F.VsTestTrx()));
        json.Should().NotContain("BUILD-AGENT-01").And.NotContain("builduser").And.NotContain(@"agent\\_work").And.NotContain("/home/vsts").And.NotContain("abc.def.ghi");
        var failed = Read(F.MtpTrx()).Executions.Single(e => e.TestName.EndsWith("Tax_FailsOnPurpose"));
        failed.StackTrace.Should().Contain(@"…\OrderTests.cs:line 19");
        failed.ErrorMessage.Should().StartWith("Assert.Equal() Failure");
    }

    [Fact]
    public void Trx_OutputAndMessagesAreBounded()
    {
        var huge = new string('x', 50_000);
        var trx = F.AbortedTrx.Replace("Test host process crashed", huge).Replace("""outcome="Error" />""", $"""outcome="Error"><Output><StdOut>{huge}</StdOut><ErrorInfo><Message>{huge}</Message></ErrorInfo></Output></UnitTestResult>""");
        var preview = Read(trx);
        preview.Executions[2].ErrorMessage!.Length.Should().BeLessThan(4_100);
        preview.Executions[2].Output!.Length.Should().BeLessThan(2_100);
        preview.Run!.Messages.Single().Length.Should().BeLessThan(600);
    }

    [Fact]
    public void Trx_MalformedOrForeignXml_IsAnImportProblem_NotATestResult()
    {
        Read("<TestRun><Results>").Should().Match<TestResultArtifactPreview>(p => p.Status == TestResultImportStatus.InvalidArtifact && p.Executions.Count == 0 && p.Run == null);
        Read("<testsuites><testsuite /></testsuites>").Error.Should().Contain("not a TRX TestRun");
        Read("").Status.Should().Be(TestResultImportStatus.InvalidArtifact);
        Trx.Read("results.xml", Encoding.UTF8.GetBytes(F.MtpTrx())).Status.Should().Be(TestResultImportStatus.UnsupportedFormat);
    }

    [Fact]
    public void Trx_DtdAndExternalEntitiesAreRefused()
    {
        var secret = Path.Combine(Path.GetTempPath(), $"trx-xxe-{Guid.NewGuid():N}.txt");
        File.WriteAllText(secret, "TOP-SECRET-CONTENT");
        try
        {
            var xxe = $"""<?xml version="1.0"?><!DOCTYPE TestRun [<!ENTITY x SYSTEM "file:///{secret.Replace('\\', '/')}">]><TestRun id="1" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results><UnitTestResult testId="a" executionId="b" testName="&x;" outcome="Passed" /></Results></TestRun>""";
            var preview = Read(xxe);
            preview.Status.Should().Be(TestResultImportStatus.InvalidArtifact);
            JsonSerializer.Serialize(preview).Should().NotContain("TOP-SECRET-CONTENT");
            var bomb = """<?xml version="1.0"?><!DOCTYPE r [<!ENTITY a "aaaaaaaaaa"><!ENTITY b "&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;">]><TestRun>&b;</TestRun>""";
            Read(bomb).Status.Should().Be(TestResultImportStatus.InvalidArtifact);
        }
        finally { File.Delete(secret); }
    }

    [Fact]
    public void Trx_SizeAndResultCountAreBounded()
    {
        var small = new TrxTestExecutionEvidenceProvider(TestEvidenceOptions.Default with { MaxArtifactBytes = 2048 });
        small.Read("big.trx", Encoding.UTF8.GetBytes(F.MtpTrx())).Status.Should().Be(TestResultImportStatus.TooLarge);
        var few = new TrxTestExecutionEvidenceProvider(TestEvidenceOptions.Default with { MaxResults = 3 });
        few.Read("many.trx", Encoding.UTF8.GetBytes(F.MtpTrx())).Should().Match<TestResultArtifactPreview>(p => p.Status == TestResultImportStatus.TooLarge && p.Error!.Contains("limit"));
    }

    [Fact]
    public void Trx_FingerprintIdentifiesTheArtifactBytes()
    {
        Read(F.MtpTrx()).Fingerprint.Should().Be(Read(F.MtpTrx()).Fingerprint);
        Read(F.MtpTrx()).Fingerprint.Should().NotBe(Read(F.MtpTrx() + " ").Fingerprint);
        Read(F.MtpTrx() + " ").Run!.ProviderRunId.Should().Be(Read(F.MtpTrx()).Run!.ProviderRunId, "re-serialized bytes keep the run identity used for execution dedupe");
    }

    // ── Correlation ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Correlation_MatchesExactIdentities_AndKeepsTheoryRowsSeparate()
    {
        var inventory = Discover();
        var preview = Read(F.MtpTrx());
        var correlated = preview.Executions.Select(e => (e, c: TestEvidenceCorrelation.Correlate(e, inventory.Definitions))).ToList();
        correlated.Should().OnlyContain(x => x.c.State == TestCorrelationState.Confirmed);
        correlated.Where(x => x.e.FullyQualifiedName?.EndsWith(".Square") == true).Select(x => x.c.TestDefinitionId).Distinct().Should().Equal(Def(inventory, "Square").TestDefinitionId);
        correlated.Single(x => x.e.TestName == "Shipping is free above threshold").c.Basis.Should().Be("Display name declared in source for this class");
        correlated.Single(x => x.e.TestName.EndsWith("Discount_NeverNegative")).c.Basis.Should().Be("Fully-qualified test name");
        TestEvidenceCorrelation.Aggregate(correlated.Where(x => x.e.FullyQualifiedName?.EndsWith(".Square") == true).Select(x => x.e.Result)).Should().Be("1 of 3 failed");

        var vstest = Read(F.VsTestTrx()).Executions.ToDictionary(e => e.ProviderExecutionId, e => TestEvidenceCorrelation.Correlate(e, inventory.Definitions));
        var shippingId = Def(inventory, "Shipping_Free").TestDefinitionId;
        vstest["e2"].Should().Match<TestCorrelation>(c => c.State == TestCorrelationState.Confirmed && c.TestDefinitionId == shippingId);
        vstest["e4"].State.Should().Be(TestCorrelationState.Unresolved, "NUnit results import, but there is no NUnit source discovery to correlate with");
    }

    [Fact]
    public void Correlation_AmbiguousIdentity_IsNeverAutoLinked_UnlessTheAssemblyDecides()
    {
        var definitions = Discover().Definitions;
        var execution = new NormalizedTestExecution { ClassName = "Shared.Contract.HealthEndpointTests", MethodName = "Health_ReturnsOk", FullyQualifiedName = "Shared.Contract.HealthEndpointTests.Health_ReturnsOk", TestName = "x" };
        var ambiguous = TestEvidenceCorrelation.Correlate(execution, definitions);
        ambiguous.State.Should().Be(TestCorrelationState.Ambiguous);
        ambiguous.TestDefinitionId.Should().BeNull();
        ambiguous.CandidateDefinitionIds.Should().HaveCount(2);
        var decided = TestEvidenceCorrelation.Correlate(execution with { AssemblyName = "Contoso.Billing.ContractTests.dll" }, definitions);
        decided.State.Should().Be(TestCorrelationState.Confirmed);
        definitions.Single(d => d.TestDefinitionId == decided.TestDefinitionId).Project.Should().Be("Contoso.Billing.ContractTests");
    }

    [Fact]
    public void Correlation_NoFuzzyMatching()
    {
        var definitions = Discover().Definitions;
        TestEvidenceCorrelation.Correlate(new NormalizedTestExecution { ClassName = "Contoso.Orders.Unit.Tests.OrderPricingTests", MethodName = "Discount_NeverNegativ", TestName = "Discount_NeverNegativ" }, definitions)
            .State.Should().Be(TestCorrelationState.Unresolved);
        TestEvidenceCorrelation.Correlate(new NormalizedTestExecution { TestName = "Discount_NeverNegative" }, definitions).State.Should().Be(TestCorrelationState.Unresolved, "a bare name without class is not an identity");
        TestEvidenceCorrelation.Correlate(new NormalizedTestExecution { TestName = "x" }, []).State.Should().Be(TestCorrelationState.NotAssessed);
    }

    // ── Preview service: binding and provenance ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Preview_CorrelatesAgainstTheSelectedSnapshot_ButBindsTheSourceVersionOnlyWhenConfirmed()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var store = new IqrSourceStore(db);
        var (snapshot, _) = await store.AnalyzeAsync("dev", IqrSourceStore.SourceAnalysisOwner, "contoso.zip", IqrSourceEvidenceTests.Zip(F.Solution()));
        var service = new TestExecutionImportService([Trx], store);
        var bytes = Encoding.UTF8.GetBytes(F.MtpTrx());

        var unbound = await service.PreviewAsync("Unit-test-results.trx", bytes, new("dev", snapshot!.Id, false, " 20260930.4 ", null, "QA"));
        unbound.SourceBinding.Should().Be(TestSourceBinding.Unknown);
        unbound.CorrelationSnapshotId.Should().Be(snapshot.Id);
        unbound.Executions.Should().OnlyContain(e => e.Correlation.State == TestCorrelationState.Confirmed);
        unbound.MatchedDefinitions.Select(d => d.MethodName).Should().Contain(["Square", "Shipping_Free", "Inner_Passes"]).And.NotContain("Dynamic");
        unbound.Limitations.Should().Contain(l => l.StartsWith("Source version not established"));
        (unbound.BuildReference, unbound.BuildBinding, unbound.CommitBinding, unbound.EnvironmentReference).Should().Be(("20260930.4", TestSourceBinding.Provided, TestSourceBinding.Unknown, "QA"));

        var bound = await service.PreviewAsync("Unit-test-results.trx", bytes, new("dev", snapshot.Id, true, null, "a1b2c3", null));
        bound.SourceBinding.Should().Be(TestSourceBinding.Provided);
        bound.CommitBinding.Should().Be(TestSourceBinding.Provided);

        var none = await service.PreviewAsync("Unit-test-results.trx", bytes, new(null, null, false, null, null, null));
        none.Executions.Should().OnlyContain(e => e.Correlation.State == TestCorrelationState.NotAssessed);
        none.Limitations.Should().Contain(l => l.StartsWith("No source snapshot selected"));

        var missing = await service.PreviewAsync("Unit-test-results.trx", bytes, new("dev", Guid.NewGuid(), true, null, null, null));
        missing.SourceBinding.Should().Be(TestSourceBinding.Unknown);
        missing.Limitations.Should().Contain(l => l.Contains("not found"));

        var invalid = await service.PreviewAsync("broken.trx", Encoding.UTF8.GetBytes("<TestRun>"), new("dev", snapshot.Id, true, null, null, null));
        invalid.Status.Should().Be(TestResultImportStatus.InvalidArtifact);
        (await service.PreviewAsync("results.json", bytes, new(null, null, false, null, null, null))).Status.Should().Be(TestResultImportStatus.UnsupportedFormat);
    }

    [Fact]
    public async Task Preview_HistoricalSnapshotWithoutDiscovery_SaysSo()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var old = new IqrSourceSnapshot { IntegrationId = IqrSourceStore.SourceAnalysisOwner, AnalyzedAt = DateTimeOffset.UtcNow };
        db.IqrSourceSnapshots.Add(new BirkNext.Api.Models.IqrSourceSnapshotRecord { Id = old.Id, EnvironmentId = "dev", IntegrationId = IqrSourceStore.SourceAnalysisOwner, AnalyzedAt = old.AnalyzedAt,
            EvidenceJson = JsonSerializer.Serialize(old, new JsonSerializerOptions(JsonSerializerDefaults.Web)) });
        await db.SaveChangesAsync();
        var service = new TestExecutionImportService([Trx], new IqrSourceStore(db));
        var preview = await service.PreviewAsync("a.trx", Encoding.UTF8.GetBytes(F.MtpTrx()), new("dev", old.Id, true, null, null, null));
        preview.Limitations.Should().Contain(l => l.Contains("before source test discovery existed"));
        preview.SourceBinding.Should().Be(TestSourceBinding.Unknown);
        (await service.SourceTestsAsync("dev", old.Id)).Error.Should().Contain("before source test discovery");
    }

    [Fact]
    public async Task Controller_PreviewsOneMultipartTrxFile_AndListsProviders()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var store = new IqrSourceStore(db);
        var (snapshot, _) = await store.AnalyzeAsync("dev", IqrSourceStore.SourceAnalysisOwner, "contoso.zip", IqrSourceEvidenceTests.Zip(F.Solution()));
        var controller = new BirkNext.Api.Controllers.TestEvidenceController(new TestExecutionImportService([Trx], store), TestEvidenceOptions.Default);
        var bytes = Encoding.UTF8.GetBytes(F.MtpTrx());
        var form = new Microsoft.AspNetCore.Http.FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            ["environmentId"] = "dev", ["sourceSnapshotId"] = snapshot!.Id.ToString(), ["sourceBindingConfirmed"] = "true", ["buildReference"] = "20260930.4",
        }, new Microsoft.AspNetCore.Http.FormFileCollection { new Microsoft.AspNetCore.Http.FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "Unit-test-results.trx") });
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        http.Request.ContentType = "multipart/form-data; boundary=x";
        http.Request.Form = form;
        controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = http };

        var result = (await controller.Preview(default)).Result.Should().BeOfType<Microsoft.AspNetCore.Mvc.OkObjectResult>().Subject.Value.Should().BeOfType<TestResultArtifactPreview>().Subject;
        result.SourceBinding.Should().Be(TestSourceBinding.Provided);
        result.BuildReference.Should().Be("20260930.4");
        result.Executions.Should().HaveCount(9).And.OnlyContain(e => e.Correlation.State == TestCorrelationState.Confirmed);
        controller.Providers().Result.Should().BeOfType<Microsoft.AspNetCore.Mvc.OkObjectResult>();
        (await controller.SourceTests("dev", snapshot.Id, default)).Result.Should().BeOfType<Microsoft.AspNetCore.Mvc.OkObjectResult>();
        (await controller.SourceTests("dev", Guid.NewGuid(), default)).Result.Should().BeOfType<Microsoft.AspNetCore.Mvc.NotFoundObjectResult>();
    }

    // ── Registry / technology coverage ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Registry_StatesSupportPerDimension_WithoutOverclaiming()
    {
        var trx = TestEvidenceProviderRegistry.Find(TestEvidenceProviderIds.Trx)!;
        trx.Capabilities.ToDictionary(c => c.Dimension, c => c.Level).Should().BeEquivalentTo(new Dictionary<string, string>
        { ["ArtifactImport"] = "Full", ["SourceCorrelation"] = "Partial", ["RequirementCorrelation"] = "Partial", ["LiveRetrieval"] = "Unsupported", ["PipelineRetrieval"] = "Planned" });
        TestEvidenceProviderRegistry.Find(TestEvidenceProviderIds.DotNetXunitDiscovery).Should().NotBeNull();
        TestEvidenceProviderRegistry.Providers.Select(p => p.ProviderId).Should().BeEquivalentTo("test.execution.trx", "test.discovery.dotnet.xunit");

        TechnologySupportRegistry.Find("test.trx")!.ResultImport.Should().Be(SupportLevel.Full);
        TechnologySupportRegistry.Find("test.trx")!.Overall.Should().Be(SupportLevel.Full);
        TechnologySupportRegistry.Find("test.xunit")!.SourceAnalysis.Should().Be(SupportLevel.Partial);
        TechnologySupportRegistry.Find("test.xunit")!.ResultImport.Should().Be(SupportLevel.Unsupported, "xUnit itself has no result format; its results come through TRX");
        foreach (var id in new[] { "test.nunit", "test.mstest", "test.playwright", "test.junit" })
            TechnologySupportRegistry.Find(id)!.Overall.Should().Be(SupportLevel.Unsupported, id);
        TechnologySupportRegistry.Matrix(TechnologyArea.Testing).Should().NotBeEmpty();
        TechnologySupportRegistry.Label(SupportDimension.ResultImport).Should().Be("Result import");
        TechnologySupportRegistry.Find("lang.csharp")!.ResultImport.Should().Be(SupportLevel.NotApplicable, "existing rows are unchanged");
    }

    [Fact]
    public void TechnologyInventory_DetectsTestFrameworksAndTrxConfiguration()
    {
        var coverage = TechnologyInventory.Detect(Workspace(F.Solution()));
        var ids = coverage.Technologies.Select(t => t.TechnologyId).ToList();
        ids.Should().Contain(["test.xunit", "test.nunit", "test.trx"]);
        ids.Should().NotContain("test.playwright");
        coverage.Limitations.Should().Contain(l => l.Contains("NUnit"));
    }

    // ── Real pilots (run only where the developer's archives exist) ───────────────────────────────────────────────────

    private static readonly string Downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    [Fact]
    public void RealM2lb_DiscoversItsXunitV3TestEstate_WithTheGenericProvider()
    {
        var path = Path.Combine(Downloads, "M2LB (2).zip");
        if (!File.Exists(path)) return;
        var (workspace, error) = IqrSourceArchiveReader.Read("M2LB (2).zip", File.ReadAllBytes(path));
        error.Should().BeNull();
        var inventory = DotNetXunitTestDiscoveryProvider.Discover(Guid.NewGuid(), workspace!.Archive.Sha256, "M2LB", workspace);

        inventory.Projects.Should().HaveCount(23);
        inventory.Projects.Should().OnlyContain(p => p.Framework == "xUnit" && p.FrameworkPackage == "xunit.v3" && p.TrxReportConfigured && p.DiscoverySupported);
        inventory.Projects.Single(p => p.Name == "M2LB.Frontend.Tests").Kind.Should().Be(TestKind.FrontendComponent);
        inventory.Projects.Single(p => p.Name == "M2LB.Person.Contract.Tests").Kind.Should().Be(TestKind.Contract);
        inventory.Projects.Single(p => p.Name == "M2LB.Autorisasjon.ContractTests").Kind.Should().Be(TestKind.Contract);
        inventory.Projects.Single(p => p.Name == "M2LB.Person.Integration.Tests").Kind.Should().Be(TestKind.Integration);
        inventory.Projects.Single(p => p.Name == "M2LB.Hendelse.Unit").Kind.Should().Be(TestKind.Unit);
        inventory.Projects.Where(p => p.Packages.Any(x => x.StartsWith("Testcontainers"))).Should().NotBeEmpty();
        // Every [Fact]/[Theory] in the archive (967 at line start), including files with C# 12 collection expressions the bundled parser predates.
        inventory.Definitions.Should().HaveCount(967);
        inventory.Definitions.Count(d => d.DefinitionKind == TestDefinitionKind.Theory).Should().BeGreaterThan(40);
        inventory.Definitions.Count(d => d.Project == "M2LB.Tjeneste.Unit").Should().Be(30);
        inventory.Projects.Where(p => p.Kind == TestKind.Unknown).Select(p => p.Name).Should().BeEquivalentTo(
            ["M2LB.CdcReplay.Tests", "M2LB.Person.Api.Tests", "M2LB.Person.Application.Tests", "M2LB.Person.Domain.Tests"], "a name that does not state the kind stays Unknown");
        inventory.Definitions.Select(d => d.TestDefinitionId).Should().OnlyHaveUniqueItems();
        inventory.TrxConfiguration.Should().Contain(e => e.Kind == "TrxGeneration").And.Contain(e => e.Kind == "TrxPublication");
        inventory.TrxConfiguration.Where(e => e.Kind == "TrxPublication" && e.File.EndsWith(".pipeline/runtests.yml")).Should().HaveCount(10)
            .And.OnlyContain(e => e.Detail.Contains("publishes TRX"));
        inventory.Projects.Should().NotContain(p => p.Packages.Any(x => x.Contains("Playwright")), "no Playwright in this source");

        var references = inventory.Definitions.SelectMany(d => d.References.Select(r => (d, r))).ToList();
        references.Should().Contain(x => x.r.Id == "FR-023" && x.r.Confidence == TestEvidenceConfidence.StronglySupported && x.d.ClassName.EndsWith("InnmatingServiceTests"));
        references.Should().Contain(x => x.r.Id == "FR-026" && x.d.Project == "M2LB.Person.Contract.Tests");
        references.Select(x => x.r.Id).Should().Contain(["FR-010", "FR-012", "FR-025", "FR-027", "FR-029"]);
        references.Should().NotContain(x => x.r.Confidence == TestEvidenceConfidence.Confirmed, "the source declares no requirement traits");
        references.Should().Contain(x => x.r.Id == "AC-003" && x.r.Confidence == TestEvidenceConfidence.StronglySupported && x.r.Kind == TestReferenceKind.AcceptanceCriterion);
        references.Where(x => x.r.Id == "FR-012").Should().OnlyContain(x => x.r.Confidence == TestEvidenceConfidence.Inferred, "FR-012 appears only in class and prose comments");
        inventory.Definitions.SelectMany(d => d.Categories).Distinct().Should().Contain(["Unit", "Security", "Contract"]);
    }

    [Fact]
    public void RealM2lbCommon_UsesTheSameProviderWithNoSpecialCase()
    {
        var path = Path.Combine(Downloads, "M2LB.Common (1).zip");
        if (!File.Exists(path)) return;
        var (workspace, error) = IqrSourceArchiveReader.Read("M2LB.Common (1).zip", File.ReadAllBytes(path));
        error.Should().BeNull();
        var inventory = DotNetXunitTestDiscoveryProvider.Discover(Guid.NewGuid(), workspace!.Archive.Sha256, "M2LB.Common", workspace);
        inventory.Projects.Should().ContainSingle(p => p.Name == "M2LB.Common.Tests" && p.Framework == "xUnit" && p.TrxReportConfigured);
        inventory.Definitions.Should().HaveCount(113);
        inventory.TrxConfiguration.Should().Contain(e => e.Kind == "TrxGeneration" && e.File.EndsWith(".pipeline/runtests.yml"))
            .And.Contain(e => e.Kind == "TrxPublication" && e.Detail.Contains("publishes TRX"));
        inventory.Definitions.SelectMany(d => d.References).Should().BeEmpty("M2LB.Common tests declare no requirement identifiers");
        inventory.Definitions.Should().OnlyContain(d => d.StableIdentity.StartsWith("M2LB.Common::"));
        // Partial only because two files use syntax newer than the bundled parser; their tests are still all discovered.
        inventory.Status.Should().Be(SourceTestDiscoveryStatus.Partial);
        inventory.Limitations.Where(l => l.StartsWith("Partial syntax analysis")).Should().HaveCount(2);
    }

    private static (TestEvidenceConfidence, string) Ref(SourceTestInventory inventory, string method, string id)
    {
        var reference = Def(inventory, method).References.Single(r => r.Id == id);
        return (reference.Confidence, reference.Basis);
    }
}
