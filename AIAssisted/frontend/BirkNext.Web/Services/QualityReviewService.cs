using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Orchestrates the unified Quality Review experience.
/// Discovers available review packs and runs any selected combination against
/// shared parsed artifacts — artifacts are parsed exactly once regardless of
/// how many packs are selected.
///
/// Four pack groups are supported:
///   Quality       — QA Auditor (structural quality, code-driven)
///   Governance    — Constitution Compliance (code-driven)
///   Standards     — keyword-based packs discovered from index.json
///   Readiness     — QA Readiness, Delivery Readiness (code-driven)
///
/// Adding a new industry standard requires only a JSON file and an index.json
/// entry — no changes to this service or the Quality Review page.
/// Adding a new code-driven pack requires implementing the internal adapter
/// interface and registering it in the constructor.
/// </summary>
public sealed class QualityReviewService : IQualityReviewService
{
    private readonly IArtifactParserService               _parser;
    private readonly IQaAuditorService                    _auditor;
    private readonly IConstitutionComplianceService       _compliance;
    private readonly IStandardsComplianceService          _standards;
    private readonly IQAReadinessService                  _qaReadiness;
    private readonly IDeliveryReadinessAssessmentService  _delivery;
    private readonly IDataModelAnalysisService            _dataModelAnalysis;

    private readonly List<IPackAdapter> _adapters = [];
    private bool _initialized;

    public IReadOnlyList<QualityReviewPackDescriptor> AvailablePacks =>
        _adapters.Select(a => a.Descriptor).ToList();

    public QualityReviewService(
        IArtifactParserService              parser,
        IQaAuditorService                   auditor,
        IConstitutionComplianceService      compliance,
        IStandardsComplianceService         standards,
        IQAReadinessService                 qaReadiness,
        IDeliveryReadinessAssessmentService delivery,
        IDataModelAnalysisService           dataModelAnalysis)
    {
        _parser             = parser;
        _auditor            = auditor;
        _compliance         = compliance;
        _standards          = standards;
        _qaReadiness        = qaReadiness;
        _delivery           = delivery;
        _dataModelAnalysis  = dataModelAnalysis;

        // Static packs — always available, registered in display order.
        _adapters.Add(new QaAuditorAdapter(auditor));
        _adapters.Add(new ConstitutionComplianceAdapter(compliance));
        _adapters.Add(new QaReadinessAdapter(qaReadiness));
        _adapters.Add(new DeliveryReadinessAdapter(delivery));
        _adapters.Add(new DataModelQualityAdapter(dataModelAnalysis));
    }

    // ── Initialisation ────────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;

        await _standards.InitializeAsync();

        foreach (var entry in _standards.DiscoveredPacks)
            _adapters.Add(new StandardKeywordAdapter(_standards, entry));
    }

    // ── Execution ─────────────────────────────────────────────────────────────

    public async Task<QualityReviewReport> RunAsync(
        string? constitutionText,
        string? specText,
        string? planText,
        string? taskText,
        string? dataModelText,
        IEnumerable<string> selectedPackIds)
    {
        // Parse all artifacts once — shared across every selected pack.
        var parsed = _parser.Parse(constitutionText, specText, planText, taskText);

        // Build clean combined text from the shared engine's token stream.
        // Used by keyword-based packs (WCAG, OWASP, GDPR, ISO 25010).
        var combinedText = BuildCombinedText(constitutionText, specText, planText, taskText);

        DataModelDocument? dataModel = null;
        if (!string.IsNullOrWhiteSpace(dataModelText))
            dataModel = _dataModelAnalysis.Parse(dataModelText);

        var ctx = new RunContext
        {
            CombinedText     = combinedText,
            Constitution     = parsed.Constitution,
            Spec             = parsed.Spec,
            Plan             = parsed.Plan,
            Tasks            = parsed.Tasks,
            DataModel        = dataModel,
        };

        var selected = selectedPackIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var results  = new List<QualityReviewPackResult>();

        foreach (var adapter in _adapters.Where(a => selected.Contains(a.Descriptor.PackId)))
        {
            try
            {
                results.Add(await adapter.ExecuteAsync(ctx));
            }
            catch (Exception ex)
            {
                results.Add(new QualityReviewPackResult
                {
                    PackId    = adapter.Descriptor.PackId,
                    PackName  = adapter.Descriptor.PackName,
                    PackGroup = adapter.Descriptor.PackGroup,
                    Error     = ex.Message,
                });
            }
        }

        var valid = results.Where(r => r.Error is null).ToList();

        return new QualityReviewReport
        {
            PackResults   = results,
            OverallScore  = valid.Count > 0
                ? Math.Round(valid.Average(r => r.Score), 1) : 0,
            TotalFindings = results.Sum(r => r.Critical + r.High + r.Medium + r.Low),
            CriticalCount = results.Sum(r => r.Critical),
            HighCount     = results.Sum(r => r.High),
            MediumCount   = results.Sum(r => r.Medium),
            LowCount      = results.Sum(r => r.Low),
            RunAt         = DateTimeOffset.UtcNow,
        };
    }

    // ── Combined-text builder ─────────────────────────────────────────────────

    /// <summary>
    /// Extracts clean searchable text from each artifact using the shared
    /// Markdown Document Engine. Markdown syntax (##, -, |---|, etc.) is
    /// stripped; only semantic content (headings, prose, bullets, table cells,
    /// code lines) is retained. This is the single tokenisation pass for all
    /// keyword-based packs — no pack re-parses the raw markdown.
    /// </summary>
    private static string BuildCombinedText(
        string? constitutionText,
        string? specText,
        string? planText,
        string? taskText)
    {
        var parts = new List<string>(4);

        foreach (var text in new[] { constitutionText, specText, planText, taskText })
        {
            if (string.IsNullOrWhiteSpace(text)) continue;

            var content = string.Join("\n",
                MarkdownTokenizer.Tokenize(text)
                    .Where(t => t.Kind is not (
                        MarkdownTokenKind.Blank          or
                        MarkdownTokenKind.FencedCodeStart or
                        MarkdownTokenKind.FencedCodeEnd   or
                        MarkdownTokenKind.TableSeparator  or
                        MarkdownTokenKind.HorizontalRule))
                    .Select(t => t.Kind == MarkdownTokenKind.TableRow
                        ? string.Join(" ", t.TableCells ?? [])
                        : t.Content)
                    .Where(c => !string.IsNullOrWhiteSpace(c)));

            if (!string.IsNullOrEmpty(content))
                parts.Add(content);
        }

        return string.Join("\n", parts);
    }

    // ── Internal execution context ────────────────────────────────────────────

    private sealed class RunContext
    {
        // Clean text extracted from all artifacts by the shared Markdown Document Engine.
        // Used by keyword-based packs (Standards group).
        public string CombinedText { get; init; } = string.Empty;

        // Parsed domain models — used by structural packs (Quality / Governance / Readiness).
        public ConstitutionDocument? Constitution { get; init; }
        public SpecTree?             Spec         { get; init; }
        public PlanDocument?         Plan         { get; init; }
        public TaskTree?             Tasks        { get; init; }
        public DataModelDocument?    DataModel    { get; init; }
    }

    // ── Internal adapter interface ────────────────────────────────────────────

    private interface IPackAdapter
    {
        QualityReviewPackDescriptor Descriptor { get; }
        Task<QualityReviewPackResult> ExecuteAsync(RunContext ctx);
    }

    // ── QA Auditor adapter ────────────────────────────────────────────────────

    private sealed class QaAuditorAdapter : IPackAdapter
    {
        private readonly IQaAuditorService _auditor;

        public QualityReviewPackDescriptor Descriptor { get; } = new(
            PackId:          "qa-auditor",
            PackGroup:       "Quality",
            PackName:        "QA Auditor",
            PackDescription: "Structural consistency checks",
            IsDefault:       true);

        public QaAuditorAdapter(IQaAuditorService auditor) => _auditor = auditor;

        public Task<QualityReviewPackResult> ExecuteAsync(RunContext ctx)
        {
            var report = _auditor.Audit(ctx.Constitution, ctx.Spec, ctx.Plan, ctx.Tasks);
            return Task.FromResult(new QualityReviewPackResult
            {
                PackId    = Descriptor.PackId,
                PackName  = Descriptor.PackName,
                PackGroup = Descriptor.PackGroup,
                Score     = report.Health.AuditScore,
                Critical  = report.Health.CriticalCount,
                High      = report.Health.HighCount,
                Medium    = report.Health.MediumCount,
                Low       = report.Health.LowCount,
                Info      = report.Health.InfoCount,
                QaAudit   = report,
            });
        }
    }

    // ── Constitution Compliance adapter ───────────────────────────────────────

    private sealed class ConstitutionComplianceAdapter : IPackAdapter
    {
        private readonly IConstitutionComplianceService _compliance;

        public QualityReviewPackDescriptor Descriptor { get; } = new(
            PackId:          "constitution-compliance",
            PackGroup:       "Governance",
            PackName:        "Constitution Compliance",
            PackDescription: "Governance rule validation",
            IsDefault:       true);

        public ConstitutionComplianceAdapter(IConstitutionComplianceService compliance) =>
            _compliance = compliance;

        public Task<QualityReviewPackResult> ExecuteAsync(RunContext ctx)
        {
            if (ctx.Constitution is null)
                return Task.FromResult(new QualityReviewPackResult
                {
                    PackId    = Descriptor.PackId,
                    PackName  = Descriptor.PackName,
                    PackGroup = Descriptor.PackGroup,
                    Error     = "Constitution not loaded — load constitution.md to run this pack.",
                });

            var report = _compliance.Analyze(ctx.Constitution, ctx.Spec, ctx.Plan, ctx.Tasks);

            // Map ViolationSeverity → normalised counts (violations + gaps combined).
            int critical = Count(report.Violations, ViolationSeverity.Critical)
                         + Count(report.Gaps,       ViolationSeverity.Critical);
            int high     = Count(report.Violations, ViolationSeverity.High)
                         + Count(report.Gaps,       ViolationSeverity.High);
            int medium   = Count(report.Violations, ViolationSeverity.Medium)
                         + Count(report.Gaps,       ViolationSeverity.Medium);
            int low      = Count(report.Violations, ViolationSeverity.Low)
                         + Count(report.Gaps,       ViolationSeverity.Low);

            return Task.FromResult(new QualityReviewPackResult
            {
                PackId     = Descriptor.PackId,
                PackName   = Descriptor.PackName,
                PackGroup  = Descriptor.PackGroup,
                Score      = report.Health.CompliancePercentage,
                Critical   = critical,
                High       = high,
                Medium     = medium,
                Low        = low,
                Compliance = report,
            });
        }

        private static int Count(IEnumerable<ComplianceViolation> items, ViolationSeverity sev) =>
            items.Count(v => v.Severity == sev);

        private static int Count(IEnumerable<ComplianceGap> items, ViolationSeverity sev) =>
            items.Count(g => g.Severity == sev);
    }

    // ── QA Readiness adapter ──────────────────────────────────────────────────

    private sealed class QaReadinessAdapter : IPackAdapter
    {
        private readonly IQAReadinessService _qaReadiness;

        public QualityReviewPackDescriptor Descriptor { get; } = new(
            PackId:          "qa-readiness",
            PackGroup:       "Readiness",
            PackName:        "QA Readiness",
            PackDescription: "Testing readiness assessment",
            IsDefault:       false);

        public QaReadinessAdapter(IQAReadinessService qaReadiness) => _qaReadiness = qaReadiness;

        public Task<QualityReviewPackResult> ExecuteAsync(RunContext ctx)
        {
            var report = _qaReadiness.Assess(ctx.Constitution, ctx.Spec, ctx.Plan, ctx.Tasks);

            int critical = report.Gaps.Count(g => g.Severity == ViolationSeverity.Critical);
            int high     = report.Gaps.Count(g => g.Severity == ViolationSeverity.High);
            int medium   = report.Gaps.Count(g => g.Severity == ViolationSeverity.Medium);
            int low      = report.Gaps.Count(g => g.Severity == ViolationSeverity.Low);

            return Task.FromResult(new QualityReviewPackResult
            {
                PackId      = Descriptor.PackId,
                PackName    = Descriptor.PackName,
                PackGroup   = Descriptor.PackGroup,
                Score       = report.OverallScore,
                Critical    = critical,
                High        = high,
                Medium      = medium,
                Low         = low,
                QaReadiness = report,
            });
        }
    }

    // ── Delivery Readiness adapter ────────────────────────────────────────────

    private sealed class DeliveryReadinessAdapter : IPackAdapter
    {
        private readonly IDeliveryReadinessAssessmentService _delivery;

        public QualityReviewPackDescriptor Descriptor { get; } = new(
            PackId:          "delivery-readiness",
            PackGroup:       "Readiness",
            PackName:        "Delivery Readiness",
            PackDescription: "Release readiness assessment",
            IsDefault:       false);

        public DeliveryReadinessAdapter(IDeliveryReadinessAssessmentService delivery) =>
            _delivery = delivery;

        public Task<QualityReviewPackResult> ExecuteAsync(RunContext ctx)
        {
            var report = _delivery.Assess(ctx.Constitution, ctx.Spec, ctx.Plan, ctx.Tasks);

            int critical = report.Blockers.Count(b => b.Severity == GateSeverity.Critical);
            int high     = report.Blockers.Count(b => b.Severity == GateSeverity.High);
            int medium   = report.Blockers.Count(b => b.Severity == GateSeverity.Medium);
            int low      = report.Blockers.Count(b => b.Severity == GateSeverity.Low);

            return Task.FromResult(new QualityReviewPackResult
            {
                PackId            = Descriptor.PackId,
                PackName          = Descriptor.PackName,
                PackGroup         = Descriptor.PackGroup,
                Score             = report.Health.OverallReadinessScore,
                Critical          = critical,
                High              = high,
                Medium            = medium,
                Low               = low,
                DeliveryReadiness = report,
            });
        }
    }

    // ── Standard keyword adapter (one instance per discovered standard) ────────

    private sealed class StandardKeywordAdapter : IPackAdapter
    {
        private readonly IStandardsComplianceService _standards;
        private readonly RulePackIndexEntry           _entry;

        public QualityReviewPackDescriptor Descriptor { get; }

        public StandardKeywordAdapter(IStandardsComplianceService standards, RulePackIndexEntry entry)
        {
            _standards = standards;
            _entry     = entry;
            Descriptor = new QualityReviewPackDescriptor(
                PackId:          entry.StandardId,
                PackGroup:       "Standards",
                PackName:        entry.Label,
                PackDescription: entry.Description,
                IsDefault:       entry.StandardId is "WCAG22" or "OWASP");
        }

        public Task<QualityReviewPackResult> ExecuteAsync(RunContext ctx)
        {
            if (string.IsNullOrWhiteSpace(ctx.CombinedText))
                return Task.FromResult(new QualityReviewPackResult
                {
                    PackId    = Descriptor.PackId,
                    PackName  = Descriptor.PackName,
                    PackGroup = Descriptor.PackGroup,
                    Error     = "Specification not loaded — load spec.md to run standards checks.",
                });

            var report  = _standards.Assess(
                ctx.CombinedText,
                ctx.Constitution is not null,
                ctx.Spec         is not null,
                ctx.Plan         is not null,
                ctx.Tasks        is not null,
                [_entry.StandardId]);

            var summary = report.Summaries.FirstOrDefault();
            var results = report.Results;

            // Count only Failed rules; Warnings are gaps, not outright failures.
            int high   = results.Count(r => r.Status == CheckStatus.Failed && r.Severity == CheckSeverity.High);
            int medium = results.Count(r => r.Status == CheckStatus.Failed && r.Severity == CheckSeverity.Medium);
            int low    = results.Count(r => r.Status == CheckStatus.Failed && r.Severity == CheckSeverity.Low);

            return Task.FromResult(new QualityReviewPackResult
            {
                PackId    = Descriptor.PackId,
                PackName  = Descriptor.PackName,
                PackGroup = Descriptor.PackGroup,
                Score     = summary?.Score ?? 0,
                High      = high,
                Medium    = medium,
                Low       = low,
                Standards = report,
            });
        }
    }

    // ── Data Model Quality adapter ────────────────────────────────────────────

    private sealed class DataModelQualityAdapter : IPackAdapter
    {
        private readonly IDataModelAnalysisService _dataModel;

        public QualityReviewPackDescriptor Descriptor { get; } = new(
            PackId:          "data-model-quality",
            PackGroup:       "Quality",
            PackName:        "Data Model Quality",
            PackDescription: "Schema structure, relationships, and traceability checks",
            IsDefault:       false);

        public DataModelQualityAdapter(IDataModelAnalysisService dataModel) => _dataModel = dataModel;

        public Task<QualityReviewPackResult> ExecuteAsync(RunContext ctx)
        {
            if (ctx.DataModel is null)
                return Task.FromResult(new QualityReviewPackResult
                {
                    PackId    = Descriptor.PackId,
                    PackName  = Descriptor.PackName,
                    PackGroup = Descriptor.PackGroup,
                    Error     = "Data model not loaded — load data-model.md to run this pack.",
                });

            var doc = ctx.DataModel;

            int critical = doc.Findings.Count(f => f.Severity == DataModelSeverity.Critical);
            int medium   = doc.Findings.Count(f => f.Severity == DataModelSeverity.Error);
            int low      = doc.Findings.Count(f => f.Severity == DataModelSeverity.Warning);
            int info     = doc.Findings.Count(f => f.Severity == DataModelSeverity.Info);

            int totalPenalty = critical * 25 + medium * 10 + low * 3;
            double score = doc.EntityCount == 0 ? 0 : Math.Max(0, 100 - totalPenalty);

            return Task.FromResult(new QualityReviewPackResult
            {
                PackId    = Descriptor.PackId,
                PackName  = Descriptor.PackName,
                PackGroup = Descriptor.PackGroup,
                Score     = score,
                Critical  = critical,
                High      = 0,
                Medium    = medium,
                Low       = low,
                Info      = info,
                DataModel = doc,
            });
        }
    }
}
