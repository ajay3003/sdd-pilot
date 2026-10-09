using System.Reflection;
using System.Text.Json;
using BirkNext.RealProjectAcceptance;
using BirkNext.RealProjectAcceptance.Datasets;
using FluentAssertions;

namespace BirkNext.Web.Tests.RealProjectAcceptance;

/// <summary>
/// The dataset-agnostic acceptance framework: descriptors, preparation (skip / hash mismatch), the runner's status semantics and
/// truthfulness rules, the report writer, and the boundary that keeps acceptance (and any dataset) out of production code.
/// </summary>
public sealed class RealProjectAcceptanceFrameworkTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("rpa-tests-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    /// <summary>A second, synthetic dataset: proves a new project needs only a provider, not framework changes.</summary>
    private sealed class SyntheticProvider(string id = "SYNTHETIC", string? sha = null) : IRealProjectDatasetProvider
    {
        public string DatasetId => id;
        public string ArchiveEnvironmentVariable => $"BIRKNEXT_REAL_PROJECT_{id}";
        public RealProjectDataset Describe(string? archivePath) => new()
        {
            DatasetId = id, DisplayName = "Synthetic Python service", ArchivePath = archivePath, ExpectedSha256 = sha,
            ExpectedTechnologyHints = ["Python"],
            Expectations = new Dictionary<string, FeatureExpectation>
            {
                ["source"] = new() { Presence = ExpectedDataPresence.RealData, MustMention = ["Python"], Reason = "*.py files" },
                ["iac"] = new() { Presence = ExpectedDataPresence.NotApplicable, Reason = "no IaC" },
                ["runtime"] = new() { Presence = ExpectedDataPresence.RuntimeNotVerified },
            },
        };
    }

    private sealed class Feature(string id, Func<RealProjectAcceptanceContext, FeatureAcceptanceResult> run, AcceptanceType type = AcceptanceType.Source,
        AcceptanceMode mode = AcceptanceMode.Smoke, string[]? dependsOn = null, string? cannotRun = null) : IRealProjectAcceptanceFeature
    {
        public string FeatureId => id;
        public string DisplayName => id;
        public string Area => "Test";
        public string Route => "/" + id;
        public AcceptanceType AcceptanceType => type;
        public AcceptanceMode MinimumMode => mode;
        public IReadOnlyList<string> RequiredEvidence => [];
        public IReadOnlyList<string> DependsOn => dependsOn ?? [];
        public string? CanRun(RealProjectAcceptanceContext context) => cannotRun;
        public Task<FeatureAcceptanceResult> ExecuteAsync(RealProjectAcceptanceContext context, CancellationToken ct) => Task.FromResult(run(context));
    }

    private static FeatureAcceptanceResult Result(EvidenceState evidence = EvidenceState.Verified, DataState data = DataState.RealDataObserved,
        ProvenanceState provenance = ProvenanceState.Traced, ExecutionStatus execution = ExecutionStatus.Completed, params string[] facts) =>
        new() { ExecutionStatus = execution, EvidenceState = evidence, DataState = data, ProvenanceState = provenance, Facts = [.. facts] };

    private string Archive(string content = "archive")
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.zip");
        File.WriteAllText(path, content);
        return path;
    }

    private static string Sha(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private RealProjectAcceptanceContext ReadyContext(RealProjectAcceptanceDatasetOverrides? overrides = null)
    {
        var archive = Archive();
        var dataset = new SyntheticProvider(sha: Sha(archive)).Describe(archive) with { RuntimeProfile = overrides?.Runtime };
        return new RealProjectAcceptanceContext { Dataset = dataset, Preparation = DatasetPreparation.Prepare(dataset), Mode = overrides?.Mode ?? AcceptanceMode.Standard };
    }

    private sealed record RealProjectAcceptanceDatasetOverrides(RealProjectRuntimeProfile? Runtime = null, AcceptanceMode? Mode = null);

    // ── datasets and preparation ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Registry_resolves_the_archive_from_explicit_path_first_then_the_dataset_variable()
    {
        var registry = new RealProjectDatasetRegistry([new SyntheticProvider()]);
        var env = new Dictionary<string, string?> { ["BIRKNEXT_REAL_PROJECT_SYNTHETIC"] = "from-env.zip" };

        registry.Resolve("SYNTHETIC", null, k => env.GetValueOrDefault(k)).ArchivePath.Should().Be("from-env.zip");
        registry.Resolve("SYNTHETIC", "explicit.zip", k => env.GetValueOrDefault(k)).ArchivePath.Should().Be("explicit.zip");
        registry.Resolve("SYNTHETIC", null, _ => null).ArchivePath.Should().BeNull();
        FluentActions.Invoking(() => registry.Get("UNKNOWN")).Should().Throw<KeyNotFoundException>();
    }

    [Fact]
    public void Registry_rejects_duplicate_dataset_ids()
    {
        FluentActions.Invoking(() => new RealProjectDatasetRegistry([new SyntheticProvider("X"), new SyntheticProvider("X")])).Should().Throw<Exception>();
    }

    [Fact]
    public void Missing_dataset_is_an_explicit_not_configured_state_never_ready()
    {
        var preparation = DatasetPreparation.Prepare(new SyntheticProvider().Describe(null));

        preparation.State.Should().Be(DatasetPreparationState.NotConfigured);
        preparation.Message.Should().Be("External real-project dataset not configured.");
        preparation.IsReady.Should().BeFalse();
        DatasetPreparation.Prepare(new SyntheticProvider().Describe(Path.Combine(_dir, "absent.zip"))).State.Should().Be(DatasetPreparationState.ArchiveMissing);
    }

    [Fact]
    public void Archive_is_fingerprinted_and_a_changed_archive_is_refused()
    {
        var archive = Archive("v1");
        var ready = DatasetPreparation.Prepare(new SyntheticProvider(sha: Sha(archive)).Describe(archive));
        ready.State.Should().Be(DatasetPreparationState.Ready);
        ready.Fingerprint!.Sha256.Should().Be(Sha(archive));
        ready.Fingerprint.SizeBytes.Should().Be(2);

        var changed = DatasetPreparation.Prepare(new SyntheticProvider(sha: new string('0', 64)).Describe(archive));
        changed.State.Should().Be(DatasetPreparationState.HashMismatch);
        changed.Message.Should().Contain("refusing");
    }

    [Fact]
    public async Task Not_configured_run_executes_nothing_and_reports_an_info_finding_not_a_pass()
    {
        var dataset = new SyntheticProvider().Describe(null);
        var executed = false;
        var runner = new RealProjectAcceptanceRunner([new Feature("source", _ => { executed = true; return Result(); })]);

        var result = await runner.RunAsync(new RealProjectAcceptanceContext { Dataset = dataset, Preparation = DatasetPreparation.Prepare(dataset) });

        executed.Should().BeFalse();
        result.Features.Should().BeEmpty();
        result.Succeeded.Should().BeFalse("a skipped dataset is never a successful acceptance run");
        result.RunFindings.Should().ContainSingle(f => f.Severity == AcceptanceFindingSeverity.Info);
    }

    [Fact]
    public void Mode_comes_from_the_environment_and_defaults_to_standard()
    {
        RealProjectDatasetRegistry.ResolveMode(_ => null).Should().Be(AcceptanceMode.Standard);
        RealProjectDatasetRegistry.ResolveMode(_ => "full").Should().Be(AcceptanceMode.Full);
        RealProjectDatasetRegistry.ResolveMode(_ => "nonsense").Should().Be(AcceptanceMode.Standard);
    }

    // ── runner semantics ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Runner_honours_mode_dependencies_availability_and_exceptions()
    {
        var context = ReadyContext(new(Mode: AcceptanceMode.Smoke));
        var runner = new RealProjectAcceptanceRunner(
        [
            new Feature("import", _ => Result(execution: ExecutionStatus.Failed)),
            new Feature("dependent", _ => Result(), dependsOn: ["import"]),
            new Feature("full-only", _ => Result(), mode: AcceptanceMode.Full),
            new Feature("unavailable", _ => Result(), cannotRun: "needs X"),
            new Feature("throws", _ => throw new InvalidOperationException("boom")),
        ]);

        var result = await runner.RunAsync(context);

        result.Features.Select(f => f.FeatureId).Should().Equal("import", "dependent", "unavailable", "throws");
        result.Features.Single(f => f.FeatureId == "dependent").Category.Should().Be(AcceptanceSummaryCategory.Blocked);
        result.Features.Single(f => f.FeatureId == "unavailable").Category.Should().Be(AcceptanceSummaryCategory.NotVerified);
        result.Features.Single(f => f.FeatureId == "throws").Findings.Should().ContainSingle(f => f.Code == "unhandled-error");
        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public void Category_never_turns_not_verified_or_not_applicable_into_completed()
    {
        Result().Category.Should().Be(AcceptanceSummaryCategory.Completed);
        Result(EvidenceState.PartiallyVerified).Category.Should().Be(AcceptanceSummaryCategory.Partial);
        Result(EvidenceState.NotVerified, DataState.NotAssessed).Category.Should().Be(AcceptanceSummaryCategory.NotVerified);
        Result(EvidenceState.NotApplicable, DataState.NoApplicableData).Category.Should().Be(AcceptanceSummaryCategory.NotApplicable);
        (Result() with { Findings = [new(AcceptanceFindingSeverity.Defect, "x", "y")] }).Category.Should().Be(AcceptanceSummaryCategory.Failed);
        (Result() with { Findings = [new(AcceptanceFindingSeverity.Warning, "x", "y")] }).Category.Should().Be(AcceptanceSummaryCategory.Completed);
    }

    [Fact]
    public void Rules_reject_verified_without_assessed_data_and_source_data_without_provenance()
    {
        var context = ReadyContext();
        var feature = new Feature("other", _ => Result());

        RealProjectAcceptanceRunner.ApplyRules(feature, Result(data: DataState.NotAssessed), context).Findings.Should().Contain(f => f.Code == "false-verified");
        RealProjectAcceptanceRunner.ApplyRules(feature, Result(provenance: ProvenanceState.Missing), context).Findings.Should().Contain(f => f.Code == "missing-provenance");
        RealProjectAcceptanceRunner.ApplyRules(feature, Result(), context).Findings.Should().BeEmpty();
    }

    [Fact]
    public void Runtime_evidence_without_a_runtime_profile_is_a_defect_and_not_verified_is_the_truthful_outcome()
    {
        var feature = new Feature("runtime", _ => Result(), AcceptanceType.Runtime);

        RealProjectAcceptanceRunner.ApplyRules(feature, Result(EvidenceState.PartiallyVerified), ReadyContext()).Findings.Should().Contain(f => f.Code == "runtime-verified-without-runtime");
        var truthful = RealProjectAcceptanceRunner.ApplyRules(feature, Result(EvidenceState.NotVerified, DataState.NotAssessed), ReadyContext());
        truthful.Findings.Should().BeEmpty();
        truthful.Category.Should().Be(AcceptanceSummaryCategory.NotVerified);
        RealProjectAcceptanceRunner.ApplyRules(feature, Result(), ReadyContext(new(new RealProjectRuntimeProfile { TargetEnvironmentId = "qa" }))).Findings.Should().BeEmpty();
    }

    [Fact]
    public void Dataset_expectations_turn_absent_real_data_and_missing_facts_into_defects()
    {
        var context = ReadyContext();
        var source = new Feature("source", _ => Result());
        var iac = new Feature("iac", _ => Result());

        RealProjectAcceptanceRunner.ApplyRules(source, Result(data: DataState.NoApplicableData, evidence: EvidenceState.Verified, facts: ["Python"]), context)
            .Findings.Should().Contain(f => f.Code == "expected-data-absent" && f.Severity == AcceptanceFindingSeverity.Defect);
        RealProjectAcceptanceRunner.ApplyRules(source, Result(facts: ["C#"]), context).Findings.Should().Contain(f => f.Code == "expected-fact-missing");
        RealProjectAcceptanceRunner.ApplyRules(source, Result(facts: ["Python 3.12"]), context).Findings.Should().BeEmpty();
        RealProjectAcceptanceRunner.ApplyRules(iac, Result(), context).Findings.Should().ContainSingle(f => f.Code == "unexpected-data" && f.Severity == AcceptanceFindingSeverity.Warning);
        RealProjectAcceptanceRunner.ApplyRules(iac, Result(data: DataState.NoApplicableData), context).Findings.Should().BeEmpty();
    }

    [Fact]
    public void Builder_maps_zero_to_the_supplied_empty_state_never_to_real_data()
    {
        new FeatureResultBuilder().DataFromCount(0).Data.Should().Be(DataState.NoApplicableData);
        new FeatureResultBuilder().DataFromCount(0, DataState.NotAssessed).Data.Should().Be(DataState.NotAssessed);
        new FeatureResultBuilder().DataFromCount(3).Data.Should().Be(DataState.RealDataObserved);
    }

    // ── report ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Report_redacts_credentials_connection_strings_and_national_ids()
    {
        const string jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U";
        var text = $"Authorization: Bearer abc.def.ghi; token {jwt}; Server=db;Password=hunter2; client_secret=s3cr3t; person 01017012345";

        var redacted = AcceptanceReportWriter.Redact(text);

        redacted.Should().NotContain("hunter2").And.NotContain("s3cr3t").And.NotContain(jwt).And.NotContain("01017012345").And.NotContain("abc.def.ghi");
        AcceptanceReportWriter.Redact("Specification Explorer: 22 requirements").Should().Be("Specification Explorer: 22 requirements");
        AcceptanceReportWriter.Redact("Test appears to check \"bearer token missing\"").Should().Be("Test appears to check \"bearer token missing\"", "plain words after bearer are not a credential");
        AcceptanceReportWriter.Redact("Bearer 0a1b2c3d4e5f6a7b8c9d").Should().NotContain("0a1b2c3d4e5f6a7b8c9d");
    }

    [Fact]
    public async Task Report_is_written_as_json_and_html_with_categories_and_no_overall_percentage()
    {
        var context = ReadyContext();
        var runner = new RealProjectAcceptanceRunner(
        [
            new Feature("source", _ => Result(facts: ["Python"]) with { Observations = new() { ["note"] = "Password=hunter2" } }),
            new Feature("runtime", _ => Result(EvidenceState.NotVerified, DataState.NotAssessed), AcceptanceType.Runtime),
        ]);
        var result = await runner.RunAsync(context);

        var (jsonPath, htmlPath) = AcceptanceReportWriter.Write(result, _dir);

        jsonPath.Should().StartWith(Path.Combine(_dir, "SYNTHETIC"));
        var json = File.ReadAllText(jsonPath);
        var html = File.ReadAllText(htmlPath);
        json.Should().NotContain("hunter2");
        html.Should().NotContain("hunter2").And.NotContain("%</");
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("datasetId").GetString().Should().Be("SYNTHETIC");
        doc.RootElement.GetProperty("features").GetArrayLength().Should().Be(2);
        result.Summary[AcceptanceSummaryCategory.Completed].Should().Be(1);
        result.Summary[AcceptanceSummaryCategory.NotVerified].Should().Be(1);
        result.Succeeded.Should().BeTrue("NotVerified runtime evidence is a truthful, successful outcome");
    }

    // ── boundaries ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void M2lb_descriptor_carries_semantic_expectations_and_a_hash_bound_baseline_but_no_archive()
    {
        var registry = KnownRealProjectDatasets.Registry();
        var m2lb = registry.Resolve(M2lbDatasetProvider.Id, null, _ => null);

        m2lb.ArchivePath.Should().BeNull("the archive is external and only ever configured per machine");
        m2lb.ExpectedSha256.Should().MatchRegex("^[0-9a-f]{64}$");
        m2lb.Expectations["contract-xsd"].Presence.Should().Be(ExpectedDataPresence.NotApplicable);
        m2lb.Expectations["fqr-runtime"].Presence.Should().Be(ExpectedDataPresence.RuntimeNotVerified);
        m2lb.RuntimeProfile.Should().BeNull("no runtime target is paired: runtime acceptance is NotVerified by design");
        m2lb.HashBoundBaselines.Keys.Should().OnlyContain(k => k.StartsWith("explorer-coverage.", StringComparison.Ordinal));
    }

    [Fact]
    public void Production_assemblies_do_not_reference_acceptance_or_any_dataset()
    {
        var acceptance = typeof(RealProjectAcceptanceRunner).Assembly.GetName().Name!;
        typeof(BirkNext.Web.Services.NavigationCatalog).Assembly.GetReferencedAssemblies().Select(a => a.Name).Should().NotContain(acceptance);
        typeof(RealProjectAcceptanceRunner).Assembly.GetReferencedAssemblies().Select(a => a.Name)
            .Should().NotContain(n => n!.StartsWith("BirkNext.Web", StringComparison.Ordinal) || n.StartsWith("BirkNext.Api", StringComparison.Ordinal),
                "the framework stays dataset- and product-agnostic test infrastructure");
    }

    [Fact]
    public void Production_source_has_no_acceptance_or_dataset_special_cases()
    {
        var root = RepoRoot();
        string[] productionDirs = [Path.Combine(root, "frontend", "BirkNext.Web"), Path.Combine(root, "backend", "BirkNext.Api"), Path.Combine(root, "shared")];
        string[] forbidden = ["BirkNext.RealProjectAcceptance", "M2lbDatasetProvider", "RealProjectDataset", "BIRKNEXT_REAL_PROJECT_"];
        var offenders = productionDirs.Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.*", SearchOption.AllDirectories))
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".razor", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => forbidden.Any(File.ReadAllText(f).Contains))
            .Select(f => Path.GetRelativePath(root, f)).ToList();

        offenders.Should().BeEmpty();
    }

    [Fact]
    public void Repository_never_contains_an_external_dataset_archive()
    {
        var root = RepoRoot();
        var archives = Directory.EnumerateFiles(root, "*.zip", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}artifacts{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}"))
            .Where(f => new FileInfo(f).Length == M2lbArchiveBytes && Sha(f) == M2lbDatasetProvider.Sha256)
            .ToList();

        archives.Should().BeEmpty();
    }

    private const long M2lbArchiveBytes = 4_734_868;

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "frontend")) && Directory.Exists(Path.Combine(dir.FullName, "backend"))) return dir.FullName;
        throw new DirectoryNotFoundException("AIAssisted root not found.");
    }
}
