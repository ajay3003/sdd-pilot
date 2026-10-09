using System.Text;
using BirkNext.ApiReview;
using BirkNext.RuntimeSecurity;

namespace BirkNext.Web.Services;

/// <summary>
/// HTML export sections for runtime security evidence: API documentation exposure, the CORS probe matrix, authorization scenario outcomes,
/// cookie metadata and the body-fuzzing trust/opt-in summary. The models carry no credential, cookie value, token or response body, and every
/// text cell is passed through <see cref="Redact"/> as defence in depth.
/// </summary>
public static class RuntimeSecurityExport
{
    /// <summary>Removes anything token- or credential-shaped from a text before it is written (the models should never contain any).</summary>
    public static string Redact(string? text) => string.IsNullOrEmpty(text) ? "" :
        System.Text.RegularExpressions.Regex.Replace(text, @"(?i)(bearer\s+[A-Za-z0-9._~+/=-]+|eyJ[A-Za-z0-9_-]{8,}(\.[A-Za-z0-9_-]+){0,2}|(password|secret|token|cookie)\s*[:=]\s*\S+)", "[redacted]");

    public static string DocumentationAndCors(ApiReviewTargetResult t, Func<string[], IEnumerable<string[]>, string> table, Func<string, string> badge, Func<string?, string> esc)
    {
        var sb = new StringBuilder();
        if (t.DocumentationExposure is { } docs)
        {
            sb.Append("<h3>API documentation exposure</h3>");
            sb.Append($"<p><strong>Expectation:</strong> {esc(docs.Expectation.ToString())} · <strong>Observed:</strong> {esc(ApiDocumentationExposureRules.Label(docs.Observed))} · <strong>Assessment:</strong> {badge(docs.Assessment.ToString())} — {esc(Redact(docs.Reason))}</p>");
            if (docs.Probes.Count > 0)
                sb.Append(table(["Path", "Source", "Status", "State", "Document", "Validation"], docs.Probes.Select(p => new[]
                {
                    esc(p.Path), esc(p.Source), p.StatusCode?.ToString() ?? "—", esc(p.State.ToString()), esc(p.DocumentKind ?? "—"), esc(Redact(p.DocumentValidation ?? p.Note)),
                })));
        }
        if (t.CorsProbes.Count > 0)
        {
            sb.Append("<h3>CORS probes</h3>");
            sb.Append(table(["Probe", "Origin", "Status", "Allow-Origin", "Credentials", "Expected", "Observed", "Assessment", "Note"], t.CorsProbes.Select(o => new[]
            {
                esc(o.Kind.ToString()), esc(o.Origin), o.StatusCode?.ToString() ?? "—", esc(o.AllowOrigin ?? "absent"), o.AllowCredentials ? "true" : "absent/false",
                esc(o.Expected), esc(o.Observed.ToString()), badge(o.Assessment.ToString()), esc(Redact(o.Note)),
            })));
        }
        return sb.ToString();
    }

    public static string AuthorizationSection(AuthorizationRunReport run, Func<string[], IEnumerable<string[]>, string> table, Func<string, string> badge, Func<string?, string> esc)
    {
        var sb = new StringBuilder();
        sb.Append("<section class=\"block\"><h2>Authorization scenarios</h2>");
        sb.Append($"<p><strong>{esc(RuntimeSecurityWording.NotAPenetrationTest)}</strong></p><dl>");
        sb.Append($"<dt>Run</dt><dd>{esc(run.RunId)}</dd>");
        sb.Append($"<dt>Environment</dt><dd>{esc(run.EnvironmentName)} — safety: {esc(run.Safety.State.ToString())}</dd>");
        sb.Append($"<dt>Server registration</dt><dd>{esc(run.Trust.State.ToString())} — {esc(Redact(run.Trust.Reason))}</dd>");
        sb.Append($"<dt>Expectation fingerprint</dt><dd>{esc(run.ExpectationFingerprint)}</dd>");
        sb.Append($"<dt>Started / completed</dt><dd>{run.StartedAt:u} / {run.CompletedAt:u}</dd>");
        if (run.BlockedReason is { } blocked) sb.Append($"<dt>Not run</dt><dd>{esc(Redact(blocked))}</dd>");
        sb.Append("</dl>");
        sb.Append(table(["Outcome", "Count"], run.Outcomes.Select(kv => new[] { esc(kv.Key.ToString()), kv.Value.ToString() })));
        if (run.Observations.Count > 0)
            sb.Append(table(["Scenario", "Identity", "Role", "Expected", "Observed", "Result", "Reason"], run.Observations.Select(o => new[]
            {
                esc(o.Scenario), esc(o.IdentityAlias), esc(o.Role), esc(o.Expected.ToString()), o.StatusCode is { } s ? $"HTTP {s}{(o.GraphQlAuthorizationError == true ? " · GraphQL auth error" : "")}" : "—",
                badge(o.Outcome.ToString()), esc(Redact(o.Reason)),
            })));
        sb.Append("<h3>Limitations</h3><ul>");
        foreach (var l in run.Limitations) sb.Append($"<li>{esc(l)}</li>");
        sb.Append("</ul></section>\n");
        return sb.ToString();
    }

    public static string BuildAuthorization(AuthorizationRunReport run, string? projectName, Func<string[], IEnumerable<string[]>, string> table, Func<string, string> badge, Func<string?, string> esc,
        Func<string, string?, string?, string, string> buildHtml) =>
        buildHtml("API Authorization Scenarios Report", projectName, $"Target: {run.EnvironmentName}  Started: {run.StartedAt:yyyy-MM-dd HH:mm} UTC", AuthorizationSection(run, table, badge, esc));

    public static string CookieSection(CookieSecurityAssessment? cookies, Func<string[], IEnumerable<string[]>, string> table, Func<string, string> badge, Func<string?, string> esc)
    {
        var sb = new StringBuilder("<h3>Cookie security (Set-Cookie attributes; values are never recorded)</h3>");
        if (cookies is null) return sb.Append("<p>Not recorded in this review.</p>").ToString();
        foreach (var note in cookies.Notes) sb.Append($"<p>{esc(note)}</p>");
        if (cookies.Cookies.Count == 0) return sb.Append("<p>No cookie was set in the inspected response.</p>").ToString();
        sb.Append(table(["Cookie", "Host", "Class", "Secure", "HttpOnly", "SameSite", "Domain", "Path", "Lifetime"], cookies.Cookies.Select(c => new[]
        {
            esc(c.Name), esc(c.Host), c.Classification == CookieClassification.DeclaredAuthOrSession ? "Auth/session (declared)" : "Unclassified", c.Secure ? "Yes" : "No", c.HttpOnly ? "Yes" : "No",
            esc(c.SameSite.ToString()), esc(c.Domain ?? "host-only"), esc(c.Path), c.Persistent ? "Persistent" : "Session",
        })));
        if (cookies.Findings.Count > 0)
            sb.Append(table(["Severity", "Rule", "Cookie", "Finding"], cookies.Findings.Select(f => new[] { badge(f.Severity), esc(f.RuleId), esc(f.Cookie), $"<strong>{esc(f.Title)}</strong> — {esc(f.Detail)}" })));
        return sb.ToString();
    }

    public static string BodyFuzzingSummary(ApiFuzzingReport run, Func<string?, string> esc)
    {
        if (!run.Settings.BodyFuzzing) return "<p>Request-body fuzzing: off for this run.</p>";
        var sb = new StringBuilder("<h3>Request-body fuzzing</h3><ul>");
        foreach (var trust in run.BodyTrust) sb.Append($"<li>Server registration: {esc(trust.State.ToString())} — {esc(Redact(trust.Reason))}</li>");
        foreach (var op in run.BodyOperations) sb.Append($"<li>Opt-in {esc(op.Key)}: {esc(op.Policy.ToString())}{(op.CleanupStrategyId is { Length: > 0 } id ? $" (cleanup {esc(id)})" : "")}</li>");
        var bodyCases = run.Cases.Count(c => c.Location == ApiFuzzParameterLocation.Body);
        sb.Append($"<li>Body cases planned: {bodyCases}; executed: {run.Results.Count(r => r.Executed && run.Cases.Any(c => c.CaseId == r.CaseId && c.Location == ApiFuzzParameterLocation.Body))}</li>");
        sb.Append("</ul>");
        return sb.ToString();
    }
}
