using System.Text;
using BirkNext.ApiReview;

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
        sb.Append($"<dt>Policy</dt><dd>{(report.Policy.ReadOnly ? "Read-only" : "")}; error probes {(report.Policy.ErrorHandlingProbes && !env.IsProduction ? "enabled" : "disabled")}; slow &gt; {report.Policy.SlowWarningMs} / {report.Policy.SlowPoorMs} ms</dd>");
        sb.Append($"<dt>Started / generated</dt><dd>{report.StartedAt:u} / {report.GeneratedAt:u}</dd></dl></section>\n");

        var c = report.Coverage;
        sb.Append("<div class=\"kpi-row\">");
        sb.Append(Kpi(report.RestServices.ToString(), "REST services")).Append(Kpi(report.GraphQlServices.ToString(), "GraphQL services"));
        sb.Append(Kpi($"{c.RestOperationsReviewed} / {c.RestOperationsTotal}", "REST operations reviewed")).Append(Kpi($"{c.GraphQlOperationsMatched} / {c.GraphQlOperationsObserved}", "GraphQL operations matched"));
        sb.Append(Kpi(c.ContractChecks.ToString(), "Contract checks")).Append(Kpi(c.SecurityChecks.ToString(), "Security checks"));
        sb.Append(Kpi($"{c.AuthenticatedExecuted} / {c.AuthenticatedPlanned}", "Authenticated targets")).Append(Kpi($"{c.PublicExecuted} / {c.PublicPlanned}", "Public targets"));
        sb.Append(Kpi(c.TargetsBlocked.ToString(), "Blocked targets"));
        sb.Append("</div>\n");

        sb.Append("<section class=\"block\"><h2>API services</h2>");
        sb.Append(table(["Service", "Type", "Endpoint", "Source", "Access", "Status", "Contract", "Findings"], report.Targets.Select(t => new[]
        {
            esc(t.Target.ServiceName), esc(t.Target.ApiType.ToString()), esc(t.Target.Url), esc(t.Target.Source.ToString()), esc(t.AccessMode.ToString()), esc(t.Status.ToString()),
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
            if (t.Operations.Count > 0)
                sb.Append(table(["Operation", "Access", "Executed", "Status", "Content type", "Latency", "Bytes", "Result", "Contract", "Note"], t.Operations.Select(o => new[]
                {
                    esc(o.Display), esc(o.AccessMode.ToString()), o.Executed ? "yes" : "no", o.Executed ? o.StatusCode.ToString() : "—", esc(o.ContentType ?? "—"),
                    o.ElapsedMs is { } ms ? $"{ms:0} ms" : "—", o.ContentLength?.ToString() ?? "—", badge(o.Result.ToString()), esc(o.ContractMatched is null ? "—" : o.ContractMatched.Value ? "matched" : "not documented"), esc(o.Note ?? ""),
                })));
            var checks = t.Checks.Concat(t.Operations.SelectMany(o => o.Checks.Select(ch => ch with { Title = $"{o.Display}: {ch.Title}" }))).ToList();
            if (checks.Count > 0)
                sb.Append(table(["Area", "Check", "Result", "Detail", "Evidence"], checks.Select(ch => new[] { esc(ch.Area.ToString()), esc(ch.Title), badge(ch.Result.ToString()), esc(ch.Detail), esc(string.Join("; ", ch.Evidence)) })));
            if (t.GraphQlOperationMatches.Count > 0)
                sb.Append(table(["Observed operation", "Root field", "Result", "Note"], t.GraphQlOperationMatches.Select(m => new[] { esc(m.Operation), esc(m.MatchedRootField ?? "—"), badge(m.Result.ToString()), esc(m.Note) })));
            sb.Append("</section>\n");
        }

        sb.Append("<section class=\"block\"><h2>Findings</h2>");
        if (report.Findings.Count == 0) sb.Append("<p>No findings on the completed targets. Blocked/Not tested targets are not passes.</p>");
        else sb.Append(table(["Severity", "Type", "Endpoint / operation", "Check", "Result", "Finding", "Evidence", "Recommendation", "Drift"], report.Findings.Select(f => new[]
        {
            badge(f.Severity.ToString()), esc(f.Type.ToString()), esc(f.Endpoint), esc(f.Check), esc(f.Result.ToString()), $"<strong>{esc(f.Title)}</strong><br/>{esc(f.Description)}",
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
