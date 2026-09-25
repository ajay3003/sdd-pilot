using BirkNext.Web.Models;
using System.Text;

namespace BirkNext.Web.Services;

public sealed class ReportExportService : IReportExportService
{
    // ── Public API ────────────────────────────────────────────────────────────────

    public string ExportQualityReview(QualityReviewReport report, string? projectName)
    {
        var sb = new StringBuilder();

        // KPI row
        sb.Append("<div class=\"kpi-row\">");
        sb.Append(Kpi($"{report.OverallScore:0}", "Overall Score"));
        sb.Append(Kpi(report.TotalFindings.ToString(), "Total Findings"));
        sb.Append(Kpi(report.CriticalCount.ToString(), "Critical"));
        sb.Append(Kpi(report.HighCount.ToString(), "High"));
        sb.Append(Kpi(report.MediumCount.ToString(), "Medium"));
        sb.Append(Kpi(report.LowCount.ToString(), "Low"));
        sb.Append("</div>\n");

        // Per-pack sections
        foreach (var pack in report.PackResults)
        {
            sb.Append($"<section class=\"block\">\n<h2>{Esc(pack.PackName)}</h2>\n");

            if (pack.Error is not null)
            {
                sb.Append($"<p style=\"color:#991b1b\">{Esc(pack.Error)}</p>\n");
                sb.Append("</section>\n");
                continue;
            }

            if (pack.QaAudit is { } qa)
            {
                sb.Append("<div class=\"kpi-row\">");
                sb.Append(Kpi($"{qa.Health.AuditScore:0}", "Score"));
                sb.Append(Kpi(qa.Health.TotalFindings.ToString(), "Findings"));
                sb.Append(Kpi(qa.Health.CriticalCount.ToString(), "Critical"));
                sb.Append(Kpi(qa.Health.HighCount.ToString(), "High"));
                sb.Append(Kpi(qa.Health.MediumCount.ToString(), "Medium"));
                sb.Append("</div>\n");

                if (qa.Findings.Count > 0)
                {
                    sb.Append("<h3>Findings</h3>\n");
                    sb.Append(Table(
                        ["Severity", "Category", "Rule", "Title", "Description"],
                        qa.Findings.Select(f => new[]
                        {
                            Badge(f.Severity.ToString()),
                            Esc(f.Category.ToString()),
                            Esc(f.RuleCode),
                            Esc(f.Title),
                            Esc(f.Description)
                        })));
                }

                if (qa.Gaps.Count > 0)
                {
                    sb.Append("<h3>Coverage Gaps</h3>\n");
                    sb.Append(Table(
                        ["Severity", "Area", "Description"],
                        qa.Gaps.Select(g => new[]
                        {
                            Badge(g.Severity.ToString()),
                            Esc(g.GapArea),
                            Esc(g.Description)
                        })));
                }

                if (qa.Recommendations.Count > 0)
                    sb.Append(RecommendationList(qa.Recommendations.Select(r => r.Text)));
            }

            else if (pack.Compliance is { } cc)
            {
                sb.Append("<div class=\"kpi-row\">");
                sb.Append(Kpi($"{cc.Coverage.CompliancePercentage:0.#}%", "Compliance"));
                sb.Append(Kpi(cc.Coverage.TotalItems.ToString(), "Rules"));
                sb.Append(Kpi(cc.Violations.Count.ToString(), "Violations"));
                sb.Append(Kpi(cc.Gaps.Count.ToString(), "Gaps"));
                sb.Append("</div>\n");

                if (cc.Violations.Count > 0)
                {
                    sb.Append("<h3>Violations</h3>\n");
                    sb.Append(Table(
                        ["Severity", "Rule", "Artifact", "Issue"],
                        cc.Violations.Select(v => new[]
                        {
                            Badge(v.Severity.ToString()),
                            Esc($"{v.RuleId} — {v.RuleTitle}"),
                            Esc(v.Artifact.ToString()),
                            Esc(v.Issue)
                        })));
                }

                if (cc.Gaps.Count > 0)
                {
                    sb.Append("<h3>Gaps</h3>\n");
                    sb.Append(Table(
                        ["Severity", "Rule", "Missing In"],
                        cc.Gaps.Select(g => new[]
                        {
                            Badge(g.Severity.ToString()),
                            Esc($"{g.RuleId} — {g.RuleTitle}"),
                            Esc(g.MissingSummary)
                        })));
                }
            }

            else if (pack.Standards is { } st)
            {
                sb.Append("<div class=\"kpi-row\">");
                sb.Append(Kpi($"{st.OverallScore:0.#}", "Score"));
                sb.Append(Kpi(st.Results.Count.ToString(), "Checks"));
                sb.Append(Kpi(st.Results.Count(r => r.Status == CheckStatus.Passed).ToString(), "Passed"));
                sb.Append(Kpi(st.Results.Count(r => r.Status == CheckStatus.Failed).ToString(), "Failed"));
                sb.Append("</div>\n");

                if (st.Summaries.Count > 0)
                {
                    sb.Append("<h3>Per-Standard Summary</h3>\n");
                    sb.Append(Table(
                        ["Standard", "Version", "Score", "Passed", "Warnings", "Failed"],
                        st.Summaries.Select(s => new[]
                        {
                            Esc(s.StandardName),
                            Esc(s.StandardVersion),
                            $"{s.Score:0.#}",
                            s.Passed.ToString(),
                            s.Warnings.ToString(),
                            s.Failed.ToString()
                        })));
                }

                if (st.Results.Count > 0)
                {
                    sb.Append("<h3>Check Results</h3>\n");
                    sb.Append(Table(
                        ["Status", "Severity", "Standard", "Category", "Rule", "Title"],
                        st.Results.Select(r => new[]
                        {
                            Badge(r.Status.ToString()),
                            Badge(r.Severity.ToString()),
                            Esc(r.StandardId),
                            Esc(r.Category),
                            Esc(r.RuleId),
                            Esc(r.Title)
                        })));
                }
            }

            else if (pack.QaReadiness is { } qar)
            {
                sb.Append("<div class=\"kpi-row\">");
                sb.Append(Kpi($"{qar.OverallScore:0.#}", "Score"));
                sb.Append(Kpi(qar.OverallStatus.ToString(), "Status"));
                sb.Append(Kpi(qar.Gates.Count(g => g.IsReady).ToString(), "Gates Passed"));
                sb.Append(Kpi(qar.Gaps.Count.ToString(), "Gaps"));
                sb.Append("</div>\n");

                if (qar.Scores.Count > 0)
                {
                    sb.Append("<h3>Category Scores</h3>\n");
                    sb.Append(Table(
                        ["Category", "Score", "Status"],
                        qar.Scores.Select(s => new[]
                        {
                            Esc(s.Category),
                            $"{s.Score:0.#}",
                            Badge(s.Status.ToString())
                        })));
                }

                if (qar.Gates.Count > 0)
                {
                    sb.Append("<h3>Readiness Gates</h3>\n");
                    sb.Append(Table(
                        ["Gate", "Ready", "Block Reason"],
                        qar.Gates.Select(g => new[]
                        {
                            Esc(g.Name),
                            g.IsReady ? "✓" : "✗",
                            Esc(g.BlockReason ?? "")
                        })));
                }

                if (qar.Recommendations.Count > 0)
                    sb.Append(RecommendationList(qar.Recommendations.Select(r => r.Text)));
            }

            else if (pack.DeliveryReadiness is { } dr)
            {
                sb.Append("<div class=\"kpi-row\">");
                sb.Append(Kpi($"{dr.Health.OverallReadinessScore:0.#}%", "Overall"));
                sb.Append(Kpi($"{dr.Health.DevelopmentScore:0.#}%", "Dev"));
                sb.Append(Kpi($"{dr.Health.TestingScore:0.#}%", "Testing"));
                sb.Append(Kpi($"{dr.Health.ReleaseScore:0.#}%", "Release"));
                sb.Append(Kpi(dr.Blockers.Count.ToString(), "Blockers"));
                sb.Append("</div>\n");

                sb.Append("<div class=\"gate-row\">\n");
                sb.Append(GateCard(dr.DevelopmentDecision.Name, dr.DevelopmentDecision.State.ToString(), dr.DevelopmentDecision.Score));
                sb.Append(GateCard(dr.TestingDecision.Name, dr.TestingDecision.State.ToString(), dr.TestingDecision.Score));
                sb.Append(GateCard(dr.ReleaseDecision.Name, dr.ReleaseDecision.State.ToString(), dr.ReleaseDecision.Score));
                sb.Append("</div>\n");

                if (dr.Blockers.Count > 0)
                {
                    sb.Append("<h3>Blockers</h3>\n");
                    sb.Append(Table(
                        ["Severity", "Category", "Phase", "Title", "Description"],
                        dr.Blockers.Select(b => new[]
                        {
                            Badge(b.Severity.ToString()),
                            Esc(b.Category),
                            Esc(b.Phase ?? "All"),
                            Esc(b.Title),
                            Esc(b.Description)
                        })));
                }

                if (dr.Recommendations.Count > 0)
                    sb.Append(RecommendationList(dr.Recommendations.Select(r => r.Text)));
            }

            else if (pack.DataModel is { } dm)
            {
                sb.Append("<div class=\"kpi-row\">");
                sb.Append(Kpi(dm.EntityCount.ToString(), "Entities"));
                sb.Append(Kpi(dm.ColumnCount.ToString(), "Columns"));
                sb.Append(Kpi(dm.RelationshipCount.ToString(), "Relationships"));
                sb.Append(Kpi(dm.FindingCount.ToString(), "Findings"));
                sb.Append("</div>\n");

                sb.Append(BuildDataModelBody(dm));
            }

            sb.Append("</section>\n");
        }

        return BuildHtml("Quality Review Report", projectName, $"Run: {report.RunAt:yyyy-MM-dd HH:mm} UTC", sb.ToString());
    }

    public string ExportFrontendQualityReview(FrontendQualityReviewReport report, string? projectName)
    {
        var sb = new StringBuilder();
        if (report.TargetEnvironment is { } target)
        {
            sb.Append("<section class=\"block\"><h2>Target Environment</h2><dl>");
            sb.Append($"<dt>Environment name</dt><dd>{Esc(target.Name)}</dd>");
            sb.Append($"<dt>Environment ID</dt><dd>{Esc(target.EnvironmentId)}</dd>");
            sb.Append($"<dt>Target URL</dt><dd>{Esc(target.TargetUrl)}</dd>");
            sb.Append($"<dt>Environment Type</dt><dd>{Esc(target.EnvironmentType)}</dd>");
            sb.Append($"<dt>Review started</dt><dd>{target.StartedAt:u}</dd></dl></section>");
        }
        AppendFrontendDecisionSupport(sb, report);

        // ── Assessment Metadata ────────────────────────────────────────
        sb.Append("<section class=\"block\">\n<h2>Assessment Summary</h2>\n");
        sb.Append("<dl style=\"display:grid;grid-template-columns:120px 1fr;gap:0.5rem;font-size:.9rem;margin-left:1rem\">\n");

        sb.Append($"<dt><strong>Target URL:</strong></dt><dd>{Esc(report.TargetUrl)}</dd>\n");
        if (!string.IsNullOrEmpty(report.FinalUrl) && report.FinalUrl != report.TargetUrl)
            sb.Append($"<dt><strong>Final URL:</strong></dt><dd>{Esc(report.FinalUrl)}</dd>\n");

        sb.Append($"<dt><strong>Generated:</strong></dt><dd>{report.GeneratedAt:yyyy-MM-dd HH:mm:ss} UTC</dd>\n");
        if (report.CompletedAt.HasValue)
            sb.Append($"<dt><strong>Completed:</strong></dt><dd>{report.CompletedAt.Value:yyyy-MM-dd HH:mm:ss} UTC</dd>\n");

        if (report.DurationMs.HasValue)
            sb.Append($"<dt><strong>Duration:</strong></dt><dd>{report.DurationMs:N0} ms</dd>\n");

        // Four dimensions, never one "Full": required coverage alone used to call a review with outstanding manual
        // assessment and missing optional engines complete.
        if (FrontendQualityResultPresentation.Build(report).Completeness is { } completeness)
        {
            sb.Append($"<dt><strong>Execution:</strong></dt><dd>{Esc(completeness.ExecutionLabel)}</dd>\n");
            sb.Append($"<dt><strong>Required coverage:</strong></dt><dd>{Esc(completeness.RequiredCoverageLabel)} ({completeness.RequiredAssessed} of {completeness.RequiredTotal})</dd>\n");
            sb.Append($"<dt><strong>Optional coverage:</strong></dt><dd>{Esc(completeness.OptionalCoverageLabel)} ({completeness.OptionalAssessed} of {completeness.OptionalTotal})</dd>\n");
            sb.Append($"<dt><strong>Manual assessment:</strong></dt><dd>{Esc(completeness.ManualAssessmentLabel)}</dd>\n");
        }

        sb.Append("</dl>\n");

        // ── Engine Status ──────────────────────────────────────────────
        if (report.EngineOutcomes.Count == 0 && (report.AssessedEngines.Count > 0 || report.FailedEngines.Count > 0 || report.SkippedEngines.Count > 0))
        {
            sb.Append("<dl style=\"display:grid;grid-template-columns:120px 1fr;gap:0.5rem;font-size:.85rem;margin-left:1rem\">\n");

            if (report.AssessedEngines.Count > 0)
                sb.Append($"<dt><strong>Assessed:</strong></dt><dd>{Esc(string.Join(", ", report.AssessedEngines))}</dd>\n");

            if (report.FailedEngines.Count > 0)
                sb.Append($"<dt><strong>Failed:</strong></dt><dd>{Esc(string.Join(", ", report.FailedEngines))}</dd>\n");

            if (report.SkippedEngines.Count > 0)
                sb.Append($"<dt><strong>Skipped/Disabled:</strong></dt><dd>{Esc(string.Join(", ", report.SkippedEngines))}</dd>\n");

            sb.Append("</dl>\n");
        }

        sb.Append("</section>\n");

        // ── KPI Scores (with null safety) ──────────────────────────────
        if (report.Wcag is { } wcag)
        {
            sb.Append($"<section class=\"block\"><h2>{Esc(wcag.TargetLabel)} — Accessibility / WCAG</h2>");
            sb.Append($"<p>{Esc(WcagAssessment.Disclaimer)}</p>");
            sb.Append($"<p>Profile: {Esc(wcag.Profile?.ProfileId ?? "Unknown (legacy)")} ? Version: {Esc(wcag.Profile?.VersionLabel ?? "Legacy recorded target")} ? Criteria: {wcag.Profile?.CriterionIds.Count ?? wcag.Results.Select(r => r.Definition.CriterionId).Distinct().Count()} ? Assessment timestamp: {Esc(wcag.AssessedAt?.ToString("u") ?? "Unknown (legacy)")} ? Pages with evidence: {wcag.PagesWithEvidence}</p>");
            sb.Append("<p>Automatic check evidence and manual decisions are reported separately. Missing evidence is not a pass; partial rule coverage does not establish complete criterion coverage.</p>");
            sb.Append(Table(["Page", "Criterion", "Level", "Title", "Automation", "Result", "Findings", "Confidence", "Evidence", "Last tested", "Manual review"],
                wcag.Results.Select(r => new[]
                {
                    Esc(r.Page), Esc(r.Definition.CriterionId), Esc(r.Definition.Level.ToString()), Esc(r.Definition.Title),
                    Esc(r.Definition.AutomationLevel.ToString()), Esc(r.Status.ToString()), r.Findings.ToString(),
                    Esc(r.Confidence?.ToString() ?? "—"), Esc($"{r.EvidenceSource}: {r.AutomatedEvidence} " + string.Join("; ", r.Checks.Select(c =>
                        $"{c.CheckId}: {c.Outcome}; tested {c.Tested}; failed {c.Failed}; uncertain {c.Uncertain}; {string.Join(", ", c.Selectors)}"))),
                    Esc(r.LastTested?.ToString("u") ?? "—"),
                    r.ManualReview is { } m ? Esc($"{(r.ManualReviewStale ? "STALE — " : "")}{m.Result}; {m.ReviewedBy}; {m.ReviewedAt:u}; {m.Comment}; {m.EvidenceNote}") : "No manual review recorded",
                })));
            sb.Append("</section>");
        }
        if (report.AccessibilityReport is { } accessibility)
        {
            sb.Append("<section class=\"block\">\n<h2>Automated Accessibility Checks</h2>\n");
            sb.Append($"<p><strong>Status:</strong> {Esc(accessibility.ExecutionStatus.ToString())}</p>\n");
            sb.Append($"<p><strong>axe-core:</strong> {Esc(accessibility.AxeVersion ?? "Unavailable")} &nbsp; <strong>Browser:</strong> {Esc($"{accessibility.BrowserName} {accessibility.BrowserVersion}".Trim())}</p>\n");
            sb.Append($"<p><strong>Executed tags:</strong> {Esc(string.Join(", ", accessibility.RuleTags ?? []))}</p>\n");
            sb.Append($"<p><strong>Automated violations:</strong> {accessibility.ViolationCount} &nbsp; <strong>Needs manual review:</strong> {accessibility.IncompleteCount}</p>\n");
            sb.Append("<p>Automated tooling cannot verify all WCAG requirements. Manual accessibility testing is still required. Zero automated violations does not establish WCAG conformance.</p>\n");
            foreach (var finding in accessibility.Findings ?? [])
            {
                sb.Append($"<h3>{Esc(finding.RuleId)} — {Esc(finding.Title)}</h3>\n");
                sb.Append($"<p>{Esc(finding.Kind.ToString())}; impact {Esc(finding.Impact ?? "unspecified")}; affected nodes {finding.AffectedNodeCount}.</p>\n");
                if (finding.Selectors.Count > 0) sb.Append($"<p><strong>Selectors:</strong> {Esc(string.Join(", ", finding.Selectors))}</p>\n");
                if (finding.HtmlSnippets.Count > 0) sb.Append($"<p><strong>Bounded evidence:</strong> {Esc(string.Join(" | ", finding.HtmlSnippets))}</p>\n");
            }
            sb.Append("</section>\n");
        }

        if (report.LighthouseReport is { } lighthouse)
        {
            sb.Append("<section class=\"block\">\n<h2>Lighthouse Lab Performance</h2>\n");
            sb.Append($"<p><strong>Status:</strong> {Esc(lighthouse.ExecutionStatus.ToString())} &nbsp; <strong>Measurement:</strong> Synthetic / Lab</p>\n");
            sb.Append($"<p><strong>Lighthouse:</strong> {Esc(lighthouse.LighthouseVersion ?? "Unavailable")} &nbsp; <strong>Node:</strong> {Esc(lighthouse.NodeVersion ?? "Unavailable")} &nbsp; <strong>Chromium:</strong> {Esc(lighthouse.BrowserVersion ?? "Unavailable")}</p>\n");
            sb.Append($"<p><strong>Lighthouse Performance Score:</strong> {Esc(lighthouse.PerformanceScore?.ToString() ?? "Not assessed")}</p>\n");
            foreach (var metric in lighthouse.Metrics ?? [])
                sb.Append($"<p><strong>Lab {Esc(metric.Name)}:</strong> {Esc(metric.ObservedValue?.ToString("0.##") ?? metric.Status.ToString())} {Esc(metric.Unit ?? "")} ({Esc(metric.Status.ToString())}){(metric.Threshold.HasValue ? $"; threshold {metric.Threshold:0.##} — {Esc(metric.ThresholdSource ?? "configured lab threshold")}" : "")}</p>\n");
            sb.Append("<p>Field data is not included. Lighthouse is a synthetic lab measurement; INP and real-user Core Web Vitals require field data.</p>\n</section>\n");
        }

        if (report.PerformanceQualityReport is { } performanceQuality)
            AppendPerformanceQuality(sb, performanceQuality, report.LighthouseReport);

        if (report.PassiveSecurityReport is { } passive)
        {
            sb.Append("<section class=\"block\">\n<h2>Passive Security Assessment</h2>\n");
            var status = passive.ExecutionStatus == PassiveSecurityExecutionStatusDto.Assessed ? "Assessed" : "Engine Error / Not Assessed";
            sb.Append($"<p><strong>Engine status:</strong> {Esc(status)} &nbsp; <strong>Mode:</strong> Passive only &nbsp; <strong>ZAP version:</strong> {Esc(passive.ZapVersion ?? "Unavailable")}</p>\n");
            sb.Append($"<p><strong>Configured scope:</strong> {Esc(SanitizePassive(passive.ScopeSummary))} &nbsp; <strong>Duration:</strong> {Esc(passive.DurationMs.HasValue ? $"{passive.DurationMs.Value} ms" : "Unavailable")}</p>\n");
            if (passive.ExecutionStatus == PassiveSecurityExecutionStatusDto.Assessed)
            {
                sb.Append($"<p><strong>High:</strong> {passive.HighCount} &nbsp; <strong>Medium:</strong> {passive.MediumCount} &nbsp; <strong>Low:</strong> {passive.LowCount} &nbsp; <strong>Informational:</strong> {passive.InformationalCount}</p>\n");
                sb.Append(Table(["Risk", "Confidence", "PluginId", "Alert", "URL", "Instances", "Sanitized evidence", "Solution"],
                    (passive.Findings ?? []).Select(f => new[] { Esc(f.Risk), Esc(f.Confidence), Esc(f.PluginId), Esc(SanitizePassive(f.Name)), Esc(SanitizePassive(f.Url)),
                        f.InstancesCount.ToString(), Esc(SanitizePassive(f.Evidence)), Esc(SanitizePassive(f.Solution)) })));
            }
            else sb.Append($"<p><strong>Engine error:</strong> {Esc(SanitizePassive(passive.EngineError))}. Alert counts were not assessed.</p>\n");
            sb.Append("<p>Passive automated scanning cannot prove that an application is secure and does not replace authenticated or active penetration testing.</p>\n");
            sb.Append("<p>Active scanning, spidering and authenticated penetration testing were not performed.</p>\n</section>\n");
        }

        // Per-category sections
        var categories = Enum.GetValues<FrontendQualityCategory>();
        foreach (var cat in categories)
        {
            var catFindings = report.Findings.Where(f => f.Category == cat).OrderBy(f => f.Severity).ToList();
            if (catFindings.Count == 0) continue;

            sb.Append($"<section class=\"block\">\n<h2>{Esc(CategoryLabel(cat))}</h2>\n");
            sb.Append(Table(
                ["Severity", "Title", "Description", "Recommendation"],
                catFindings.Select(f => new[]
                {
                    Badge(f.Severity.ToString()),
                    Esc(f.Title),
                    Esc(f.Description),
                    Esc(f.Recommendation)
                })));
            sb.Append("</section>\n");
        }

        if (report.Recommendations.Count > 0)
        {
            sb.Append("<section class=\"block\">\n<h2>Recommendations</h2>\n");
            sb.Append(RecommendationList(report.Recommendations));
            sb.Append("</section>\n");
        }

        // The same method limitations as the page, each once; critical/high finding titles are findings, not limitations.
        if (FrontendQualityResultPresentation.MethodLimitations(report) is { Count: > 0 } methodLimitations)
        {
            sb.Append("<section class=\"block\">\n<h2>Assessment Limitations & Scope</h2>\n");
            sb.Append("<ul style=\"margin-left:1.2rem;font-size:.85rem;color:#374151\">\n");
            foreach (var l in methodLimitations)
                sb.Append($"<li>{Esc(l)}</li>\n");
            sb.Append("</ul>\n");
            sb.Append("<p style=\"margin-top:0.5rem;font-size:.8rem;color:#6b7280\"><strong>Note:</strong> This review uses passive static analysis. " +
                      "Automated tooling cannot verify all WCAG requirements. Manual accessibility testing is still required. Lighthouse lab measurements do not include field/real-user Core Web Vitals. Active vulnerability testing is not included.</p>\n");
            sb.Append("</section>\n");
        }

        var subtitle = string.IsNullOrWhiteSpace(report.TargetUrl)
            ? null
            : $"Target: {report.TargetUrl}  Generated: {report.GeneratedAt:yyyy-MM-dd HH:mm} UTC";
        return BuildHtml("Frontend Quality Review Report", projectName, subtitle, sb.ToString());
    }

    public string ExportApiReview(BirkNext.ApiReview.ApiReviewReport report, string? projectName) => ApiReviewExport.Build(report, projectName, Table, Badge, Esc, BuildHtml);

    public string ExportIntegrationQualityReview(IntegrationQualityReport report, string? projectName)
    {
        var sb = new StringBuilder();

        sb.Append("<div class=\"kpi-row\">");
        sb.Append(Kpi(report.OverallScore.ToString(), "Readiness Score"));
        sb.Append(Kpi(report.IntegrationCount.ToString(), "Integrations"));
        sb.Append(Kpi(report.EnabledCount.ToString(), "Enabled"));
        sb.Append(Kpi(report.MissingConfigCount.ToString(), "Missing Config"));
        sb.Append(Kpi(report.IsReadyForDeployment ? "Ready" : "Not Ready", "Deployment"));
        sb.Append("</div>\n");

            // History. Same presenter as the UI; export never recomputes history state.
            sb.Append("<section class=\"block\">\n<h2>History</h2>\n");
            sb.Append($"<p>{Esc(ContractStatePresenter.History(report))}</p>\n");

            var persistenceNote = ContractStatePresenter.HistoryPersistenceNote(report);
            if (!string.IsNullOrWhiteSpace(persistenceNote))
                sb.Append($"<p>{Esc(persistenceNote)}</p>\n");

            sb.Append(Table(
                ["Previous review", "Current review", "Baseline available", "Changes since previous"],
                [[
                    Esc(report.PreviousSnapshotCapturedAt?.UtcDateTime.ToString("yyyy-MM-dd HH:mm") ?? "Not recorded"),
                    Esc(report.GeneratedAt.ToString("yyyy-MM-dd HH:mm")),
                    report.BaselineAvailable ? "Yes" : "No",
                    report.HistoricalChangeCount.ToString()
                ]]));

            if (report.HistoricalChanges.Count > 0)
                sb.Append(Table(
                    ["Integration", "Change", "Previous", "Current"],
                    report.HistoricalChanges.Select(c => new[]
                    {
                        Esc(c.IntegrationName),
                        Esc(ContractStatePresenter.HistoricalChangeLabel(c.Type)),
                        Esc(c.OldValue ?? "Not recorded"),
                        Esc(c.NewValue ?? "Not recorded")
                    })));

            sb.Append("</section>\n");

        if (report.Statuses.Count > 0)
        {
            sb.Append("<section class=\"block\">\n<h2>Configured Integrations</h2>\n");
            sb.Append(Table(
                ["Integration", "Type", "Enabled", "Score", "Health", "Worker", "Missing Fields"],
                report.Statuses.Select(s => new[]
                {
                    Esc(s.Name),
                    Esc(s.Type.ToString()),
                    s.Enabled ? "Yes" : "No",
                    s.Score.ToString(),
                    Esc(StatusLabel(s.HealthReachable)),
                    Esc(StatusLabel(s.WorkerReachable)),
                    Esc(string.Join(", ", s.MissingFields))
                })));
            sb.Append("</section>\n");

            // Contract evidence. Wording comes from the shared presenter, identical to the UI;
            // export never recomputes compatibility or drift state.
            sb.Append("<section class=\"block\">\n<h2>Contract Evidence</h2>\n");
            sb.Append(Table(
                ["Integration", "Producer", "Consumer", "Producer contract", "Consumer contract",
                 "Compatibility", "Compared at", "Previous baseline", "Drift"],
                report.Statuses.Select(s => new[]
                {
                    Esc(s.Name),
                    Esc(s.ProducerService ?? "Not recorded"),
                    Esc(s.ConsumerService ?? "Not recorded"),
                    Esc(s.ProducerContractSource ?? "Not recorded"),
                    Esc(s.ConsumerContractSource ?? "Not recorded"),
                    Esc(ContractStatePresenter.Compatibility(s)),
                    Esc(s.CompatibilityComparedAt?.ToString("yyyy-MM-dd HH:mm") ?? "Not recorded"),
                    Esc(s.PreviousBaselineTimestamp?.ToString("yyyy-MM-dd HH:mm") ?? "Not recorded"),
                    Esc(ContractStatePresenter.Drift(s))
                })));
            sb.Append("</section>\n");

            // Performance. Same presenter as the UI; nothing is recomputed here.
            sb.Append("<section class=\"block\">\n<h2>Performance</h2>\n");
            sb.Append(Table(
                ["Integration", "Evidence", "p50", "p95", "p99", "Errors", "Throughput", "Observation window"],
                report.Statuses.Select(s => new[]
                {
                    Esc(s.Name),
                    Esc(ContractStatePresenter.Performance(s)),
                    Esc(ContractStatePresenter.Metric(s.Performance?.P50DurationMs, "ms")),
                    Esc(ContractStatePresenter.Metric(s.Performance?.P95DurationMs, "ms")),
                    Esc(ContractStatePresenter.Metric(s.Performance?.P99DurationMs, "ms")),
                    Esc(ContractStatePresenter.ErrorRate(s)),
                    Esc(ContractStatePresenter.Metric(s.Performance?.RequestsPerSecond, "req/s")),
                    Esc(ContractStatePresenter.ObservationWindow(s))
                })));

            var performanceChanges = report.Statuses.Where(s => s.PerformanceChanges.Count > 0).ToList();
            if (performanceChanges.Count > 0)
                sb.Append(Table(
                    ["Integration", "Change since previous review"],
                    performanceChanges.SelectMany(s => s.PerformanceChanges.Select(c => new[]
                    {
                        Esc(s.Name), Esc(ContractStatePresenter.PerformanceChangeLabel(c))
                    }))));

            sb.Append("</section>\n");

            var withDifferences = report.Statuses
                .Where(s => s.CompatibilityDifferences.Count > 0 || s.DriftDifferences.Count > 0)
                .ToList();

            if (withDifferences.Count > 0)
            {
                sb.Append("<section class=\"block\">\n<h2>Contract Differences</h2>\n");
                sb.Append(Table(
                    ["Integration", "Kind", "Severity", "Detail"],
                    withDifferences.SelectMany(s =>
                        s.CompatibilityDifferences.Select(d => new[]
                        {
                            Esc(s.Name), "Compatibility",
                            Esc(ContractStatePresenter.SeverityLabel(d.Severity)), Esc(d.Explanation)
                        })
                        .Concat(s.DriftDifferences.Select(d => new[]
                        {
                            Esc(s.Name), "Drift",
                            Esc(ContractStatePresenter.SeverityLabel(d.Severity)), Esc(d.Explanation)
                        })))));
                sb.Append("</section>\n");
            }
        }

        if (report.Findings.Count > 0)
        {
            sb.Append("<section class=\"block\">\n<h2>Findings</h2>\n");
            sb.Append(Table(
                ["Severity", "Integration", "Title", "Description", "Recommendation"],
                report.Findings
                    .OrderBy(f => f.Severity)
                    .ThenBy(f => f.IntegrationName)
                    .Select(f => new[]
                    {
                        Badge(f.Severity.ToString()),
                        Esc(f.IntegrationName),
                        Esc(f.Title),
                        Esc(f.Description),
                        Esc(f.Recommendation)
                    })));
            sb.Append("</section>\n");
        }

        if (report.Recommendations.Count > 0)
        {
            sb.Append("<section class=\"block\">\n<h2>Recommendations</h2>\n");
            sb.Append(RecommendationList(report.Recommendations));
            sb.Append("</section>\n");
        }

        if (report.Limitations.Count > 0)
        {
            sb.Append("<section class=\"block\">\n<h2>Limitations</h2>\n");
            sb.Append("<ul style=\"margin-left:1.2rem;font-size:.85rem;color:#374151\">\n");
            foreach (var l in report.Limitations)
                sb.Append($"<li>{Esc(l)}</li>\n");
            sb.Append("</ul>\n</section>\n");
        }

        var subtitle = $"Environment: {report.EnvironmentName}  Generated: {report.GeneratedAt:yyyy-MM-dd HH:mm} UTC";
        return BuildHtml("Integration Quality Review Report", projectName, subtitle, sb.ToString());
    }

    private static string StatusLabel(bool? value) => value switch
    {
        true => "Reachable",
        false => "Unreachable",
        null => "Not checked"
    };

    private static void AppendFrontendDecisionSupport(StringBuilder sb, FrontendQualityReviewReport report)
    {
        var disposition = report.ReleaseDisposition ?? FrontendQualityReleaseDisposition.ReviewRequired;
        var requiredAll = report.EngineOutcomes.Count(o => o.Requirement == FrontendQualityEngineRequirement.Required);
        var requiredDone = report.EngineOutcomes.Count(o => o.Requirement == FrontendQualityEngineRequirement.Required && o.ExecutionState == FrontendQualityEngineExecutionState.Assessed);
        var dispositionText = disposition switch
        {
            FrontendQualityReleaseDisposition.Blocked when requiredAll > 0 && requiredDone < requiredAll =>
                $"Required engine could not assess the target ({requiredDone} of {requiredAll} required engines completed). See the engine table for the exact access blocker.",
            FrontendQualityReleaseDisposition.Blocked => "Automated review is blocked by one or more configured release-blocking conditions.",
            FrontendQualityReleaseDisposition.NoAutomatedBlockDetected => "No configured automated release block was detected.",
            _ => "Automated evidence requires review before a release decision can be made.",
        };
        var coverage = report.Coverage?.RequiredCoverageState ?? FrontendQualityRequiredCoverageState.NoTrustworthyRequiredAssessment;
        var coverageText = coverage switch
        {
            FrontendQualityRequiredCoverageState.AllRequiredAssessed => "All required engines assessed",
            FrontendQualityRequiredCoverageState.SomeRequiredNotAssessed => "Some required engines not assessed",
            _ => "No trustworthy required assessment",
        };
        var required = report.EngineOutcomes.Where(o => o.Requirement == FrontendQualityEngineRequirement.Required).ToList();

        sb.Append("<section class=\"block\">\n<h2>Release disposition</h2>\n");
        sb.Append($"<p><strong>{Esc(disposition.ToString())}</strong> — {Esc(dispositionText)}</p>\n");
        // The page's count model, not raw list lengths: informational and derived items are neither logical issues nor
        // source findings, and are listed by their own names.
        var view = FrontendQualityResultPresentation.Build(report);
        var actionable = view.ActionableIssues;
        sb.Append($"<p><strong>Critical/high logical issues:</strong> {actionable.Count(i => i.PrimarySeverity is FrontendQualitySeverity.Critical or FrontendQualitySeverity.High)} &nbsp; "
            + $"<strong>Logical issues:</strong> {view.LogicalIssueCount} &nbsp; <strong>Source findings:</strong> {view.SourceFindingCount} &nbsp; "
            + $"<strong>Derived indicators:</strong> {view.DerivedIndicatorCount} &nbsp; <strong>Informational observations:</strong> {view.InformationalIssueCount}</p>\n");
        sb.Append("<p>Logical issues are grouped actionable problems; source findings are individual engine observations and may map to the same logical issue.</p>\n</section>\n");

        if (report.TargetAccess is { } access)
        {
            sb.Append("<section class=\"block\">\n<h2>Target environment access</h2>\n<dl>\n");
            sb.Append($"<dt><strong>Target:</strong></dt><dd>{Esc(access.EnvironmentName)} ({Esc(access.EnvironmentType)})</dd>\n");
            sb.Append($"<dt><strong>URL:</strong></dt><dd>{Esc(access.TargetUrl)}</dd>\n");
            var executed = FrontendQualityReviewScopes.Executed(report);
            sb.Append($"<dt><strong>Authentication configured:</strong></dt><dd>{Esc(FrontendQualityReviewScopes.ConfiguredProviderLabel(access.AuthenticationType))}</dd>\n");
            sb.Append($"<dt><strong>Review scope:</strong></dt><dd>{Esc(FrontendQualityReviewScopes.Label(FrontendQualityReviewScopes.ConfiguredScope(access.RequiresAuthentication)))}</dd>\n");
            sb.Append($"<dt><strong>Access used:</strong></dt><dd>{Esc(FrontendQualityReviewScopes.ExecutedLabel(executed))}{(executed.Partial ? " (partial: the configured scope was not fully assessed)" : "")}</dd>\n");
            sb.Append($"<dt><strong>Public frontend reviewed by:</strong></dt><dd>{Esc(executed.PublicEngines.Count == 0 ? "No engine assessed this path" : string.Join(", ", executed.PublicEngines))}</dd>\n");
            if (access.RequiresAuthentication || executed.AuthenticatedEngines.Count > 0)
                sb.Append($"<dt><strong>Signed-in pages reviewed by:</strong></dt><dd>{Esc(executed.AuthenticatedEngines.Count == 0 ? "No engine assessed this path" : string.Join(", ", executed.AuthenticatedEngines))}</dd>\n");
            sb.Append($"<dt><strong>Testing method:</strong></dt><dd>{Esc(AuthenticatedTestingMethodLabels.Option(access.Method))}</dd>\n");
            sb.Append($"<dt><strong>Access mode:</strong></dt><dd>{Esc(FrontendQualityTargetAccess.ModeLabel(access.Mode))}</dd>\n");
            sb.Append($"<dt><strong>Authenticated context:</strong></dt><dd>{Esc(FrontendQualityTargetAccess.ApiContextLabel(access))}</dd>\n");
            sb.Append($"<dt><strong>Browser DOM:</strong></dt><dd>{Esc(FrontendQualityTargetAccess.BrowserDomLabel(access))}</dd>\n");
            sb.Append($"<dt><strong>Manual verification:</strong></dt><dd>{Esc(FrontendQualityTargetAccess.ManualVerificationLabel(access.ManualVerificationStatus))}</dd>\n");
            sb.Append($"<dt><strong>Automated engine access:</strong></dt><dd>{Esc(FrontendQualityTargetAccess.AutomatedAccessLabel(access))}</dd>\n");
            sb.Append("</dl>\n</section>\n");
        }

        var counts = report.Coverage is { } c && c.RequiredTotal + c.OptionalTotal + c.InactiveCount > 0 ? c : FrontendQualityCoverage.Evaluate(report.EngineOutcomes);
        var requiredAssessed = counts.RequiredAssessed;
        if (report.ActiveEngines is { } activeEngines)
        {
            // Activation is the snapshot captured when the review started, never the settings at export time.
            sb.Append("<section class=\"block\">\n<h2>Active review engines</h2>\n");
            sb.Append($"<p><strong>{activeEngines.ActiveCount} enabled</strong> (required {activeEngines.RequiredActiveCount}, optional {activeEngines.OptionalActiveCount}); {activeEngines.Inactive.Count} not active at review start ({activeEngines.CapturedAtUtc:u}).</p>\n");
            sb.Append(Table(
                ["Engine", "Policy", "Enabled at review start", "Selected", "Active"],
                activeEngines.Engines.OrderBy(e => e.EngineId).Select(e => new[]
                {
                    Esc(e.DisplayName), Esc(e.Policy.ToString()), e.Enabled ? "Yes" : "No", e.Selected ? "Yes" : "No", e.Active ? "Yes" : "No"
                })));
            if (activeEngines.RequiredButDisabled.Count > 0)
                sb.Append($"<p><strong>Configuration inconsistency:</strong> required engine(s) disabled — {Esc(string.Join(", ", activeEngines.RequiredButDisabled.Select(e => e.DisplayName)))}.</p>\n");
            sb.Append("</section>\n");
        }
        sb.Append("<section class=\"block\">\n<h2>Automated coverage</h2>\n");
        var optionalText = counts.OptionalTotal == 0 ? "0 of 0 (no optional engine enabled)" : $"{counts.OptionalAssessed} of {counts.OptionalTotal} active";
        var inactiveText = counts.InactiveCount > 0 ? $" &nbsp; {counts.InactiveCount} engine(s) not active — excluded from coverage" : "";
        sb.Append($"<p><strong>{Esc(coverageText)}</strong></p><p><strong>Required assessed:</strong> {requiredAssessed} of {counts.RequiredTotal} &nbsp; <strong>Optional assessed:</strong> {Esc(optionalText)}{inactiveText}</p>\n");
        if (counts.RequiredTotal > 0 && requiredAssessed < counts.RequiredTotal)
        {
            sb.Append($"<p>{requiredAssessed} of {counts.RequiredTotal} required engines completed.</p>\n<ul>\n");
            foreach (var o in required.Where(o => o.ExecutionState != FrontendQualityEngineExecutionState.Assessed).OrderBy(o => o.EngineId))
                sb.Append($"<li>{Esc(o.DisplayName)}: {Esc(FrontendQualityEngineOutcomePresentation.StateLabel(o))} — {Esc(SanitizePassive(o.SanitizedFailureReason ?? FrontendQualityEngineOutcomePresentation.GetLabel(o.OutcomeReason)))}</li>\n");
            sb.Append("</ul>\n");
        }
        sb.Append(Table(
            ["Engine", "Policy", "Enabled", "Assessment", "Access", "Outcome", "Evidence records", "Findings", "Duration", "Tool / browser", "Reason / required action"],
            report.EngineOutcomes.OrderBy(o => o.EngineId).Select(o => new[]
            {
                Esc(o.DisplayName), Esc(o.Requirement.ToString()), o.Enabled ? "Yes" : "No",
                Esc(FrontendQualityEngineOutcomePresentation.AssessmentLabel(o)),
                Esc(o.AccessLabel ?? (o.AccessKind.HasValue ? FrontendQualityEngineOutcomePresentation.AccessKindLabel(o.AccessKind.Value) : "—")),
                Esc($"{FrontendQualityEngineOutcomePresentation.StateLabel(o)} · {FrontendQualityEngineOutcomePresentation.GetLabel(o.OutcomeReason)}"),
                Esc(FrontendQualityEngineOutcomePresentation.EvidenceLabel(o)),
                Esc(FrontendQualityEngineOutcomePresentation.FindingsLabel(o)),
                o.DurationMs.HasValue ? $"{o.DurationMs.Value} ms" : "—",
                Esc(string.Join(" · ", new[] { o.ToolName, o.ToolVersion, o.BrowserName, o.BrowserVersion }.Where(v => !string.IsNullOrWhiteSpace(v)))),
                Esc(SanitizePassive(string.Join(" ", new[] { o.SanitizedFailureReason, o.RequiredAction is { Length: > 0 } action ? $"Required action: {action}." : null }
                    .Concat(o.ManualTestingObligations).Where(v => !string.IsNullOrWhiteSpace(v)))))
            })));
        sb.Append("</section>\n");

        if (report.AuthenticatedApiSurface is { } surface)
        {
            sb.Append("<section class=\"block\">\n<h2>Authenticated API surface (Local HTTPS Proxy)</h2>\n");
            if (surface.ContextAvailable && surface.Checks.Count > 0)
            {
                sb.Append("<p>Approved read-only requests executed by the backend gateway with the memory-only proxy credential; bodies and non-allow-listed headers are never captured.</p>\n");
                sb.Append(Table(
                    ["Check", "Endpoint", "Mode", "Status", "Latency", "Outcome", "Security headers"],
                    surface.Checks.Select(c => new[]
                    {
                        Esc(c.Label), Esc(c.Url), Esc(c.Mode.ToString()), Esc(c.StatusCode.HasValue ? $"HTTP {c.StatusCode}" : c.Status.ToString()),
                        c.ElapsedMs.HasValue ? $"{c.ElapsedMs:0} ms" : "—", Esc(SanitizePassive(c.Outcome)),
                        Esc(c.SecurityHeaders.Count == 0 ? "—" : string.Join(" · ", c.SecurityHeaders.Select(h => $"{h.Key}: {h.Value}")))
                    })));
            }
            else
            {
                sb.Append($"<p><strong>Not checked.</strong> {Esc(surface.NotExecutedReason)}</p>\n");
            }
            sb.Append("</section>\n");
        }

        sb.Append("<section class=\"block\">\n<h2>Logical issues</h2>\n");
        if (report.LogicalIssues.Count == 0)
            sb.Append("<p>No automated findings were produced. This does not remove manual review obligations or assessment limitations.</p>\n");
        foreach (var issue in report.LogicalIssues)
        {
            sb.Append($"<article><h3>{Esc(issue.CanonicalTitle)}</h3><p><strong>Logical ID:</strong> {Esc(issue.LogicalId)}</p><p><strong>Severity:</strong> {Esc(issue.PrimarySeverity.ToString())} &nbsp; <strong>Category:</strong> {Esc(issue.Category.ToString())}</p>\n");
            sb.Append($"<p><strong>Sources:</strong> {Esc(string.Join(", ", issue.Sources.Select(FrontendQualityDecisionSupportService.SourceLabel)))}</p>\n");
            sb.Append($"<p><strong>Evidence strength:</strong> {Esc(issue.EvidenceStrength.ToString())} &nbsp; <strong>Confidence:</strong> {Esc(issue.Confidence?.ToString() ?? "Not assigned")}</p>\n");
            sb.Append($"<p><strong>Review disposition:</strong> {Esc(issue.ReviewDisposition.ToString())}</p><p><strong>Next step:</strong> {Esc(SanitizePassive(issue.Recommendation))}</p>\n");
            sb.Append(Table(["Source", "Original severity", "Source finding / rule", "Sanitized evidence", "Source recommendation"], issue.FindingInstances.Select(instance => new[]
            {
                Esc(FrontendQualityDecisionSupportService.SourceLabel(instance.EngineId)), Esc(instance.Severity.ToString()),
                Esc($"{instance.SourceFindingId} / {instance.SourceRuleId ?? "—"}"),
                Esc(SanitizePassive(string.Join(" | ", instance.SanitizedEvidence))), Esc(SanitizePassive(instance.Recommendation))
            })));
            sb.Append("</article>\n");
        }
        sb.Append("</section>\n");

        sb.Append("<section class=\"block\">\n<h2>Manual verification required</h2>\n");
        if (report.ManualReviewItems.Count == 0)
            sb.Append("<p>No explicit manual-verification item was generated. Existing assessment limitations still apply.</p>\n");
        else
            sb.Append(Table(["What needs verification", "Why automation cannot determine it", "Source", "Related issue"], report.ManualReviewItems.Select(item => new[]
            {
                Esc(SanitizePassive(item.Title)), Esc(SanitizePassive(item.Reason)), Esc(SanitizePassive(item.Source)), Esc(item.RelatedLogicalId ?? "—")
            })));
        sb.Append("</section>\n");

        sb.Append("<section class=\"block\">\n<h2>Source-specific scores</h2><div class=\"kpi-row\">");
        // Same rule as the page: a score belongs to an engine that looked at the target. A value left on the report
        // from an earlier computation must not be exported next to that engine reporting it did not assess.
        bool Assessed(FrontendQualityEngineId id) => report.EngineOutcomes
            .Any(o => o.EngineId == id && o.ExecutionState == FrontendQualityEngineExecutionState.Assessed);
        string EngineScore(int? value, params FrontendQualityEngineId[] sources) =>
            sources.All(Assessed) ? value?.ToString() ?? "Not Assessed" : "Not Assessed";
        sb.Append(Kpi(EngineScore(report.OverallScore, FrontendQualityEngineId.StaticSecurity, FrontendQualityEngineId.PassivePerformance), "Legacy static review score"));
        sb.Append(Kpi(EngineScore(report.SecurityScore, FrontendQualityEngineId.StaticSecurity), "Static security score"));
        sb.Append(Kpi(EngineScore(report.PerformanceScore, FrontendQualityEngineId.PassivePerformance), "Passive performance score"));
        sb.Append(Kpi(report.LighthouseReport?.PerformanceScore?.ToString() ?? "Not Assessed", "Lighthouse lab performance score"));
        sb.Append("</div><p>The legacy static review score does not represent all enabled engines.</p></section>\n");

        if (report.BrowserRuntimeReport is { } runtime)
        {
            sb.Append("<section class=\"block\">\n<h2>Browser Runtime</h2>\n");
            sb.Append($"<p><strong>Assessment state:</strong> {Esc(runtime.Status.ToString())} &nbsp; <strong>Browser:</strong> {Esc(runtime.BrowserName ?? "Unavailable")} {Esc(runtime.BrowserVersion)} &nbsp; <strong>Duration:</strong> {Esc(runtime.DurationMs.HasValue ? $"{runtime.DurationMs.Value} ms" : "Unavailable")}</p>\n");
            sb.Append($"<p><strong>Console errors:</strong> {runtime.ConsoleErrorCount} &nbsp; <strong>Page errors:</strong> {runtime.PageErrorCount} &nbsp; <strong>Critical resource failures:</strong> {runtime.CriticalResourceFailureCount}</p>\n");
            sb.Append(Table(["Category", "Finding", "Sanitized evidence"], (runtime.Findings ?? []).Select(f => new[] { Esc(f.Category), Esc(SanitizePassive(f.Title)), Esc(SanitizePassive(string.Join(" | ", f.Evidence ?? []))) })));
            sb.Append($"<p><strong>Limitations:</strong> {Esc(SanitizePassive(string.Join(" ", runtime.Limitations ?? [])))}</p></section>\n");
        }
    }

    /// <summary>
    /// BirkNext Performance Quality export: coverage, per-page phase/metrics/status/threshold (with source), findings, API and Blazor
    /// summaries and missing evidence. Only sanitized values (metrics, counts, operation names, query-stripped URLs) — never a token,
    /// cookie, header value, body or query value.
    /// </summary>
    private static void AppendPerformanceQuality(StringBuilder sb, PerformanceQualityReviewResult result, LighthouseResultDto? lighthouse)
    {
        sb.Append("<section class=\"block\">\n<h2>BirkNext Performance Quality</h2>\n");
        sb.Append("<p>Native engine: browser metrics from the Browser Companion in the user's managed Edge (field, PerformanceObserver); API/network metrics from the Local HTTPS proxy. Independent of Lighthouse, Playwright and CDP. Initial load and SPA navigation are reported separately.</p>\n");
        sb.Append($"<p><strong>Assessment:</strong> {Esc(result.Coverage.OverallLabel)} &nbsp; <strong>Pages with evidence:</strong> {result.PagesWithEvidence} &nbsp; <strong>Findings:</strong> {result.Findings.Count()}</p>\n");
        sb.Append(Table(["Evidence category", "Coverage"],
        [
            ["Browser", Cov(result.Coverage.Browser)], ["Runtime", Cov(result.Coverage.Runtime)], ["Resources", Cov(result.Coverage.Resources)],
            ["API / network", Cov(result.Coverage.Api)], ["Blazor WASM", Cov(result.Coverage.Blazor)],
        ]));
        foreach (var reason in result.Coverage.Reasons) sb.Append($"<p><em>Missing evidence:</em> {Esc(reason)}</p>\n");
        if (!result.Assessed)
        {
            sb.Append($"<p><strong>Not assessed</strong> — {Esc(result.CompanionMessage)}</p>\n</section>\n");
            return;
        }
        sb.Append("<h3>Application-wide overview</h3>\n");
        sb.Append(Table(["Page", "Phase", "LCP", "Stabilization", "Transfer", "API calls", "Slow calls", "Long tasks", "Status", "Coverage"],
            PerformanceQualityRules.Overview(result.Pages).Select(r => new[]
            {
                Esc(r.Title), Esc(PerformanceFormat.PhaseLabel(r.Phase)), Esc(MetricCell(r.Lcp)), Esc(MetricCell(r.Stabilization)), Esc(MetricCell(r.Transfer)),
                Esc(MetricCell(r.ApiCalls)), Esc(MetricCell(r.SlowCalls)), Esc(MetricCell(r.LongTasks)), Esc(PerformanceFormat.StatusLabel(r.Status)), Esc(r.Coverage.ToString()),
            })));
        foreach (var page in result.Pages)
        {
            sb.Append($"<h3>{Esc(page.PageTitle)} <small>({Esc(page.PageId)} · {Esc(PerformanceFormat.PhaseLabel(page.ObservationType))} · generation {page.Generation} · {Esc(page.Coverage.OverallLabel)})</small></h3>\n");
            foreach (var note in page.Notes.Concat(page.Coverage.Reasons).Distinct()) sb.Append($"<p><em>{Esc(note)}</em></p>\n");
            sb.Append(Table(["Layer", "Phase", "Metric", "Result", "Status", "Threshold", "Threshold source", "Source", "Note"],
                page.Metrics.Select(m => new[]
                {
                    Esc(m.Layer.ToString()), Esc(PerformanceFormat.PhaseLabel(m.Phase)), Esc(m.Name), Esc(m.Display), Esc(PerformanceFormat.StatusLabel(m.Status)),
                    Esc(m.Threshold?.Display ?? "—"), Esc(m.Threshold?.SourceLabel ?? "—"), Esc(m.Source), Esc(m.Note ?? ""),
                })));
            if (page.Findings.Count > 0)
                sb.Append(Table(["Severity", "Category", "Phase", "Finding", "Observed", "Threshold", "Threshold source", "Evidence", "Confidence", "Recommendation"],
                    page.Findings.Select(f => new[]
                    {
                        Badge(f.Severity.ToString()), Esc(f.Category.ToString()), Esc(PerformanceFormat.PhaseLabel(f.Phase)), $"<strong>{Esc(f.Title)}</strong><br/>{Esc(f.Explanation)}",
                        Esc(f.ObservedValue), Esc(f.Threshold), Esc(f.ThresholdSource is { } s ? PerformanceFormat.SourceLabel(s) : "—"),
                        Esc($"{f.EvidenceSource}: {string.Join("; ", f.Evidence)}"), Esc(f.Confidence.ToString()), Esc(f.Recommendation),
                    })));
            else sb.Append("<p>No performance findings for the assessed layers.</p>\n");
            if (page.Api.Available)
            {
                sb.Append($"<p><strong>API / network (Local HTTPS proxy):</strong> REST {page.Api.RestCalls} · GraphQL {page.Api.GraphQlCalls} · WebSocket {page.Api.WebSocketConnections} · slow {page.Api.SlowCalls} · duplicate operations {page.Api.DuplicateOperations.Count} · errors {page.Api.ErrorResponses} (auth {page.Api.AuthRejected}) · bursts {page.Api.Bursts.Count}{(page.Api.TimingAvailable ? "" : " · counts only (no timing samples)")}</p>\n");
                sb.Append(Table(["Operation", "Calls", "Latency", "p95", "Max", "Status", "Errors", "Flags"],
                    page.Api.Operations.Take(40).Select(o => new[]
                    {
                        Esc(o.Display), o.Count.ToString(), Esc(o.Statistics.Representative is { } r ? $"{PerformanceFormat.Value(r, "ms")} ({o.Statistics.RepresentativeLabel})" : "—"),
                        Esc(o.Statistics.Sufficient ? PerformanceFormat.Value(o.Statistics.P95, "ms") : $"n/a ({o.Statistics.SampleCount} samples)"), Esc(PerformanceFormat.Value(o.Statistics.Max, "ms")),
                        Esc(PerformanceFormat.StatusLabel(o.LatencyStatus)), o.ErrorCount.ToString(), Esc($"{(o.IsDuplicate ? "duplicate " : "")}{(o.IsPollingLike ? "polling-like " : "")}{(o.CacheDirectives is { Length: > 0 } cd ? $"cache: {cd}" : "")}"),
                    })));
                foreach (var p in page.Api.SequentialPatterns) sb.Append($"<p>Observed sequential request pattern: {Esc(string.Join(" → ", p.Operations))} ({Esc(PerformanceFormat.Value(p.TotalMs, "ms"))}).</p>\n");
            }
            else sb.Append("<p><strong>API / network:</strong> Not available — no Local HTTPS proxy traffic recorded for this page.</p>\n");
            if (page.Blazor is { Detected: true } blazor)
                sb.Append($"<p><strong>Blazor WASM:</strong> framework {FormatBytes(blazor.FrameworkBytes)} ({blazor.FrameworkResourceCount} resources, {blazor.CachedFrameworkResourceCount} cached, load {Esc(blazor.LoadKind)}) · WASM {FormatBytes(blazor.WasmBytes)} · assemblies {blazor.AssemblyCount} ({FormatBytes(blazor.AssemblyBytes)}) · runtime {blazor.RuntimeResourceCount} · culture/timezone {blazor.CultureResourceCount} · boot manifest {(blazor.BootManifestFailed ? "failed" : blazor.BootManifestObserved ? "loaded" : "not observed")} · framework load window {Esc(PerformanceFormat.Value(blazor.FrameworkLoadEndMs, "ms"))} · failures {blazor.FrameworkFailures.Count} · repeated {blazor.RepeatedFrameworkDownloads}</p>\n");
            else if (page.Resources is not null) sb.Append("<p><strong>Blazor WASM:</strong> no framework resources observed on this page.</p>\n");
            if (page.Comparison is { } cmp)
                sb.Append(Table(["Metric", $"Current (gen {cmp.CurrentGeneration})", $"Previous (gen {cmp.PreviousGeneration})", "Change"],
                    cmp.Deltas.Select(d => new[] { Esc(d.Metric), Esc(d.Current), Esc(d.Previous), Esc(d.Change) })));
            var rows = PerformanceLighthouseComparison.Build(page, lighthouse);
            if (rows.Count > 0)
            {
                sb.Append(Table(["Metric", "BirkNext (field)", "Lighthouse (lab)", "Difference", "Note"], rows.Select(r => new[] { Esc(r.Metric), Esc(r.BirkNext), Esc(r.Lighthouse), Esc(r.Difference), Esc(r.Note) })));
                sb.Append($"<p>{Esc(PerformanceLighthouseComparison.Methodology)}</p>\n");
            }
        }
        sb.Append("<h3>Thresholds</h3>\n");
        sb.Append(Table(["Threshold", "Value", "Source"], result.Thresholds.Select(t => new[] { Esc(t.Name), Esc(t.Display), Esc(t.SourceLabel) })));
        foreach (var limitation in result.Limitations) sb.Append($"<p><em>{Esc(limitation)}</em></p>\n");
        sb.Append("</section>\n");

        static string Cov(PerformanceCoverageState s) => s switch { PerformanceCoverageState.Complete => "Complete", PerformanceCoverageState.Partial => "Partial", _ => "Not available" };
        static string MetricCell(PerformanceQualityMetric? m) => m is null ? "—" : m.Status == PerformanceMetricStatus.NotMeasured ? "Not measured" : m.Display;
    }

    private static string CategoryLabel(FrontendQualityCategory c) => c switch
    {
        FrontendQualityCategory.Performance   => "Performance",
        FrontendQualityCategory.Security      => "Security",
        FrontendQualityCategory.Accessibility => "Accessibility",
        FrontendQualityCategory.Standards     => "Standards",
        FrontendQualityCategory.BlazorWasm    => "Blazor WASM",
        FrontendQualityCategory.Readiness     => "Readiness",
        _                                     => c.ToString(),
    };

    public string ExportSecurityReview(WasmSecurityReviewReport report, string? projectName)
    {
        var sb = new StringBuilder();
        var h = report.Health;

        sb.Append("<div class=\"kpi-row\">");
        sb.Append(Kpi(h.Score.ToString(), "Score /100"));
        sb.Append(Kpi(h.AssetsScanned.ToString(), "Assets Scanned"));
        sb.Append(Kpi(h.EndpointsDiscovered.ToString(), "Endpoints"));
        sb.Append(Kpi(h.HeadersChecked.ToString(), "Headers Checked"));
        sb.Append(Kpi(h.FindingsCount.ToString(), "Findings"));
        if (h.Critical > 0) sb.Append(Kpi(h.Critical.ToString(), "Critical"));
        if (h.High > 0)     sb.Append(Kpi(h.High.ToString(), "High"));
        sb.Append("</div>\n");

        if (report.Findings.Count > 0)
        {
            sb.Append("<section class=\"block\">\n<h2>Findings</h2>\n");
            sb.Append(Table(
                ["Severity", "Category", "Title", "Description", "Recommendation"],
                report.Findings
                    .OrderBy(f => f.Severity)
                    .Select(f => new[]
                    {
                        Badge(f.Severity.ToString()),
                        Esc(f.Category.ToString()),
                        Esc(f.Title),
                        Esc(f.Description),
                        Esc(f.Recommendation)
                    })));
            sb.Append("</section>\n");
        }

        if (report.Recommendations.Count > 0)
        {
            sb.Append("<section class=\"block\">\n<h2>Recommendations</h2>\n");
            sb.Append(RecommendationList(report.Recommendations));
            sb.Append("</section>\n");
        }

        if (report.Headers.Count > 0)
        {
            sb.Append("<section class=\"block\">\n<h2>Security Headers</h2>\n");
            sb.Append(Table(
                ["Header", "Status", "Recommendation"],
                report.Headers.Select(h2 => new[]
                {
                    Esc(h2.Header),
                    Esc(h2.Status),
                    Esc(h2.Recommendation)
                })));
            sb.Append("</section>\n");
        }

        var subtitle = string.IsNullOrWhiteSpace(report.TargetUrl) ? null : $"Target: {report.TargetUrl}  Scanned: {report.ScannedAt:yyyy-MM-dd HH:mm} UTC";
        return BuildHtml("Security Review Report", projectName, subtitle, sb.ToString());
    }

    public string ExportPerformanceReview(WasmPerformanceReviewReport report, string? projectName)
    {
        var sb = new StringBuilder();
        var h = report.Health;

        sb.Append("<div class=\"kpi-row\">");
        sb.Append(Kpi(h.AssetsDiscovered.ToString(), "Assets"));
        sb.Append(Kpi(FormatBytes(h.TotalTransferBytes), "Total Transfer"));

        if (report.StartupMetrics is { } sm)
            sb.Append(Kpi(sm.StartupRequestCount.ToString(), "Startup Requests"));

        if (report.ReadinessReport is { HasData: true } rdy)
        {
            sb.Append(Kpi(rdy.OverallScore.ToString(), "Readiness Score"));
            sb.Append(Kpi(rdy.OverallState.ToString(), "State"));
            sb.Append(Kpi(rdy.Health.CriticalFindings.ToString(), "Critical"));
            sb.Append(Kpi(rdy.Health.HighFindings.ToString(), "High"));
        }
        sb.Append("</div>\n");

        if (report.ReadinessReport is { HasData: true } rdySec)
        {
            if (rdySec.Categories.Count > 0)
            {
                sb.Append("<section class=\"block\">\n<h2>Category Scores</h2>\n");
                sb.Append(Table(
                    ["Category", "Score", "State", "Findings"],
                    rdySec.Categories.Where(c => c.WasAssessed).Select(c => new[]
                    {
                        Esc(c.CategoryName),
                        c.Score.ToString(),
                        Badge(c.State.ToString()),
                        c.FindingsCount.ToString()
                    })));
                sb.Append("</section>\n");
            }

            if (rdySec.TopRisks.Count > 0)
            {
                sb.Append("<section class=\"block\">\n<h2>Top Risks</h2>\n");
                sb.Append(Table(
                    ["Severity", "Category", "Title", "Description"],
                    rdySec.TopRisks.Select(r => new[]
                    {
                        Badge(r.Severity.ToString()),
                        Esc(r.Category.ToString()),
                        Esc(r.Title),
                        Esc(r.Description)
                    })));
                sb.Append("</section>\n");
            }

            if (rdySec.TopRecommendations.Count > 0)
            {
                sb.Append("<section class=\"block\">\n<h2>Recommendations</h2>\n");
                sb.Append(RecommendationList(rdySec.TopRecommendations.Select(r => $"[{r.Category}] {r.Title} — {r.Description}")));
                sb.Append("</section>\n");
            }
        }

        if (report.Findings.Count > 0)
        {
            sb.Append("<section class=\"block\">\n<h2>All Findings</h2>\n");
            sb.Append(Table(
                ["Severity", "Category", "Title", "Description"],
                report.Findings
                    .OrderBy(f => f.Severity)
                    .Select(f => new[]
                    {
                        Badge(f.Severity.ToString()),
                        Esc(f.Category.ToString()),
                        Esc(f.Title),
                        Esc(f.Description)
                    })));
            sb.Append("</section>\n");
        }

        var subtitle = string.IsNullOrWhiteSpace(report.TargetUrl) ? null : $"Target: {report.TargetUrl}  Reviewed: {report.ReviewedAt:yyyy-MM-dd HH:mm} UTC";
        return BuildHtml("Performance Review Report", projectName, subtitle, sb.ToString());
    }

    public string ExportArtifactTraceability(ArtifactTraceabilityReport report, string? projectName)
    {
        var sb = new StringBuilder();

        // Coverage KPIs
        sb.Append("<div class=\"kpi-row\">");
        if (report.HasConstitution && report.HasSpecification)
            sb.Append(Kpi($"{report.ConstitutionCoverage.CoveragePercentage:0.#}%", "Constitution Coverage"));
        if (report.HasSpecification && report.HasPlan)
            sb.Append(Kpi($"{report.SpecificationCoverage.CoveragePercentage:0.#}%", "Spec Coverage"));
        if (report.HasPlan && report.HasTasks)
            sb.Append(Kpi($"{report.PlanCoverage.CoveragePercentage:0.#}%", "Plan Coverage"));
        if (report.HasTasks)
            sb.Append(Kpi($"{report.TaskCoverage.CoveragePercentage:0.#}%", "Task Coverage"));
        sb.Append(Kpi(report.Health.GapCount.ToString(), "Gaps"));
        sb.Append("</div>\n");

        // Artifacts loaded
        var loaded = new List<string>();
        if (report.HasConstitution)  loaded.Add("Constitution");
        if (report.HasSpecification) loaded.Add("Specification");
        if (report.HasPlan)          loaded.Add("Plan");
        if (report.HasTasks)         loaded.Add("Tasks");
        if (loaded.Count > 0)
            sb.Append($"<p class=\"meta\"><span>Artifacts: {Esc(string.Join(", ", loaded))}</span></p>\n");

        // Chain tables
        if (report.ConstitutionToSpec.Count > 0)
        {
            sb.Append("<section class=\"block\">\n<h2>Constitution → Specification</h2>\n");
            sb.Append(ChainTable(report.ConstitutionToSpec, 100));
            sb.Append("</section>\n");
        }

        if (report.SpecToPlan.Count > 0)
        {
            sb.Append("<section class=\"block\">\n<h2>Specification → Plan</h2>\n");
            sb.Append(ChainTable(report.SpecToPlan, 100));
            sb.Append("</section>\n");
        }

        if (report.PlanToTask.Count > 0)
        {
            sb.Append("<section class=\"block\">\n<h2>Plan → Tasks</h2>\n");
            sb.Append(ChainTable(report.PlanToTask, 100));
            sb.Append("</section>\n");
        }

        if (report.Gaps.Count > 0)
        {
            sb.Append("<section class=\"block\">\n<h2>Traceability Gaps</h2>\n");
            sb.Append(Table(
                ["Artifact", "Item ID", "Title", "Status", "Severity", "Description"],
                report.Gaps.Select(g => new[]
                {
                    Esc(g.GapIn.ToString()),
                    Esc(g.ItemId),
                    Esc(g.ItemTitle),
                    Badge(g.Status.ToString()),
                    Badge(g.Severity.ToString()),
                    Esc(g.Description)
                })));
            sb.Append("</section>\n");
        }

        if (report.Matrix.Count > 0)
        {
            var cap = 200;
            sb.Append("<section class=\"block\">\n<h2>Traceability Matrix</h2>\n");
            if (report.Matrix.Count > cap)
                sb.Append($"<p class=\"meta\"><span>Showing {cap} of {report.Matrix.Count} rows</span></p>\n");
            sb.Append(Table(
                ["Constitution", "Specification", "Plan", "Task", "Status"],
                report.Matrix.Take(cap).Select(r => new[]
                {
                    Esc(r.ConstitutionRuleId + (r.ConstitutionRuleTitle.Length > 0 ? $" — {r.ConstitutionRuleTitle}" : "")),
                    Esc(r.SpecRequirementId ?? ""),
                    Esc(r.PlanItemId ?? ""),
                    Esc(r.TaskId ?? ""),
                    Badge(r.Status.ToString())
                })));
            sb.Append("</section>\n");
        }

        return BuildHtml("Artifact Traceability Report", projectName, null, sb.ToString());
    }

    public string ExportImplementationReview(AlignmentReport report, string? projectName)
    {
        var sb = new StringBuilder();

        sb.Append("<div class=\"kpi-row\">");
        sb.Append(Kpi(report.TotalTasks.ToString(), "Total Tasks"));
        sb.Append(Kpi(report.LinkedTasks.ToString(), "Spec Linked"));
        sb.Append(Kpi(report.TechnicalOnlyTasks.ToString(), "Technical Only"));
        sb.Append(Kpi(report.NeedsReviewTasks.ToString(), "Needs Review"));
        sb.Append(Kpi(report.PossibleDeviations.ToString(), "Deviations"));
        sb.Append(Kpi(report.HighImpactTasks.ToString(), "High Risk"));
        sb.Append(Kpi(report.RegressionCandidates.ToString(), "Regression"));
        sb.Append("</div>\n");

        if (report.Findings.Count > 0)
        {
            sb.Append("<section class=\"block\">\n<h2>Task Findings</h2>\n");
            sb.Append(Table(
                ["Task ID", "Title", "Status", "Risk", "Confidence", "Reason", "Recommended Action"],
                report.Findings.Select(f => new[]
                {
                    Esc(f.TaskId),
                    Esc(f.Title),
                    Badge(f.Status.ToString()),
                    Badge(f.Risk.ToString()),
                    $"{(int)(f.Confidence * 100)}%",
                    Esc(f.Reason),
                    Esc(f.RecommendedAction)
                })));
            sb.Append("</section>\n");
        }

        return BuildHtml("Implementation Review Report", projectName, null, sb.ToString());
    }

    public string ExportDataModel(DataModelDocument document, string? projectName)
    {
        var sb = new StringBuilder();

        sb.Append("<div class=\"kpi-row\">");
        sb.Append(Kpi(document.EntityCount.ToString(), "Entities"));
        sb.Append(Kpi(document.ColumnCount.ToString(), "Columns"));
        sb.Append(Kpi(document.RelationshipCount.ToString(), "Relationships"));
        sb.Append(Kpi(document.IndexCount.ToString(), "Indexes"));
        if (document.FindingCount > 0)
            sb.Append(Kpi(document.FindingCount.ToString(), "Findings"));
        sb.Append("</div>\n");

        sb.Append(BuildDataModelBody(document));

        return BuildHtml(Esc(document.Title), projectName, null, sb.ToString());
    }

    public string ExportDashboardSummary(
        string? projectName,
        ArtifactTraceabilityReport? traceability,
        ConstitutionComplianceReport? compliance,
        QaAuditReport? audit,
        DeliveryReadinessReport? delivery,
        QAReadinessReport? readiness,
        WasmPerformanceReviewReport? performance,
        IntegrationQualityReport? integrationQuality)
    {
        var sb = new StringBuilder();

        // Health KPIs
        sb.Append("<div class=\"kpi-row\">");
        if (traceability is not null)
            sb.Append(Kpi($"{traceability.Health.CoveragePercentage:0.#}%", "Traceability"));
        if (compliance is not null)
            sb.Append(Kpi($"{compliance.Coverage.CompliancePercentage:0.#}%", "Compliance"));
        if (audit is not null)
            sb.Append(Kpi($"{audit.Health.AuditScore:0.#}", "QA Score"));
        if (delivery is not null)
            sb.Append(Kpi($"{delivery.Health.OverallReadinessScore:0.#}%", "Delivery"));
        if (readiness is not null)
            sb.Append(Kpi($"{readiness.OverallScore:0.#}%", "QA Readiness"));
        if (performance?.ReadinessReport is { HasData: true } pr)
            sb.Append(Kpi(pr.OverallScore.ToString(), "Performance"));
        if (integrationQuality is not null)
            sb.Append(Kpi(integrationQuality.OverallScore.ToString(), "Integrations"));
        sb.Append("</div>\n");

        // Governance status
        sb.Append("<section class=\"block\">\n<h2>Governance Status</h2>\n");
        bool hasAny = traceability is not null || compliance is not null || audit is not null || delivery is not null;
        if (hasAny)
        {
            var artifacts = new[]
            {
                ("Constitution",  traceability?.HasConstitution  == true || compliance?.HasConstitution  == true || audit?.HasConstitution  == true || delivery?.HasConstitution  == true),
                ("Specification", traceability?.HasSpecification == true || compliance?.HasSpecification == true || audit?.HasSpecification == true || delivery?.HasSpecification == true),
                ("Plan",          traceability?.HasPlan          == true || compliance?.HasPlan          == true || audit?.HasPlan          == true || delivery?.HasPlan          == true),
                ("Tasks",         traceability?.HasTasks         == true || compliance?.HasTasks         == true || audit?.HasTasks         == true || delivery?.HasTasks         == true),
            };
            sb.Append("<div class=\"kpi-row\">");
            foreach (var (name, loaded) in artifacts)
                sb.Append(Kpi(loaded ? "✓ Loaded" : "— Not loaded", name));
            sb.Append("</div>\n");
        }
        sb.Append("</section>\n");

        // Analyses run
        var ran = new List<string>();
        if (traceability is not null)  ran.Add($"Artifact Traceability — {traceability.Health.CoveragePercentage:0.#}% coverage");
        if (compliance is not null)    ran.Add($"Constitution Compliance — {compliance.Coverage.CompliancePercentage:0.#}% compliant");
        if (audit is not null)         ran.Add($"QA Audit — score {audit.Health.AuditScore:0.#}, {audit.Health.TotalFindings} findings");
        if (delivery is not null)      ran.Add($"Delivery Readiness — {delivery.Health.OverallReadinessScore:0.#}% overall");
        if (readiness is not null)     ran.Add($"QA Readiness — {readiness.OverallScore:0.#}%, {readiness.OverallStatus}");
        if (performance is not null)   ran.Add($"Performance Review — target {Esc(performance.TargetUrl)}");

        if (integrationQuality is not null)
            ran.Add($"Integration Quality Review - score {integrationQuality.OverallScore}, {integrationQuality.EnabledCount}/{integrationQuality.IntegrationCount} enabled integrations");

        if (ran.Count > 0)
        {
            sb.Append("<section class=\"block\">\n<h2>Analyses Run</h2>\n");
            sb.Append("<ul style=\"margin-left:1.2rem;font-size:.85rem;color:#374151\">\n");
            foreach (var item in ran) sb.Append($"<li>{Esc(item)}</li>\n");
            sb.Append("</ul>\n</section>\n");
        }

        // Top Risks
        var risks = new List<(string Title, string Severity, string Category, string? Desc)>();
        if (audit is not null)
            risks.AddRange(audit.Risks.OrderBy(r => r.Severity).Take(5)
                .Select(r => (r.Title, r.Severity.ToString(), r.Category.ToString(), (string?)r.Description)));
        if (delivery is not null)
            risks.AddRange(delivery.Blockers.OrderBy(b => b.Severity).Take(Math.Max(0, 5 - risks.Count))
                .Select(b => (b.Title, b.Severity.ToString(), b.Category, (string?)b.Description)));

        if (risks.Count > 0)
        {
            sb.Append("<section class=\"block\">\n<h2>Top Risks</h2>\n");
            sb.Append(Table(
                ["Severity", "Category", "Title", "Description"],
                risks.Take(5).Select(r => new[]
                {
                    Badge(r.Severity),
                    Esc(r.Category),
                    Esc(r.Title),
                    Esc(r.Desc ?? "")
                })));
            sb.Append("</section>\n");
        }

        // Top Recommendations
        var recs = new List<string>();
        if (audit is not null)
            recs.AddRange(audit.Recommendations.OrderBy(r => r.Priority).Take(5).Select(r => r.Text));
        if (delivery is not null)
            recs.AddRange(delivery.Recommendations.OrderBy(r => r.Priority).Take(Math.Max(0, 5 - recs.Count)).Select(r => r.Text));

        if (recs.Count > 0)
        {
            sb.Append("<section class=\"block\">\n<h2>Top Recommendations</h2>\n");
            sb.Append(RecommendationList(recs));
            sb.Append("</section>\n");
        }

        return BuildHtml("SDD Governance Summary", projectName, null, sb.ToString());
    }

    // ── Private helpers ────────────────────────────────────────────────────────────

    private static string BuildDataModelBody(DataModelDocument dm)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(dm.Overview))
        {
            sb.Append("<section class=\"block\">\n<h2>Overview</h2>\n");
            sb.Append($"<p style=\"font-size:.88rem;line-height:1.55\">{Esc(dm.Overview)}</p>\n");
            sb.Append("</section>\n");
        }

        if (dm.Entities.Count > 0)
        {
            sb.Append("<section class=\"block\">\n<h2>Entities</h2>\n");
            foreach (var entity in dm.Entities)
            {
                sb.Append("<div class=\"entity-block\">\n");
                sb.Append($"<p class=\"entity-name\">{Esc(entity.Name)}<span class=\"entity-type\">{(entity.IsTable ? "Table" : "Entity")}</span></p>\n");
                if (!string.IsNullOrWhiteSpace(entity.Description))
                    sb.Append($"<p style=\"font-size:.82rem;color:#4b5563;margin:.2rem 0 .4rem\">{Esc(entity.Description)}</p>\n");

                if (entity.Columns.Count > 0)
                    sb.Append(Table(
                        ["Column", "Type", "Nullable", "PK", "FK", "Unique", "Description"],
                        entity.Columns.Select(c => new[]
                        {
                            Esc(c.Name),
                            Esc(c.Type ?? ""),
                            c.Nullable.HasValue ? (c.Nullable.Value ? "Yes" : "No") : "",
                            c.IsPrimaryKey ? "✓" : "",
                            c.IsForeignKey ? "✓" : "",
                            c.IsUnique ? "✓" : "",
                            Esc(c.Description ?? "")
                        })));

                sb.Append("</div>\n");
            }
            sb.Append("</section>\n");
        }

        if (dm.Relationships.Count > 0)
        {
            sb.Append("<section class=\"block\">\n<h2>Relationships</h2>\n");
            sb.Append(Table(
                ["Source Entity", "Source Column", "Target Entity", "Target Column", "Type"],
                dm.Relationships.Select(r => new[]
                {
                    Esc(r.SourceEntity), Esc(r.SourceColumn),
                    Esc(r.TargetEntity), Esc(r.TargetColumn),
                    Esc(r.RelationshipType ?? "")
                })));
            sb.Append("</section>\n");
        }

        if (dm.Indexes.Count > 0)
        {
            sb.Append("<section class=\"block\">\n<h2>Indexes</h2>\n");
            sb.Append(Table(
                ["Name", "Entity", "Columns", "Unique"],
                dm.Indexes.Select(i => new[]
                {
                    Esc(i.Name),
                    Esc(i.EntityName),
                    Esc(string.Join(", ", i.Columns)),
                    i.IsUnique ? "✓" : ""
                })));
            sb.Append("</section>\n");
        }

        if (dm.Constraints.Count > 0)
        {
            sb.Append("<section class=\"block\">\n<h2>Constraints</h2>\n");
            sb.Append(Table(
                ["Name", "Entity", "Type", "Definition"],
                dm.Constraints.Select(c => new[]
                {
                    Esc(c.Name),
                    Esc(c.EntityName),
                    Esc(c.ConstraintType),
                    Esc(c.Definition ?? "")
                })));
            sb.Append("</section>\n");
        }

        if (dm.Enums.Count > 0)
        {
            sb.Append("<section class=\"block\">\n<h2>Enums</h2>\n");
            sb.Append(Table(
                ["Name", "Values", "Description"],
                dm.Enums.Select(e => new[]
                {
                    Esc(e.Name),
                    Esc(string.Join(", ", e.Values)),
                    Esc(e.Description ?? "")
                })));
            sb.Append("</section>\n");
        }

        if (dm.Findings.Count > 0)
        {
            sb.Append("<section class=\"block\">\n<h2>Model Findings</h2>\n");
            sb.Append(Table(
                ["Severity", "Category", "Entity", "Description"],
                dm.Findings.Select(f => new[]
                {
                    Badge(f.Severity.ToString()),
                    Esc(f.Category),
                    Esc(f.EntityName ?? ""),
                    Esc(f.Description)
                })));
            sb.Append("</section>\n");
        }

        return sb.ToString();
    }

    private static string ChainTable(IEnumerable<ChainCoverage> chain, int cap)
    {
        var items = chain.ToList();
        var sb = new StringBuilder();
        if (items.Count > cap)
            sb.Append($"<p class=\"meta\"><span>Showing {cap} of {items.Count} items</span></p>\n");
        sb.Append(Table(
            ["Item ID", "Title", "Type", "Links", "Status"],
            items.Take(cap).Select(c => new[]
            {
                Esc(c.ItemId),
                Esc(c.ItemTitle),
                Esc(c.ItemSubType ?? c.ItemType.ToString()),
                c.Links.Count.ToString(),
                Badge(c.Status.ToString())
            })));
        return sb.ToString();
    }

    private static string GateCard(string name, string state, double score)
    {
        var stateClass = state.ToLowerInvariant().Replace(" ", "");
        return $"<div class=\"gate-card\"><div class=\"gate-title\">{Esc(name)}</div><div class=\"gate-state gate-state-{stateClass}\">{Esc(state)}</div><div style=\"font-size:.78rem;color:#6b7280\">{score:0.#}%</div></div>\n";
    }

    private static string RecommendationList(IEnumerable<string> texts)
    {
        var sb = new StringBuilder();
        int i = 0;
        foreach (var t in texts)
        {
            i++;
            sb.Append($"<div class=\"rec-item\"><span class=\"rec-num\">{i}.</span>{Esc(t)}</div>\n");
        }
        return sb.ToString();
    }

    private static string Table(string[] headers, IEnumerable<string[]> rows)
    {
        var sb = new StringBuilder();
        sb.Append("<table>\n<thead><tr>");
        foreach (var h in headers) sb.Append($"<th>{Esc(h)}</th>");
        sb.Append("</tr></thead>\n<tbody>\n");
        foreach (var row in rows)
        {
            sb.Append("<tr>");
            foreach (var cell in row) sb.Append($"<td>{cell}</td>");
            sb.Append("</tr>\n");
        }
        sb.Append("</tbody>\n</table>\n");
        return sb.ToString();
    }

    private static string Kpi(string value, string label) =>
        $"<div class=\"kpi\"><span class=\"kpi-val\">{Esc(value)}</span><span class=\"kpi-label\">{Esc(label)}</span></div>";

    private static string Badge(string sev)
    {
        var cls = sev.ToLowerInvariant().Replace(" ", "").Replace("_", "");
        return $"<span class=\"badge badge-{cls}\">{Esc(sev)}</span>";
    }

    internal static string SanitizePassive(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var safe = System.Text.RegularExpressions.Regex.Replace(value, @"(?i)SECRET-[A-Z0-9-]+", "[REDACTED]");
        safe = System.Text.RegularExpressions.Regex.Replace(safe, @"(?i)((?:access_token|id_token|code|api_key|apikey|secret|authorization|cookie)\s*[=:]\s*)([^&\s,;]+)", "$1[REDACTED]");
        safe = System.Text.RegularExpressions.Regex.Replace(safe, @"(?i)(bearer\s+)[^\s,;]+", "$1[REDACTED]");
        safe = System.Text.RegularExpressions.Regex.Replace(safe, @"//[^/@\s]+@", "//[REDACTED]@");
        return safe.Length <= 512 ? safe : safe[..512] + "…";
    }

    private static string Esc(string? s)
    {
        if (s is null) return string.Empty;
        return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)        return "0 B";
        if (bytes < 1_024)     return $"{bytes} B";
        if (bytes < 1_048_576) return $"{bytes / 1024} KB";
        return $"{bytes / 1_048_576.0:F1} MB";
    }

    private static string Ts() => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm") + " UTC";

    private static string PrintButton() =>
        "<div class=\"print-bar\"><button onclick=\"window.print()\">&#128424; Print / Save as PDF</button></div>\n";

    private static string BuildHtml(string title, string? projectName, string? subtitle, string body)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n");
        sb.Append("<meta charset=\"UTF-8\">\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">\n");
        sb.Append("<title>").Append(Esc(title));
        if (!string.IsNullOrWhiteSpace(projectName)) sb.Append(" — ").Append(Esc(projectName));
        sb.Append("</title>\n<style>\n").Append(Css()).Append("\n</style>\n</head>\n<body>\n");
        sb.Append(PrintButton());
        sb.Append("<div class=\"report-wrap\">\n");
        sb.Append("<h1>").Append(Esc(title)).Append("</h1>\n");
        sb.Append("<p class=\"meta\">");
        if (!string.IsNullOrWhiteSpace(projectName)) sb.Append("<span>Project: ").Append(Esc(projectName)).Append("</span> ");
        sb.Append("<span>Generated: ").Append(Ts()).Append("</span>");
        if (!string.IsNullOrWhiteSpace(subtitle)) sb.Append(" <span>").Append(Esc(subtitle)).Append("</span>");
        sb.Append("</p>\n");
        sb.Append(body);
        sb.Append("</div>\n</body>\n</html>");
        return sb.ToString();
    }

    private static string Css() => """
*,*::before,*::after{box-sizing:border-box;margin:0;padding:0}
body{font-family:system-ui,-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;font-size:14px;color:#1a1a1a;background:#fff;max-width:1100px;margin:0 auto;padding:2rem 1.5rem}
h1{font-size:1.6rem;margin-bottom:.25rem;color:#111}
h2{font-size:1.1rem;margin:1.5rem 0 .5rem;color:#374151;border-bottom:1px solid #e5e7eb;padding-bottom:.25rem}
h3{font-size:.95rem;margin:.8rem 0 .3rem;color:#4b5563}
.meta{font-size:.82rem;color:#6b7280;margin-bottom:1.5rem}
.meta span{margin-right:1.5rem}
.kpi-row{display:flex;gap:.75rem;flex-wrap:wrap;margin:.75rem 0 1.25rem}
.kpi{background:#f9fafb;border:1px solid #e5e7eb;border-radius:6px;padding:.6rem 1.1rem;min-width:110px;text-align:center}
.kpi-val{font-size:1.45rem;font-weight:700;display:block;line-height:1.2}
.kpi-label{font-size:.72rem;color:#6b7280;display:block}
table{width:100%;border-collapse:collapse;font-size:.82rem;margin:.4rem 0 1rem}
thead th{background:#f3f4f6;text-align:left;padding:.38rem .6rem;border-bottom:2px solid #d1d5db;font-weight:600;white-space:nowrap}
td{padding:.32rem .6rem;border-bottom:1px solid #f0f0f0;vertical-align:top;max-width:420px;word-break:break-word}
tr:last-child td{border-bottom:none}
.badge{display:inline-block;padding:.1rem .4rem;border-radius:999px;font-size:.7rem;font-weight:600;white-space:nowrap}
.badge-critical{background:#fee2e2;color:#991b1b}
.badge-high{background:#fef3c7;color:#92400e}
.badge-medium{background:#fef9c3;color:#713f12}
.badge-low{background:#dcfce7;color:#166534}
.badge-info{background:#dbeafe;color:#1e40af}
.badge-warning{background:#fef9c3;color:#713f12}
.badge-error{background:#fee2e2;color:#991b1b}
.badge-covered{background:#dcfce7;color:#166534}
.badge-partial{background:#fef9c3;color:#713f12}
.badge-missing{background:#fee2e2;color:#991b1b}
.badge-passed{background:#dcfce7;color:#166534}
.badge-failed{background:#fee2e2;color:#991b1b}
.badge-notapplicable{background:#f3f4f6;color:#6b7280}
.badge-orphaned{background:#f3e8ff;color:#6b21a8}
.badge-linked{background:#dcfce7;color:#166534}
.badge-technicalonly{background:#dbeafe;color:#1e40af}
.badge-needsreview{background:#fef9c3;color:#713f12}
.badge-possibledeviation{background:#fee2e2;color:#991b1b}
.badge-ready{background:#dcfce7;color:#166534}
.badge-mostlyready{background:#fef9c3;color:#713f12}
.badge-notready,.badge-blocked{background:#fee2e2;color:#991b1b}
.badge-notassessed{background:#f3f4f6;color:#6b7280}
section.block{margin-bottom:2rem}
.rec-item{padding:.4rem 0;border-bottom:1px solid #f0f0f0;font-size:.85rem}
.rec-num{font-weight:700;margin-right:.4rem;color:#1d4ed8}
.gate-row{display:flex;gap:1rem;margin:.75rem 0}
.gate-card{flex:1;background:#f9fafb;border:1px solid #e5e7eb;border-radius:6px;padding:.6rem .9rem}
.gate-title{font-size:.8rem;font-weight:600;color:#374151;margin-bottom:.25rem}
.gate-state{font-size:.95rem;font-weight:700}
.gate-state-ready{color:#166534}
.gate-state-mostlyready{color:#92400e}
.gate-state-notready,.gate-state-blocked{color:#991b1b}
.entity-block{margin:1rem 0 1.75rem}
.entity-name{font-size:.95rem;font-weight:700;color:#111;margin-bottom:.25rem}
.entity-type{font-size:.72rem;color:#6b7280;font-weight:400;margin-left:.4rem}
.report-wrap{padding-bottom:4rem}
.print-bar{position:fixed;bottom:1.5rem;right:1.5rem;display:flex;gap:.5rem;z-index:999}
.print-bar button{background:#1d4ed8;color:#fff;border:none;padding:.5rem 1.1rem;border-radius:6px;cursor:pointer;font-size:.82rem;box-shadow:0 2px 8px rgba(0,0,0,.2)}
.print-bar button:hover{background:#1e40af}
@media print{
.print-bar{display:none}
table{page-break-inside:auto}
tr{page-break-inside:avoid}
thead{display:table-header-group}
h2{page-break-after:avoid}
section.block{page-break-inside:avoid}
.entity-block{page-break-inside:avoid}
a{text-decoration:none;color:inherit}
body{max-width:none;padding:1rem}
}
""";
}
