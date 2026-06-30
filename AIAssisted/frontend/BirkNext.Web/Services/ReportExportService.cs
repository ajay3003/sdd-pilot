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
        WasmPerformanceReviewReport? performance)
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
