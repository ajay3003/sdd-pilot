using System.Text;
using BirkNext.ApiReview;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// HTML export of the API Quality Review: target environment snapshot, API services and access mode, contract versions (hashes),
/// coverage, per-target operations and checks (incl. Not Tested / Blocked states), findings table, manual review items and
/// limitations. The report model carries no secrets, so nothing has to be redacted here.
/// </summary>
public static class ApiReviewExport
{
    public static string Build(ApiReviewReport report, string? projectName, Func<string[], IEnumerable<string[]>, string> table, Func<string, string> badge, Func<string?, string> esc,
        Func<string, string?, string?, string, string> buildHtml)
    {
        var sb = new StringBuilder();
        var env = report.Environment;
        sb.Append("<section class=\"block\"><h2>Target Environment</h2><dl>");
        sb.Append($"<dt>Environment</dt><dd>{esc(env.Name)} ({esc(env.EnvironmentType)})</dd>");
        sb.Append($"<dt>Environment ID</dt><dd>{esc(env.EnvironmentId)}</dd>");
        sb.Append($"<dt>Target URL</dt><dd>{esc(env.TargetUrl)}</dd>");
        sb.Append($"<dt>Authenticated testing method</dt><dd>{esc(env.AuthenticatedTestingMethod.ToString())}</dd>");
        sb.Append($"<dt>Access</dt><dd>{esc(AccessLabel(report))}</dd>");
        sb.Append($"<dt>Policy</dt><dd>{(report.Policy.ReadOnly ? "Read-only" : "")}; error probes {(report.Policy.ErrorHandlingProbes && !env.IsProduction ? "enabled" : "disabled")}; response time {esc(report.Policy.LatencyPolicyText)}{(report.Policy.LatencySource is { } src ? $" ({esc(src)})" : "")}; REST payload warning &gt; {esc(ApiReviewPolicy.Bytes(report.Policy.RestPayloadThreshold))}{(report.Policy.GraphQlPayloadWarningBytes is { } gql ? $"; GraphQL payload warning &gt; {esc(ApiReviewPolicy.Bytes(gql))}" : "")} — thresholds captured when the review ran</dd>");
        sb.Append($"<dt>Started / generated</dt><dd>{report.StartedAt:u} / {report.GeneratedAt:u}</dd></dl></section>\n");

        var c = report.Coverage;
        sb.Append("<div class=\"kpi-row\">");
        sb.Append(Kpi(report.RestServices.ToString(), "REST services")).Append(Kpi(report.GraphQlServices.ToString(), "GraphQL services"));
        sb.Append(Kpi($"{c.RestOperationsReviewed} / {c.RestOperationsTotal}", "REST operations reviewed")).Append(Kpi(c.GraphQlOperationsObserved.ToString(), "GraphQL observed operations"));
        // Same wording as the page: compatibility that never ran is "Not assessed", never "0 / N".
        var gqlCounts = report.Targets.Where(t => t.Target.ApiType == ApiReviewTargetType.GraphQl).Select(ApiReviewPresentation.GraphQlCounts).Where(x => x.Observed > 0).ToList();
        if (gqlCounts.Count > 0)
            sb.Append(Kpi(gqlCounts.Any(x => x.CompatibilityAssessed) ? $"{gqlCounts.Sum(x => x.Compatible)} compatible · {gqlCounts.Sum(x => x.Incompatible)} incompatible" : "Not assessed", "GraphQL client/server compatibility"));
        sb.Append(Kpi(c.ContractChecks.ToString(), "Contract checks")).Append(Kpi(c.SecurityChecks.ToString(), "Security checks"));
        sb.Append(Kpi($"{c.AuthenticatedExecuted} / {c.AuthenticatedPlanned}", "Authenticated targets")).Append(Kpi($"{c.PublicExecuted} / {c.PublicPlanned}", "Public targets"));
        sb.Append(Kpi(c.TargetsBlocked.ToString(), "Blocked targets"));
        sb.Append("</div>\n");

        sb.Append("<section class=\"block\"><h2>API services</h2>");
        sb.Append(table(["Service", "Type", "Endpoint", "Source", "Access", "Status", "Contract", "Findings"], report.Targets.Select(t => new[]
        {
            // The same derivation the page renders, so the export cannot say "Assessed" while the UI says "Reviewed".
            esc(t.Target.ServiceName), esc(t.Target.ApiType.ToString()), esc(t.Target.Url), esc(t.Target.Source.ToString()), esc(t.AccessMode.ToString()),
            esc(ApiReviewStatusLabels.Label(ApiReviewStatusLabels.Of(t))),
            esc(t.Contract is null ? "—" : $"{t.Contract.Kind}: {t.Contract.Status} {(t.Contract.Version is null ? "" : t.Contract.Version)} {(t.Contract.Hash is null ? "" : "hash " + t.Contract.Hash)}"),
            esc(t.FindingCount is null ? "Not tested" : t.FindingCount.Value.ToString()),
        })));
        sb.Append("</section>\n");

        foreach (var t in report.Targets)
        {
            sb.Append($"<section class=\"block\"><h2>{esc(t.Target.ServiceName)} <small>({esc(t.Target.ApiType.ToString())} · {esc(t.Target.Url)})</small></h2>");
            sb.Append($"<p><strong>Access:</strong> {esc(t.AccessMode.ToString())} — {esc(t.AccessReason)}{(t.RequiredAction is null ? "" : $" <strong>Action:</strong> {esc(t.RequiredAction)}")}</p>");
            if (t.Contract is { } contract)
                sb.Append($"<p><strong>{esc(contract.Kind)}:</strong> {esc(contract.Status.ToString())}{(contract.Version is null ? "" : $" · version {esc(contract.Version)}")}{(contract.Hash is null ? "" : $" · hash {esc(contract.Hash)}")} · {esc(contract.Note)}</p>");
            var isGraphQl = t.Target.ApiType == ApiReviewTargetType.GraphQl;
            // GraphQL: the review's own requests and the observed inventory are separate sets, each listed once.
            var requestRows = isGraphQl ? t.Operations.Where(o => o.Executed).ToList() : t.Operations;
            if (requestRows.Count > 0)
                sb.Append(table(["Operation", "Access", "Executed", "Status", "Content type", "Latency", "Bytes", "Result", "Contract", "Note"], requestRows.Select(o => new[]
                {
                    esc(o.Display), esc(o.AccessMode.ToString()), o.Executed ? "yes" : "no", o.Executed ? o.StatusCode.ToString() : "—", esc(o.ContentType ?? "—"),
                    o.ElapsedMs is { } ms ? $"{ms:0} ms" : "—", o.ContentLength?.ToString() ?? "—", badge(o.Result.ToString()), esc(o.ContractMatched is null ? "—" : o.ContractMatched.Value ? "matched" : "not documented"), esc(o.Note ?? ""),
                })));
            var checks = t.Checks.Concat(t.Operations.SelectMany(o => o.Checks.Select(ch => ch with { Title = $"{o.Display}: {ch.Title}" }))).ToList();
            if (checks.Count > 0)
                sb.Append(table(["Area", "Check", "Result", "Detail", "Evidence"], checks.Select(ch => new[] { esc(ApiReviewStatusLabels.AreaLabel(ch.Area)), esc(ch.Title), badge(ApiReviewEvidencePresentation.CheckLabel(ch, report.Policy)), esc(ch.Detail), esc(string.Join("; ", ch.Evidence)) })));
            if (isGraphQl)
            {
                var counts = ApiReviewPresentation.GraphQlCounts(t);
                sb.Append($"<p><strong>Observed operations:</strong> {esc(counts.Summary)}</p>");
                if (t.GraphQlCompatibility is { Observed: > 0 } compat)
                {
                    // Client/server compatibility, apart from drift and from runtime.
                    sb.Append("<h3>GraphQL client/server compatibility</h3><dl>");
                    sb.Append($"<dt>Schema source</dt><dd>{esc(ApiReviewGraphQlCompatibilityPresentation.SchemaSourceLabel(compat.SchemaSource))}{(compat.SchemaRetrievedAt is { } at ? $" (retrieved {at:u})" : "")}</dd>");
                    sb.Append($"<dt>Observed operations</dt><dd>{compat.Observed}{(compat.HistoricalOnly > 0 ? $" ({compat.Current} current, {compat.HistoricalOnly} historical-only)" : "")}</dd>");
                    sb.Append($"<dt>Assessed</dt><dd>{compat.Assessed}</dd><dt>Compatible</dt><dd>{(compat.Assessed == 0 ? "—" : compat.Compatible)}</dd>");
                    sb.Append($"<dt>Incompatible</dt><dd>{(compat.Assessed == 0 ? "—" : compat.Incompatible)}</dd><dt>Not assessed</dt><dd>{compat.NotAssessed}</dd>");
                    sb.Append($"<dt>Contract coverage</dt><dd>{esc(ApiReviewGraphQlCompatibilityPresentation.Coverage(compat))}</dd></dl>");
                    if (compat.NotAssessedReason is { } reason) sb.Append($"<p><em>Not assessed: {esc(reason)}</em></p>");
                    sb.Append(table(["Operation", "Type", "Observations", "Runtime", "Contract", "Issues", "Last observed"], compat.Operations.Select(o => new[]
                    {
                        esc((o.OperationName ?? "(anonymous)") + (o.Historical ? " (historical)" : "")), esc(o.OperationType.ToString()), o.ObservationCount.ToString(),
                        esc(ApiReviewGraphQlCompatibilityPresentation.RuntimeLabel(o.OperationType)), badge(ApiReviewGraphQlCompatibilityPresentation.StatusLabel(o.Status)),
                        esc(o.Status == GraphQlCompatibilityStatus.Incompatible ? string.Join("; ", o.Issues.Select(i => $"{i.Code}: {i.Message}")) : o.NotAssessedReason ?? ""),
                        esc(o.LastObservedAt?.ToString("u") ?? "—"),
                    })));
                    if (compat.SchemaChangeImpact.Count > 0)
                        sb.Append("<p><strong>Schema drift client impact:</strong></p><ul>" + string.Concat(compat.SchemaChangeImpact.Select(l => $"<li>{esc(l)}</li>")) + "</ul>");
                }
                else if (counts.Observed > 0)
                    sb.Append(table(["Observed operation", "Runtime", "Contract", "Note"], t.Operations.Where(o => !o.Executed).Select(o => new[] { esc(o.Display), "Not executed", "Not assessed", esc(o.Note ?? "") })));
            }
            sb.Append("</section>\n");
        }

        var issues = ApiReviewPresentation.LogicalIssues(report);
        if (issues.Count > 0)
        {
            sb.Append($"<section class=\"block\"><h2>Logical issues</h2><p>{issues.Count} logical issue{(issues.Count == 1 ? "" : "s")} from {report.Findings.Count} source finding{(report.Findings.Count == 1 ? "" : "s")}.</p>");
            sb.Append(table(["Severity", "Issue", "Endpoint", "Affects", "Source observations"], issues.Select(i => new[]
            {
                badge(i.Severity.ToString()), esc(i.Title), esc(i.Endpoint), esc(string.Join(", ", i.Affects)), i.Sources.Count.ToString(),
            })));
            sb.Append("</section>\n");
        }

        sb.Append("<section class=\"block\"><h2>Source findings</h2>");
        if (report.Findings.Count == 0) sb.Append("<p>No findings on the completed targets. Blocked/Not tested targets are not passes.</p>");
        else sb.Append(table(["Severity", "Type", "Endpoint / operation", "Check", "Check result", "Finding", "Evidence", "Recommendation", "Drift"], report.Findings.Select(f => new[]
        {
            badge(f.Severity.ToString()), esc(ApiReviewStatusLabels.AreaLabel(f.Type)), esc(f.Endpoint), esc(f.Check), esc(ApiReviewStatusLabels.FindingCheckLabel(f)), $"<strong>{esc(f.Title)}</strong><br/>{esc(f.Description)}",
            esc(string.Join("; ", f.Evidence)), esc(f.Recommendation), esc(f.Drift?.ToString() ?? "—"),
        })));
        sb.Append("</section>\n");

        if (report.ManualReviewItems.Count > 0)
        {
            sb.Append("<section class=\"block\"><h2>Manual review</h2><ul>");
            foreach (var m in report.ManualReviewItems) sb.Append($"<li>{esc(m)}</li>");
            sb.Append("</ul></section>\n");
        }
        if (report.Limitations.Count > 0)
        {
            sb.Append("<section class=\"block\"><h2>Limitations</h2><ul>");
            foreach (var l in report.Limitations) sb.Append($"<li>{esc(l)}</li>");
            sb.Append("</ul></section>\n");
        }
        return buildHtml("API Quality Review Report", projectName, $"Target: {env.Name} ({env.EnvironmentType})  Generated: {report.GeneratedAt:yyyy-MM-dd HH:mm} UTC", sb.ToString());
    }

    public static string AccessLabel(ApiReviewReport report) => report.Access.AuthenticatedApi
        ? "Authenticated HTTP via Local HTTPS Proxy (read-only, gateway) + Public HTTP"
        : report.Environment.AuthenticatedTestingMethod == BirkNext.LocalHttpsProxy.AuthenticatedTestingMethod.ManualOnly ? "Public HTTP only (manual verification environment)" : "Public HTTP only (no authenticated API context)";

    private static string Kpi(string value, string label) => $"<div class=\"kpi\"><div class=\"kpi-value\">{value}</div><div class=\"kpi-label\">{label}</div></div>";
}
