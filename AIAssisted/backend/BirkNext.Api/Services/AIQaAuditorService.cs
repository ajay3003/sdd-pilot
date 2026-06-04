using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BirkNext.Api.Models;

namespace BirkNext.Api.Services;

/// <summary>
/// Aggregates all QA quality signals into a single executive report.
///
/// Data sources (reuse — no logic duplication):
///   - <see cref="SpecDriftDetectionService"/>: coverage%, risk counts, orphan tests, drift findings.
///     This transitively calls <see cref="ImpactAnalysisService"/> — no direct call needed.
///   - <see cref="CodeTraceabilityService"/>: code file registration and link stats.
///
/// Quality score (0–100) is deterministic. Claude is called only when
/// <c>includeAiSummary = true</c> and an API key is configured, and degrades
/// gracefully to null on failure.
/// </summary>
public sealed class AIQaAuditorService
{
    private readonly SpecDriftDetectionService _driftService;
    private readonly CodeTraceabilityService _codeService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<AIQaAuditorService> _logger;

    private static readonly JsonSerializerOptions _snakeCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // Tool definition for structured AI output
    private static readonly object _toolDefinition = new
    {
        Name = "submit_qa_summary",
        Description = "Submit the AI-generated QA executive summary, concerns, and recommended actions.",
        InputSchema = new
        {
            Type = "object",
            Required = new[] { "executive_summary", "concerns", "recommended_actions" },
            Properties = new Dictionary<string, object>
            {
                ["executive_summary"] = new
                {
                    Type = "string",
                    Description = "3-4 sentence executive summary suitable for a project manager or test manager.",
                },
                ["concerns"] = new
                {
                    Type = "array",
                    Items = new { Type = "string" },
                    Description = "Specific QA risks and concerns.",
                },
                ["recommended_actions"] = new
                {
                    Type = "array",
                    Items = new { Type = "string" },
                    Description = "Concrete recommended actions complementary to the deterministic recommendations.",
                },
            },
        },
    };

    public AIQaAuditorService(
        SpecDriftDetectionService driftService,
        CodeTraceabilityService codeService,
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        ILogger<AIQaAuditorService> logger)
    {
        _driftService = driftService;
        _codeService = codeService;
        _httpClientFactory = httpClientFactory;
        _config = config;
        _logger = logger;
    }

    public async Task<QaAuditReport> GetQaAuditReportAsync(
        string projectId,
        bool includeAiSummary,
        CancellationToken ct = default)
    {
        // Sequential: DbContext is scoped and not thread-safe across concurrent awaits.
        // SpecDriftDetectionService transitively calls ImpactAnalysisService — no duplicate call.
        var drift = await _driftService.GetSpecDriftReportAsync(projectId, ct);
        var code = await _codeService.GetCodeSummaryAsync(projectId, ct);

        // Guard: no requirements → scoring is meaningless; return a dedicated empty-state report
        // so the UI can display "--" instead of a misleading 100/100 Ready.
        if (drift.TotalRequirements == 0)
        {
            return new QaAuditReport
            {
                QualityScore = 0,
                ReadinessStatus = QaReadinessStatus.InsufficientData,
                CoveragePercent = 0,
                TotalRequirements = 0,
                TotalCodeFiles = code.TotalFiles,
                UnlinkedCodeFiles = code.UnlinkedFiles,
                RecommendedActions =
                [
                    "Create requirements via the QA Artifact Library.",
                    "Create tests and link them to requirements via Traceability & Coverage.",
                    "Run Traceability & Coverage to verify your links.",
                    "Return to QA Readiness once requirements and tests are in place.",
                ],
            };
        }

        var (score, deductions) = ComputeScore(drift, code);
        var readiness = DetermineReadiness(score, drift.CoveragePercent, drift);
        var actions = BuildRecommendedActions(drift, code, readiness);

        string? aiExecSummary = null;
        IReadOnlyList<string> aiConcerns = [];
        IReadOnlyList<string> aiActions = [];

        if (includeAiSummary)
        {
            var ai = await CallClaudeAsync(score, readiness, drift, code, deductions, actions, ct);
            aiExecSummary = ai?.ExecutiveSummary;
            aiConcerns = ai?.Concerns ?? [];
            aiActions = ai?.RecommendedActions ?? [];
        }

        return new QaAuditReport
        {
            QualityScore = score,
            ReadinessStatus = readiness,
            AiExecutiveSummary = aiExecSummary,
            AiConcerns = aiConcerns,
            AiRecommendedActions = aiActions,
            CoveragePercent = drift.CoveragePercent,
            TotalRequirements = drift.TotalRequirements,
            RequirementsAtRisk = drift.RequirementsAtRisk,
            HighRiskRequirements = drift.CoverageGaps,
            DriftFindingsCount = drift.Findings.Count,
            HighRiskDriftFindings = drift.Findings.Count(f => f.Severity == RiskLevel.High),
            OrphanTestCount = drift.OrphanTestCount,
            TotalCodeFiles = code.TotalFiles,
            UnlinkedCodeFiles = code.UnlinkedFiles,
            DriftFindings = drift.Findings,
            TopRisks = drift.RequirementsAtRiskList.Take(5).ToList(),
            RecommendedActions = actions,
            ScoreDeductions = deductions,
        };
    }

    // ── Scoring ───────────────────────────────────────────────────────────────

    private static (int score, IReadOnlyList<QaScoreDeduction> deductions) ComputeScore(
        SpecDriftReport drift, CodeSummary code)
    {
        var deductions = new List<QaScoreDeduction>();
        int score = 100;

        // Coverage deductions
        if (drift.TotalRequirements > 0)
        {
            var pct = drift.CoveragePercent;
            if (pct < 50)
                Deduct(deductions, "Coverage", $"Coverage critically low at {pct:F0}%", 30);
            else if (pct < 75)
                Deduct(deductions, "Coverage", $"Coverage below 75% at {pct:F0}%", 15);
            else if (pct < 80)
                Deduct(deductions, "Coverage", $"Coverage slightly below target at {pct:F0}%", 5);
        }

        // Spec drift deductions
        int highDrift = drift.Findings.Count(f => f.Severity == RiskLevel.High);
        int medDrift = drift.Findings.Count(f => f.Severity == RiskLevel.Medium);

        if (highDrift > 0)
            Deduct(deductions, "Spec Drift", $"{highDrift} high-risk drift finding(s)", Math.Min(highDrift * 15, 30));
        if (medDrift > 0)
            Deduct(deductions, "Spec Drift", $"{medDrift} medium-risk drift finding(s)", Math.Min(medDrift * 5, 10));

        // Orphan test deductions
        if (drift.OrphanTestCount > 0)
            Deduct(deductions, "Orphan Tests", $"{drift.OrphanTestCount} test(s) not linked to any requirement", Math.Min(drift.OrphanTestCount * 3, 10));

        // Code traceability deductions
        if (code.TotalFiles > 0 && code.UnlinkedFiles > 0)
            Deduct(deductions, "Code Traceability", $"{code.UnlinkedFiles} code file(s) not linked to any QA artifact", Math.Min(code.UnlinkedFiles * 2, 10));

        foreach (var d in deductions) score -= d.Points;

        return (Math.Max(score, 0), deductions);
    }

    private static void Deduct(List<QaScoreDeduction> list, string category, string reason, int points) =>
        list.Add(new QaScoreDeduction { Category = category, Reason = reason, Points = points });

    private static QaReadinessStatus DetermineReadiness(
        int score, double coveragePercent, SpecDriftReport drift)
    {
        bool hasHighDrift = drift.Findings.Any(f => f.Severity == RiskLevel.High);

        if (score >= 80 && !hasHighDrift && coveragePercent >= 80)
            return QaReadinessStatus.Ready;

        if (score < 50 || coveragePercent < 50 || hasHighDrift)
            return QaReadinessStatus.HighRisk;

        return QaReadinessStatus.ReviewNeeded;
    }

    private static IReadOnlyList<string> BuildRecommendedActions(
        SpecDriftReport drift, CodeSummary code, QaReadinessStatus readiness)
    {
        var actions = new List<string>(drift.RecommendedActions);

        if (code.TotalFiles > 0 && code.UnlinkedFiles > 0)
            actions.Add($"Link {code.UnlinkedFiles} unregistered code file(s) to their requirements and tests via Code Traceability.");

        if (readiness == QaReadinessStatus.Ready && !actions.Any())
            actions.Add("All quality indicators are healthy. Continue monitoring after each sprint.");

        return actions;
    }

    // ── AI layer ──────────────────────────────────────────────────────────────

    private async Task<AiSummaryResult?> CallClaudeAsync(
        int score,
        QaReadinessStatus readiness,
        SpecDriftReport drift,
        CodeSummary code,
        IReadOnlyList<QaScoreDeduction> deductions,
        IReadOnlyList<string> actions,
        CancellationToken ct)
    {
        var apiKey = _config["Anthropic:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("Anthropic:ApiKey not configured — skipping AI QA summary.");
            return null;
        }

        var model = _config["Anthropic:Model"] ?? "claude-sonnet-4-6";

        try
        {
            var prompt = BuildPrompt(score, readiness, drift, code, deductions, actions);

            var requestBody = new
            {
                Model = model,
                MaxTokens = 1000,
                Tools = new[] { _toolDefinition },
                ToolChoice = new { Type = "tool", Name = "submit_qa_summary" },
                Messages = new[] { new { Role = "user", Content = prompt } },
            };

            var json = JsonSerializer.Serialize(requestBody, _snakeCase);
            var httpClient = _httpClientFactory.CreateClient("Anthropic");
            using var response = await httpClient.PostAsync(
                "https://api.anthropic.com/v1/messages",
                new StringContent(json, Encoding.UTF8, "application/json"),
                ct);

            response.EnsureSuccessStatusCode();

            using var responseDoc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return ParseAiResponse(responseDoc.RootElement);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Claude AI call failed for QA audit project={ProjectId}", "<redacted>");
            return null;
        }
    }

    private static AiSummaryResult? ParseAiResponse(JsonElement root)
    {
        if (!root.TryGetProperty("content", out var content)) return null;

        foreach (var block in content.EnumerateArray())
        {
            if (!block.TryGetProperty("type", out var typeEl)) continue;
            if (typeEl.GetString() != "tool_use") continue;
            if (!block.TryGetProperty("input", out var input)) continue;

            var execSummary = input.TryGetProperty("executive_summary", out var esProp)
                ? esProp.GetString() ?? string.Empty
                : string.Empty;

            var concerns = input.TryGetProperty("concerns", out var cProp)
                ? cProp.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList()
                : new List<string>();

            var recActions = input.TryGetProperty("recommended_actions", out var raProp)
                ? raProp.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList()
                : new List<string>();

            return new AiSummaryResult(execSummary, concerns, recActions);
        }

        return null;
    }

    private static string BuildPrompt(
        int score,
        QaReadinessStatus readiness,
        SpecDriftReport drift,
        CodeSummary code,
        IReadOnlyList<QaScoreDeduction> deductions,
        IReadOnlyList<string> actions)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a senior QA analyst reviewing a software project's test quality metrics.");
        sb.AppendLine("Analyze the data below and provide a concise executive summary, specific concerns, and concrete recommended actions.");
        sb.AppendLine();
        sb.AppendLine($"QUALITY SCORE: {score}/100  |  READINESS: {readiness}");
        sb.AppendLine();
        sb.AppendLine("COVERAGE:");
        sb.AppendLine($"  {drift.CoveragePercent:F1}% of {drift.TotalRequirements} requirements have at least one linked test.");
        sb.AppendLine($"  At risk: {drift.RequirementsAtRisk} ({drift.CoverageGaps} with zero tests, {drift.RequirementsAtRisk - drift.CoverageGaps} with only one test).");
        sb.AppendLine();

        sb.AppendLine($"SPEC DRIFT FINDINGS ({drift.Findings.Count}):");
        if (drift.Findings.Count == 0)
            sb.AppendLine("  None.");
        else
            foreach (var f in drift.Findings)
                sb.AppendLine($"  [{f.Severity}] {f.Category}: {f.Description}");
        sb.AppendLine();

        sb.AppendLine($"ORPHAN TESTS: {drift.OrphanTestCount} test(s) not linked to any requirement.");
        sb.AppendLine();

        sb.AppendLine("CODE TRACEABILITY:");
        if (code.TotalFiles == 0)
            sb.AppendLine("  No code files registered.");
        else
            sb.AppendLine($"  {code.TotalFiles} file(s) registered; {code.UnlinkedFiles} not linked to any QA artifact.");
        sb.AppendLine();

        sb.AppendLine("SCORE DEDUCTIONS:");
        if (deductions.Count == 0)
            sb.AppendLine("  None — full score.");
        else
            foreach (var d in deductions)
                sb.AppendLine($"  [{d.Category}] {d.Reason} (−{d.Points} pts)");
        sb.AppendLine();

        sb.AppendLine("DETERMINISTIC RECOMMENDED ACTIONS:");
        foreach (var a in actions)
            sb.AppendLine($"  - {a}");
        sb.AppendLine();

        sb.AppendLine("Guidelines:");
        sb.AppendLine("- executive_summary: 3-4 sentences for a project manager, non-technical stakeholder level.");
        sb.AppendLine("- concerns: specific risks not obvious from the raw numbers.");
        sb.AppendLine("- recommended_actions: concrete steps that complement — don't repeat — the deterministic actions above.");

        return sb.ToString();
    }

    private sealed record AiSummaryResult(
        string ExecutiveSummary,
        IReadOnlyList<string> Concerns,
        IReadOnlyList<string> RecommendedActions);
}
