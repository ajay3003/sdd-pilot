using System.Runtime.CompilerServices;
using System.Text.Json;
using BirkNext.Api.Services.Specifications;
using BirkNext.TestPlanning;
using FluentAssertions;
using Xunit;
using D = BirkNext.Api.Services.TestPlanning.TestScenarioDiscoveryEngine;

namespace BirkNext.Api.Tests.Services.Specifications;

/// <summary>
/// Spec Kit regression gate for the OpenSpec isolated route (2026-11-27). The golden file was RECORDED AT HEAD 9af21c93, BEFORE any OpenSpec code
/// existed, from the real M2LB specifications (Fixtures/m2lb-specs, copied unchanged) plus a synthetic root-level specification. It pins what the
/// Spec Kit path produces: catalog documents and requirement entries, stored evidence (requirements, acceptance scenarios, stories), discovery
/// candidates (candidate id, canonical key, source identity, fingerprint, requirement ids, story) for single- and multi-specification selections,
/// requirement-only candidates and the Testoversikt module of every path. Any difference is a release blocker unless explained.
/// Re-record ONLY deliberately: set BIRKNEXT_RECORD_SPECKIT_GOLDEN=1 and review the diff.
/// </summary>
public sealed class SpecKitRegressionBaselineTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static string Here([CallerFilePath] string here = "") => Path.GetDirectoryName(here)!;
    private static string Fixtures => Path.Combine(Here(), "..", "TestPlanning", "Fixtures", "m2lb-specs");
    private static string GoldenPath => Path.Combine(Here(), "Fixtures", "speckit-golden-m2lb.json");

    /// <summary>A synthetic project-root specification (specs/&lt;feature&gt;/spec.md at the root: «Prosjektnivå»), with a requirement-only FR.</summary>
    internal const string RootSpec = """
        # Feature Specification: Felles innlogging

        ## User Scenarios & Testing

        ### User Story 1 - Logg inn (Priority: P1)

        **Acceptance Scenarios**:

        1. **Given** en registrert bruker, **When** brukeren logger inn med gyldig passord, **Then** forsiden vises.
        2. **Given** en registrert bruker, **When** brukeren logger inn med feil passord, **Then** en feilmelding vises (FR-002).

        ## Requirements

        ### Functional Requirements

        - **FR-001**: Systemet MÅ kreve innlogging før forsiden vises.
        - **FR-002**: Systemet MÅ vise en feilmelding ved feil passord.
        - **FR-003**: Systemet MÅ logge alle innloggingsforsøk.
        """;

    /// <summary>The documents of the baseline: every file of the M2LB fixture (spec.md and tasks.md) plus the root specification.</summary>
    internal static List<SpecificationCatalogService.Document> Documents()
    {
        var root = Path.GetFullPath(Fixtures);
        var docs = Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories)
            .Select(f => new SpecificationCatalogService.Document(Path.GetRelativePath(root, f).Replace('\\', '/'), File.ReadAllText(f)))
            .ToList();
        docs.Add(new("specs/001-felles-innlogging/spec.md", RootSpec));
        return [.. docs.OrderBy(d => d.Path, StringComparer.Ordinal)];
    }

    /// <summary>The canonical projection of everything the Spec Kit path produces for <paramref name="documents"/>.</summary>
    internal static object Projection(IReadOnlyList<SpecificationCatalogService.Document> documents, Func<IReadOnlyList<SpecificationCatalogService.Document>,
        (List<BirkNext.Specifications.SpecificationDocumentRef>, List<BirkNext.Specifications.SpecificationRequirementEntry>)>? read = null,
        Func<IReadOnlyList<SpecificationCatalogService.Document>, List<StoredSpecificationEvidence>>? evidence = null)
    {
        var (refs, entries) = (read ?? (d => SpecificationCatalogService.Read(d)))(documents);
        var stored = (evidence ?? (d => SpecificationEvidenceBuilder.BuildAll(d)))(documents);
        const string project = "import:import-golden";
        object Candidates(IEnumerable<BirkNext.TestPlanning.DiscoveredScenario> list) => list.Select(c => new
        {
            c.CandidateId, c.CanonicalKey, c.SourceIdentity, c.Fingerprint, c.Title, c.Area, RequirementIds = c.RequirementIds.ToList(), Story = c.Story?.Key, c.NumberInStory,
            Evidence = c.Evidence.Select(e => $"{e.Kind}|{e.Reference}").ToList(),
        }).ToList();
        // Single selection (the browser chose one specification: no module scope) and the resolver's multi-file selection (scope = path).
        var single = stored.Select(s => new { s.Path, Candidates = Candidates(D.FromSpecification(project, s.Evidence).Candidates) }).ToList();
        var multi = D.Finalize(project, "golden-snapshot", stored.SelectMany(s => D.FromSpecification(project, s.Evidence with { ArtifactScope = stored.Count > 1 ? s.Path : null }).Candidates), []);
        var roots = ModuleStructure.RootsOf(stored.Select(s => (string?)s.Path));
        return new
        {
            Documents = refs.Select(r => $"{r.Path}|{r.Sha256}").ToList(),
            Entries = entries.Select(e => $"{e.Path}|{e.RequirementId}|{e.TextSha256}").ToList(),
            Evidence = stored.Select(s => new
            {
                s.Path, s.ContentSha256, Semantic = SpecificationEvidenceBuilder.SemanticFingerprint(s.Evidence),
                Requirements = s.Evidence.Requirements.Select(r => r.Id).ToList(),
                Scenarios = s.Evidence.AcceptanceScenarios.Select(a => $"{a.Id}|{a.StoryKey}|{a.NumberInStory}|{string.Join(",", a.RequirementIds)}").ToList(),
                Stories = s.Evidence.Stories.Select(u => $"{u.Key}|{u.StoryId}|{u.Priority}|{u.Scenarios}").ToList(),
            }).ToList(),
            Counts = new
            {
                Requirements = stored.Sum(s => s.Evidence.Requirements.Count), Scenarios = stored.Sum(s => s.Evidence.AcceptanceScenarios.Count),
                MultiCandidates = multi.Count, RequirementOnly = multi.Count(c => c.CanonicalKey.StartsWith("requirement:", StringComparison.Ordinal)),
            },
            Single = single,
            Multi = Candidates(multi),
            Modules = stored.Select(s => $"{s.Path}|{ModuleStructure.OverviewModule(s.Path, roots)}").ToList(),
        };
    }

    private static string Serialize(object o) => JsonSerializer.Serialize(o, Json).ReplaceLineEndings("\n");

    [Fact]
    public void S01_S02_S03_S04_SpecKit_output_equals_the_golden_baseline_recorded_before_OpenSpec()
    {
        var actual = Serialize(Projection(Documents()));
        if (Environment.GetEnvironmentVariable("BIRKNEXT_RECORD_SPECKIT_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(GoldenPath)!);
            File.WriteAllText(GoldenPath, actual);
        }
        File.Exists(GoldenPath).Should().BeTrue("the golden baseline is committed with this test");
        var golden = File.ReadAllText(GoldenPath).ReplaceLineEndings("\n");
        actual.Should().Be(golden, "Spec Kit documents, identities, candidates, counts and module grouping must not change");
    }

    [Fact]
    public void Golden_baseline_covers_root_module_requirement_only_and_story_scenarios()
    {
        var p = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(GoldenPath));
        var modules = p.GetProperty("modules").EnumerateArray().Select(m => m.GetString()!).ToList();
        modules.Should().Contain(m => m.EndsWith("|" + ModuleStructure.ProjectWide), "a root-level specification is covered");
        modules.Should().Contain(m => m.StartsWith("SkoleService/") && m.EndsWith("|SkoleService"), "a module specification is covered");
        p.GetProperty("counts").GetProperty("requirementOnly").GetInt32().Should().BeGreaterThan(0, "requirement-only candidates are covered");
        p.GetProperty("counts").GetProperty("scenarios").GetInt32().Should().BeGreaterThan(50, "the real M2LB acceptance scenarios are covered");
    }
}
