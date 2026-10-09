using System.Text.Json;
using BirkNext.RealProjectAcceptance;

namespace BirkNext.Web.PlaywrightTests.RealProjectAcceptance.Features;

/// <summary>What a target-gated page may consume from the source snapshot, measured from the snapshot JSON.</summary>
public delegate (int Count, string Description) SourceEvidenceProbe(JsonElement snapshot);

/// <summary>
/// A review whose execution needs a Target Environment (runtime, or target-scoped source selection). Without a runtime profile the run
/// never selects or invents a target: the page must load with an honest no-target state (no Pass, no score), its run action must not be
/// offered as runnable, and — when the review also consumes source — the source evidence it would read is verified on the snapshot.
/// Runtime evidence is NotVerified, which is a successful truthful outcome. Run buttons are NEVER clicked: no sends, no mutations.
/// </summary>
public sealed class TargetGatedFeature(
    string featureId, string displayName, string area, string route, string[] noTargetSelectors, string[] forbiddenRunSelectors,
    SourceEvidenceProbe? sourceProbe = null, AcceptanceType type = AcceptanceType.Runtime, string? note = null) : AcceptanceFeature
{
    public override string FeatureId => featureId;
    public override string DisplayName => displayName;
    public override string Area => area;
    public override string Route => route;
    public override AcceptanceType AcceptanceType => type;

    protected override async Task RunAsync(RealProjectAcceptanceContext context, PlaywrightAcceptanceSession session, FeatureResultBuilder result, CancellationToken ct)
    {
        await OpenAsync(session, result);
        var state = await session.WaitAnyAsync(60_000, noTargetSelectors);
        await session.Page.WaitForTimeoutAsync(500);
        result.Observe("page state", state ?? "no recognised state");
        var body = await session.BodyTextAsync();
        if (context.RuntimeProfileConfigured)
            result.Note("A runtime profile is configured, but runtime execution is outside the bounded acceptance run; nothing was executed.");
        if (state is null) result.Warn("no-target-state-unrecognised", "The page did not show a recognised no-target state.");
        foreach (var run in forbiddenRunSelectors)
            if (await session.CountAsync(run) > 0 && await session.Page.Locator(run).First.IsEnabledAsync())
                result.Defect("runtime-action-runnable-without-target", $"{run} is enabled although no Target Environment is configured.");
        if (System.Text.RegularExpressions.Regex.IsMatch(body, @"\b(Overall|Score)\s*:?\s*\d{1,3}\s*%"))
            result.Warn("score-without-run", "A percentage score is shown although nothing was run for this project.");

        var sourceCount = 0;
        if (sourceProbe is not null && await SnapshotAsync(context, session, ct) is { } snap)
        {
            var (count, description) = sourceProbe(snap);
            sourceCount = count;
            result.Observe("source evidence available", $"{count} {description}");
            result.Provenance = count > 0 ? ProvenanceState.Traced : ProvenanceState.NotApplicable;
        }
        result.Execution = sourceProbe is null ? ExecutionStatus.NotAvailable : ExecutionStatus.Partial;
        // A runtime review stays NotVerified even when its source inputs exist: the inputs are not the runtime evidence.
        result.Evidence = type != AcceptanceType.Runtime && sourceCount > 0 ? EvidenceState.PartiallyVerified : EvidenceState.NotVerified;
        result.Data = sourceProbe is not null && sourceCount > 0 ? DataState.RealDataObserved : DataState.NotAssessed;
        result.Note(note ?? "Runtime evidence needs a Target Environment and was not exercised (NotVerified).");
        RecordIdentity(context, FeatureId, "project", context.Workspace.ProjectName);
    }

    // ── source probes over the snapshot JSON ────────────────────────────────────────────────────────────────────────
    private static int Count(JsonElement e, params string[] path) => Arr(e, path).Count();
    private static bool Has(JsonElement e, string name) => TryProperty(e, name, out var v) && v.ValueKind is JsonValueKind.Object or JsonValueKind.Array;

    public static IReadOnlyList<TargetGatedFeature> All() =>
    [
        new("fqr-runtime", "Frontend Quality Review", "Quality & Testing", "/frontend-quality-review",
            ["[data-testid=fqr-readiness]"], ["[data-testid=fqr-run]"],
            note: "Frontend quality is measured in a browser against a running target; no source-only verdict exists."),
        new("aqr-runtime", "API Quality Review", "Quality & Testing", "/api-quality-review",
            ["[data-testid=aqr-active-target-error]", "[data-testid=aqr-readiness]"], ["[data-testid=aqr-run]"],
            s => (Arr(s, "evidenceDomains", "contracts", "contracts").Count(c => Str(c, "type") == "OpenApi"), "OpenAPI contract(s) AQR can review once a target is set"),
            note: "Contract evidence exists in source; requests (and safe fuzzing) need a target and were not sent."),
        new("iqr-source", "Integration Quality Review", "Quality & Testing", "/integration-quality-review",
            ["[data-testid=iqr-error]", "[data-testid=iqr-readiness]", "[data-testid=iqr-no-systems]"], ["[data-testid=iqr-run]"],
            s => (Count(s, "integrationSignals") + (Has(s, "integrationPath") ? 1 : 0) + (Has(s, "applicationMessagingEvidence") ? 1 : 0), "integration signal(s)/path/messaging evidence on the snapshot"),
            type: AcceptanceType.Source, note: "IQR scopes source evidence by Target Environment; the snapshot's integration evidence is verified, the review itself was not run."),
        new("active-event-testing", "Active Event Testing", "Quality & Testing", "/integration-quality-review",
            ["[data-testid=iqr-error]", "[data-testid=act-no-integration]", "[data-testid=act-summary]"], ["[data-testid=act-run]", "[data-testid=act-start]", "[data-testid=act-confirm-send]"],
            note: "Active sends are never performed by acceptance (ActiveSendAllowed=false); the send path was not invoked."),
        new("performance-test-review", "Performance Test Review", "Quality & Testing", "/performance-test-review",
            ["[data-testid=pt-no-target]", "[data-testid=pt-readiness]"], ["[data-testid=pt-run-start]", "[data-testid=pt-network-check]"]),
        new("critical-e2e", "Critical E2E Regression", "Quality & Testing", "/critical-e2e-regression",
            ["[data-testid=e2e-summary]", "[data-testid=e2e-no-flows]"], ["[data-testid=e2e-run]"],
            note: "No critical flows are configured for an imported project; flows run against a target and were not executed."),
        new("security-classification", "Security Classification", "Security", "/security-classification-review",
            ["[data-testid=sc-no-target]", "[data-testid=sc-summary]"], ["[data-testid=sc-run]"],
            s => (Has(s, "securityClassificationEvidence") ? 1 : 0, "classification evidence block on the snapshot"),
            type: AcceptanceType.Source, note: "Classification source evidence was extracted at import; the review is scoped by Target Environment and was not run."),
        new("security-configuration", "Security Configuration Review", "Security", "/admin/system-settings?section=target-environments&tab=security",
            ["[data-testid=te-environment]", ".fa-profile-chip", "main"], ["[data-testid=sec-accept]", "[data-testid=sec-reject]"],
            s => (Has(s, "securityExpectationsEvidence") ? 1 : 0, "security-expectation evidence block on the snapshot"),
            type: AcceptanceType.Source, note: "Security configuration is reviewed per selected Target Environment profile; none exists in an acceptance run."),
    ];
}

/// <summary>Implementation Traceability reads Azure DevOps work items/commits, not the imported archive: not applicable to an archive dataset.</summary>
public sealed class ImplementationTraceabilityFeature : AcceptanceFeature
{
    public override string FeatureId => "implementation-traceability";
    public override string DisplayName => "Implementation Traceability";
    public override string Area => "Traceability";
    public override string Route => "/implementation-traceability";
    public override AcceptanceType AcceptanceType => AcceptanceType.Runtime;

    protected override async Task RunAsync(RealProjectAcceptanceContext context, PlaywrightAcceptanceSession session, FeatureResultBuilder result, CancellationToken ct)
    {
        await OpenAsync(session, result);
        await session.Page.WaitForTimeoutAsync(1500);
        result.Observe("heading", await session.TextAsync("h1"));
        result.Execution = ExecutionStatus.NotApplicable;
        result.Evidence = EvidenceState.NotApplicable;
        result.Data = DataState.NoApplicableData;
        result.Note("Needs an Azure DevOps provider connection (work items, commits); an archive dataset has none. The page loaded.");
    }
}

/// <summary>
/// The three System Settings diagnostics. They read the backend-configured acceptance archive (the run script points it at the SAME
/// external archive); hash-bound baselines apply only when the archive hash matches the dataset descriptor.
/// </summary>
public sealed class DiagnosticFeature(string featureId, string displayName, string section, string endpoint) : AcceptanceFeature
{
    public const string CoverageExpectedIdsKey = "explorer-coverage:expected-document-ids";
    private static readonly string[] StatusNames = ["Pass", "Partial", "Unsupported", "Fail", "NotRun"];

    public override string FeatureId => featureId;
    public override string DisplayName => displayName;
    public override string Area => "Diagnostics";
    public override string Route => $"/admin/system-settings?section={section}";
    public override AcceptanceType AcceptanceType => AcceptanceType.Diagnostics;
    public override IReadOnlyList<string> RequiredEvidence => ["backend-configured acceptance archive"];

    public static IReadOnlyList<DiagnosticFeature> All() =>
    [
        new("diag-project-compatibility", "Project Compatibility", "project-compatibility", "api/system-diagnostics/project-compatibility/run"),
        new("diag-content-integrity", "Markdown Content Integrity", "content-integrity", "api/system-diagnostics/markdown/content-integrity/run"),
        new("diag-explorer-coverage", "Explorer Text Coverage", "explorer-text-coverage", "api/system-diagnostics/markdown/explorer-coverage/run"),
    ];

    private static string StatusName(JsonElement run, string property) =>
        TryProperty(run, property, out var v) ? v.ValueKind == JsonValueKind.Number && v.GetInt32() is var i && i >= 0 && i < StatusNames.Length ? StatusNames[i] : v.ToString() : "—";

    protected override async Task RunAsync(RealProjectAcceptanceContext context, PlaywrightAcceptanceSession session, FeatureResultBuilder result, CancellationToken ct)
    {
        await OpenAsync(session, result);
        var (status, body) = await session.PostJsonAsync(endpoint, null, ct);
        if (status != 200 || body is not { } run)
        {
            result.Execution = ExecutionStatus.Failed;
            result.Defect("diagnostic-failed", $"{endpoint} returned HTTP {status}.");
            return;
        }
        var realFixture = Str(run, "realProjectFixtureUsed") == "true";
        result.Observe("overall", StatusName(run, "overallStatus")).Observe("real project fixture used", realFixture);
        if (!realFixture)
        {
            result.Execution = ExecutionStatus.Partial;
            result.Evidence = EvidenceState.NotVerified;
            result.Data = DataState.NotAssessed;
            result.Warn("diagnostic-no-real-fixture", "The backend has no acceptance archive configured (ProjectCompatibility:AcceptanceArchivePath); only built-in fixtures ran.");
            return;
        }
        result.Observe("real fixture status", StatusName(run, "realFixtureStatus"));
        switch (featureId)
        {
            case "diag-project-compatibility":
            {
                var scenarios = Arr(run, "scenarios").ToList();
                var failing = scenarios.Where(s => Str(s, "status") is "3" or "Fail").Select(s => Str(s, "name")).ToList();
                result.Observe("scenarios", scenarios.Count).Observe("failing", string.Join(", ", failing));
                var markdownMutation = scenarios.FirstOrDefault(s => Str(s, "name") == "Optional real-project Markdown mutation");
                if (markdownMutation.ValueKind != JsonValueKind.Undefined)
                    result.Observe("markdown mutation", StatusName(markdownMutation, "status"));
                foreach (var name in failing) result.Defect("compatibility-scenario-failed", $"Compatibility scenario failed: {name}");
                result.DataFromCount(scenarios.Count, DataState.NotAssessed);
                break;
            }
            case "diag-content-integrity":
            {
                int N(string p) => int.TryParse(Str(run, p), out var n) ? n : 0;
                var files = N("markdownFilesChecked");
                result.Observe("markdown files", files).Observe("exact import matches", N("exactImportMatches")).Observe("canonical matches", N("canonicalMatches"))
                    .Observe("parser EOF reached", N("parserEofReached")).Observe("parser warnings", N("parserWarnings")).Observe("potential truncations", N("potentialTruncations"));
                if (N("potentialTruncations") > 0) result.Defect("content-truncation", $"{N("potentialTruncations")} document(s) potentially truncated.");
                if (files > 0 && N("canonicalMatches") < files) result.Defect("content-mismatch", $"{files - N("canonicalMatches")} document(s) differ from the archive after import.");
                result.DataFromCount(files, DataState.NotAssessed);
                break;
            }
            default:
            {
                var archiveDocuments = ExplorerCoverageScope.ArchiveOwnedDocuments(run);
                var totals = ExplorerCoverageScope.Aggregate(archiveDocuments);
                foreach (var (k, v) in totals) result.Observe(k["explorer-coverage.".Length..], v);
                context.Shared[CoverageExpectedIdsKey] = archiveDocuments.Select(d => Str(d, "expectedDocumentId")).Where(id => id is { Length: > 0 }).Select(id => id!).ToHashSet(StringComparer.Ordinal);
                if (totals["explorer-coverage.missing"] > 0) result.Defect("coverage-missing-text", $"{totals["explorer-coverage.missing"]} source block(s) are not represented by any explorer.");
                if (context.HashMatched)
                {
                    foreach (var (key, expected) in context.Dataset.HashBoundBaselines.Where(b => b.Key.StartsWith("explorer-coverage.", StringComparison.Ordinal)))
                        if (totals.TryGetValue(key, out var actual) && actual != expected)
                            result.Defect("coverage-baseline-changed", $"{key}: {actual} (baseline {expected} for this archive hash).");
                    result.Note("Hash-bound baseline compared (archive hash matched).");
                }
                else result.Note("Archive hash differs from the descriptor: baseline not compared.");
                result.DataFromCount(archiveDocuments.Count, DataState.NotAssessed);
                break;
            }
        }
        result.Provenance = ProvenanceState.Traced;
    }
}

/// <summary>Scopes hash-bound Explorer coverage baselines to project artifacts classified from the configured archive, excluding local fixtures.</summary>
public static class ExplorerCoverageScope
{
    public static IReadOnlyList<JsonElement> ArchiveOwnedDocuments(JsonElement run)
    {
        if (!run.TryGetProperty("documents", out var documents) || documents.ValueKind != JsonValueKind.Array) return [];
        return documents.EnumerateArray()
            .Where(document => document.TryGetProperty("sourceOrigin", out var origin) &&
                origin.ValueKind == JsonValueKind.String && origin.GetString() == "ConfiguredArchive")
            .ToArray();
    }

    public static IReadOnlyDictionary<string, long> Aggregate(IReadOnlyCollection<JsonElement> documents)
    {
        long Sum(string property) => documents.Sum(document =>
            document.TryGetProperty(property, out var value) && value.TryGetInt64(out var count) ? count : 0);
        return new Dictionary<string, long>
        {
            ["explorer-coverage.documents"] = documents.Count,
            ["explorer-coverage.blocks"] = Sum("sourceBlockCount"),
            ["explorer-coverage.direct"] = Sum("representedDirectlyCount"),
            ["explorer-coverage.structured"] = Sum("representedStructurallyCount"),
            ["explorer-coverage.ignored"] = Sum("intentionallyIgnoredCount"),
            ["explorer-coverage.unsupported"] = Sum("unsupportedCount"),
            ["explorer-coverage.missing"] = Sum("missingCount")
        };
    }
}
