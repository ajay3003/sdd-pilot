using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Documentation Quality on the one Documentation Drift engine. Synthetic control modules with independently stated expectations (A exact match,
/// B reworded descriptions, C missing implementation field, D changed type, E missing generated, F missing authored, G additive authored scopes,
/// H superseded statement (explicit change) and an unresolved conflict, I ambiguous pairing, J moved document, L unrelated project, M unsupported
/// area). Bulk comparison is idempotent; findings have stable ids; reviews are append-only and reopen on new inputs; the report says
/// "Not assessed" — never "0 drift" — before any comparison.
/// </summary>
public sealed class DocumentationQualityTests : BunitContext
{
    private readonly WorkspaceArtifactRepository _repository = new();
    private static readonly DataModelAnalysisService Parser = new();
    private int _revision;

    public DocumentationQualityTests()
    {
        Services.AddSingleton(_repository);
        Services.AddSingleton<IWorkspaceArtifactRepository>(_repository);
        Services.AddSingleton(Mock.Of<IWorkspaceAutoSaveService>(s => s.SaveNowAsync() == Task.FromResult(true)));
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static string Table(string entity, params (string Field, string Type, string Nullable, string Description)[] fields) =>
        $"### {entity}\n\n| Field | Type | Nullable | Description |\n|---|---|---|---|\n" + string.Concat(fields.Select(f => $"| {f.Field} | {f.Type} | {f.Nullable} | {f.Description} |\n")) + "\n";
    private static string Model(params string[] tables) => "# Data Model\n\n## Entities\n\n" + string.Concat(tables);

    private void Import(string importId, string project = "proj", DateTimeOffset? at = null)
    {
        var state = _repository.SddLifecycle;
        state.ProjectImports.Add(new SddProjectImportRecord { ImportId = importId, ProjectName = project, ProjectNameBasis = "ArchiveRoot", ArchiveFileName = project + ".zip", ImportedAt = at ?? DateTimeOffset.UnixEpoch.AddDays(state.ProjectImports.Count), SourceSnapshotId = Guid.NewGuid() });
        state.CurrentProjectImportId = importId;
    }

    private void Authored(string importId, string path, string text) =>
        _repository.SddLifecycle.Revisions.Add(new SddArtifactRevision
        {
            RevisionId = Guid.NewGuid(), Role = "DataModel", FileName = path, SourceReference = path, Content = text, Fingerprint = "fp-" + Hash(text),
            Revision = ++_revision, WorkspaceScope = "import:" + importId, ProjectImportId = importId,
        });

    private static string Hash(string s) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s)))[..12];

    private void Generated(string importId, string module, string text, string file = "data-model.md", string kind = "DataModel")
    {
        var state = _repository.SddLifecycle;
        var fingerprint = "gen-" + Hash(text);
        var set = state.GeneratedDocumentationSets.FirstOrDefault(s => s.ProjectImportId == importId);
        if (set is null) state.GeneratedDocumentationSets.Add(set = new SddGeneratedDocumentationSet { SetId = "set-" + importId, ProjectImportId = importId, State = "Present" });
        set.Artifacts.Add(new SddGeneratedDocumentArtifact { ModuleId = "module:" + module.ToLowerInvariant(), ModuleRoot = module, RelativePath = $"{module}/autodoc/{file}", Kind = kind, Fingerprint = fingerprint, Generator = "maintain-docs", ContentCaptured = true });
        state.GeneratedDocumentContents[fingerprint] = text;
    }

    private static readonly string Person = Table("Person", ("Id", "Guid", "No", "Key"), ("Navn", "string", "No", "Name"));

    /// <summary>The synthetic control project (one import).</summary>
    private void ControlProject(string importId = "i1")
    {
        Import(importId);
        // A exact match.
        Authored(importId, "Exact/specs/001-exact/data-model.md", Model(Person)); Generated(importId, "Exact", Model(Person));
        // B reworded descriptions only.
        Authored(importId, "Reworded/specs/001-r/data-model.md", Model(Table("Person", ("Id", "Guid", "No", "The identifier"), ("Navn", "string", "No", "Full name of the person"))));
        Generated(importId, "Reworded", Model(Table("Person", ("Id", "Guid", "No", "Key"), ("Navn", "string", "No", "Name"))));
        // C authored field missing from the implementation-derived documentation.
        Authored(importId, "MissingField/specs/001-m/data-model.md", Model(Table("Person", ("Id", "Guid", "No", "Key"), ("Email", "string", "Yes", "Mail"))));
        Generated(importId, "MissingField", Model(Table("Person", ("Id", "Guid", "No", "Key"))));
        // D changed type.
        Authored(importId, "TypeChange/specs/001-t/data-model.md", Model(Table("Person", ("Id", "Guid", "No", "Key"), ("Age", "int", "No", "Age"))));
        Generated(importId, "TypeChange", Model(Table("Person", ("Id", "Guid", "No", "Key"), ("Age", "string", "No", "Age"))));
        // E no generated · F no authored.
        Authored(importId, "NoGen/specs/001-n/data-model.md", Model(Person));
        Generated(importId, "NoAuth", Model(Person));
        // G additive authored scopes (two packs, different entities).
        Authored(importId, "Additive/specs/001-a/data-model.md", Model(Person));
        Authored(importId, "Additive/specs/002-b/data-model.md", Model(Table("Skole", ("Id", "Guid", "No", "Key"))));
        Generated(importId, "Additive", Model(Person, Table("Skole", ("Id", "Guid", "No", "Key"))));
        // H superseded statement (explicit change in the later pack) and an unresolved conflict.
        Authored(importId, "Superseded/specs/001-a/data-model.md", Model(Table("Person", ("Id", "Guid", "No", "Key"), ("Status", "string", "No", "State"))));
        Authored(importId, "Superseded/specs/002-b/data-model.md", Model(Table("Person", ("Id", "Guid", "No", "Key"), ("Status", "int", "No", "Changed from string to int"))));
        Generated(importId, "Superseded", Model(Table("Person", ("Id", "Guid", "No", "Key"), ("Status", "int", "No", "State"))));
        Authored(importId, "Conflict/specs/001-a/data-model.md", Model(Table("Person", ("Id", "Guid", "No", "Key"), ("Kode", "string", "No", "Code"))));
        Authored(importId, "Conflict/specs/002-b/data-model.md", Model(Table("Person", ("Id", "Guid", "No", "Key"), ("Kode", "int", "No", "Code"))));
        Generated(importId, "Conflict", Model(Table("Person", ("Id", "Guid", "No", "Key"), ("Kode", "int", "No", "Code"))));
        // I ambiguous pairing (two generated data models in one module).
        Authored(importId, "Ambiguous/specs/001-a/data-model.md", Model(Person));
        Generated(importId, "Ambiguous", Model(Person)); Generated(importId, "Ambiguous", Model(Person, Table("Extra", ("Id", "Guid", "No", "Key"))), "data-model-2.md");
        // M an area without a structured extractor.
        Generated(importId, "Exact", "# Architecture\n\nComponents.", "architecture.md", "Architecture");
    }

    private IReadOnlyList<DriftDocument> Catalog => DocumentationDrift.Catalog(_repository.SddLifecycle);
    private IReadOnlyList<QualityArea> Areas(string importId = "i1") => DocumentationQuality.Areas(Catalog, _repository.SddLifecycle.DocumentationDriftComparisons, importId);
    private IReadOnlyList<BulkComparisonRow> Bulk(BulkScope scope = BulkScope.AllEligible, IReadOnlySet<string>? selected = null) =>
        DocumentationQuality.RunBulk(_repository.SddLifecycle, Areas(), scope, selected, Parser, null, DateTimeOffset.UtcNow);
    private IReadOnlyList<QualityFinding> Findings() => DocumentationQuality.Findings(_repository.SddLifecycle, Areas());

    [Fact]
    public void Areas_separate_comparable_scopes_from_missing_ambiguous_and_unsupported_evidence()
    {
        ControlProject();
        var areas = Areas().ToDictionary(a => $"{a.Module}/{a.Concern}", a => a.Status);
        areas.Should().Contain(new Dictionary<string, QualityAreaStatus>
        {
            ["Exact/DataModel"] = QualityAreaStatus.NotAssessed, ["Reworded/DataModel"] = QualityAreaStatus.NotAssessed, ["MissingField/DataModel"] = QualityAreaStatus.NotAssessed,
            ["TypeChange/DataModel"] = QualityAreaStatus.NotAssessed, ["Additive/DataModel"] = QualityAreaStatus.NotAssessed, ["Superseded/DataModel"] = QualityAreaStatus.NotAssessed,
            ["Conflict/DataModel"] = QualityAreaStatus.NotAssessed, ["NoGen/DataModel"] = QualityAreaStatus.MissingGenerated, ["NoAuth/DataModel"] = QualityAreaStatus.MissingAuthored,
            ["Ambiguous/DataModel"] = QualityAreaStatus.Ambiguous, ["Exact/Architecture"] = QualityAreaStatus.NotSupported,
        });
        var report = DocumentationQuality.Report(null, Areas(), [], DocumentationQuality.Summary(Catalog, "i1", Areas(), []), [], DateTimeOffset.UtcNow);
        report.Should().Contain("Drift: **Not assessed**").And.NotContain("0 confirmed");
    }

    [Fact]
    public void Bulk_comparison_compares_every_eligible_area_once_and_classifies_findings_as_the_oracle_states()
    {
        ControlProject();
        var rows = Bulk();
        rows.Where(r => r.Outcome == "Compared").Select(r => r.Module).Should().BeEquivalentTo(["Exact", "Reworded", "MissingField", "TypeChange", "Additive", "Superseded", "Conflict"]);
        rows.Where(r => r.Outcome == "Skipped").Select(r => r.Module).Should().BeEquivalentTo(["NoGen", "NoAuth", "Ambiguous", "Exact"], "missing, ambiguous and unsupported areas are never compared");
        var saved = _repository.SddLifecycle.DocumentationDriftComparisons.Count;

        var again = Bulk();
        again.Where(r => r.Outcome == "Compared").Should().BeEmpty("unchanged inputs are never compared or saved again");
        again.Count(r => r.Outcome == "Unchanged").Should().Be(7);
        _repository.SddLifecycle.DocumentationDriftComparisons.Should().HaveCount(saved);

        var open = Findings().Where(f => f.IsDifference).Select(f => (f.Module, f.Concept, f.Classification, f.Expected, f.Actual)).ToList();
        open.Should().BeEquivalentTo(new[]
        {
            ("MissingField", "Person.Email", FindingClassification.PotentialDrift, (string?)"Field", (string?)null),
            ("TypeChange", "Person.Age", FindingClassification.PotentialDrift, (string?)"int", (string?)"string"),
            ("Conflict", "Person.Kode", FindingClassification.Conflict, (string?)null, (string?)null),
        }, "A, B (reworded descriptions), G (additive) and H (explicit change) are consistent; only C, D and the unresolved conflict are findings");
        Findings().Single(f => f.Concept == "Person.Age").Should().Match<QualityFinding>(f => f.Severity == FindingSeverity.Medium && f.AuthoredReferences.Any(r => r.Contains("TypeChange/specs/001-t/data-model.md"))
            && f.GeneratedReference!.Contains("TypeChange/autodoc/data-model.md") && f.SpecificationPacks.Contains("001-t"));
        Findings().Single(f => f.Concept == "Person.Kode").Severity.Should().Be(FindingSeverity.NeedsReview);
    }

    [Fact]
    public void Reviews_are_append_only_keep_history_and_reopen_when_the_inputs_change()
    {
        ControlProject();
        Bulk();
        var finding = Findings().Single(f => f.Concept == "Person.Age");
        var id = finding.Id;
        DocumentationQuality.Review(_repository.SddLifecycle, finding, DriftReviewDecision.ConfirmDifference, "Type really differs", "tester@example.test", DateTimeOffset.UtcNow);
        var reviewed = Findings().Single(f => f.Id == id);
        reviewed.Should().Match<QualityFinding>(f => f.ReviewState == "ConfirmDifference" && f.Closed && f.History.Count == 1);
        DocumentationQuality.Summary(Catalog, "i1", Areas(), Findings()).ConfirmedDifferences.Should().Be(1);

        // A new import of the same project with changed generated documentation: same finding id, reopened, history kept.
        Import("i2");
        Authored("i2", "TypeChange/specs/001-t/data-model.md", Model(Table("Person", ("Id", "Guid", "No", "Key"), ("Age", "int", "No", "Age"))));
        Generated("i2", "TypeChange", Model(Table("Person", ("Id", "Guid", "No", "Key"), ("Age", "long", "No", "Age"))));
        DocumentationQuality.RunBulk(_repository.SddLifecycle, Areas("i2"), BulkScope.AllEligible, null, Parser, null, DateTimeOffset.UtcNow);
        var reopened = DocumentationQuality.Findings(_repository.SddLifecycle, Areas("i2")).Single(f => f.Concept == "Person.Age");
        reopened.Id.Should().Be(id, "same project lineage, module, area, concept and category");
        reopened.Should().Match<QualityFinding>(f => f.ReviewState == "Reopened" && f.Review == null && f.History.Count == 1 && f.Actual == "long");
        _repository.SddLifecycle.DocumentationDriftReviews.Should().ContainSingle("the earlier decision is never overwritten");
    }

    [Fact]
    public void Exact_content_moves_are_detected_within_a_project_lineage_only()
    {
        Import("a1", "proj"); Authored("a1", "Old/specs/001-x/data-model.md", Model(Person));
        Import("a2", "proj"); Authored("a2", "New/specs/001-x/data-model.md", Model(Person));
        Import("b1", "other"); Authored("b1", "Elsewhere/specs/001-x/data-model.md", Model(Person));
        var imports = DocumentationDrift.Imports(_repository.SddLifecycle);
        var moves = DocumentationQuality.Moves(Catalog, imports, imports.First(i => i.ImportId == "a1").LineageKey);
        moves.Should().ContainSingle().Which.Should().Match<DocumentMove>(m => m.FromPath.EndsWith("Old/specs/001-x/data-model.md") && m.ToPath.EndsWith("New/specs/001-x/data-model.md"));
        DocumentationQuality.Moves(Catalog, imports, imports.First(i => i.ImportId == "b1").LineageKey).Should().BeEmpty("another project is never an earlier revision");
    }

    [Fact]
    public void The_overview_compare_and_findings_views_run_bulk_review_and_report_without_manual_selection()
    {
        ControlProject();
        var view = "overview";
        var panel = Render<DocumentationDriftPanel>(p => p.Add(x => x.View, view).Add(x => x.OnViewChange, (string v) => view = v));
        panel.Find("[data-testid=dq-kpi-compared] dd").TextContent.Should().Be("0 / 7");
        panel.Find("[data-testid=dq-kpi-potential] dd").TextContent.Should().Be("Not assessed", "nothing was compared yet");
        panel.Find("[data-testid=dq-kpi-missing] dd").TextContent.Should().Be("2");
        panel.Find("[data-testid=dq-evidence-explain]").TextContent.Should().Contain("does not decide comparability");

        panel = Render<DocumentationDriftPanel>(p => p.Add(x => x.View, "compare"));
        panel.FindAll("[data-testid=dq-candidate][data-status=NotAssessed]").Should().HaveCount(7);
        panel.Find("[data-testid=dq-preview]").Click();
        panel.FindAll("[data-testid=dq-preview-list] li").Should().HaveCount(7);
        panel.Find("[data-testid=dq-run-all]").Click();
        panel.FindAll("[data-testid=dq-bulk-row][data-outcome=Compared]").Should().HaveCount(7);

        panel = Render<DocumentationDriftPanel>(p => p.Add(x => x.View, "findings"));
        panel.FindAll("[data-testid=dq-finding]").Should().HaveCount(3);
        var age = panel.FindAll("[data-testid=dq-finding]").Single(f => f.TextContent.Contains("Person.Age"));
        age.QuerySelector(".dq-finding-head")!.Click();
        panel.Find("[data-testid=dq-finding-detail]").TextContent.Should().Contain("int").And.Contain("string").And.Contain("TypeChange/autodoc/data-model.md");
        panel.Find("[data-testid=dq-reviewer]").Input("tester");
        panel.Find("[data-testid=dq-decision-FalsePositive]").Click();
        _repository.SddLifecycle.DocumentationDriftReviews.Should().ContainSingle(r => r.Decision == "FalsePositive" && r.Reviewer == "tester");
        panel.FindAll("[data-testid=dq-finding]").Should().HaveCount(2, "a false positive is no longer an open difference");

        panel = Render<DocumentationDriftPanel>(p => p.Add(x => x.View, "overview"));
        panel.Find("[data-testid=dq-report]").Click();
        panel.Instance.LastReport.Should().Contain("## 6. Potential drift").And.Contain("Person.Email").And.Contain("## 10. Human review status");
    }

    [Fact]
    public void Choosing_another_import_reports_it_for_the_URL_and_a_preselected_import_is_honoured()
    {
        ControlProject("i1");
        Import("i2", "other"); Authored("i2", "Solo/specs/001-s/data-model.md", Model(Person)); Generated("i2", "Solo", Model(Person));
        string? chosen = null;
        var panel = Render<DocumentationDriftPanel>(p => p.Add(x => x.View, "overview").Add(x => x.OnImportChange, (string id) => chosen = id));
        panel.Find("[data-testid=dq-import-select]").Change("i1");
        chosen.Should().Be("i1");
        panel.Find("[data-testid=dq-kpi-comparable] dd").TextContent.Should().Be("7");
        var preselected = Render<DocumentationDriftPanel>(p => p.Add(x => x.View, "overview").Add(x => x.PreselectImport, "i1"));
        preselected.Find("[data-testid=dq-kpi-comparable] dd").TextContent.Should().Be("7", "a reload with ?import= keeps the chosen import even when another import is current");
    }

    [Fact]
    public void The_changes_view_shows_moves_of_the_selected_imports_project_even_when_another_import_is_current()
    {
        Import("a1", "proj"); Authored("a1", "Old/specs/001-x/data-model.md", Model(Person));
        Import("a2", "proj"); Authored("a2", "New/specs/001-x/data-model.md", Model(Person));
        Import("b1", "other"); Authored("b1", "Elsewhere/specs/001-y/data-model.md", Model(Table("Other", ("Id", "Guid", "No", "Key"))));
        var panel = Render<DocumentationDriftPanel>(p => p.Add(x => x.View, "changes").Add(x => x.PreselectImport, "a2"));
        panel.Find("[data-testid=dq-move]").TextContent.Should().Contain("Old/specs/001-x/data-model.md").And.Contain("New/specs/001-x/data-model.md");
    }
}
