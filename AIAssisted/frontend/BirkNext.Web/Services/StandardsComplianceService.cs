using System.Net.Http.Json;
using BirkNext.Web.Models;
using BirkNext.Web.Services.Engine;
using BirkNext.Web.Services.Engine.Packs;

namespace BirkNext.Web.Services;

/// <summary>
/// Runs keyword-based documentation coverage checks against discovered industry standards.
/// Rule packs are loaded from JSON files discovered via wwwroot/standards/index.json.
///
/// Adding a new standard requires only:
///   1. A new JSON rule pack file (e.g. standards/nist/800-53/rule-pack.json)
///   2. A new entry in standards/index.json
///   No C# changes needed.
/// </summary>
public sealed class StandardsComplianceService : IStandardsComplianceService
{
    private readonly HttpClient _http;
    private readonly RuleEngine _engine = new();

    private readonly Dictionary<string, StandardRulePack> _packs =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<RulePackIndexEntry> _indexEntries = [];
    private readonly List<RulePackLoadResult> _loadResults  = [];
    private bool _initialized;

    public IReadOnlyList<RulePackLoadResult> LoadedPacks    => _loadResults;
    public IReadOnlyList<RulePackIndexEntry> DiscoveredPacks => _indexEntries;

    public StandardsComplianceService(HttpClient http)
    {
        _http = http;
    }

    // ── Initialization ────────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;

        List<RulePackIndexEntry>? index;
        try
        {
            index = await _http.GetFromJsonAsync<List<RulePackIndexEntry>>("standards/index.json");
        }
        catch (Exception ex)
        {
            _loadResults.Add(new RulePackLoadResult(
                "index", "standards/index.json", null,
                $"Could not load standards/index.json: {ex.Message}"));
            return;
        }

        if (index is null || index.Count == 0)
        {
            _loadResults.Add(new RulePackLoadResult(
                "index", "standards/index.json", null,
                "standards/index.json is empty or returned no entries."));
            return;
        }

        foreach (var entry in index)
        {
            if (string.IsNullOrWhiteSpace(entry.StandardId) || string.IsNullOrWhiteSpace(entry.Path))
                continue;
            _indexEntries.Add(entry);
            await LoadPackAsync(entry);
        }
    }

    private async Task LoadPackAsync(RulePackIndexEntry entry)
    {
        try
        {
            var pack = await _http.GetFromJsonAsync<StandardRulePack>(entry.Path);

            var validationError = ValidatePack(pack);
            if (validationError is not null)
            {
                _loadResults.Add(new RulePackLoadResult(entry.StandardId, entry.Path, null,
                    $"Rule pack at {entry.Path} is invalid: {validationError}"));
                return;
            }

            _packs[entry.StandardId] = pack!;
            _loadResults.Add(new RulePackLoadResult(entry.StandardId, entry.Path, pack, null));
        }
        catch (Exception ex)
        {
            _loadResults.Add(new RulePackLoadResult(entry.StandardId, entry.Path, null,
                $"Could not load {entry.Path}: {ex.Message}"));
        }
    }

    private static string? ValidatePack(StandardRulePack? pack)
    {
        if (pack is null)
            return "pack deserialised as null";
        if (string.IsNullOrWhiteSpace(pack.StandardId))
            return "standardId is missing";
        if (string.IsNullOrWhiteSpace(pack.StandardName))
            return "standardName is missing";
        if (pack.Rules.Count == 0)
            return "rules list is empty";

        var validSeverities = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Critical", "High", "Medium", "Low", "Info" };

        for (int i = 0; i < pack.Rules.Count; i++)
        {
            var rule = pack.Rules[i];
            if (string.IsNullOrWhiteSpace(rule.RuleId))
                return $"rules[{i}].ruleId is missing";
            if (!validSeverities.Contains(rule.Severity))
                return $"rule '{rule.RuleId}' has unrecognised severity '{rule.Severity}'";
            if (rule.RequiredKeywords.Count == 0 && rule.OptionalKeywords.Count == 0)
                return $"rule '{rule.RuleId}' has no keywords";
        }

        return null;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public StandardsComplianceReport Assess(
        string              combinedText,
        bool                hasConstitution,
        bool                hasSpec,
        bool                hasPlan,
        bool                hasTasks,
        IEnumerable<string> selectedStandards)
    {
        var context  = new RuleContext { CombinedText = combinedText };
        var selected = selectedStandards.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Preserve index.json order; skip packs that failed to load.
        var rulePacks = _indexEntries
            .Where(e => selected.Contains(e.StandardId) && _packs.ContainsKey(e.StandardId))
            .Select(e => (IRulePack)new StandardsKeywordRulePack(_packs[e.StandardId]))
            .ToList();

        var packResults = _engine.Run(context, rulePacks);

        var results = packResults
            .SelectMany(pr => pr.Findings.Select(MapToCheckResult))
            .ToList();

        var summaries    = BuildSummaries(packResults, results, selected);
        var overallScore = summaries.Count > 0
            ? Math.Round(summaries.Average(s => s.Score), 1)
            : 0.0;

        return new StandardsComplianceReport
        {
            Results          = results,
            Summaries        = summaries,
            OverallScore     = overallScore,
            HasSpecification = hasSpec,
            HasConstitution  = hasConstitution,
            HasPlan          = hasPlan,
            HasTasks         = hasTasks,
            CheckedAt        = DateTimeOffset.UtcNow,
        };
    }

    // ── Context & mapping ─────────────────────────────────────────────────────

    private static StandardCheckResult MapToCheckResult(RuleFinding f)
    {
        var status = f.Status switch
        {
            "Passed"  => CheckStatus.Passed,
            "Warning" => CheckStatus.Warning,
            _         => CheckStatus.Failed,
        };

        var severity = Enum.TryParse<CheckSeverity>(f.Severity, ignoreCase: true, out var sev)
            ? sev
            : CheckSeverity.Medium;

        return new StandardCheckResult
        {
            RuleId         = f.RuleId,
            StandardId     = f.RulePackId,
            Category       = f.Category,
            Title          = f.Title,
            Description    = f.Description,
            Severity       = severity,
            Status         = status,
            Evidence       = f.Evidence,
            Recommendation = status == CheckStatus.Passed ? string.Empty : f.Recommendation,
        };
    }

    private List<StandardsComplianceSummary> BuildSummaries(
        List<RulePackResult>      packResults,
        List<StandardCheckResult> checkResults,
        HashSet<string>           selected)
    {
        var list = new List<StandardsComplianceSummary>();

        foreach (var entry in _indexEntries.Where(e => selected.Contains(e.StandardId)))
        {
            _packs.TryGetValue(entry.StandardId, out var jsonPack);

            var pr = packResults.FirstOrDefault(p =>
                string.Equals(p.RulePackId, entry.StandardId, StringComparison.OrdinalIgnoreCase));

            var group = checkResults
                .Where(r => string.Equals(r.StandardId, entry.StandardId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var applicable = group.Where(r => r.Status != CheckStatus.NotApplicable).ToList();

            list.Add(new StandardsComplianceSummary
            {
                StandardId      = entry.StandardId,
                StandardName    = jsonPack?.StandardName    ?? entry.Label,
                StandardVersion = jsonPack?.StandardVersion ?? string.Empty,
                RulePackVersion = jsonPack?.RulePackVersion ?? string.Empty,
                LastUpdated     = jsonPack?.LastUpdated     ?? string.Empty,
                TotalChecks     = applicable.Count,
                Passed          = applicable.Count(r => r.Status == CheckStatus.Passed),
                Warnings        = applicable.Count(r => r.Status == CheckStatus.Warning),
                Failed          = applicable.Count(r => r.Status == CheckStatus.Failed),
                Score           = pr?.Score ?? 0.0,
            });
        }

        return list;
    }
}
