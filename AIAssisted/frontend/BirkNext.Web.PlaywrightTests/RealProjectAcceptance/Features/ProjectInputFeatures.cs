using BirkNext.RealProjectAcceptance;
using Microsoft.Playwright;

namespace BirkNext.Web.PlaywrightTests.RealProjectAcceptance.Features;

/// <summary>
/// The single import of the run, through the production Project Import page: preview fingerprint → import → documents to the current
/// workspace, source to one Source Analysis snapshot. Establishes the workspace identity every later feature is checked against.
/// </summary>
public sealed class ProjectImportFeature : AcceptanceFeature
{
    public override string FeatureId => ImportFeatureId;
    public override string DisplayName => "Project Import";
    public override string Area => "Project Inputs";
    public override string Route => "/project-import";
    public override AcceptanceMode MinimumMode => AcceptanceMode.Smoke;
    public override IReadOnlyList<string> RequiredEvidence => ["external project archive"];
    public override IReadOnlyList<string> DependsOn => [];

    protected override async Task RunAsync(RealProjectAcceptanceContext context, PlaywrightAcceptanceSession session, FeatureResultBuilder result, CancellationToken ct)
    {
        var archive = context.Dataset.ArchivePath!;
        var sha = context.Preparation.Fingerprint!.Sha256;
        // Fresh context: a full load of the app is the first navigation of the run.
        await session.Page.GotoAsync(session.FrontendUrl + Route, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 120_000 });
        var chooser = await BirkNext.Web.PlaywrightTests.Fixtures.SyntheticProjects.OpenZipFormAsync(session.Page);
        result.Browser = BrowserState.Rendered;
        await chooser.SetInputFilesAsync(archive);
        var preview = await session.WaitAnyAsync(300_000, "[data-testid=pi-preview]", "[data-testid=pi-failure]");
        if (preview != "[data-testid=pi-preview]")
        {
            result.Execution = ExecutionStatus.Failed;
            result.Defect("import-preview-failed", $"The import preview did not succeed ({await session.AttrAsync("[data-testid=pi-failure]", "data-code")}).");
            return;
        }
        var previewSha = await session.AttrAsync("[data-testid=pi-fingerprint]", "title");
        result.Observe("preview fingerprint", previewSha);
        if (!string.Equals(previewSha, sha, StringComparison.OrdinalIgnoreCase))
            result.Defect("fingerprint-mismatch", "The import preview fingerprint differs from the archive SHA-256 measured before import.");

        // The project name is shown in the preview only; record it before importing. A new project has an editable name field (its note says
        // "Suggested from the archive file name: …"); an existing project is named in the preview's Project row.
        var nameInput = session.Page.Locator("[data-testid=pi-new-project-name]");
        var previewName = await nameInput.CountAsync() > 0 ? (await nameInput.First.InputValueAsync()).Trim()
            : (await session.Page.Locator("[data-testid=pi-project-name]").EvaluateAllAsync<string[]>(
                "els => els.map(e => (e.firstChild && e.firstChild.nodeType === 3 ? e.firstChild.textContent : e.innerText).trim())")).FirstOrDefault() ?? "";
        result.Observe("project name (preview)", previewName);
        await session.Page.Locator("[data-testid=pi-import]").ClickAsync();
        if (await session.WaitAnyAsync(900_000, "[data-testid=pi-result]", "[data-testid=pi-failure]") != "[data-testid=pi-result]")
        {
            result.Execution = ExecutionStatus.Failed;
            result.Defect("import-failed", "Project Import did not complete.");
            return;
        }
        var ambiguous = await session.Page.Locator("[data-testid=pi-role]").EvaluateAllAsync<string[]>("els => els.map(e => e.getAttribute('data-role') + '=' + e.getAttribute('data-state'))");
        // A role is imported when it has a detected artifact or several candidates; "Not found"/"Missing" states are absent roles.
        var detected = ambiguous.Count(r => r.Split('=', 2)[1] is var state && state.Length > 0
            && !System.Text.RegularExpressions.Regex.IsMatch(state, "not|missing|absent|none", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        context.Shared[ImportRolesKey] = ambiguous.Select(r => r.Split('=', 2)).Where(r => r.Length == 2)
            .ToDictionary(r => r[0], r => r[1], StringComparer.OrdinalIgnoreCase) as IReadOnlyDictionary<string, string>;
        var sourceState = await session.AttrAsync("[data-testid=pi-source]", "data-state");
        var snapshotId = await session.AttrAsync("[data-testid=pi-snapshot-id]", "title");
        var importId = await session.TextAsync("[data-testid=pi-import-id]");
        var technologies = await session.Page.Locator("[data-testid=pi-technology]").AllInnerTextsAsync();
        result.Observe("outcome", await session.AttrAsync("[data-testid=pi-result]", "data-outcome"))
            .Observe("document roles", string.Join(", ", ambiguous))
            .Observe("source", sourceState).Observe("snapshot", snapshotId).Observe("import id", importId)
            .Observe("technologies", string.Join(", ", technologies.Select(t => t.Trim())));
        result.Fact([.. technologies.Select(t => t.Trim())]);
        if (await session.ExistsAsync("[data-testid=pi-save-warning]"))
            result.Defect("import-not-persisted", "The import result says the workspace was not saved.");
        if (sourceState is not ("Created" or "Reused") || string.IsNullOrWhiteSpace(snapshotId))
            result.Defect("source-snapshot-missing", $"The archive contains source, but no Source Analysis snapshot was produced (state {sourceState}).");

        var state = await session.GetJsonAsync("api/workspace-persistence/current-state", ct);
        context.Workspace = new WorkspaceIdentity
        {
            ProjectName = previewName.Length > 0 ? previewName : state is { } s1 ? Str(s1, "projectName") : null,
            WorkspaceId = state is { } s ? Str(s, "currentWorkspaceId") : null,
            ImportId = importId.Length > 0 ? importId : null,
            SourceSnapshotId = snapshotId,
            ArchiveSha256 = sha,
            ImportedDocuments = detected,
            SourceDetected = sourceState is "Created" or "Reused",
        };
        result.Observe("workspace", context.Workspace.WorkspaceId).Observe("project", context.Workspace.ProjectName);

        // The snapshot must point back to THIS import and THIS archive.
        var snapshot = await SnapshotListedAsync(context, session, ct) ? await SnapshotAsync(context, session, ct) : null;
        if (snapshot is not { } snap)
        {
            result.Defect("snapshot-not-listed", "The snapshot produced by the import is not listed by Source Analysis.");
            result.Provenance = ProvenanceState.Missing;
        }
        else
        {
            var snapshotSha = Str(snap, "projectImport", "archiveSha256") ?? Str(snap, "archive", "sha256");
            result.Provenance = string.Equals(snapshotSha, sha, StringComparison.OrdinalIgnoreCase) ? ProvenanceState.Traced : ProvenanceState.Missing;
            if (result.Provenance == ProvenanceState.Missing) result.Defect("snapshot-provenance", "The Source Analysis snapshot does not carry the imported archive's fingerprint.");
            result.Observe("snapshot files analyzed", Str(snap, "archive", "filesAnalyzed")).Observe("snapshot status", Str(snap, "status"));
        }
        result.Data = detected > 0 || context.Workspace.SourceDetected ? DataState.RealDataObserved : DataState.NoApplicableData;
        if (string.IsNullOrWhiteSpace(context.Workspace.ProjectName)) result.Warn("import-project-name", "The import named no project.");
        result.Evidence = result.Findings.Any(f => f.Severity == AcceptanceFindingSeverity.Defect) ? EvidenceState.PartiallyVerified : EvidenceState.Verified;
        RecordIdentity(context, FeatureId, "snapshot", snapshotId);
    }
}

/// <summary>Dashboard reflects the imported project (origin, roles, artifact cards) and neutral states for anything not run.</summary>
public sealed class DashboardFeature : AcceptanceFeature
{
    public override string FeatureId => "dashboard";
    public override string DisplayName => "Dashboard";
    public override string Area => "Getting Started";
    public override string Route => "/dashboard";
    public override AcceptanceMode MinimumMode => AcceptanceMode.Smoke;

    protected override async Task RunAsync(RealProjectAcceptanceContext context, PlaywrightAcceptanceSession session, FeatureResultBuilder result, CancellationToken ct)
    {
        await OpenAsync(session, result);
        await session.Page.Locator("[data-testid=db-workspace-panel]").WaitForAsync();
        await session.WaitAnyAsync(30_000, "[data-testid=db-workspace-name]", "[data-testid=db-import-project]");
        await session.WaitAnyAsync(30_000, "[data-testid=db-artifact-card]");
        var name = await session.TextAsync("[data-testid=db-workspace-name]");
        // The origin is the import notice and the imported evidence packages (no separate origin line since 2026-11).
        var origin = string.Join(" · ", new[] { await session.TextAsync("[data-testid=db-import-success]"), await session.TextAsync("[data-testid=db-workspace-packages]") }.Where(t => t.Length > 0));
        var roles = await session.TextAsync("[data-testid=db-workspace-roles]");
        var loadedCards = await session.CountAsync("[data-testid=db-artifact-card][data-availability=Available]");
        var governance = await session.AttrAsync("[data-testid=db-health-area][data-key=governance]", "data-status") ?? "";
        result.Observe("workspace", name).Observe("origin", origin).Observe("roles", roles).Observe("available artifact cards", loadedCards).Observe("governance", governance);
        RecordIdentity(context, FeatureId, "project", name.Replace("📁", "").Trim());
        if (await session.ExistsAsync("[data-testid=db-workspace-empty]")) result.Defect("dashboard-onboarding-after-import", "The dashboard shows the empty state although a project was imported.");
        if (name.Length == 0) result.Defect("dashboard-project-not-recognised", "The dashboard does not name the current workspace.");
        if (roles.Contains("unavailable", StringComparison.OrdinalIgnoreCase) || roles.Contains("Unable", StringComparison.OrdinalIgnoreCase))
            result.Defect("dashboard-roles-unavailable", $"Artifact availability reads \"{roles}\".");
        if (!origin.Contains("import", StringComparison.OrdinalIgnoreCase)) result.Warn("dashboard-origin", $"Origin reads \"{origin}\" instead of the imported archive.");
        if (governance is not ("" or "NotAssessed" or "NotApplicable")) result.Warn("dashboard-governance-value", $"Governance is {governance} although no decision was made in this run.");
        result.DataFromCount(loadedCards, context.Workspace.ImportedDocuments > 0 ? DataState.NotAssessed : DataState.NoApplicableData);
        if (loadedCards == 0 && context.Workspace.ImportedDocuments > 0)
            result.Defect("dashboard-false-zero", $"No artifact card is marked available although the import found {context.Workspace.ImportedDocuments} document role(s).");
        await ExportAsync(session, result, session.Page.GetByRole(AriaRole.Button, new() { Name = "Export HTML" }), context);
    }
}

/// <summary>The User Guide needs no project data: it must load and keep its sections and links.</summary>
public sealed class UserGuideFeature : AcceptanceFeature
{
    public override string FeatureId => "user-guide";
    public override string DisplayName => "User Guide (Help Center)";
    public override string Area => "Getting Started";
    public override string Route => "/user-guide";
    public override AcceptanceType AcceptanceType => AcceptanceType.Navigation;
    public override IReadOnlyList<string> RequiredEvidence => [];

    protected override async Task RunAsync(RealProjectAcceptanceContext context, PlaywrightAcceptanceSession session, FeatureResultBuilder result, CancellationToken ct)
    {
        await OpenAsync(session, result);
        // The User Guide is the Help Center since 2026-11: a home with categories and topic links, articles opened by topic.
        await session.Page.Locator("[data-testid=ug-page]").WaitForAsync();
        var sections = await session.CountAsync("[data-testid=ug-category]");
        var topics = await session.CountAsync("[data-testid=ug-category-link]");
        var links = await session.CountAsync("main a[href]");
        result.Observe("categories", sections).Observe("topic links", topics).Observe("links", links);
        if (sections == 0 || topics == 0) result.Defect("user-guide-empty", "The Help Center rendered no categories or topics.");
        result.Data = DataState.NoApplicableData;
    }
}

/// <summary>Sample Projects stays optional/demo: it acknowledges the imported project and never forces a Sample Project.</summary>
public sealed class SampleProjectsFeature : AcceptanceFeature
{
    public override string FeatureId => "sample-projects";
    public override string DisplayName => "Sample Projects";
    public override string Area => "Project Inputs";
    public override string Route => "/sample-projects";

    protected override async Task RunAsync(RealProjectAcceptanceContext context, PlaywrightAcceptanceSession session, FeatureResultBuilder result, CancellationToken ct)
    {
        await OpenAsync(session, result);
        var state = await session.WaitAnyAsync(60_000, "[data-testid=sp-imported-project]", "[data-testid=sp-selected-project]", "[data-testid=sp-selection-unavailable]", "[data-testid=sp-card]", "[data-testid=sp-empty]");
        result.Observe("state", state);
        if (state != "[data-testid=sp-imported-project]")
            result.Defect("sample-projects-ignores-import", "Sample Projects does not acknowledge the imported project as the current workspace.");
        if (await session.ExistsAsync("[data-testid=sp-selected-project]") || await session.ExistsAsync("[data-testid=sp-selection-unavailable]"))
            result.Defect("sample-project-forced", "A Sample Project selection is presented as current alongside the imported project.");
        result.Data = DataState.NoApplicableData;
    }
}

/// <summary>Target Environments loads; with no runtime pairing nothing is invented — the run creates no target.</summary>
public sealed class TargetEnvironmentsFeature : AcceptanceFeature
{
    public override string FeatureId => "target-environments";
    public override string DisplayName => "Target Environments";
    public override string Area => "Project Inputs";
    public override string Route => "/admin/system-settings?section=target-environments";
    public override AcceptanceType AcceptanceType => AcceptanceType.Runtime;

    protected override async Task RunAsync(RealProjectAcceptanceContext context, PlaywrightAcceptanceSession session, FeatureResultBuilder result, CancellationToken ct)
    {
        await OpenAsync(session, result);
        await session.Page.WaitForTimeoutAsync(1500);
        var profiles = await session.CountAsync(".fa-profile-chip");
        result.Observe("target profiles", profiles).Observe("runtime profile paired", context.RuntimeProfileConfigured ? "yes" : "no");
        result.Data = DataState.NoApplicableData;
        result.Evidence = context.RuntimeProfileConfigured ? EvidenceState.PartiallyVerified : EvidenceState.NotVerified;
        result.Note(context.RuntimeProfileConfigured ? "Runtime profile paired; runtime checks consume it." : "No runtime target configured: runtime acceptance is NotVerified by design.");
    }
}
