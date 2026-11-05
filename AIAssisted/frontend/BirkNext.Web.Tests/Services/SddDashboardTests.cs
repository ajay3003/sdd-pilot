using BirkNext.ProjectActivity;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Services.ProjectEvidence;
using BirkNext.Web.Services.ProjectImport;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// SDD Quality &amp; Readiness: hero, charts, packages and activity are counted from canonical sources (Review availability rows, review
/// navigator capabilities, Project Evidence associations, persisted activity) — no dashboard-only rule. Real M2LB shape: 7 packages imported.
/// </summary>
public sealed class SddDashboardTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 9, 33, 0, TimeSpan.Zero);

    private static ProjectActivityItem Import(string name, string summary, int minutesAgo, string context = "app-m2lb", ProjectActivityStatus status = ProjectActivityStatus.Attention) => new()
    {
        Id = Guid.NewGuid(), ProjectContextId = context, Type = ProjectActivityType.ImportCompleted, Category = ProjectActivityCategory.Import, Status = status,
        Title = $"Imported {name}", Summary = summary, PackageName = name, Route = "project-import", OccurredAt = T0.AddMinutes(-minutesAgo),
    };

    /// <summary>The stored M2LB activity as recorded before the producer fix (every import "Attention" with an application-wide count).</summary>
    private static List<ProjectActivityItem> M2lbActivity() =>
    [
        new()
        {
            Id = Guid.NewGuid(), ProjectContextId = "app-m2lb", Type = ProjectActivityType.PerformanceTlsTrustRepaired, Category = ProjectActivityCategory.Run,
            Status = ProjectActivityStatus.Configured, Title = "Performance TLS trust repaired automatically",
            Summary = "m2lbdev.bufetat.no · missing intermediate validated against a trusted root · server chain still incomplete",
            Route = "admin/system-settings?section=performance-test-engines&focus=container-tls-trust", EnvironmentName = "Development", OccurredAt = T0,
        },
        Import("Terraform-iac-meldingsutveksler", "Infrastructure evidence · no project documents · Source Analysis Partial · no application code · 1 item needs attention", 17),
        Import("Terraform-iac-m2lbm01", "Infrastructure evidence · no project documents · Source Analysis Partial · no application code · 1 item needs attention", 17),
        Import("Terraform-iac-m2lb", "Infrastructure evidence · 17 documents · Source Analysis Partial · 1 item needs attention", 17),
        Import("Terraform-BasicOpsInfra_Meldingsutveksler", "Infrastructure evidence · no project documents · Source Analysis Partial · no application code · 1 item needs attention", 18),
        Import("Terraform-BasicOpsInfra_M2LBM01", "Infrastructure evidence · no project documents · Source Analysis Partial · no application code · 1 item needs attention", 18),
        Import("M2LB", "Primary application source · 228 documents · Source Analysis Partial · 1 item needs attention", 18),
        Import("M2LB.Common", "Supporting/shared source · 4 documents · Source Analysis Partial · 2 items need attention", 21),
    ];

    [Fact]
    public void Consecutive_imports_of_one_role_are_grouped_and_no_import_carries_an_attention_badge()
    {
        var rows = SddDashboard.Timeline(M2lbActivity(), T0);

        rows.Select(r => r.Title).Should().Equal("Local TLS trust repair applied", "Imported 5 infrastructure packages", "Imported M2LB", "Imported M2LB.Common");
        rows[1].Members.Select(m => m.Title).Should().HaveCount(5).And.Contain("Imported Terraform-iac-m2lb");
        rows.Where(r => r.Kind == SddActivityKind.Import).Should().OnlyContain(r => r.Badge == "Imported" && r.BadgeTone == "import");
        rows.SelectMany(r => r.Members.Append(r)).Should().NotContain(r => r.Badge == "Attention" || (r.Summary ?? "").Contains("need"));
    }

    [Fact]
    public void Import_lines_drop_what_the_role_never_carries_and_keep_the_package_own_facts()
    {
        SddDashboard.ImportSummary("Infrastructure evidence · no project documents · Source Analysis Partial · no application code · 1 item needs attention")
            .Should().Be("Infrastructure evidence · Source Analysis Partial");
        SddDashboard.ImportSummary("Primary application source · 228 documents · Source Analysis Partial · 1 item needs attention")
            .Should().Be("Primary application source · 228 documents · Source Analysis Partial");
        SddDashboard.ImportSummary("Primary application source · no project documents · Source Analysis Ready")
            .Should().Be("Primary application source · No SDD project documents detected · Source Analysis Ready", "an application package without SDD documents is a real fact");
    }

    [Fact]
    public void The_TLS_repair_is_a_system_event_that_says_the_server_chain_is_still_incomplete()
    {
        var tls = SddDashboard.Timeline(M2lbActivity(), T0)[0];
        (tls.Kind, tls.Icon, tls.Badge, tls.BadgeTone, tls.LinkLabel).Should().Be((SddActivityKind.System, "⚙", "Locally repaired", "system", "View TLS trust settings"));
        tls.Summary.Should().StartWith("m2lbdev.bufetat.no").And.Contain("trusted locally").And.Contain("remains incomplete");
    }

    [Fact]
    public void Grouping_is_safe_other_roles_applications_and_time_gaps_stay_separate_and_the_timeline_is_limited()
    {
        var items = new List<ProjectActivityItem>
        {
            Import("A", "Infrastructure evidence · Source Analysis Ready", 0),
            Import("B", "Infrastructure evidence · Source Analysis Ready", 1, context: "app-other"),
            Import("C", "Infrastructure evidence · Source Analysis Ready", 2),
            Import("D", "Infrastructure evidence · Source Analysis Ready", 40),
            Import("E", "Contracts/schemas · Source Analysis Ready", 41),
            Import("F", "Contracts/schemas · Source Analysis Ready", 42),
            new() { Id = Guid.NewGuid(), ProjectContextId = "app-m2lb", Type = ProjectActivityType.DocumentQualityReviewCompleted, Category = ProjectActivityCategory.Review,
                Status = ProjectActivityStatus.Completed, Title = "Document Quality Review completed", OccurredAt = T0.AddMinutes(-50) },
            new() { Id = Guid.NewGuid(), ProjectContextId = "app-m2lb", Type = ProjectActivityType.PerformanceRunFailed, Category = ProjectActivityCategory.Run,
                Status = ProjectActivityStatus.Failed, Title = "Performance run failed", OccurredAt = T0.AddMinutes(-60) },
        };

        var all = SddDashboard.AllRows(items, T0);
        all.Select(r => r.IsGroup ? $"group:{r.Members.Count}" : r.Title).Should().Equal(
            "Imported A", "Imported B", "Imported C", "Imported D", "group:2", "Document Quality Review completed", "Performance run failed");
        all[4].Title.Should().Be("Imported 2 contract packages");
        all[5].Kind.Should().Be(SddActivityKind.ReviewDone);
        all[6].Should().Match<SddActivityRow>(r => r.Kind == SddActivityKind.Failed && r.Badge == "Failed");
        SddDashboard.Timeline(items, T0).Should().HaveCount(SddDashboard.ActivityLimit);
    }

    private static ReviewCapability Capability(string id, ReviewReadiness readiness, ReviewDecisionState decision,
        ReviewApplicabilityState applicability = ReviewApplicabilityState.Applicable) =>
        new(id, id, ReviewSection.Runtime, applicability, readiness, ReviewRequiredness.Recommended, decision, $"/{id}", [], "");

    [Fact]
    public void Review_execution_counts_each_review_once_and_excludes_not_applicable()
    {
        var chart = SddDashboard.ReviewExecution(
        [
            Capability("dqr", ReviewReadiness.Ready, ReviewDecisionState.Reviewed),
            Capability("trace", ReviewReadiness.Ready, ReviewDecisionState.NotReviewed),
            Capability("src", ReviewReadiness.Ready, ReviewDecisionState.NoDecisionTracked),
            Capability("spec", ReviewReadiness.NeedsSetup, ReviewDecisionState.EvidenceChanged),
            Capability("fqr", ReviewReadiness.NeedsSetup, ReviewDecisionState.NoDecisionTracked),
            Capability("aqr", ReviewReadiness.WaitingForEvidence, ReviewDecisionState.NoDecisionTracked),
            Capability("iqr", ReviewReadiness.NotApplicable, ReviewDecisionState.NoDecisionTracked, ReviewApplicabilityState.NotApplicable),
        ]);

        chart.Slices.ToDictionary(s => s.Key, s => s.Count).Should().Equal(new Dictionary<string, int> { ["completed"] = 1, ["needs-review"] = 1, ["ready"] = 2, ["blocked"] = 2 });
        chart.Total.Should().Be(6);
        chart.ExcludedNote.Should().Be("1 not applicable, not counted");
    }

    private static DashboardReadinessRow Row(string key, DashboardStatus status) => new(key, key, status, status.ToString(), "");

    [Fact]
    public void Evidence_readiness_and_the_hero_count_the_review_availability_rows()
    {
        IReadOnlyList<DashboardReadinessRow> rows =
        [
            Row("Specification", DashboardStatus.Available), Row("Plan", DashboardStatus.Available), Row("Tasks", DashboardStatus.Partial),
            Row("source", DashboardStatus.NeedsSetup), Row("runtime", DashboardStatus.NotAssessed), Row("contracts", DashboardStatus.NotApplicable),
            Row("dqr", DashboardStatus.NotAssessed),
        ];
        var evidence = SddDashboard.EvidenceReadiness(rows);
        evidence.Slices.Select(s => s.Count).Should().Equal(2, 1, 1, 1);
        evidence.ExcludedNote.Should().Contain("1 not applicable");

        var reviews = SddDashboard.ReviewExecution([Capability("dqr", ReviewReadiness.Ready, ReviewDecisionState.NotReviewed)]);
        var blocker = new DashboardAttentionItem("source-binding", "Source binding not established", null, "Review", "/x");
        var withBlocker = SddDashboard.Hero(rows, [blocker, blocker with { Key = "note", Informational = true }], reviews, [], T0);
        (withBlocker.Readiness, withBlocker.Blockers, withBlocker.AvailableReviews, withBlocker.LastAssessment)
            .Should().Be(("Needs attention", 1, 1, "No review has run yet"));
        SddDashboard.Hero(rows, [], reviews, [], T0).Readiness.Should().Be("Partial", "partial evidence is not attention");
        SddDashboard.Hero([Row("Specification", DashboardStatus.Available), Row("contracts", DashboardStatus.NotApplicable)], [], reviews, [], T0).Readiness.Should().Be("Ready");
        SddDashboard.Hero([], [], reviews, [], T0).Readiness.Should().Be("Not assessed");
    }

    private static ProjectEvidencePackage Package(string name, int documents, params ProjectEvidenceCapability[] capabilities) => new(
        $"import-{name}", name, $"{name}.zip", "sha", T0, null, [.. capabilities.Select(c => new PackageCapability(c, PackageCapabilityStatus.Ready, ""))],
        new Dictionary<WorkspaceArtifactType, int>(), documents, [], [], [], null);

    [Fact]
    public void Packages_show_every_capability_role_and_an_uncertain_package_is_resolved_never_reimported()
    {
        var associations = new List<PackageAssociation>
        {
            new(Package("Terraform-iac-meldingsutveksler", 0, ProjectEvidenceCapability.Infrastructure, ProjectEvidenceCapability.ContractSchema),
                EvidenceAssociationState.Associated, PackageEvidenceRole.Infrastructure, "Infrastructure evidence of M2LB, associated automatically."),
            new(Package("M2LB", 228, ProjectEvidenceCapability.ApplicationSource, ProjectEvidenceCapability.Documentation),
                EvidenceAssociationState.Associated, PackageEvidenceRole.PrimaryApplicationSource, "The application source of M2LB."),
            new(Package("MU", 0, ProjectEvidenceCapability.ApplicationSource), EvidenceAssociationState.NeedsReview, PackageEvidenceRole.RelatedApplicationSource, "Confirm its role."),
        };

        var rows = SddDashboard.Packages(associations);
        rows.Select(r => r.Name).Should().Equal("M2LB", "MU", "Terraform-iac-meldingsutveksler");
        rows[0].Should().Match<SddPackageRow>(r => r.Documents == "228 SDD documents" && r.Roles.Contains("SDD documents") && r.Association == "Associated with application");
        rows[2].Roles.Should().Equal("Infrastructure evidence", "Contracts/schemas");
        rows[2].Documents.Should().Be("SDD documents: not applicable");
        rows[1].Should().Match<SddPackageRow>(r => r.NeedsResolution && r.Documents == "No SDD project documents detected");

        var attention = SddDashboard.AssociationAttention(associations)!;
        attention.ActionLabel.Should().Be("Resolve package association");
        attention.Text.Should().Contain("1 imported package needs a decision");
        (attention.Text + attention.Detail).Should().NotContainAny("Import project", "Re-import", "import it");
        SddDashboard.AssociationAttention(associations.Take(2).ToList()).Should().BeNull();
    }

    [Fact]
    public void An_import_records_only_its_own_facts_and_is_attention_only_for_its_own_analysis_problem()
    {
        var infra = new ImportActivitySummary("app", "M2LB", "Terraform-iac-m2lbm01", PackageEvidenceRole.Infrastructure, 0, "Partial · no application code", Attention: 1);
        infra.Line.Should().Be("Infrastructure evidence · Source Analysis Partial");
        var record = new SddProjectImportRecord { ImportId = "import-1" };
        infra.Request(record, T0).Status.Should().Be(ProjectActivityStatus.Imported, "application-wide setup items are not the package's problem");

        var primary = new ImportActivitySummary("app", "M2LB", "M2LB", PackageEvidenceRole.PrimaryApplicationSource, 228, "Partial", Attention: 1);
        primary.Line.Should().Be("Primary application source · 228 SDD documents · Source Analysis Partial");
        new ImportActivitySummary("app", "M2LB", "M2LB", PackageEvidenceRole.PrimaryApplicationSource, 0, "Not created", 0).Request(record, T0).Status
            .Should().Be(ProjectActivityStatus.Attention);
    }
}
