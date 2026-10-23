using System.Text.Json;
using BirkNext.Api.Services.SourceAnalysis.GeneratedDocumentation;
using BirkNext.GeneratedDocumentation;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using static BirkNext.Api.Tests.Services.SourceAnalysis.GeneratedDocumentation.GeneratedDocumentationFixture;

namespace BirkNext.Api.Tests.Services.SourceAnalysis.GeneratedDocumentation;

/// <summary>
/// Autodoc diagnostics inside Generated Documentation Health: the complete module inventory of the snapshot (not only autodoc folders), API
/// capability from architecture evidence, module usage only from module-specific output (a repository-level maintain-docs workflow never
/// implies it), repository-root output kept out of the denominators, Not configured neutral, parse errors visible. Generic fixtures only.
/// </summary>
public sealed class AutodocDiagnosticsTests
{
    private const string MaintainDocs = """
        ---
        name: maintain-docs
        description: Keeps module documentation current.
        ---
        # Maintain docs

        All output goes to `autodoc/`.

        ### Write autodoc/README.md
        ### Write autodoc/openapi.yaml
        """;

    private const string WorkerCsproj = "<Project Sdk=\"Microsoft.NET.Sdk.Worker\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>";
    private const string LibraryCsproj = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>";
    private const string WorkerProgram = """
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddHostedService<Sweeper>();
        builder.Build().Run();
        public sealed class Sweeper : BackgroundService { protected override Task ExecuteAsync(CancellationToken ct) => Task.CompletedTask; }
        """;

    private static List<(string Path, string Content)> Mixed(bool workflow = true)
    {
        var files = new List<(string, string)>
        {
            ("persons/src/Persons.Api/Persons.Api.csproj", Csproj),
            ("persons/src/Persons.Api/Program.cs", Program("/persons")),
            ("persons/autodoc/README.md", "Last updated: 2026-03-01\n\n# Persons\n\nPersons service.\n"),
            ("persons/autodoc/openapi.yaml", OpenApi("/persons")),
            ("billing/src/Billing.Api/Billing.Api.csproj", Csproj),
            ("billing/src/Billing.Api/Program.cs", Program("/invoices")),
            ("jobs/src/Jobs.Worker/Jobs.Worker.csproj", WorkerCsproj),
            ("jobs/src/Jobs.Worker/Program.cs", WorkerProgram),
            ("jobs/autodoc/README.md", "Last updated: 2026-03-01\n\n# Jobs\n\nBackground jobs.\n"),
            ("common/src/Common/Common.csproj", LibraryCsproj),
            ("common/src/Common/Text.cs", "namespace Common; public static class Text { public static string Trim(string s) => s.Trim(); }\n"),
            ("autodoc/README.md", "Last updated: 2026-03-01\n\n# Repository\n\nAll services.\n"),
        };
        if (workflow) files.Add((".claude/skills/maintain-docs/SKILL.md", MaintainDocs));
        return files;
    }

    private static GeneratedDocumentationDiagnosticService Diagnostic(BirkNext.Api.Data.AppDbContext db) =>
        new(new BirkNext.Api.Services.Integrations.SourceEvidence.IqrSourceStore(db), new MemoryCache(new MemoryCacheOptions()));

    private static AutodocModuleInventory Row(AutodocDiagnostics autodoc, string name) => autodoc.Modules.Single(m => m.Name.Contains(name, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public async Task Mixed_repository_classifies_each_module_independently_and_counts_against_detected_modules()
    {
        using var db = Db();
        await AnalyzeAsync(db, Mixed());
        var run = await Diagnostic(db).RunAsync(new GeneratedDocumentationDiagnosticRequest());
        var autodoc = run.Autodoc!;

        autodoc.MaintainDocsWorkflowDetected.Should().BeTrue();
        autodoc.WorkflowSources.Should().Contain(".claude/skills/maintain-docs/SKILL.md");
        var persons = Row(autodoc, "Persons");
        persons.Usage.Should().Be(AutodocUsage.Configured, persons.UsageReason);
        persons.ApiCapable.Should().BeTrue();
        persons.ModuleType.Should().Contain("API");
        persons.GeneratedOutputPresent.Should().BeTrue();
        persons.ArtifactCount.Should().Be(2);
        persons.ArtifactKinds.Should().Contain(GeneratedDocumentKind.ApiContract);
        persons.Generator.Should().Be("maintain-docs");
        persons.OutputPath.Should().Contain("persons/autodoc");

        var billing = Row(autodoc, "Billing");
        billing.Usage.Should().Be(AutodocUsage.NotConfigured, "the repository-level skill never implies module usage");
        billing.ApiCapable.Should().BeTrue();
        billing.GeneratedOutputPresent.Should().BeFalse();
        billing.ArtifactCount.Should().Be(0);
        billing.Freshness.Should().Be(GeneratedDocumentationFreshnessStatus.NotApplicable);
        billing.Health.Should().BeNull("not configured is neutral, never unhealthy");
        billing.MissingExpectedDocs.Should().Be(0);

        var jobs = Row(autodoc, "Jobs");
        jobs.Usage.Should().Be(AutodocUsage.Configured, jobs.UsageReason);
        jobs.ApiCapable.Should().BeFalse();

        autodoc.Modules.Should().NotContain(m => m.ProjectPath.Length == 0 || m.Name == "autodoc", "the repository root is never a module");
        autodoc.RepositoryLevelDocuments.Should().Contain("autodoc/README.md");
        autodoc.DetectedModules.Should().Be(autodoc.Modules.Count);
        autodoc.ModulesUsingAutodoc.Should().Be(autodoc.Modules.Count(m => m.Usage != AutodocUsage.NotConfigured));
        autodoc.ModulesWithoutAutodoc.Should().Be(autodoc.DetectedModules - autodoc.ModulesUsingAutodoc);
        autodoc.ApiModules.Should().Be(autodoc.Modules.Count(m => m.ApiCapable));
        autodoc.ApisUsingAutodoc.Should().Be(1);
        autodoc.ApisWithoutAutodoc.Should().Be(1);
        autodoc.GeneratedArtifacts.Should().Be(persons.ArtifactCount + jobs.ArtifactCount, "repository-root output is not module output");
        autodoc.MissingExpectedDocs.Should().Be(run.Modules.Where(m => m.RootPath.Length > 0).Sum(m => m.MissingExpectedDocs.Count));
        autodoc.ParseErrors.Should().Be(0);

        // Existing health is unchanged: module cards are still the generated-documentation modules, summary as before.
        run.Modules.Should().Contain(m => m.RootPath == "persons").And.Contain(m => m.RootPath == "jobs");
        run.Summary.ModulesWithGeneratedDocs.Should().Be(run.Modules.Count(m => m.GeneratedDocsDetected));
    }

    [Fact]
    public async Task Inventory_includes_modules_without_any_autodoc_folder()
    {
        using var db = Db();
        await AnalyzeAsync(db, Mixed());
        var autodoc = (await Diagnostic(db).RunAsync(new GeneratedDocumentationDiagnosticRequest())).Autodoc!;
        autodoc.Modules.Select(m => m.Name).Should().Contain(n => n.Contains("Billing"));
        autodoc.Modules.Where(m => m.Usage == AutodocUsage.NotConfigured).Should().OnlyContain(m => m.UsageReason.Contains("No module-specific"));
        autodoc.InventoryBasis.Should().StartWith("Source Analysis");
    }

    [Fact]
    public async Task Without_a_traced_workflow_module_output_is_partial_and_maintain_docs_is_not_detected()
    {
        using var db = Db();
        await AnalyzeAsync(db, Mixed(workflow: false));
        var autodoc = (await Diagnostic(db).RunAsync(new GeneratedDocumentationDiagnosticRequest())).Autodoc!;
        autodoc.MaintainDocsWorkflowDetected.Should().BeFalse();
        var persons = autodoc.Modules.SingleOrDefault(m => m.Name.Contains("Persons"));
        persons.Should().NotBeNull();
        persons!.GeneratedOutputPresent.Should().BeTrue("the autodoc folder convention still detects likely-generated output");
        persons.Usage.Should().Be(AutodocUsage.Partial);
        persons.Generator.Should().Be("Unknown generator", "a generator name is never inferred");
        autodoc.ModulesPartial.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task A_generated_artifact_that_does_not_parse_is_an_error_without_failing_the_page()
    {
        using var db = Db();
        var files = Mixed().Where(f => f.Path is not ("persons/autodoc/openapi.yaml" or ".claude/skills/maintain-docs/SKILL.md")).ToList();
        files.Add((".claude/skills/maintain-docs/SKILL.md", MaintainDocs.Replace("### Write autodoc/openapi.yaml", "### Write autodoc/openapi.json")));
        files.Add(("persons/autodoc/openapi.json", "{ \"openapi\": \"3.0.1\", \"paths\": { \"/persons\": { "));
        await AnalyzeAsync(db, files);
        var run = await Diagnostic(db).RunAsync(new GeneratedDocumentationDiagnosticRequest());
        var autodoc = run.Autodoc!;
        var invalid = run.Modules.SelectMany(m => m.Evidence).Count(e => e.StructurallyInvalid);
        invalid.Should().Be(1, "the declared generated OpenAPI is not well-formed JSON");
        autodoc.ParseErrors.Should().Be(1);
        Row(autodoc, "Persons").Usage.Should().Be(AutodocUsage.Error);
        Row(autodoc, "Persons").ParseErrors.Should().Be(1);
        Row(autodoc, "Jobs").Usage.Should().Be(AutodocUsage.Configured, "one artifact error never fails other modules");
    }

    [Fact]
    public async Task A_repository_without_autodoc_lists_every_module_as_not_configured_with_no_error_and_no_missing_docs()
    {
        using var db = Db();
        await AnalyzeAsync(db, [("billing/src/Billing.Api/Billing.Api.csproj", Csproj), ("billing/src/Billing.Api/Program.cs", Program("/invoices"))]);
        var run = await Diagnostic(db).RunAsync(new GeneratedDocumentationDiagnosticRequest());

        run.State.Should().Be(GeneratedDocumentationRunState.NoGeneratedDocumentation);
        var autodoc = run.Autodoc!;
        var billing = autodoc.Modules.Should().ContainSingle().Subject;
        billing.Usage.Should().Be(AutodocUsage.NotConfigured);
        billing.ApiCapable.Should().BeTrue("API capability still comes from source evidence");
        billing.Freshness.Should().Be(GeneratedDocumentationFreshnessStatus.NotApplicable);
        autodoc.ApisWithoutAutodoc.Should().Be(1);
        autodoc.MissingExpectedDocs.Should().Be(0);
        autodoc.ParseErrors.Should().Be(0);
        autodoc.MaintainDocsWorkflowDetected.Should().BeFalse();
        JsonSerializer.Serialize(autodoc).Should().NotContain("Unhealthy").And.NotContain("\"Missing\"");
    }

    [Fact]
    public void Disclaimer_and_labels_keep_diagnostics_neutral()
    {
        GeneratedDocumentationText.AutodocDisclaimer.Should().Be("Autodoc diagnostics shows coverage and health of generated documentation. It does not determine source correctness, runtime behavior, or whether a module must use autodoc.");
        GeneratedDocumentationText.Label(AutodocUsage.NotConfigured).Should().Be("Not configured");
        GeneratedDocumentationText.Label(AutodocUsage.Configured).Should().Be("Configured");
    }
}
