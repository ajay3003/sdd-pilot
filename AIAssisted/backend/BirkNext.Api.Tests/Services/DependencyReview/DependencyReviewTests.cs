using System.IO.Compression;
using System.Text;
using BirkNext.Api.Data;
using BirkNext.Api.Services.DependencyReview;
using BirkNext.Dependencies;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Tests.Services.DependencyReview;

/// <summary>
/// Renovate / dependency policy review: config discovery in Renovate's order, declared-dependency inventory (central package management owned by
/// Directory.Packages.props), packageRule precedence with explanations, synthetic patch/minor/major simulation that never becomes "latest",
/// distinct outcomes (ignored ≠ up to date, deferred ≠ blocked), conservative heuristics, redaction, immutable snapshots and read-only behaviour.
/// </summary>
public sealed class DependencyReviewTests
{
    private static RepositoryFile F(string path, string content) => new(path, content);

    private static string Csproj(params (string Id, string? Version)[] refs) =>
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n" + string.Join("\n", refs.Select(r => r.Version is null ? $"    <PackageReference Include=\"{r.Id}\" />" : $"    <PackageReference Include=\"{r.Id}\" Version=\"{r.Version}\" />")) + "\n  </ItemGroup>\n</Project>";

    private static RepositoryDependencyReview Review(string? renovate, params RepositoryFile[] files) =>
        DependencyReviewBuilder.Build(new RepositoryInput("Repo", "sha", renovate is null ? files : [F("renovate.json", renovate), .. files]), null);

    private static PolicySimulation Sim(RepositoryDependencyReview review, string package, string scenario) =>
        review.Simulations.Single(s => s.PackageName == package && s.Scenario == scenario);

    private static readonly RepositoryFile Api = F("Api/Api.csproj", Csproj(("Serilog", "3.1.1"), ("Polly", "8.2.0")));

    // ── Discovery and validity ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ConfigDiscoveryFollowsRenovateFileOrder()
    {
        var review = DependencyReviewBuilder.Build(new RepositoryInput("Repo", "sha", [F(".github/renovate.json", "{\"automerge\":false}"), F("renovate.json", "{}"), Api]), null);

        review.ConfigFiles.Should().HaveCount(2);
        review.ConfigFiles.Single(f => f.Used).Path.Should().Be("renovate.json");
        review.ConfigFiles.Single(f => !f.Used).Note.Should().Contain("earlier file");
        review.Coverage.Should().Be(RenovateCoverage.Configured);
    }

    [Fact]
    public void NestedRenovateJsonIsNotTheRepositoryConfig()
    {
        var review = Review(null, F("Tools/renovate.json", "{}"), Api);

        review.Coverage.Should().Be(RenovateCoverage.Missing);
        review.ConfigFiles.Single().Note.Should().Contain("Not a repository-root config location");
    }

    [Fact]
    public void MissingConfigInDependencyBearingRepositoryIsMissingNotFailure()
    {
        var review = Review(null, Api);

        review.Coverage.Should().Be(RenovateCoverage.Missing);
        review.Findings.Should().ContainSingle(f => f.RuleId == "renovate-missing").Which.Detail.Should().Contain("not a runtime failure");
        review.Dependencies.Should().OnlyContain(d => d.IgnoredBy == "No Renovate configuration in this repository.");
        review.RuntimeAutomation.State.Should().Be(ReviewCategoryState.NotAssessed);
    }

    [Fact]
    public void InvalidJsonIsReportedAndPolicyNotAssessed()
    {
        var review = Review("{ \"automerge\": tru }", Api);

        review.Coverage.Should().Be(RenovateCoverage.Partial);
        review.ConfigSyntaxValid.Should().BeFalse();
        review.Findings.Should().ContainSingle(f => f.RuleId == "config-invalid");
        review.Simulations.Should().BeEmpty();
    }

    [Fact]
    public void Json5BeyondCommentsIsUnsupportedSyntaxNotInvalid()
    {
        var review = DependencyReviewBuilder.Build(new RepositoryInput("Repo", "sha", [F("renovate.json5", "{ automerge: false }"), Api]), null);

        review.Findings.Should().ContainSingle(f => f.RuleId == "config-syntax-unsupported").Which.Severity.Should().Be(DependencyFindingSeverity.Info);
    }

    [Fact]
    public void CommentsAndTrailingCommasParse()
    {
        var review = Review("{ // comment\n \"automerge\": false, }", Api);

        review.ConfigSyntaxValid.Should().BeTrue();
    }

    [Fact]
    public void UnknownKeysAreListedAsNotEvaluatedNotInvalid()
    {
        var review = Review("{\"someFutureOption\": 1, \"packageRules\":[{\"matchManagers\":[\"nuget\"],\"postUpgradeTasks\":{}}]}", Api);

        review.UnrecognizedKeys.Should().Contain(["someFutureOption", "packageRules[0].postUpgradeTasks"]);
        review.ConfigSyntaxValid.Should().BeTrue();
    }

    [Fact]
    public void SuppliedConfigReplacesInRepositoryConfig()
    {
        var input = new RepositoryInput("Repo", "sha", [F("renovate.json", "{\"automerge\":true}"), Api], ("renovate.json", "{\"automerge\":false}"));
        var review = DependencyReviewBuilder.Build(input, null);

        review.ConfigFiles.Single(f => f.Used).Path.Should().StartWith("(supplied)");
        review.ConfigFiles.Single(f => f.Path == "renovate.json").Note.Should().Contain("Replaced");
        Sim(review, "Serilog", "Patch").Effective.Automerge.Should().BeFalse();
    }

    [Fact]
    public void ExternalPresetIsInheritedAndBuiltInPresetMakesSimulationPartial()
    {
        var inherited = Review("{\"extends\":[\"local>org/renovate-config\"]}", Api);
        var builtIn = Review("{\"extends\":[\"config:recommended\"]}", Api);

        inherited.Coverage.Should().Be(RenovateCoverage.Inherited);
        builtIn.Coverage.Should().Be(RenovateCoverage.Configured);
        Sim(builtIn, "Serilog", "Minor").Limitations.Should().ContainSingle(l => l.Contains("config:recommended") && l.Contains("not expanded"));
        builtIn.Findings.Should().Contain(f => f.RuleId == "preset-unresolved");
    }

    // ── Inventory ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CentralPackageManagementIsOwnedByDirectoryPackagesPropsOnce()
    {
        var props = F("Directory.Packages.props", "<Project>\n  <ItemGroup>\n    <PackageVersion Include=\"Serilog\" Version=\"3.1.1\" />\n  </ItemGroup>\n</Project>");
        var review = Review("{}", props, F("A/A.csproj", Csproj(("Serilog", null))), F("B/B.csproj", Csproj(("Serilog", null))));

        var serilog = review.Dependencies.Should().ContainSingle(d => d.PackageName == "Serilog").Subject;
        serilog.OwnerFile.Should().Be("Directory.Packages.props");
        serilog.Line.Should().Be(3);
        serilog.ReferencedBy.Should().BeEquivalentTo(["A/A.csproj", "B/B.csproj"]);
    }

    [Fact]
    public void VersionOverrideIsOwnedByTheProject()
    {
        var props = F("Directory.Packages.props", "<Project><ItemGroup><PackageVersion Include=\"Serilog\" Version=\"3.1.1\" /></ItemGroup></Project>");
        var project = F("A/A.csproj", "<Project><ItemGroup><PackageReference Include=\"Serilog\" VersionOverride=\"4.0.0\" /></ItemGroup></Project>");
        var review = Review("{}", props, project);

        review.Dependencies.Should().Contain(d => d.OwnerFile == "A/A.csproj" && d.CurrentValue == "4.0.0");
        review.Dependencies.Should().Contain(d => d.OwnerFile == "Directory.Packages.props" && d.CurrentValue == "3.1.1");
    }

    [Fact]
    public void DockerfileSkipsBuildStagesScratchAndArgImages()
    {
        var dockerfile = F("Api/Dockerfile", "ARG BASE=x\nFROM mcr.microsoft.com/dotnet/sdk:8.0 AS build\nFROM build AS publish\nFROM $BASE\nFROM scratch\nFROM mcr.microsoft.com/dotnet/aspnet:8.0.4-alpine AS final\n");
        var review = Review("{}", dockerfile);

        review.Dependencies.Select(d => $"{d.PackageName}:{d.CurrentValue}").Should().BeEquivalentTo(["mcr.microsoft.com/dotnet/sdk:8.0", "mcr.microsoft.com/dotnet/aspnet:8.0.4-alpine"]);
    }

    [Fact]
    public void DockerTagSuffixIsKeptInSyntheticCandidates()
    {
        var review = Review("{}", F("Api/Dockerfile", "FROM mcr.microsoft.com/dotnet/aspnet:8.0.4-alpine\n"));

        Sim(review, "mcr.microsoft.com/dotnet/aspnet", "Patch").CandidateVersion.Should().Be("8.0.5-alpine");
        Sim(review, "mcr.microsoft.com/dotnet/aspnet", "Major").CandidateVersion.Should().Be("9.0.0-alpine");
    }

    [Fact]
    public void AzurePipelinesManagerIsDisabledByDefault()
    {
        var review = Review("{}", F(".pipeline/build.yml", "container: mcr.microsoft.com/dotnet/sdk:8.0\n"));

        review.Managers.Single(m => m.Manager == "azure-pipelines").State.Should().Be(ManagerState.DisabledByDefault);
        review.Dependencies.Single().IgnoredBy.Should().Contain("disabled by Renovate's default");
        review.Findings.Should().Contain(f => f.RuleId == "manager-not-processed");
    }

    [Fact]
    public void EnabledManagersRestrictsManagers()
    {
        var review = Review("{\"enabledManagers\":[\"dockerfile\"]}", Api);

        review.Managers.Single(m => m.Manager == "nuget").State.Should().Be(ManagerState.DisabledByConfig);
    }

    // ── Outcomes ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void IgnorePathsIsIgnoredNotUpToDate()
    {
        var review = Review("{\"ignorePaths\":[\"Api/**\"]}", Api);

        var sim = review.Simulations.First(s => s.PackageName == "Serilog");
        sim.Result.Should().Be(PolicyResult.Ignored);
        sim.Explanation.Should().Contain("not \"up to date\"");
        review.Managers.Single().State.Should().Be(ManagerState.IgnoredByPaths);
    }

    [Fact]
    public void IgnoreDepsIsIgnored()
    {
        var review = Review("{\"ignoreDeps\":[\"Serilog\"]}", Api);

        review.Dependencies.Single(d => d.PackageName == "Serilog").IgnoredBy.Should().Contain("ignoreDeps");
        review.Dependencies.Single(d => d.PackageName == "Polly").IgnoredBy.Should().BeNull();
    }

    [Fact]
    public void EnabledFalseIsBlockedWithRuleExplanation()
    {
        var review = Review("{\"packageRules\":[{\"description\":\"No majors\",\"matchUpdateTypes\":[\"major\"],\"enabled\":false}]}", Api);

        var major = Sim(review, "Serilog", "Major");
        major.Result.Should().Be(PolicyResult.Blocked);
        major.MatchedRules.Should().ContainSingle().Which.RuleIndex.Should().Be(1);
        major.Explanation.Should().Contain("disabled by policy");
        Sim(review, "Serilog", "Minor").Result.Should().Be(PolicyResult.Allowed);
    }

    [Fact]
    public void LaterMatchingRuleOverridesEarlierSetting()
    {
        var review = Review("{\"automerge\":true,\"packageRules\":[{\"matchManagers\":[\"nuget\"],\"automerge\":true},{\"matchUpdateTypes\":[\"major\"],\"automerge\":false}]}", Api);

        var major = Sim(review, "Serilog", "Major");
        major.Effective.Automerge.Should().BeFalse();
        major.Effective.Sources["automerge"].Should().StartWith("rule #2");
        major.MatchedRules.Select(m => m.RuleIndex).Should().Equal(1, 2);
        Sim(review, "Serilog", "Patch").Effective.Sources["automerge"].Should().StartWith("rule #1");
    }

    [Fact]
    public void LabelsReplaceAndAddLabelsAppend()
    {
        var review = Review("{\"labels\":[\"deps\"],\"packageRules\":[{\"matchManagers\":[\"nuget\"],\"labels\":[\"nuget\"]},{\"matchUpdateTypes\":[\"major\"],\"addLabels\":[\"breaking\"]}]}", Api);

        Sim(review, "Serilog", "Major").Effective.Labels.Should().Equal("nuget", "breaking");
        Sim(review, "Serilog", "Patch").Effective.Labels.Should().Equal("nuget");
    }

    [Fact]
    public void AllowedVersionsBlocksByVersionConstraint()
    {
        var interval = Review("{\"packageRules\":[{\"matchPackageNames\":[\"Serilog\"],\"allowedVersions\":\"[3.0,4.0)\"}]}", Api);
        var regex = Review("{\"packageRules\":[{\"matchPackageNames\":[\"Serilog\"],\"allowedVersions\":\"/^3\\\\./\"}]}", Api);

        Sim(interval, "Serilog", "Major").Result.Should().Be(PolicyResult.BlockedByVersionConstraint);
        Sim(interval, "Serilog", "Minor").Result.Should().Be(PolicyResult.Allowed);
        Sim(regex, "Serilog", "Major").Result.Should().Be(PolicyResult.BlockedByVersionConstraint);
    }

    [Fact]
    public void DashboardApprovalRequiresApproval()
    {
        var review = Review("{\"packageRules\":[{\"matchUpdateTypes\":[\"major\"],\"dependencyDashboardApproval\":true}]}", Api);

        Sim(review, "Serilog", "Major").Result.Should().Be(PolicyResult.RequiresApproval);
    }

    [Fact]
    public void ScheduleIsDeferredNotBlocked()
    {
        var review = Review("{\"schedule\":[\"before 6am on monday\"]}", Api);

        var sim = Sim(review, "Serilog", "Minor");
        sim.Result.Should().Be(PolicyResult.DeferredBySchedule);
        sim.Explanation.Should().Contain("Allowed by policy");
        DependencyLabels.Result(sim.Result).Should().Be("Deferred by schedule");
    }

    [Fact]
    public void PrereleaseIsNotProposedUnderDefaultIgnoreUnstable()
    {
        var byDefault = Review("{}", Api);
        var optedIn = Review("{\"ignoreUnstable\":false}", Api);

        Sim(byDefault, "Serilog", "Prerelease major").Result.Should().Be(PolicyResult.NotProposed);
        Sim(optedIn, "Serilog", "Prerelease major").Result.Should().Be(PolicyResult.Allowed);
    }

    [Theory]
    [InlineData("0.9.1", "0.x")]
    [InlineData("[1.0,2.0)", "range")]
    [InlineData("1.2.3.4", null)]
    public void UnsupportedVersionShapesAreNotAssessable(string version, string? reasonPart)
    {
        var review = Review("{}", F("Api/Api.csproj", Csproj(("Pkg", version))));

        var patchOrMinor = review.Simulations.Where(s => s.PackageName == "Pkg" && s.Scenario is "Minor" or "Major").ToList();
        patchOrMinor.Should().OnlyContain(s => s.Result == PolicyResult.NotAssessable || s.UpdateType != DependencyUpdateType.NotAssessable);
        if (reasonPart is not null) patchOrMinor.Should().OnlyContain(s => s.Result == PolicyResult.NotAssessable && s.Explanation.Contains(reasonPart));
        var evaluator = new RenovatePolicyEvaluator([], []);
        RenovatePolicyEvaluator.UpdateType("1.2.3.4", "1.2.3.5", "nuget").Type.Should().Be(DependencyUpdateType.NotAssessable);
        evaluator.RuleCount.Should().Be(0);
    }

    [Fact]
    public void UnsupportedMatcherMakesResultNotAssessable()
    {
        var review = Review("{\"packageRules\":[{\"matchCurrentAge\":\"> 1 year\",\"automerge\":false}]}", Api);

        var sim = Sim(review, "Serilog", "Minor");
        sim.Result.Should().Be(PolicyResult.NotAssessable);
        sim.Limitations.Should().Contain(l => l.Contains("matchCurrentAge"));
        review.Rules.Single().UnsupportedMatchers.Should().Equal("matchCurrentAge");
    }

    [Fact]
    public void NameListMatchingSupportsGlobRegexAndNegation()
    {
        RenovatePolicyEvaluator.NameListMatches("Microsoft.Extensions.Http", ["Microsoft.*"]).Should().BeTrue();
        RenovatePolicyEvaluator.NameListMatches("Microsoft.Extensions.Http", ["/^microsoft\\./i"]).Should().BeTrue();
        RenovatePolicyEvaluator.NameListMatches("Microsoft.Extensions.Http", ["Microsoft.*", "!Microsoft.Extensions.*"]).Should().BeFalse();
        RenovatePolicyEvaluator.NameListMatches("Serilog", ["!Microsoft.*"]).Should().BeTrue();
        RenovatePolicyEvaluator.PathMatches("Api/Api.csproj", "Api/**").Should().BeTrue();
        RenovatePolicyEvaluator.PathMatches("ApiTests/ApiTests.csproj", "Api/**").Should().BeFalse();
    }

    [Fact]
    public void FileScopedRulesOnlyMatchTheirFolder()
    {
        var other = F("Worker/Worker.csproj", Csproj(("Serilog", "3.1.1")));
        var review = Review("{\"packageRules\":[{\"matchFileNames\":[\"Api/**\"],\"groupName\":\"Api\"}]}", Api, other);

        review.Simulations.Where(s => s.OwnerFile == "Api/Api.csproj").Should().OnlyContain(s => s.Effective.GroupName == "Api" || s.Result != PolicyResult.Allowed);
        review.Rules.Single().MatchedDependencies.Should().Be(2);
    }

    // ── Synthetic candidates ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SyntheticCandidatesAreLabelledAndNeverLatest()
    {
        var review = Review("{}", Api);

        Sim(review, "Serilog", "Patch").CandidateVersion.Should().Be("3.1.2");
        Sim(review, "Serilog", "Minor").CandidateVersion.Should().Be("3.2.0");
        Sim(review, "Serilog", "Major").CandidateVersion.Should().Be("4.0.0");
        review.Simulations.Should().OnlyContain(s => s.IsSyntheticCandidate);
        review.Simulations.Should().NotContain(s => s.Explanation.Contains("latest", StringComparison.OrdinalIgnoreCase)
            || s.Explanation.Contains("available", StringComparison.OrdinalIgnoreCase) || s.Explanation.Contains("published", StringComparison.OrdinalIgnoreCase));
        DependencyLabels.SyntheticCandidate.Should().Be("Synthetic candidate");
    }

    // ── Heuristics ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RuleTextNamingAnotherFolderNeedsReview()
    {
        var aspire = F("Aspire/Aspire.csproj", Csproj(("Aspire.Hosting", "8.0.0")));
        var config = "{\"packageRules\":[{\"description\":\"Aspire MAJOR\",\"matchFileNames\":[\"Aspire/**\"],\"matchUpdateTypes\":[\"major\"]},"
            + "{\"description\":\"CdcReplay MAJOR\",\"matchFileNames\":[\"CdcReplay/**\"],\"matchUpdateTypes\":[\"major\"],\"prBodyNotes\":[\"Aspire-pakker kan ha store breaking changes\"]}]}";
        var review = Review(config, aspire);

        var finding = review.Findings.Should().ContainSingle(f => f.RuleId == "rule-scope-inconsistent").Subject;
        finding.RuleIndex.Should().Be(2);
        finding.Severity.Should().Be(DependencyFindingSeverity.NeedsReview);
        finding.Detail.Should().Contain("CdcReplay").And.Contain("Aspire").And.NotContainAny("intended", "intent");
        review.Findings.Should().Contain(f => f.RuleId == "rule-matches-nothing" && f.RuleIndex == 2);
    }

    [Fact]
    public void WholeWordScopeMatchingAvoidsPrefixFalsePositives()
    {
        var files = new[] { F("Hendelse/H.csproj", Csproj(("A", "1.0.0"))), F("HendelseAdapter/HA.csproj", Csproj(("B", "1.0.0"))) };
        var review = Review("{\"packageRules\":[{\"description\":\"HendelseAdapter minor\",\"matchFileNames\":[\"HendelseAdapter/**\"],\"matchUpdateTypes\":[\"minor\",\"patch\"]}]}", files);

        review.Findings.Should().NotContain(f => f.RuleId == "rule-scope-inconsistent");
    }

    [Fact]
    public void MajorWordingWithoutMajorOnlyMatcherNeedsReview()
    {
        var review = Review("{\"packageRules\":[{\"description\":\"Disable major\",\"matchManagers\":[\"nuget\"],\"enabled\":false}]}", Api);

        review.Findings.Should().Contain(f => f.RuleId == "rule-update-type-broad" && f.Detail.Contains("may disable more than major"));
        review.Findings.Should().Contain(f => f.RuleId == "rule-broad-disable");
    }

    [Fact]
    public void MajorAutomergeNeedsReview()
    {
        var review = Review("{\"automerge\":true}", Api);

        review.Findings.Should().ContainSingle(f => f.RuleId == "major-automerge").Which.Evidence.Should().Contain(e => e.Contains("synthetic major"));
        Review("{\"automerge\":true,\"packageRules\":[{\"matchUpdateTypes\":[\"major\"],\"automerge\":false}]}", Api).Findings.Should().NotContain(f => f.RuleId == "major-automerge");
    }

    [Fact]
    public void DuplicateMatchersWithConflictingOutcomesNeedReview()
    {
        var review = Review("{\"packageRules\":[{\"matchManagers\":[\"nuget\"],\"automerge\":true},{\"matchManagers\":[\"nuget\"],\"automerge\":false}]}", Api);

        review.Findings.Should().Contain(f => f.RuleId == "rule-duplicate-conflict");
    }

    // ── Security, runtime, redaction ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SecurityPolicyIsNotInferredAndNoVulnerabilityClaim()
    {
        var none = Review("{\"extends\":[\"config:recommended\"]}", Api);
        var configured = Review("{\"vulnerabilityAlerts\":{\"labels\":[\"security\"]}}", Api);

        none.SecurityUpdatePolicy.State.Should().Be(ReviewCategoryState.NotConfigured);
        none.SecurityUpdatePolicy.Detail.Should().Contain("no vulnerability claim");
        configured.SecurityUpdatePolicy.State.Should().Be(ReviewCategoryState.Partial);
        none.Findings.Should().NotContain(f => f.Title.Contains("vulnerab", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RuntimeAutomationIsEvidenceOnlyAndRedacted()
    {
        var pipeline = F("renovate-pipeline.yml", "schedules:\n- cron: \"0 3 * * 1\"\n  displayName: Weekly Renovate run\nsteps:\n- script: docker run renovate/renovate:44\n  env:\n    RENOVATE_TOKEN: $(System.AccessToken)\n    RENOVATE_HOST_RULES: '[{\"hostType\":\"nuget\",\"matchHost\":\"pkgs.dev.azure.com\",\"password\":\"$(System.AccessToken)\"}]'\n");
        var review = Review("{}", Api, pipeline);

        review.RuntimeAutomation.State.Should().Be(ReviewCategoryState.NotAssessed);
        review.AutomationEvidence.Should().ContainSingle().Which.Should().Contain("renovate/renovate:44").And.Contain("0 3 * * 1").And.Contain("credentials redacted").And.NotContain("AccessToken");
        review.RuntimeAutomation.Detail.Should().Contain("not assessed");
    }

    [Fact]
    public void SecretsAndHostRulesAreRedactedEverywhere()
    {
        var review = Review("{\"hostRules\":[{\"hostType\":\"nuget\",\"matchHost\":\"pkgs.dev.azure.com\",\"username\":\"svc\",\"password\":\"hunter2\"}],\"npmToken\":\"abc\",\"encrypted\":{\"x\":\"y\"},\"prBodyNotes\":[\"Bearer eyJhbGci\"]}", Api);

        var everything = System.Text.Json.JsonSerializer.Serialize(review);
        everything.Should().NotContainAny("hunter2", "svc", "\"abc\"", "eyJhbGci");
        review.NormalizedConfig.Should().Contain("pkgs.dev.azure.com").And.Contain(RenovateConfig.Redacted);
        review.RepositorySettings["hostRules"].Should().NotContain("username");
    }

    // ── Service: snapshots, drift, simulation, read-only ───────────────────────────────────────────────────────────

    private static byte[] Zip(params (string Path, string Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, content) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open(), Encoding.UTF8);
                writer.Write(content);
            }
        return stream.ToArray();
    }

    private static DependencyReviewService Service(AppDbContext db) => new(db, NullLogger<DependencyReviewService>.Instance);

    private static AppDbContext Db() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task RunIsStoredAsImmutableSnapshotWithDriftOnChange()
    {
        await using var db = Db();
        var service = Service(db);
        var v1 = Zip(("M2LB/renovate.json", "{\"packageRules\":[{\"description\":\"A\",\"matchManagers\":[\"nuget\"]}]}"), ("M2LB/Api/Api.csproj", Csproj(("Serilog", "3.1.1"))));
        var v2 = Zip(("renovate.json", "{\"packageRules\":[{\"description\":\"B\",\"matchManagers\":[\"nuget\"]}]}"), ("Api/Api.csproj", Csproj(("Serilog", "3.1.1"))));

        var (first, error) = await service.RunAsync("", [("M2LB (1).zip", v1)], []);
        error.Should().BeNull();
        var (second, _) = await service.RunAsync("second", [("M2LB.zip", v2)], []);

        first!.Repositories.Single().Repository.Should().Be("M2LB");
        second!.Repositories.Single().DriftSincePrevious.Should().Contain("Changed").And.Contain("added B").And.Contain("removed A");
        (await service.HistoryAsync()).Select(h => h.Label).Should().Equal("second", "M2LB");
        (await service.GetAsync(first.RunId))!.Repositories.Single().ConfigHash.Should().Be(first.Repositories.Single().ConfigHash);
        db.DependencyReviewRuns.Should().HaveCount(2);
    }

    [Fact]
    public async Task ArchiveWithSingleTopFolderIsReadAtItsRoot()
    {
        await using var db = Db();
        var (result, _) = await Service(db).RunAsync("", [("Repo.zip", Zip(("Repo/renovate.json", "{}"), ("Repo/Api/Api.csproj", Csproj(("Serilog", "3.1.1")))))], []);

        result!.Repositories.Single().Coverage.Should().Be(RenovateCoverage.Configured);
        result.Repositories.Single().Dependencies.Single().OwnerFile.Should().Be("Api/Api.csproj");
    }

    [Fact]
    public async Task SingleSimulationUsesTheStoredSnapshotAndIsSynthetic()
    {
        await using var db = Db();
        var service = Service(db);
        var (run, _) = await service.RunAsync("", [("Repo.zip", Zip(("renovate.json", "{\"packageRules\":[{\"matchPackageNames\":[\"Serilog\"],\"matchUpdateTypes\":[\"major\"],\"enabled\":false}]}"), ("Api/Api.csproj", Csproj(("Serilog", "3.1.1")))))], []);

        var (sim, error) = await service.SimulateAsync(new PolicySimulationRequest { RunId = run!.RunId, Repository = "Repo", PackageName = "Serilog", CurrentVersion = "3.1.1", CandidateVersion = "4.0.0" });

        error.Should().BeNull();
        sim!.IsSyntheticCandidate.Should().BeTrue();
        sim.Result.Should().Be(PolicyResult.Blocked);
        sim.OwnerFile.Should().Be("Api/Api.csproj");
        (await service.SimulateAsync(new PolicySimulationRequest { RunId = Guid.NewGuid(), Repository = "Repo", PackageName = "x", CurrentVersion = "1", CandidateVersion = "2" })).Error.Should().Contain("not found");
    }

    [Fact]
    public async Task ReviewDoesNotMutateInputsOrReachTheNetwork()
    {
        await using var db = Db();
        var archive = Zip(("renovate.json", "{}"), ("Api/Api.csproj", Csproj(("Serilog", "3.1.1"))));
        var before = archive.ToArray();

        await Service(db).RunAsync("", [("Repo.zip", archive)], [("Repo", "renovate.json", "{}")]);

        archive.Should().Equal(before);
        typeof(DependencyReviewService).GetConstructors().Single().GetParameters().Select(p => p.ParameterType)
            .Should().NotContain(t => t == typeof(HttpClient) || t == typeof(IHttpClientFactory));
    }

    [Fact]
    public async Task InvalidArchiveIsRejected()
    {
        await using var db = Db();

        (await Service(db).RunAsync("", [("x.zip", Encoding.UTF8.GetBytes("not a zip"))], [])).Error.Should().Contain("not a valid zip");
        db.DependencyReviewRuns.Should().BeEmpty();
    }

    [Fact]
    public void CategoriesKeepCoverageMissingSeparateFromPolicy()
    {
        var configured = Review("{\"extends\":[\"config:recommended\"]}", Api) with { Repository = "M2LB" };
        var missing = Review(null, Api) with { Repository = "M2LB.Common" };

        var categories = DependencyReviewService.Categories([configured, missing]);

        categories.Single(c => c.Name == "Coverage").State.Should().Be(ReviewCategoryState.Partial);
        categories.Single(c => c.Name == "Coverage").Detail.Should().Contain("M2LB.Common");
        categories.Single(c => c.Name == "Runtime automation").State.Should().Be(ReviewCategoryState.NotAssessed);
    }

    // ── Real read-only acceptance (runs only where the developer's archives exist) ─────────────────────────────────

    private static readonly string Downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    [Fact]
    public async Task RealM2lbArchivesAcceptance()
    {
        var m2lb = Path.Combine(Downloads, "M2LB (1).zip");
        var common = Path.Combine(Downloads, "M2LB.Common.zip");
        var standalone = Path.Combine(Downloads, "renovate.json");
        if (!File.Exists(m2lb) || !File.Exists(common) || !File.Exists(standalone)) return;
        await using var db = Db();
        var (result, error) = await Service(db).RunAsync("acceptance", [("M2LB (1).zip", await File.ReadAllBytesAsync(m2lb)), ("M2LB.Common.zip", await File.ReadAllBytesAsync(common))],
            [("M2LB", "renovate.json", await File.ReadAllTextAsync(standalone))]);

        error.Should().BeNull();
        var repo = result!.Repositories.Single(r => r.Repository == "M2LB");
        repo.Coverage.Should().Be(RenovateCoverage.Configured);
        repo.Findings.Should().Contain(f => f.RuleId == "rule-scope-inconsistent" && f.Detail.Contains("CdcReplay") && f.Detail.Contains("Aspire"));
        repo.Findings.Should().Contain(f => f.RuleId == "major-automerge");
        repo.Managers.Single(m => m.Manager == "azure-pipelines").State.Should().Be(ManagerState.DisabledByDefault);
        repo.Managers.Single(m => m.Manager == "docker-compose").State.Should().Be(ManagerState.IgnoredByPaths);
        System.Text.Json.JsonSerializer.Serialize(result).Should().NotContain("System.AccessToken");
        var commonRepo = result.Repositories.Single(r => r.Repository == "M2LB.Common");
        commonRepo.Coverage.Should().Be(RenovateCoverage.Missing);
        commonRepo.Dependencies.Should().OnlyContain(d => d.OwnerFile.EndsWith("Directory.Packages.props") || d.OwnerFile.EndsWith(".csproj") || d.OwnerFile.EndsWith(".json"));
    }
}
