using BirkNext.Api.Data;
using BirkNext.Api.Data.CanonicalInterpretation;
using BirkNext.Api.Services.CanonicalInterpretation;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Interpretation;
using BirkNext.TestEvidence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace BirkNext.Api.Tests.Services.CanonicalInterpretation;

/// <summary>
/// Real-archive regression corpus (runs only where the developer's untracked <c>testdata/</c> exists; archives are never committed). The full
/// Source Analysis pipeline analyzes each archive into an in-memory store, then the canonical interpretation runs over the stored snapshots.
/// Observations are compared with the evidence itself (never with hard-coded counts), and every heuristic outcome must stay a candidate.
/// </summary>
public sealed class CanonicalRealArchiveRegressionTests(ITestOutputHelper output)
{
    static readonly string[] Corpus =
    [
        "M2LB.zip", "M2LB.Common.zip", "Terraform-iac-m2lb.zip", "Terraform-iac-meldingsutveksler.zip", "Terraform-iac-m2lbm01.zip",
        "Terraform-BasicOpsInfra_Meldingsutveksler.zip", "Terraform-BasicOpsInfra_M2LBM01.zip",
    ];

    static string? Testdata(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "testdata", name))) return Path.Combine(dir.FullName, "testdata", name);
        return null;
    }

    [Fact]
    [Trait("Category", "EnvironmentDependent")]
    public async Task Real_archives_interpret_without_unsupported_merges_and_reinterpret_idempotently()
    {
        var paths = Corpus.Select(n => (Name: n, Path: Testdata(n))).Where(x => x.Path is not null).ToList();
        if (!paths.Any(p => p.Name == "M2LB.zip")) return;

        await using var app = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"real-app-{Guid.NewGuid():N}").Options);
        var store = new IqrSourceStore(app);
        var ids = new List<Guid>();
        foreach (var (name, path) in paths)
        {
            var (snapshot, error) = await store.AnalyzeAsync("local", IqrSourceStore.SourceAnalysisOwner, name, await File.ReadAllBytesAsync(path!));
            snapshot.Should().NotBeNull(error);
            ids.Add(snapshot!.Id);
        }

        await using var db = new CanonicalInterpretationDbContext(new DbContextOptionsBuilder<CanonicalInterpretationDbContext>().UseInMemoryDatabase($"real-canon-{Guid.NewGuid():N}").Options);
        var service = new CanonicalInterpretationService(db, new IqrCanonicalSourceReader(store), TimeProvider.System);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var first = await service.InterpretAsync("real", new(ids, Trigger: "regression"), default);
        var elapsed = watch.Elapsed;
        async Task<List<string>> Ids() => [.. (await db.Entities.AsNoTracking().Select(e => e.EntityType + "|" + e.IdentityKey + "|" + e.Id).ToListAsync()).Order(StringComparer.Ordinal)];
        var entityIds = await Ids();
        var second = await service.InterpretAsync("real", new(ids, Trigger: "regression again"), default);

        // Idempotent reimport: same snapshot reused, every id unchanged.
        second.Reused.Should().BeTrue();
        (await Ids()).Should().Equal(entityIds);

        var relations = await db.Relations.AsNoTracking().ToListAsync();
        relations.Should().NotContain(r => r.RelationType == nameof(CanonicalRelationType.SameAs), "no rule ever asserts identity equivalence");
        relations.Should().NotContain(r => r.ReviewState == nameof(RelationReviewState.Confirmed), "nothing is confirmed without a human decision");
        relations.Where(r => r.RuleId is CorrelationRules.IntentRule or CorrelationRules.SymbolNameRule or CorrelationRules.TestCandidateRule or CorrelationRules.AcronymAliasRule)
            .Should().OnlyContain(r => r.Resolution == nameof(RelationResolution.Candidate), "heuristics only ever propose");

        // Case M on real tests: explicit VERIFIES equals the test evidence's own explicit/strong links; candidates never count.
        var m2lb = (await store.FindSourceAnalysisAsync(ids[paths.FindIndex(p => p.Name == "M2LB.zip")]))!;
        var tests = TestEvidenceQueries.FromInventory(m2lb.TestInventory);
        var allTests = new List<CanonicalTest>();
        foreach (var id in ids) allTests.AddRange(TestEvidenceQueries.FromInventory((await store.FindSourceAnalysisAsync(id))!.TestInventory));
        var explicitLinks = allTests.DistinctBy(t => t.Id).Sum(t => t.Requirements.Where(r => r.Confidence is TestMappingConfidence.Explicit or TestMappingConfidence.Strong)
            .Select(r => CorrelationRules.NormalizeRequirement(r.RequirementId)).Distinct().Count());
        var verifies = relations.Where(r => r.RelationType == nameof(CanonicalRelationType.Verifies)).ToList();
        var explicitVerifies = verifies.Count(r => r.RuleId == CorrelationRules.TestExplicitRule);
        (await db.Entities.CountAsync(e => e.EntityType == nameof(CanonicalEntityType.Test))).Should().BeGreaterThanOrEqualTo(tests.Count > 0 ? 1 : 0);

        // Packages keep separate identities; contract occurrences keep their package.
        var packages = await db.Entities.AsNoTracking().Where(e => e.EntityType == nameof(CanonicalEntityType.Package)).ToListAsync();
        packages.Should().HaveCount(paths.Count, "every archive is its own package; none is merged by name");
        var occurrences = await db.Entities.AsNoTracking().Where(e => e.EntityType == nameof(CanonicalEntityType.ContractOccurrence)).ToListAsync();
        occurrences.Should().OnlyContain(o => o.AttributesJson.Contains("\"package\""));

        // MU is never resolved for M2LB's own receiver (MM): an acronym is at most a proposal.
        var mu = await service.ResolveAliasAsync("real", "MU", default);
        mu.Resolved.Should().NotContain(e => e.DisplayName.Contains("MeldingsutvekslerReceiver", StringComparison.OrdinalIgnoreCase));

        output.WriteLine($"archives={paths.Count} snapshot={first.Id} entities={first.EntityCount} relations={first.RelationCount} interpret={elapsed.TotalMilliseconds:F0} ms");
        output.WriteLine($"M2LB tests={tests.Count} testsWithExplicit={tests.Count(t => t.Requirements.Any(r => r.Confidence is TestMappingConfidence.Explicit or TestMappingConfidence.Strong))} " +
                         $"testsCandidateOnly={tests.Count(t => t.Requirements.Count > 0 && t.Requirements.All(r => r.Confidence is TestMappingConfidence.Candidate or TestMappingConfidence.Inferred))}");
        output.WriteLine($"verifies explicit={explicitVerifies} candidate={verifies.Count(r => r.RuleId == CorrelationRules.TestCandidateRule)} explicitLinksInEvidence={explicitLinks}");
        foreach (var g in relations.GroupBy(r => (r.RuleId, r.Resolution)).OrderBy(g => g.Key.RuleId))
            output.WriteLine($"  {g.Key.RuleId} {g.Key.Resolution}: {g.Count()}");
        foreach (var g in (await db.Entities.AsNoTracking().ToListAsync()).GroupBy(e => e.EntityType).OrderBy(g => g.Key))
            output.WriteLine($"  entity {g.Key}: {g.Count()}");
        foreach (var l in first.Limitations) output.WriteLine($"  limitation: {l}");
        explicitVerifies.Should().BeLessThanOrEqualTo(explicitLinks, "an explicit VERIFIES exists only where the test evidence states the link");
        if (explicitLinks > 0) explicitVerifies.Should().BePositive("explicit test links in the evidence become explicit VERIFIES relations (non-requirement ids are reported unresolved)");
    }
}
