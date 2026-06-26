using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services;

/// <summary>
/// Orchestrates the AI Change Audit pipeline:
///   1. Load project requirements and tests from the database.
///   2. Call the Claude API (tool use) to identify affected components.
///   3. Use <see cref="ImpactAnalysisService"/> for formal risk per identified requirement.
///   4. Assemble and return a <see cref="ChangeAuditReport"/>.
///
/// Designed for extension: the <see cref="ChangeAuditRequest"/> model carries
/// extension points for future input types (git commits, PRs, changed files).
/// </summary>
public sealed class AIChangeAuditService
{
    private readonly AppDbContext _db;
    private readonly ImpactAnalysisService _impactService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<AIChangeAuditService> _logger;

    private static readonly JsonSerializerOptions _snakeCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public AIChangeAuditService(
        AppDbContext db,
        ImpactAnalysisService impactService,
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        ILogger<AIChangeAuditService> logger)
    {
        _db = db;
        _impactService = impactService;
        _httpClientFactory = httpClientFactory;
        _config = config;
        _logger = logger;
    }

    public async Task<ChangeAuditReport?> AnalyzeChangeAsync(
        ChangeAuditRequest request,
        CancellationToken ct = default)
    {
        // ── 1. Load project data ──────────────────────────────────────────────
        var requirements = await _db.Scenarios
            .Where(s => s.ProjectId == request.ProjectId && s.Kind == ScenarioKind.Requirement)
            .OrderBy(s => s.Title)
            .ToListAsync(ct);

        var tests = await _db.Scenarios
            .Where(s => s.ProjectId == request.ProjectId && s.Kind == ScenarioKind.Test)
            .OrderBy(s => s.Title)
            .ToListAsync(ct);

        var coversLinks = await _db.TraceLinks
            .Where(t => t.ProjectId == request.ProjectId
                     && t.LinkType == TraceLinkType.Covers
                     && t.SourceKind == TraceLinkArtifactKind.Scenario
                     && t.TargetKind == TraceLinkArtifactKind.Scenario)
            .ToListAsync(ct);

        var linkCountByReq = coversLinks
            .GroupBy(l => l.TargetId)
            .ToDictionary(g => g.Key, g => g.Count());

        // ── 2. Call Claude ────────────────────────────────────────────────────
        var claudeResult = await CallClaudeAsync(
            request.ChangeDescription, requirements, tests, linkCountByReq, ct);

        if (claudeResult is null)
        {
            _logger.LogWarning(
                "AIChangeAudit_ClaudeCallFailed {ProjectId}",
                request.ProjectId);
            return null;
        }

        // ── 3. Resolve IDs → domain objects + formal impact data ─────────────
        var reqById = requirements.ToDictionary(r => NormalizeId(r.Id.ToString("N")));
        var testById = tests.ToDictionary(t => NormalizeId(t.Id.ToString("N")));

        var affectedRequirements = new List<AuditAffectedRequirement>();
        var allRegressionTests = new List<RegressionItem>();

        foreach (var rawId in claudeResult.AffectedRequirementIds.Distinct())
        {
            if (!reqById.TryGetValue(NormalizeId(rawId), out var req)) continue;

            var impact = await _impactService.GetRequirementImpactAsync(
                request.ProjectId, req.Id, ct);
            if (impact is null) continue;

            var reqReason = GetReason(claudeResult.RequirementReasons, rawId);

            affectedRequirements.Add(new AuditAffectedRequirement
            {
                Requirement = impact.Requirement,
                RiskLevel = impact.Summary.RiskLevel,
                LinkedTestCount = impact.Summary.TotalLinkedTests,
                AiRelevanceReason = NonEmpty(reqReason, "Identified as potentially affected by this change."),
            });

            allRegressionTests.AddRange(impact.RegressionRecommendation);
        }

        var affectedTests = new List<AuditAffectedTest>();
        foreach (var rawId in claudeResult.AffectedTestIds.Distinct())
        {
            if (!testById.TryGetValue(NormalizeId(rawId), out var test)) continue;
            affectedTests.Add(new AuditAffectedTest
            {
                Test = test,
                AiRelevanceReason = NonEmpty(GetReason(claudeResult.TestReasons, rawId), "Identified as potentially affected by this change."),
            });
        }

        // ── 4. Assemble report ────────────────────────────────────────────────
        var overallRisk = affectedRequirements.Count == 0
            ? RiskLevel.Low
            : affectedRequirements.Max(r => r.RiskLevel);

        var dedupedRegression = allRegressionTests
            .DistinctBy(r => r.Test.Id)
            .OrderBy(r => r.Test.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _logger.LogInformation(
            "AIChangeAudit_Complete {ProjectId} {Requirements} {Tests} {Risk}",
            request.ProjectId, affectedRequirements.Count, affectedTests.Count, overallRisk);

        return new ChangeAuditReport
        {
            ChangeDescription = request.ChangeDescription,
            OverallRiskLevel = overallRisk,
            AiReasoning = claudeResult.Reasoning,
            RegressionScope = claudeResult.RegressionScope,
            AffectedRequirements = affectedRequirements,
            AffectedTests = affectedTests,
            CoverageGaps = claudeResult.CoverageGaps,
            RecommendedRegressionTests = dedupedRegression,
        };
    }

    // ── Claude API ────────────────────────────────────────────────────────────

    private async Task<ClaudeAuditInput?> CallClaudeAsync(
        string changeDescription,
        IReadOnlyList<Scenario> requirements,
        IReadOnlyList<Scenario> tests,
        Dictionary<Guid, int> linkCounts,
        CancellationToken ct)
    {
        var apiKey = _config["Anthropic:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogError("AIChangeAudit_MissingApiKey — set Anthropic:ApiKey in configuration");
            return null;
        }

        var model = _config["Anthropic:Model"] ?? "claude-sonnet-4-6";

        var requestBody = new
        {
            model,
            max_tokens = 2048,
            system = SystemPrompt,
            messages = new[] { new { role = "user", content = BuildUserPrompt(changeDescription, requirements, tests, linkCounts) } },
            tools = new[] { AuditToolDefinition },
            tool_choice = new { type = "tool", name = "submit_audit" },
        };

        try
        {
            var http = _httpClientFactory.CreateClient("Anthropic");
            var json = JsonSerializer.Serialize(requestBody, _snakeCase);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await http.PostAsync("https://api.anthropic.com/v1/messages", content, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("AIChangeAudit_ApiError {Status} {Body}",
                    (int)response.StatusCode,
                    body.Length > 300 ? body[..300] : body);
                return null;
            }

            return ParseClaudeResponse(body);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AIChangeAudit_HttpException");
            return null;
        }
    }

    private ClaudeAuditInput? ParseClaudeResponse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (!root.TryGetProperty("content", out var content)) return null;

            foreach (var block in content.EnumerateArray())
            {
                if (!block.TryGetProperty("type", out var typeEl)) continue;
                if (typeEl.GetString() != "tool_use") continue;
                if (!block.TryGetProperty("input", out var input)) continue;

                return new ClaudeAuditInput(
                    AffectedRequirementIds: ReadStringArray(input, "affected_requirement_ids"),
                    AffectedTestIds: ReadStringArray(input, "affected_test_ids"),
                    CoverageGaps: ReadStringArray(input, "coverage_gaps"),
                    RegressionScope: ReadString(input, "regression_scope"),
                    Reasoning: ReadString(input, "reasoning"),
                    RequirementReasons: ReadStringDict(input, "requirement_reasons"),
                    TestReasons: ReadStringDict(input, "test_reasons"));
            }

            _logger.LogWarning("AIChangeAudit_NoToolUseBlock");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AIChangeAudit_ParseFailed");
            return null;
        }
    }

    // ── Prompt builders ───────────────────────────────────────────────────────

    private const string SystemPrompt = """
        You are an expert QA analyst assistant embedded in a requirements traceability platform.

        You will receive:
          - A software change description written by a developer or test lead
          - The full list of requirements in the project (with title, description, linked test count)
          - The full list of test scenarios in the project (with title)

        Your job: identify which requirements and tests are LIKELY affected by the described change.

        Guidelines:
          - Match the change description against requirement titles and descriptions semantically
          - Be specific and conservative — only flag components that are plausibly affected
          - Provide a clear, concise reason for each flagged component
          - Coverage gaps are affected requirements with 0 linked tests
          - Risk context: 0 linked tests = High, 1 = Medium, 2+ = Low
          - regression_scope should be a single human-readable sentence
          - reasoning should explain your approach in 2-4 sentences

        You MUST call the submit_audit tool with your structured analysis.
        Do not respond with plain text.
        """;

    private static string BuildUserPrompt(
        string changeDescription,
        IReadOnlyList<Scenario> requirements,
        IReadOnlyList<Scenario> tests,
        Dictionary<Guid, int> linkCounts)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Change description: {changeDescription}");
        sb.AppendLine();
        sb.AppendLine("=== Requirements ===");
        foreach (var req in requirements)
        {
            var count = linkCounts.TryGetValue(req.Id, out var c) ? c : 0;
            sb.AppendLine($"ID: {req.Id:N}");
            sb.AppendLine($"Title: {req.Title}");
            if (!string.IsNullOrWhiteSpace(req.Description))
                sb.AppendLine($"Description: {req.Description}");
            sb.AppendLine($"Linked tests: {count}");
            sb.AppendLine();
        }
        sb.AppendLine("=== Test Scenarios ===");
        foreach (var test in tests)
        {
            sb.AppendLine($"ID: {test.Id:N}");
            sb.AppendLine($"Title: {test.Title}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    // Tool definition is a static object; serialised via _snakeCase so property
    // names become snake_case automatically.
    private static readonly object AuditToolDefinition = new
    {
        Name = "submit_audit",
        Description = "Submit the structured change impact analysis result.",
        InputSchema = new
        {
            Type = "object",
            Properties = new
            {
                AffectedRequirementIds = new
                {
                    Type = "array",
                    Items = new { Type = "string" },
                    Description = "IDs of requirements likely affected (use the exact ID string from the requirements list)",
                },
                AffectedTestIds = new
                {
                    Type = "array",
                    Items = new { Type = "string" },
                    Description = "IDs of test scenarios likely affected by this change",
                },
                RequirementReasons = new
                {
                    Type = "object",
                    Description = "Map of requirement ID → short reason why it is affected",
                    AdditionalProperties = new { Type = "string" },
                },
                TestReasons = new
                {
                    Type = "object",
                    Description = "Map of test ID → short reason why it is affected",
                    AdditionalProperties = new { Type = "string" },
                },
                CoverageGaps = new
                {
                    Type = "array",
                    Items = new { Type = "string" },
                    Description = "Coverage gaps exposed by this change (e.g. 'FR-001 has no linked tests')",
                },
                RegressionScope = new
                {
                    Type = "string",
                    Description = "One sentence summarising which tests should be run",
                },
                Reasoning = new
                {
                    Type = "string",
                    Description = "2-4 sentences explaining the analysis approach and conclusions",
                },
            },
            Required = new[] { "affected_requirement_ids", "affected_test_ids", "coverage_gaps", "regression_scope", "reasoning" },
        },
    };

    // ── JSON helpers ──────────────────────────────────────────────────────────

    private static IReadOnlyList<string> ReadStringArray(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var arr)) return [];
        return arr.EnumerateArray()
            .Select(el => el.GetString() ?? string.Empty)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
    }

    private static string ReadString(JsonElement parent, string key) =>
        parent.TryGetProperty(key, out var el) ? el.GetString() ?? string.Empty : string.Empty;

    private static IReadOnlyDictionary<string, string>? ReadStringDict(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var obj)) return null;
        var dict = new Dictionary<string, string>();
        foreach (var prop in obj.EnumerateObject())
            dict[prop.Name] = prop.Value.GetString() ?? string.Empty;
        return dict;
    }

    private static string NormalizeId(string id) =>
        id.Replace("-", string.Empty).ToLowerInvariant();

    private static string NonEmpty(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static string? GetReason(IReadOnlyDictionary<string, string>? dict, string key) =>
        dict is not null && dict.TryGetValue(key, out var v) ? v : null;

    // ── Internal DTO ──────────────────────────────────────────────────────────

    private record ClaudeAuditInput(
        IReadOnlyList<string> AffectedRequirementIds,
        IReadOnlyList<string> AffectedTestIds,
        IReadOnlyList<string> CoverageGaps,
        string RegressionScope,
        string Reasoning,
        IReadOnlyDictionary<string, string>? RequirementReasons,
        IReadOnlyDictionary<string, string>? TestReasons);
}
