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
        await session.Page.Locator("[data-testid=pi-choose-file]").WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 120_000 });
        result.Browser = BrowserState.Rendered;
        await session.Page.Locator("[data-testid=pi-choose-file]").SetInputFilesAsync(archive);
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

        // The project name is shown in the preview only; record it before importing.
        var previewName = (await session.Page.Locator("[data-testid=pi-project-name]").EvaluateAllAsync<string[]>(
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
        var snapshot = await SnapshotAsync(context, session, ct);
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

/// <summary>Recommended Workflow recognises the imported project: documents and source inputs reflect it, no false "not provided".</summary>
public sealed class RecommendedWorkflowFeature : AcceptanceFeature
{
    public override string FeatureId => "recommended-workflow";
    public override string DisplayName => "Recommended Workflow";
    public override string Area => "Getting Started";
    public override string Route => "/getting-started";
    public override AcceptanceMode MinimumMode => AcceptanceMode.Smoke;

    protected override async Task RunAsync(RealProjectAcceptanceContext context, PlaywrightAcceptanceSession session, FeatureResultBuilder result, CancellationToken ct)
    {
        await OpenAsync(session, result);
        if (await session.WaitAnyAsync(60_000, "[data-testid=rw-inputs]", "[data-testid=rw-no-workspace]", "[data-testid=rw-workspace-error]") != "[data-testid=rw-inputs]")
        {
            result.Defect("workflow-project-not-recognised", "Recommended Workflow does not show the imported project's inputs.");
            result.Evidence = EvidenceState.NotVerified;
            return;
        }
        var documents = await session.AttrAsync("[data-testid=rw-input][data-kind=Documents]", "data-status");
        var source = await session.AttrAsync("[data-testid=rw-input][data-kind=Source]", "data-status");
        var target = await session.AttrAsync("[data-testid=rw-input][data-kind=Target]", "data-status");
        var project = await session.TextAsync("[data-testid=rw-project]");
        var roles = await session.TextAsync("[data-testid=rw-artifact-roles]");
        var steps = await session.Page.Locator("[data-testid=rw-step]").EvaluateAllAsync<string[]>("els => els.map(e => e.getAttribute('data-step') + ':' + e.getAttribute('data-state'))");
        result.Observe("documents input", documents).Observe("source input", source).Observe("target input", target).Observe("project", project).Observe("artifact roles", roles).Observe("steps", string.Join(", ", steps));
        RecordIdentity(context, FeatureId, "project", project);
        if (documents is "Absent" or "Unknown") result.Defect("workflow-false-absent", $"Documents input is {documents} although the import detected {context.Workspace.ImportedDocuments} document role(s).");
        if (context.Workspace.SourceDetected && source is "Absent" or "Unknown") result.Defect("workflow-false-absent", $"Source input is {source} although the import created a snapshot.");
        if (roles.StartsWith("None", StringComparison.OrdinalIgnoreCase) || roles.Contains("Not provided", StringComparison.OrdinalIgnoreCase))
            result.Defect("workflow-false-zero", $"Artifact roles shown as \"{roles}\" for an imported project with documents.");
        var blocked = steps.Count(st => st.EndsWith(":Blocked", StringComparison.Ordinal));
        if (blocked > 0)
            result.Note($"{blocked} review step(s) are Blocked: review gates need a chosen artifact revision, and the import kept several candidates per role (by design).");
        if (target is "Ready") result.Warn("target-ready-without-target", "Target input reports Ready although the acceptance run configured no Target Environment.");
        result.Data = DataState.RealDataObserved;
        result.Provenance = ProvenanceState.NotApplicable;
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
        var name = await session.TextAsync("[data-testid=db-workspace-name]");
        var origin = await session.TextAsync("[data-testid=db-workspace-origin]");
        var roles = await session.TextAsync("[data-testid=db-workspace-roles]");
        var loadedCards = await session.CountAsync("[data-testid=db-artifact-card].db-artifact-loaded");
        var governance = await session.TextAsync("[data-testid=db-governance-value]");
        result.Observe("workspace", name).Observe("origin", origin).Observe("roles", roles).Observe("loaded artifact cards", loadedCards).Observe("governance", governance);
        RecordIdentity(context, FeatureId, "project", name.Replace("📁", "").Trim());
        if (await session.ExistsAsync("[data-testid=onboarding-card]")) result.Defect("dashboard-onboarding-after-import", "The dashboard shows onboarding although a project was imported.");
        if (name.Length == 0) result.Defect("dashboard-project-not-recognised", "The dashboard does not name the current workspace.");
        if (roles.Contains("unavailable", StringComparison.OrdinalIgnoreCase) || roles.Contains("Unable", StringComparison.OrdinalIgnoreCase))
            result.Defect("dashboard-roles-unavailable", $"Artifact availability reads \"{roles}\".");
        if (!origin.Contains("Imported", StringComparison.OrdinalIgnoreCase)) result.Warn("dashboard-origin", $"Origin reads \"{origin}\" instead of the imported archive.");
        if (governance.Length > 0 && governance != "—" && governance.Contains('%')) result.Warn("dashboard-governance-value", $"Governance shows {governance} although no decision was made in this run.");
        result.DataFromCount(loadedCards, DataState.NotAssessed);
        if (loadedCards == 0) result.Defect("dashboard-false-zero", "No artifact card is marked available for an imported project with documents.");
        await ExportAsync(session, result, session.Page.GetByRole(AriaRole.Button, new() { Name = "Export HTML" }), context);
    }
}

/// <summary>The User Guide needs no project data: it must load and keep its sections and links.</summary>
public sealed class UserGuideFeature : AcceptanceFeature
{
    public override string FeatureId => "user-guide";
    public override string DisplayName => "User Guide";
    public override string Area => "Getting Started";
    public override string Route => "/user-guide";
    public override AcceptanceType AcceptanceType => AcceptanceType.Navigation;
    public override IReadOnlyList<string> RequiredEvidence => [];

    protected override async Task RunAsync(RealProjectAcceptanceContext context, PlaywrightAcceptanceSession session, FeatureResultBuilder result, CancellationToken ct)
    {
        await OpenAsync(session, result);
        await session.Page.Locator("#overview").WaitForAsync();
        var sections = await session.CountAsync("section[id]");
        var links = await session.CountAsync("main a[href]");
        result.Observe("sections", sections).Observe("links", links);
        if (sections == 0) result.Defect("user-guide-empty", "The User Guide rendered no sections.");
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
