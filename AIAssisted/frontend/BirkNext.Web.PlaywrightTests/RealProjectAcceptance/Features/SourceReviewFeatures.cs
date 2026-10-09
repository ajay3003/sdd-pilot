using System.Text.Json;
using BirkNext.RealProjectAcceptance;
using Microsoft.Playwright;

namespace BirkNext.Web.PlaywrightTests.RealProjectAcceptance.Features;

/// <summary>Source Analysis shows the import's snapshot as current, with Project Import provenance and the snapshot's evidence areas.</summary>
public sealed class SourceAnalysisFeature : AcceptanceFeature
{
    public override string FeatureId => "source-analysis";
    public override string DisplayName => "Source Analysis";
    public override string Area => "Source Review";
    public override string Route => "/source-analysis";
    public override AcceptanceMode MinimumMode => AcceptanceMode.Smoke;
    public override IReadOnlyList<string> RequiredEvidence => ["Source Analysis snapshot"];
    public override string? CanRun(RealProjectAcceptanceContext context) => context.Workspace.SourceDetected ? null : "The imported archive produced no Source Analysis snapshot.";

    protected override async Task RunAsync(RealProjectAcceptanceContext context, PlaywrightAcceptanceSession session, FeatureResultBuilder result, CancellationToken ct)
    {
        await OpenAsync(session, result);
        if (!await ContractEvidenceFeature.EnsureSourceOverviewAsync(session))
        {
            result.Defect("source-analysis-no-snapshot", "Source Analysis does not show the imported snapshot.");
            result.Evidence = EvidenceState.NotVerified;
            return;
        }
        var shown = await session.AttrAsync("[data-testid=sa-current]", "data-snapshot");
        var origin = await session.TextAsync("[data-testid=sa-current-origin]");
        var importRef = await session.AttrAsync("[data-testid=sa-import-provenance]", "data-import");
        var fingerprint = await session.AttrAsync("[data-testid=sa-current-fingerprint]", "title");
        var rows = await session.Page.Locator("[data-testid=sa-evidence-row]").EvaluateAllAsync<string[]>("els => els.map(e => e.getAttribute('data-area') + '=' + e.getAttribute('data-status'))");
        var areas = await session.Page.Locator("[data-testid=sa-area-card]").EvaluateAllAsync<string[]>("els => els.map(e => e.getAttribute('data-area') + '=' + e.getAttribute('data-status'))");
        result.Observe("snapshot", shown).Observe("origin", origin).Observe("import", importRef).Observe("evidence areas", string.Join(", ", rows)).Observe("analysis areas", string.Join(", ", areas));
        RecordIdentity(context, FeatureId, "snapshot", shown);
        if (!string.Equals(shown, context.Workspace.SourceSnapshotId, StringComparison.OrdinalIgnoreCase))
            result.Defect("source-analysis-wrong-snapshot", "Source Analysis shows a different snapshot than the one the import created.");
        result.Provenance = string.Equals(fingerprint, context.Workspace.ArchiveSha256, StringComparison.OrdinalIgnoreCase) && origin.Contains("Import", StringComparison.OrdinalIgnoreCase)
            ? ProvenanceState.Traced : ProvenanceState.PartiallyTraced;
        if (context.Workspace.ImportId is { } importId && importRef is not null && !string.Equals(importRef, importId, StringComparison.OrdinalIgnoreCase))
            result.Defect("source-analysis-import-mismatch", "The snapshot's import provenance names another import.");

        if (await SnapshotAsync(context, session, ct) is { } snap)
        {
            var technologies = Arr(snap, "technologyCoverage", "technologies").Select(t => Str(t, "displayName") ?? "").Where(t => t.Length > 0).ToList();
            var projects = Arr(snap, "projects").Count();
            result.Observe("projects", projects).Observe("technologies", string.Join(", ", technologies)).Observe("files analyzed", Str(snap, "archive", "filesAnalyzed"));
            result.Fact([.. technologies]);
            result.DataFromCount(projects + technologies.Count, DataState.NoApplicableData);
        }
        else result.Defect("snapshot-not-listed", "The run's snapshot is not returned by the Source Analysis API.");
    }
}

/// <summary>Technology Coverage reads the same snapshot without a Target Environment; support levels are never upgraded to "verified".</summary>
public sealed class TechnologyCoverageFeature : AcceptanceFeature
{
    public override string FeatureId => "technology-coverage";
    public override string DisplayName => "Technology Coverage";
    public override string Area => "Source Review";
    public override string Route => "/technology-coverage";
    public override IReadOnlyList<string> DependsOn => [ImportFeatureId, "source-analysis"];

    protected override async Task RunAsync(RealProjectAcceptanceContext context, PlaywrightAcceptanceSession session, FeatureResultBuilder result, CancellationToken ct)
    {
        await OpenAsync(session, result);
        await session.Page.Locator("[data-testid=tc-page]").WaitForAsync();
        await session.WaitAnyAsync(60_000, "[data-testid=tc-detected]", "[data-testid=tc-no-technologies]", "[data-testid=tc-unavailable]");
        var snapshotLabel = await session.TextAsync("[data-testid=tc-snapshot]");
        var count = await session.TextAsync("[data-testid=tc-technologies]");
        var rows = await session.Page.Locator("tr[data-testid^=tc-tech-]").EvaluateAllAsync<string[]>("els => els.map(e => e.getAttribute('data-testid').substring(8) + '=' + (e.getAttribute('data-overall') || '—'))");
        result.Observe("snapshot", snapshotLabel).Observe("technologies", count).Observe("rows", string.Join(", ", rows)).Observe("no-target notice", await session.ExistsAsync("[data-testid=tc-no-target]") ? "shown" : "absent");
        if (snapshotLabel is "" or "None") result.Defect("technology-coverage-no-snapshot", "Technology Coverage does not use the imported snapshot.");
        else if (context.Preparation.Fingerprint is { } fp && !snapshotLabel.Contains(Path.GetFileNameWithoutExtension(fp.FileName), StringComparison.OrdinalIgnoreCase))
            result.Warn("technology-coverage-snapshot-label", $"The snapshot label \"{snapshotLabel}\" does not name the imported archive.");
        result.DataFromCount(rows.Length, DataState.NoApplicableData);
        if (rows.Length == 0 && Number(count) > 0) result.Defect("technology-coverage-rows", "Technologies are counted but not listed.");
        result.Provenance = ProvenanceState.Traced;
        RecordIdentity(context, FeatureId, "archive", snapshotLabel);
    }
}

/// <summary>Dependency Review in Source mode over the run's snapshot: choose it, run, and check the stored result is scoped to it.</summary>
public sealed class DependencyReviewFeature : AcceptanceFeature
{
    public override string FeatureId => "dependency-review";
    public override string DisplayName => "Dependency Review";
    public override string Area => "Source Review";
    public override string Route => "/dependency-review";
    public override IReadOnlyList<string> DependsOn => [ImportFeatureId, "source-analysis"];

    protected override async Task RunAsync(RealProjectAcceptanceContext context, PlaywrightAcceptanceSession session, FeatureResultBuilder result, CancellationToken ct)
    {
        await OpenAsync(session, result);
        await session.Page.Locator("[data-testid=dr-mode][data-mode=Source]").ClickAsync();
        var picked = await session.WaitAnyAsync(60_000, "[data-testid=dr-use-current]", "[data-testid=dr-primary]");
        if (picked == "[data-testid=dr-use-current]") await session.Page.Locator("[data-testid=dr-use-current]").ClickAsync();
        else if (picked == "[data-testid=dr-primary]" && context.Workspace.SourceSnapshotId is { } id) await session.Page.Locator("[data-testid=dr-primary]").SelectOptionAsync(id);
        else { result.Defect("dependency-review-no-snapshot", "Dependency Review offers no Source Analysis snapshot."); result.Evidence = EvidenceState.NotVerified; return; }
        var run = session.Page.Locator("[data-testid=dr-run]");
        await session.Page.WaitForFunctionAsync("() => { const b = document.querySelector('[data-testid=dr-run]'); return b && !b.disabled; }", null, new() { Timeout = 60_000 });
        await run.ClickAsync();
        await session.Page.WaitForFunctionAsync("() => { const s = document.querySelector('[data-testid=dr-status]'); const e = document.querySelector('[data-testid=dr-error]'); return e || (s && /completed/i.test(s.innerText)); }", null, new() { Timeout = 300_000 });
        if (await session.ExistsAsync("[data-testid=dr-error]"))
        {
            result.Execution = ExecutionStatus.Failed;
            result.Defect("dependency-review-failed", await session.TextAsync("[data-testid=dr-error]"));
            return;
        }
        var primary = await session.AttrAsync("[data-testid=dr-result-scope-entry][data-role=Primary]", "data-snapshot");
        var depCounts = await session.Page.Locator("[data-testid=dr-dep-count]").AllInnerTextsAsync();
        var declared = depCounts.Sum(Number);
        result.Observe("status", await session.TextAsync("[data-testid=dr-status]")).Observe("primary snapshot", primary).Observe("repositories", depCounts.Count).Observe("declared dependencies", declared);
        RecordIdentity(context, FeatureId, "snapshot", primary);
        result.Provenance = string.Equals(primary, context.Workspace.SourceSnapshotId, StringComparison.OrdinalIgnoreCase) ? ProvenanceState.Traced : ProvenanceState.Missing;
        if (result.Provenance == ProvenanceState.Missing) result.Defect("dependency-review-wrong-snapshot", "The stored result is scoped to another snapshot than the import's.");
        result.DataFromCount(declared, DataState.NoApplicableData);
        await ExportAsync(session, result, session.Page.Locator("[data-testid=dr-export]"), context);
    }
}

/// <summary>Pipeline Review over the snapshot's CI/CD evidence: defined pipelines (not executed runs).</summary>
public sealed class PipelineReviewFeature : AcceptanceFeature
{
    public override string FeatureId => "pipeline-review";
    public override string DisplayName => "Pipeline Review";
    public override string Area => "Source Review";
    public override string Route => "/pipeline-review";
    public override IReadOnlyList<string> DependsOn => [ImportFeatureId, "source-analysis"];

    protected override async Task RunAsync(RealProjectAcceptanceContext context, PlaywrightAcceptanceSession session, FeatureResultBuilder result, CancellationToken ct)
    {
        await OpenAsync(session, result);
        await session.Page.Locator("[data-testid=pr-page][aria-busy=false]").WaitForAsync(new() { Timeout = 120_000 });
        if (await session.ExistsAsync("[data-testid=pr-source-required]"))
        {
            result.Defect("pipeline-review-no-source", "Pipeline Review asks for source although the import created a snapshot.");
            result.Evidence = EvidenceState.NotVerified;
            return;
        }
        await session.WaitAnyAsync(120_000, "[data-testid=pr-pipeline-count]", "[data-testid=pr-error]", "[data-testid=pr-placeholder]");
        var selected = await session.Page.Locator("[data-testid=pr-snapshot]").InputValueAsync();
        var count = await session.TextAsync("[data-testid=pr-pipeline-count]");
        var error = await session.TextAsync("[data-testid=pr-error]");
        result.Observe("snapshot", selected).Observe("pipelines", count).Observe("error", error.Length > 0 ? error : null);
        RecordIdentity(context, FeatureId, "snapshot", selected);
        result.Provenance = string.Equals(selected, context.Workspace.SourceSnapshotId, StringComparison.OrdinalIgnoreCase) ? ProvenanceState.Traced : ProvenanceState.Missing;
        if (result.Provenance == ProvenanceState.Missing) result.Defect("pipeline-review-wrong-snapshot", "Pipeline Review selected another snapshot than the import's.");
        if (error.Length > 0) result.Defect("pipeline-review-error", error);
        var pipelines = Number(count);
        if (await SnapshotAsync(context, session, ct) is { } snap)
        {
            var defined = Arr(snap, "evidenceDomains", "ciCd", "pipelines").Count(p => Str(p, "isTemplate") != "true");
            result.Observe("pipelines in snapshot", defined);
            if (defined > 0 && pipelines == 0) result.Defect("pipeline-review-false-zero", $"The snapshot has {defined} pipeline definition(s) but Pipeline Review shows 0.");
        }
        result.DataFromCount(pipelines, DataState.NoApplicableData);
        await ExportAsync(session, result, session.Page.Locator("[data-testid=pr-export]"), context);
    }
}

/// <summary>
/// Environment Analysis = declared configuration evidence (source) + observed Azure state (runtime, needs a target and sign-in).
/// Without a runtime profile the observed half is NotVerified; the declared half is read from the run's snapshot.
/// </summary>
public sealed class EnvironmentAnalysisFeature : AcceptanceFeature
{
    public override string FeatureId => "environment-analysis";
    public override string DisplayName => "Environment Analysis";
    public override string Area => "Source Review";
    public override string Route => "/azure-environment";
    public override IReadOnlyList<string> DependsOn => [ImportFeatureId, "source-analysis"];

    protected override async Task RunAsync(RealProjectAcceptanceContext context, PlaywrightAcceptanceSession session, FeatureResultBuilder result, CancellationToken ct)
    {
        await OpenAsync(session, result);
        var state = await session.WaitAnyAsync(60_000, "[data-testid=az-no-target]", "[data-testid=az-connection]", "[data-testid=az-page]");
        result.Observe("page state", state);
        var files = 0;
        if (await SnapshotAsync(context, session, ct) is { } snap)
        {
            files = Arr(snap, "evidenceDomains", "configuration", "files").Count();
            result.Observe("declared configuration files", files).Observe("configuration status", Str(snap, "evidenceDomains", "configuration", "status"));
        }
        result.DataFromCount(files, DataState.NoApplicableData);
        result.Provenance = files > 0 ? ProvenanceState.Traced : ProvenanceState.NotApplicable;
        if (!context.RuntimeProfileConfigured)
        {
            result.Execution = ExecutionStatus.Partial;
            result.Evidence = files > 0 ? EvidenceState.PartiallyVerified : EvidenceState.NotVerified;
            result.Note("Declared configuration verified from source; observed Azure state needs a Target Environment and Azure sign-in (NotVerified).");
            if (state != "[data-testid=az-no-target]") result.Warn("environment-analysis-state", "Without a target the page should say no Target Environment is selected.");
        }
    }
}

/// <summary>Contracts of one type, read from the snapshot and from the Source Analysis Contracts workspace (both must agree).</summary>
public sealed class ContractEvidenceFeature(string featureId, string displayName, params string[] contractTypes) : AcceptanceFeature
{
    public override string FeatureId => featureId;
    public override string DisplayName => displayName;
    public override string Area => "Source Evidence";
    public override string Route => "/source-analysis";
    public override IReadOnlyList<string> DependsOn => [ImportFeatureId, "source-analysis"];

    protected override async Task RunAsync(RealProjectAcceptanceContext context, PlaywrightAcceptanceSession session, FeatureResultBuilder result, CancellationToken ct)
    {
        var snap = await SnapshotAsync(context, session, ct);
        var inSnapshot = snap is { } s ? Arr(s, "evidenceDomains", "contracts", "contracts").Where(c => contractTypes.Contains(Str(c, "type"))).ToList() : [];
        await OpenSourceAreaAsync(session, result, "Contracts");
        await session.WaitAnyAsync(30_000, "[data-testid=sd-contracts-overview]", "[data-testid=sd-contracts-not-assessed]");
        var shown = 0;
        foreach (var type in contractTypes) shown += await session.CountAsync($"[data-testid=sd-contract][data-type={type}]");
        var operations = inSnapshot.Sum(c => Arr(c, "operations").Count());
        result.Observe("in snapshot", inSnapshot.Count).Observe("shown", shown).Observe("operations", operations)
            .Observe("files", string.Join(", ", inSnapshot.Select(c => Str(c, "file")).Distinct().Take(6)));
        if (inSnapshot.Count != shown && !await session.ExistsAsync("[data-testid=sd-contracts-shown]"))
            result.Defect("contract-count-mismatch", $"The snapshot has {inSnapshot.Count} {displayName} contract(s) but the Contracts workspace lists {shown}.");
        result.DataFromCount(inSnapshot.Count, DataState.NoApplicableData);
        result.Provenance = inSnapshot.Count == 0 ? ProvenanceState.NotApplicable : inSnapshot.All(c => Str(c, "file") is { Length: > 0 }) ? ProvenanceState.Traced : ProvenanceState.PartiallyTraced;
        if (inSnapshot.Count == 0) result.Note($"No {displayName} contract in the source: an empty result, not a pass.");
    }

    /// <summary>
    /// Source Analysis keeps the chosen area across client-side navigation to the same route; the current-snapshot card is on Overview.
    /// </summary>
    internal static async Task<bool> EnsureSourceOverviewAsync(PlaywrightAcceptanceSession session)
    {
        var state = await session.WaitAnyAsync(60_000, "[data-testid=sa-current]", "[data-testid=area-overview]", "[data-testid=sa-empty]", "[data-testid=sa-error]");
        if (state == "[data-testid=area-overview]")
        {
            await session.Page.Locator("[data-testid=area-overview]").ClickAsync();
            state = await session.WaitAnyAsync(30_000, "[data-testid=sa-current]", "[data-testid=sa-empty]");
        }
        return state == "[data-testid=sa-current]";
    }

    internal static async Task OpenSourceAreaAsync(PlaywrightAcceptanceSession session, FeatureResultBuilder result, string area)
    {
        await session.NavigateAsync("/source-analysis");
        result.Browser = BrowserState.Rendered;
        await EnsureSourceOverviewAsync(session);
        await session.Page.Locator($"[data-testid=area-{area.Replace("/", "").ToLowerInvariant()}]").ClickAsync();
        await session.Page.WaitForTimeoutAsync(400);
    }
}

/// <summary>Infrastructure-as-code evidence: when the source has none, the workspace must say Not assessed/not detected — never "0 resources, compliant".</summary>
public sealed class InfrastructureEvidenceFeature : AcceptanceFeature
{
    public override string FeatureId => "iac-terraform";
    public override string DisplayName => "Infrastructure as Code";
    public override string Area => "Source Evidence";
    public override string Route => "/source-analysis";
    public override IReadOnlyList<string> DependsOn => [ImportFeatureId, "source-analysis"];

    protected override async Task RunAsync(RealProjectAcceptanceContext context, PlaywrightAcceptanceSession session, FeatureResultBuilder result, CancellationToken ct)
    {
        var snap = await SnapshotAsync(context, session, ct);
        var status = snap is { } s ? Str(s, "evidenceDomains", "infrastructure", "status") : null;
        var resources = snap is { } s2 ? Arr(s2, "evidenceDomains", "infrastructure", "resources").Count() : 0;
        await ContractEvidenceFeature.OpenSourceAreaAsync(session, result, "Infrastructure");
        var notAssessed = await session.ExistsAsync("[data-testid=sd-infra-not-assessed]", 10_000);
        var shownResources = await session.CountAsync("[data-testid=sd-infra-resource]");
        result.Observe("status", status).Observe("resources in snapshot", resources).Observe("resources shown", shownResources).Observe("not-assessed notice", notAssessed ? "shown" : "absent");
        result.DataFromCount(resources, DataState.NoApplicableData);
        if (resources == 0 && !notAssessed) result.Defect("iac-false-empty", "No IaC in the source, but the workspace does not say it was not assessed.");
        if (resources > 0 && shownResources == 0 && !await session.ExistsAsync("[data-testid=sd-infra-resource-table]")) result.Defect("iac-not-shown", "IaC resources exist in the snapshot but none are listed.");
        result.Provenance = resources > 0 ? ProvenanceState.Traced : ProvenanceState.NotApplicable;
    }
}

/// <summary>Generated documentation (e.g. autodoc output) recognised as its own evidence type with module/drift information.</summary>
public sealed class GeneratedDocumentationFeature : AcceptanceFeature
{
    public override string FeatureId => "generated-documentation";
    public override string DisplayName => "Generated Documentation";
    public override string Area => "Source Evidence";
    public override string Route => "/source-analysis";
    public override IReadOnlyList<string> DependsOn => [ImportFeatureId, "source-analysis"];

    protected override async Task RunAsync(RealProjectAcceptanceContext context, PlaywrightAcceptanceSession session, FeatureResultBuilder result, CancellationToken ct)
    {
        var snap = await SnapshotAsync(context, session, ct);
        JsonElement docs = default;
        var has = snap is { } s && TryProperty(s, "generatedDocumentation", out docs) && docs.ValueKind == JsonValueKind.Object;
        var documents = has ? Arr(docs, "documents").Count() : 0;
        var modules = has ? Arr(docs, "modules").Count() : 0;
        await OpenAsync(session, result);
        await ContractEvidenceFeature.EnsureSourceOverviewAsync(session);
        var status = await session.TextAsync("[data-testid=sa-generated-docs-status]");
        result.Observe("documents", documents).Observe("modules", modules).Observe("status", status)
            .Observe("generators", has ? string.Join(", ", Arr(docs, "generators").Select(g => Str(g, "generatorType") ?? Str(g, "type")).Distinct()) : null);
        if (documents > 0 && !await session.ExistsAsync("[data-testid=sa-generated-docs]"))
            result.Defect("generated-docs-hidden", "The snapshot has generated documentation, but Source Analysis does not show it.");
        result.DataFromCount(documents, DataState.NoApplicableData);
        result.Provenance = documents > 0 ? ProvenanceState.Traced : ProvenanceState.NotApplicable;
    }
}
