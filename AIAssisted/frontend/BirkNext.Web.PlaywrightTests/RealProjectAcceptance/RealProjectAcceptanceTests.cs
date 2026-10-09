using BirkNext.RealProjectAcceptance;
using BirkNext.RealProjectAcceptance.Datasets;
using Xunit.Abstractions;

namespace BirkNext.Web.PlaywrightTests.RealProjectAcceptance;

/// <summary>
/// Skips — visibly, with the reason — unless an external dataset archive AND pre-started acceptance servers are configured. Ordinary CI
/// therefore never fails because an external archive is absent, and never reports a silent pass either.
/// </summary>
public sealed class RealProjectAcceptanceFactAttribute : FactAttribute
{
    public const string ServersNotConfigured = "Real-project acceptance servers not configured (set BIRKNEXT_ACCEPTANCE_FRONTEND_URL and BIRKNEXT_ACCEPTANCE_BACKEND_URL, or use scripts/run-real-project-acceptance.ps1).";

    public RealProjectAcceptanceFactAttribute()
    {
        var preparation = KnownRealProjectDatasets.PrepareFromEnvironment();
        if (preparation.State is DatasetPreparationState.NotConfigured or DatasetPreparationState.ArchiveMissing) Skip = preparation.Message;
        else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BIRKNEXT_ACCEPTANCE_FRONTEND_URL"))
                 || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BIRKNEXT_ACCEPTANCE_BACKEND_URL"))) Skip = ServersNotConfigured;
        Timeout = 0;
    }
}

[Trait("Category", "RealProjectAcceptance")]
[Trait("Category", "ExternalDataset")]
[Trait("Category", "BrowserAcceptance")]
public sealed class RealProjectAcceptanceTests(ITestOutputHelper output)
{
    [RealProjectAcceptanceFact]
    public async Task Real_project_acceptance_runs_every_feature_against_one_imported_workspace()
    {
        var preparation = KnownRealProjectDatasets.PrepareFromEnvironment();
        var mode = RealProjectDatasetRegistry.ResolveMode();
        var root = AcceptanceReportWriter.DefaultArtifactsRoot(AppContext.BaseDirectory);
        var artifacts = Path.Combine(root, preparation.Dataset.DatasetId);
        Directory.CreateDirectory(artifacts);
        var frontend = Environment.GetEnvironmentVariable("BIRKNEXT_ACCEPTANCE_FRONTEND_URL")!;
        var backend = Environment.GetEnvironmentVariable("BIRKNEXT_ACCEPTANCE_BACKEND_URL")!;
        // The production frontend build calls the backend it was configured with; the session rewrites those calls to the acceptance backend.
        var frontendBackend = Environment.GetEnvironmentVariable("BIRKNEXT_ACCEPTANCE_FRONTEND_BACKEND_URL") is { Length: > 0 } configured ? configured : "http://localhost:5000";

        RealProjectAcceptanceResult result;
        await using (var session = await PlaywrightAcceptanceSession.StartAsync(frontend, backend, frontendBackend, artifacts, AxeScriptPath()))
        {
            var context = new RealProjectAcceptanceContext
            {
                Dataset = preparation.Dataset, Preparation = preparation, Mode = mode, ArtifactsDirectory = artifacts, Session = session,
                BirkNextCommit = Environment.GetEnvironmentVariable("BIRKNEXT_ACCEPTANCE_COMMIT"),
            };
            var runner = new RealProjectAcceptanceRunner(BrowserAcceptanceFeatureRegistry.Create(), output.WriteLine);
            result = await runner.RunAsync(context);
            if (session.ConsoleErrors.Count > 0)
                output.WriteLine($"Browser console errors ({session.ConsoleErrors.Count}): {string.Join(" | ", session.ConsoleErrors.Distinct().Take(10))}");
            Assert.Empty(session.ConsoleErrors);
            output.WriteLine("Browser console errors: 0");
        }
        var (json, html) = AcceptanceReportWriter.Write(result, root);
        output.WriteLine($"Report: {json}");
        output.WriteLine($"Report: {html}");
        output.WriteLine(string.Join(", ", result.Summary.Select(kv => $"{kv.Key}={kv.Value}")));
        foreach (var (feature, finding) in result.Defects) output.WriteLine($"DEFECT {feature}: [{finding.Code}] {finding.Message}");

        Assert.True(result.Succeeded, $"Real-project acceptance found defects — see {html}");
    }

    private static string? AxeScriptPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "browser-companion", "vendor", "axe.min.js");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
