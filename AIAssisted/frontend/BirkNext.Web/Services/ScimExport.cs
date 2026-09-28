using System.Text;
using BirkNext.Integrations;

namespace BirkNext.Web.Services;

/// <summary>
/// HTML export of a SCIM provisioning check from its stored snapshot: configuration, operations, authentication, persistence, Service Bus
/// route, event contracts, health and runtime observations, reliability and security findings, the specification comparison and the missing
/// evidence. No token, secret or user payload exists in the snapshot, so none can be exported.
/// </summary>
public static class ScimExport
{
    public static string Section(ScimEvidenceCheck check, Func<string[], IEnumerable<string[]>, string> table, Func<string, string> badge, Func<string?, string> esc)
    {
        var sb = new StringBuilder();
        var s = check.Settings;
        sb.Append($"<section class=\"block\"><h2>SCIM identity provisioning — {esc(check.PlatformName)}</h2>");
        sb.Append($"<p>{badge(ScimLabels.Overall(check.OverallState))} {esc(ScimPresentation.Headline(check))}. Checked {check.CompletedAt:u}.</p>");
        sb.Append("<h3>Configuration</h3><dl>");
        sb.Append($"<dt>Provider / protocol</dt><dd>{esc(s.Provider)} · {esc(s.Protocol)}</dd><dt>Base URL</dt><dd>{esc(s.BaseUrl ?? "Unknown")}{esc(s.BasePath)}</dd>");
        sb.Append($"<dt>Authentication</dt><dd>{esc(s.Authentication ?? "Not configured")}</dd><dt>Persistence</dt><dd>{esc(s.Persistence ?? "Not configured")}</dd>");
        sb.Append($"<dt>Outbound</dt><dd>Service Bus {esc(s.Topic ?? "(none)")} · {esc(string.Join(", ", s.Events))}</dd><dt>Downstream</dt><dd>{esc(s.Downstream ?? "Not configured")}</dd>");
        sb.Append($"<dt>Source analyzed</dt><dd>{(check.SourceAnalyzedAt is { } at ? $"{at:u} · {esc(string.Join(", ", check.SourceArchives.Select(a => $"{a.FileName} (sha256 {a.Sha256[..Math.Min(12, a.Sha256.Length)]})")))}" : "No source analyzed")}</dd>");
        sb.Append($"<dt>Synthetic mutation test</dt><dd>{esc(ScimLabels.Mutation(check.SyntheticMutation.State))}</dd></dl>");
        sb.Append("<h3>Evidence by stage</h3>");
        sb.Append(table(["Stage", "Source / configuration", "Runtime"], check.Stages.Select(st => new[]
        {
            esc(st.Title), $"{badge(ScimLabels.State(st.Source))} {esc(st.SourceDetail)}", $"{badge(ScimLabels.State(st.Runtime))} {esc(st.RuntimeDetail)}",
        })));
        if (check.Findings.Count > 0)
        {
            sb.Append("<h3>Findings</h3>");
            sb.Append(table(["Severity", "Area", "Finding", "Detail", "Recommendation"], check.Findings.Select(f => new[]
            {
                badge(f.Severity.ToString()), esc(ScimLabels.Area(f.Area)), esc(f.Title), esc(f.Detail + (f.Evidence.Count > 0 ? $" ({string.Join(" · ", f.Evidence)})" : "")), esc(f.Recommendation),
            })));
        }
        sb.Append("<h3>Checks</h3>");
        sb.Append(table(["Area", "Check", "State", "Detail", "Source"], ScimPresentation.Areas(check).SelectMany(g => g.Checks.Select(c => new[]
        {
            esc(ScimLabels.Area(g.Area)), esc(c.Title), badge(ScimLabels.State(c.State)), esc(c.Detail), esc(IntegrationReviewLabels.Source(c.Provenance) + (c.Locations.Count > 0 ? $" · {ScimPresentation.Locations(c.Locations)}" : "")),
        }))));
        if (check.Operations.Count > 0)
        {
            sb.Append("<h3>Operations (source)</h3>");
            sb.Append(table(["Operation", "Responses", "Behaviour"], check.Operations.Select(o => new[] { esc($"{o.Method} {o.Path}"), esc(string.Join(", ", o.Responses)), esc(string.Join(" ", o.Behaviour) + (o.UnknownUser is { } u ? $" Unknown user: {u}" : "")) })));
        }
        if (check.Events.Count > 0)
        {
            sb.Append("<h3>Event contracts (source)</h3>");
            sb.Append(table(["Event", "Topic", "Body fields", "Message properties", "Identifier relation"], check.Events.Select(e => new[] { esc(e.EventType), esc(e.Topic), esc(string.Join(", ", e.BodyFields)), esc(string.Join(", ", e.MessageProperties)), esc(e.IdentifierRelation) })));
        }
        sb.Append($"<h3>Safe runtime checks (GET only)</h3><p>{esc(IntegrationReviewLabels.EvidenceState(check.Runtime.State))}: {esc(check.Runtime.Reason)}</p>");
        if (check.Runtime.Observations.Count > 0)
            sb.Append(table(["Check", "Path", "Expected", "Observed", "State", "Detail"], check.Runtime.Observations.Select(o => new[] { esc(o.Title), esc(o.Path), esc(o.Expected), o.StatusCode?.ToString() ?? "no response", badge(ScimLabels.State(o.State)), esc(o.Detail) })));
        if (check.Requirements.Count > 0)
        {
            sb.Append("<h3>Specification vs implementation</h3><p>Requirement text from the specification; status from the analyzed source only.</p>");
            sb.Append(table(["Requirement", "Status", "Evidence"], check.Requirements.Select(r => new[] { esc($"{r.Id} {r.Text}"), badge(ScimLabels.Requirement(r.Status)), esc(r.Evidence) })));
        }
        if (check.TestCoverage.Count > 0)
        {
            sb.Append("<h3>Repository tests</h3>");
            sb.Append(table(["Behaviour", "Coverage", "Tests", "Note"], check.TestCoverage.Select(t => new[] { esc(t.Scenario), badge(ScimLabels.Coverage(t.State)), esc(string.Join(", ", t.Tests.Take(3))), esc(t.Note) })));
        }
        sb.Append("<h3>Missing evidence</h3><ul>");
        foreach (var item in check.Missing) sb.Append($"<li>{esc(item)}</li>");
        sb.Append("</ul><h3>Limitations</h3><ul>");
        foreach (var item in check.Limitations) sb.Append($"<li>{esc(item)}</li>");
        sb.Append("</ul></section>\n");
        return sb.ToString();
    }

    public static string Build(ScimEvidenceCheck check, Func<string[], IEnumerable<string[]>, string> table, Func<string, string> badge, Func<string?, string> esc,
        Func<string, string?, string?, string, string> buildHtml) =>
        buildHtml("SCIM Provisioning Review", null, $"{check.PlatformName} · environment {check.EnvironmentId} · run {check.RunId}", Section(check, table, badge, esc));
}
