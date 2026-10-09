using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BirkNext.RealProjectAcceptance;

/// <summary>
/// Writes acceptance.json (machine-readable) and acceptance.html (human) under &lt;artifacts&gt;/&lt;dataset&gt;/. Every string is passed through
/// <see cref="Redact"/> first; the model already carries only counts, names, states and safe archive-relative paths.
/// </summary>
public static partial class AcceptanceReportWriter
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Default artifacts root: BIRKNEXT_ACCEPTANCE_ARTIFACTS, else &lt;repo&gt;/AIAssisted/artifacts/real-project-acceptance (gitignored).</summary>
    public static string DefaultArtifactsRoot(string startDirectory)
    {
        if (Environment.GetEnvironmentVariable("BIRKNEXT_ACCEPTANCE_ARTIFACTS") is { Length: > 0 } configured) return configured;
        for (var dir = new DirectoryInfo(startDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "frontend")) && Directory.Exists(Path.Combine(dir.FullName, "backend")))
                return Path.Combine(dir.FullName, "artifacts", "real-project-acceptance");
        return Path.Combine(startDirectory, "real-project-acceptance");
    }

    public static (string JsonPath, string HtmlPath) Write(RealProjectAcceptanceResult result, string artifactsRoot)
    {
        var dir = Path.Combine(artifactsRoot, SafeSegment(result.DatasetId));
        Directory.CreateDirectory(dir);
        var jsonPath = Path.Combine(dir, "acceptance.json");
        var htmlPath = Path.Combine(dir, "acceptance.html");
        File.WriteAllText(jsonPath, Redact(JsonSerializer.Serialize(result, Json)));
        File.WriteAllText(htmlPath, Html(result));
        return (jsonPath, htmlPath);
    }

    /// <summary>Removes values that must never appear in a report even if a feature recorded them by mistake.</summary>
    public static string Redact(string text)
    {
        text = BearerPattern().Replace(text, "Bearer [redacted]");
        text = JwtPattern().Replace(text, "[redacted-jwt]");
        text = ConnectionStringPattern().Replace(text, m => m.Groups[1].Value + "=[redacted]");
        text = SecretAssignmentPattern().Replace(text, m => m.Groups[1].Value + m.Groups[2].Value + "[redacted]");
        text = NationalIdPattern().Replace(text, "[redacted-id]");
        return text;
    }

    public static string Html(RealProjectAcceptanceResult r)
    {
        static string E(string? s) => WebUtility.HtmlEncode(Redact(s ?? ""));
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append($"<title>Real Project Acceptance · {E(r.DatasetDisplayName)}</title><style>");
        sb.Append("body{font-family:system-ui,sans-serif;margin:16px;color:#1f2937;background:#fff}table{border-collapse:collapse;width:100%;font-size:14px}th,td{border:1px solid #d1d5db;padding:6px;text-align:left;vertical-align:top}th{background:#f3f4f6}");
        sb.Append(".Failed{color:#b91c1c;font-weight:700}.Blocked{color:#92400e;font-weight:700}.NotVerified,.NotApplicable{color:#4b5563}.Completed{color:#166534;font-weight:700}.Partial{color:#92400e}dl{display:grid;grid-template-columns:14rem 1fr;gap:4px}dd{margin:0;overflow-wrap:anywhere}.wrap{overflow-x:auto}");
        sb.Append("</style></head><body>");
        sb.Append($"<h1>Real Project Acceptance — {E(r.DatasetDisplayName)}</h1>");
        sb.Append("<p>Source acceptance and runtime acceptance are separate dimensions. NotVerified and NotApplicable are truthful outcomes, not failures. There is no overall quality percentage.</p><dl>");
        void Row(string k, string? v) => sb.Append($"<dt>{E(k)}</dt><dd>{E(v)}</dd>");
        Row("Dataset", r.DatasetId); Row("Archive", r.Archive?.FileName); Row("Archive SHA-256", r.Archive?.Sha256); Row("Archive size", r.Archive?.SizeBytes.ToString());
        Row("Hash matched expected", r.HashMatched ? "yes" : "no / not configured"); Row("Preparation", $"{r.PreparationState}: {r.PreparationMessage}");
        Row("Mode", r.Mode.ToString()); Row("BirkNext commit", r.BirkNextCommit); Row("Started / completed", $"{r.StartedAt:u} / {r.CompletedAt:u}");
        Row("Project / workspace", $"{r.Workspace.ProjectName} · {r.Workspace.WorkspaceId}"); Row("Import", r.Workspace.ImportId); Row("Source snapshot", r.Workspace.SourceSnapshotId);
        Row("Runtime profile", r.RuntimeProfileConfigured ? "configured" : "not configured (runtime-only evidence stays NotVerified)"); Row("Active sends allowed", r.ActiveSendAllowed ? "yes" : "no");
        Row("Outcome", r.Succeeded ? "No true defects" : "Defects found");
        sb.Append("</dl><h2>Summary</h2><ul>");
        foreach (var (category, count) in r.Summary) sb.Append($"<li class=\"{category}\">{category}: {count}</li>");
        sb.Append("</ul>");
        if (r.Defects.Any())
        {
            sb.Append("<h2>Defects</h2><ul>");
            foreach (var (feature, finding) in r.Defects) sb.Append($"<li><strong>{E(feature)}</strong> — {E(finding.Code)}: {E(finding.Message)}</li>");
            sb.Append("</ul>");
        }
        sb.Append("<h2>Features</h2><div class=\"wrap\"><table><thead><tr><th>Area</th><th>Feature</th><th>Route</th><th>Category</th><th>Execution</th><th>Evidence</th><th>Data</th><th>Provenance</th><th>Export</th><th>Browser</th><th>Observations</th><th>Findings / notes</th><th>Duration</th></tr></thead><tbody>");
        foreach (var f in r.Features)
        {
            sb.Append($"<tr><td>{E(f.Area)}</td><td>{E(f.DisplayName)}</td><td>{E(f.Route)}</td><td class=\"{f.Category}\">{f.Category}</td><td>{f.ExecutionStatus}</td><td>{f.EvidenceState}</td><td>{f.DataState}</td><td>{f.ProvenanceState}</td><td>{f.ExportState}</td><td>{f.BrowserState}</td>");
            sb.Append($"<td>{string.Join("<br>", f.Observations.Select(o => $"{E(o.Key)}: {E(o.Value)}"))}</td>");
            sb.Append($"<td>{string.Join("<br>", f.Findings.Select(x => $"{x.Severity}: {E(x.Code)} — {E(x.Message)}").Concat(f.Notes.Select(E)))}</td><td>{f.Duration.TotalSeconds:0.0} s</td></tr>");
        }
        sb.Append("</tbody></table></div></body></html>");
        return sb.ToString();
    }

    private static string SafeSegment(string id) => InvalidSegment().Replace(id, "-");

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9\-_\.=]+", RegexOptions.IgnoreCase)] private static partial Regex BearerPattern();
    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{4,}")] private static partial Regex JwtPattern();
    [GeneratedRegex(@"\b(Password|Pwd|AccountKey|SharedAccessKey|User ID|Uid)\s*=\s*[^;""\s]+", RegexOptions.IgnoreCase)] private static partial Regex ConnectionStringPattern();
    [GeneratedRegex(@"\b(client[_-]?secret|api[_-]?key|access[_-]?token)(""?\s*[:=]\s*""?)[^\s"",;]{6,}", RegexOptions.IgnoreCase)] private static partial Regex SecretAssignmentPattern();
    [GeneratedRegex(@"\b\d{6}\s?\d{5}\b")] private static partial Regex NationalIdPattern();
    [GeneratedRegex(@"[^A-Za-z0-9._-]")] private static partial Regex InvalidSegment();
}
