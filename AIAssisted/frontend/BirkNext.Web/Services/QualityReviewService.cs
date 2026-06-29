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
        IDeliveryReadinessAssessmentService delivery)
    {
        _parser      = parser;
        _auditor     = auditor;
        _compliance  = compliance;
        _standards   = standards;
        _qaReadiness = qaReadiness;
        _delivery    = delivery;

        // Static packs — always available, registered in display order.
        _adapters.Add(new QaAuditorAdapter(auditor));
        _adapters.Add(new ConstitutionComplianceAdapter(compliance));
        _adapters.Add(new QaReadinessAdapter(qaReadiness));
        _adapters.Add(new DeliveryReadinessAdapter(delivery));
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
        IEnumerable<string> selectedPackIds)
    {
        // Parse all artifacts once — shared across every selected pack.
        var parsed = _parser.Parse(constitutionText, specText, planText, taskText);

        var ctx = new RunContext
        {
            ConstitutionText = constitutionText,
            SpecText         = specText,
            PlanText         = planText,
            TaskText         = taskText,
            Constitution     = parsed.Constitution,
            Spec             = parsed.Spec,
            Plan             = parsed.Plan,
            Tasks            = parsed.Tasks,
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

    // ── Internal execution context ────────────────────────────────────────────

    private sealed class RunContext
    {
        public string? ConstitutionText { get; init; }
        public string? SpecText         { get; init; }
        public string? PlanText         { get; init; }
        public string? TaskText         { get; init; }
        public ConstitutionDocument? Constitution { get; init; }
        public SpecTree?             Spec         { get; init; }
        public PlanDocument?         Plan         { get; init; }
        public TaskTree?             Tasks        { get; init; }
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
            if (string.IsNullOrWhiteSpace(ctx.SpecText))
                return Task.FromResult(new QualityReviewPackResult
                {
                    PackId    = Descriptor.PackId,
                    PackName  = Descriptor.PackName,
                    PackGroup = Descriptor.PackGroup,
                    Error     = "Specification not loaded — load spec.md to run standards checks.",
                });

            var report  = _standards.Assess(
                ctx.ConstitutionText,
                ctx.SpecText!,
                ctx.PlanText,
                ctx.TaskText,
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
}
