using BirkNext.RealProjectAcceptance;
using Microsoft.Playwright;

namespace BirkNext.Web.PlaywrightTests.RealProjectAcceptance.Features;

/// <summary>
/// One document explorer over the imported workspace: the shared host must load the role's artifact from the CURRENT workspace (never
/// "no workspace", never a Sample Project), show its file/source, render explorer content, and expose the rendered document identity
/// that Explorer Text Coverage later cross-checks. Artifact choice is made through the production chooser when several exist.
/// </summary>
public sealed class ExplorerFeature(string featureId, string displayName, string route, string role, string contentSelector, string contentLabel, bool hasExport = false) : AcceptanceFeature
{
    public const string RenderedDocumentKeyPrefix = "rendered-document:";

    public override string FeatureId => featureId;
    public override string DisplayName => displayName;
    public override string Area => "Document Explorers";
    public override string Route => route;
    public override AcceptanceMode MinimumMode => featureId == "specification-explorer" ? AcceptanceMode.Smoke : AcceptanceMode.Standard;
    public override IReadOnlyList<string> RequiredEvidence => [$"{role} document in the imported workspace"];

    public static IReadOnlyList<ExplorerFeature> All() =>
    [
        new("specification-explorer", "Specification Explorer", "/specification-explorer", "Specification", "[data-testid=requirements-metric]", "requirements"),
        new("constitution-explorer", "Constitution Explorer", "/constitution-explorer", "Constitution", ".ce-hcard-principles .ce-health-value", "principles"),
        new("plan-explorer", "Plan Explorer", "/plan-explorer", "Plan", "[data-testid=pe-tab]", "plan tabs"),
        new("task-explorer", "Task Explorer", "/task-explorer", "Tasks", "[data-testid=te-summary-artifact]", "task summary"),
        new("data-model-explorer", "Data Model Explorer", "/data-model-explorer", "DataModel", ".dme-stat .dme-stat-value", "data model statistics", hasExport: true),
    ];

    protected override async Task RunAsync(RealProjectAcceptanceContext context, PlaywrightAcceptanceSession session, FeatureResultBuilder result, CancellationToken ct)
    {
        await OpenAsync(session, result);
        var host = session.Page.Locator("[data-testid=artifact-explorer]");
        await host.First.WaitForAsync(new() { State = WaitForSelectorState.Attached });
        await session.Page.WaitForFunctionAsync("() => { const h = document.querySelector('[data-testid=artifact-explorer]'); return h && h.getAttribute('data-status') !== 'Loading'; }", null, new() { Timeout = 120_000 });
        var status = await session.AttrAsync("[data-testid=artifact-explorer]", "data-status");
        var hostRole = await session.AttrAsync("[data-testid=artifact-explorer]", "data-role");
        result.Observe("host role", hostRole).Observe("initial status", status);

        if (status == "SelectionRequired")
        {
            var candidates = await session.CountAsync("[data-testid=artifact-explorer-choice]");
            result.Observe("candidates", candidates).Note($"{candidates} {role} artifacts: the first was chosen through the production chooser.");
            await session.Page.Locator("[data-testid=artifact-explorer-choice]").First.ClickAsync();
            await session.Page.WaitForFunctionAsync("() => document.querySelector('[data-testid=artifact-explorer]')?.getAttribute('data-status') === 'Loaded'", null, new() { Timeout = 120_000 });
            status = "Loaded";
        }
        else if (await session.CountAsync("[data-testid=artifact-explorer-selector] option") is var options and > 0)
        {
            result.Observe("candidates", options);
        }

        if (await session.ExistsAsync("[data-testid=artifact-explorer-empty][data-state=NoWorkspace]"))
            result.Defect("explorer-no-workspace", "The explorer says no workspace is loaded although a project was imported.");
        switch (status)
        {
            case "Loaded":
                break;
            case "Empty":
                result.Data = DataState.NoApplicableData;
                result.Note($"The imported workspace has no {role} artifact.");
                return;
            default:
                result.Execution = ExecutionStatus.Failed;
                result.Evidence = EvidenceState.NotVerified;
                result.Defect("explorer-not-loaded", $"The explorer ended in state {status} for the imported workspace.");
                return;
        }

        var file = await session.TextAsync("[data-testid=artifact-explorer-file]");
        var source = await session.TextAsync("[data-testid=artifact-explorer-source]");
        var documentId = await session.AttrAsync("[data-testid=artifact-explorer]", "data-birknext-document-id");
        result.Observe("file", file).Observe("source", source).Observe("rendered document id", documentId);
        if (string.IsNullOrEmpty(documentId)) result.Defect("explorer-identity-missing", "The loaded explorer exposes no document identity.");
        else context.Shared[RenderedDocumentKeyPrefix + featureId] = documentId;
        RecordIdentity(context, featureId, "project", context.Workspace.ProjectName);

        await session.WaitAnyAsync(30_000, contentSelector);
        var content = await session.CountAsync(contentSelector);
        var firstValue = content > 0 ? (await session.Page.Locator(contentSelector).First.InnerTextAsync()).Trim() : "";
        var projected = await session.CountAsync("[data-birknext-projection-id]");
        result.Observe(contentLabel, content > 0 ? firstValue.Split('\n')[0] : "none").Observe("projection elements", projected);
        if (content == 0) result.Defect("explorer-content-missing", $"The explorer loaded the artifact but rendered no {contentLabel}.");
        if (content > 0 && firstValue.Split('\n')[0].Trim() == "0" && featureId == "specification-explorer")
            result.Defect("explorer-false-zero", "Specification Explorer shows 0 requirements for a loaded specification.");
        result.DataFromCount(content, DataState.NoApplicableData);
        result.Provenance = file.Length > 0 && !string.IsNullOrEmpty(documentId) ? ProvenanceState.Traced : ProvenanceState.PartiallyTraced;
        if (hasExport) await ExportAsync(session, result, session.Page.GetByRole(AriaRole.Button, new() { Name = "Export HTML" }), context, expectIdentity: false);
    }
}
