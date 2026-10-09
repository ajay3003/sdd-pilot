using System.Text;
using BirkNext.ApiReview;

namespace BirkNext.Web.Services;

/// <summary>
/// HTML export of a safe-fuzzing run: level, environment safety decision, contract fingerprints, eligibility, cases, outcomes, logical
/// findings with redacted evidence, and the safety limits it ran with. The model carries synthetic case values, status codes and leak
/// indicator NAMES only — never a response body, credential or header value — so nothing needs redacting here.
/// </summary>
public static class ApiFuzzingExport
{
    public static string Section(ApiFuzzingReport run, Func<string[], IEnumerable<string[]>, string> table, Func<string, string> badge, Func<string?, string> esc)
    {
        var sb = new StringBuilder();
        sb.Append("<section class=\"block\"><h2>Safe fuzzing</h2>");
        sb.Append($"<p><strong>{esc(ApiFuzzingWording.NotAPenetrationTest)}</strong></p><p>{esc(ApiFuzzingWording.Scope)}</p><dl>");
        sb.Append($"<dt>Run</dt><dd>{esc(run.RunId)}</dd>");
        sb.Append($"<dt>Level</dt><dd>{esc(ApiFuzzingPresentation.LevelLabel(run.Level))}</dd>");
        sb.Append($"<dt>Environment</dt><dd>{esc(run.EnvironmentName)} — {esc(ApiFuzzingPresentation.SafetyLabel(run.Safety.State))}: {esc(run.Safety.Reason)}</dd>");
        if (run.Safety.Evidence.Count > 0) sb.Append($"<dt>Safety evidence</dt><dd>{esc(string.Join("; ", run.Safety.Evidence))}</dd>");
        sb.Append($"<dt>Completeness</dt><dd>{esc(ApiFuzzingPresentation.CompletenessLabel(run.Completeness, run.Running))}{(run.CompletenessReason is { Length: > 0 } r ? " — " + esc(r) : "")}</dd>");
        sb.Append($"<dt>Safety limits</dt><dd>{esc(ApiFuzzingPresentation.LimitsText(run.Settings))}</dd>");
        sb.Append($"<dt>Started / completed</dt><dd>{run.StartedAt:u} / {(run.CompletedAt is { } done ? done.ToString("u") : "—")}</dd>");
        foreach (var contract in run.Contracts)
            sb.Append($"<dt>Contract · {esc(contract.Kind)}</dt><dd>{esc(contract.Source ?? "not available")}{(contract.Hash is null ? "" : $" · hash {esc(contract.Hash)}")}</dd>");
        sb.Append("</dl>");
        sb.Append(table(["Measure", "Count"], ApiFuzzingPresentation.Summary(run).Select(c => new[] { esc(c.Label), c.Value.ToString() })));
        if (run.Operations.Count > 0)
        {
            sb.Append("<h3>Eligibility</h3>");
            sb.Append(table(["Operation", "Protocol", "Eligibility", "Reason", "Cases"], run.Operations.Select(o => new[]
            {
                esc(o.Display), o.Protocol == ApiFuzzProtocol.GraphQl ? "GraphQL" : "REST", esc(ApiFuzzingPresentation.ClassificationLabel(o.Classification)), esc(o.Reason), o.CaseCount.ToString(),
            })));
        }
        sb.Append($"<h3>Findings ({run.Findings.Count})</h3>");
        if (run.Findings.Count == 0) sb.Append("<p>No robustness or contract finding in the executed cases. Rejected invalid input (4xx) is expected and not a finding.</p>");
        else sb.Append(table(["Severity", "Finding", "Operation", "Cases", "Evidence (redacted)", "Recommendation", "Standards references"], run.Findings.Select(f => new[]
        {
            badge(f.Severity.ToString()), $"<strong>{esc(f.Title)}</strong><br/>{esc(f.Description)}", esc(f.Operation), f.CaseIds.Count.ToString(), esc(string.Join("; ", f.Evidence)), esc(f.Recommendation),
            esc(string.Join("; ", f.StandardsReferences.Select(s => $"{s.StandardName} {s.ReferenceId} ({s.MappingType})"))),
        })));
        if (run.Results.Count > 0)
        {
            sb.Append("<h3>Case results</h3>");
            sb.Append(table(["Case", "Operation", "Mutation", "Parameter", "Value", "Status", "Outcome", "Note"], run.Results.Select(res =>
            {
                var c = run.Cases.FirstOrDefault(x => x.CaseId == res.CaseId);
                return new[]
                {
                    esc(res.CaseId), esc(c?.OperationDisplay ?? "—"), esc(c is null ? "—" : ApiFuzzingPresentation.MutationLabel(c.MutationType)), esc(c is null ? "—" : $"{c.Parameter} ({c.Location})"),
                    esc(c?.ValuePreview ?? ""), res.StatusCode?.ToString() ?? "—", badge(ApiFuzzingPresentation.OutcomeLabel(res.Outcome)), esc(res.Note),
                };
            })));
        }
        sb.Append("<h3>Limitations</h3><ul>");
        foreach (var l in run.Limitations.Append(ApiFuzzingPresentation.AuthorizationLimitation)) sb.Append($"<li>{esc(l)}</li>");
        sb.Append("</ul></section>\n");
        return sb.ToString();
    }

    public static string Build(ApiFuzzingReport run, string? projectName, Func<string[], IEnumerable<string[]>, string> table, Func<string, string> badge, Func<string?, string> esc,
        Func<string, string?, string?, string, string> buildHtml) =>
        buildHtml("API Safe Fuzzing Report", projectName, $"Target: {run.EnvironmentName}  Started: {run.StartedAt:yyyy-MM-dd HH:mm} UTC", Section(run, table, badge, esc));
}
