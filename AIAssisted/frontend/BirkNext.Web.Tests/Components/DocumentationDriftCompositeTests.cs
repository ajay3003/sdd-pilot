using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Composite authored intent ↔ generated documentation. Several authored Data Models of one module form one authored intent (a union with
/// provenance, never a concatenation and never "newest wins"); it is compared concept by concept with the module's generated Data Model.
/// Contradictory authored statements are an authored conflict, never a generated-drift verdict; absence on either side is neutral; ambiguity
/// is never auto-resolved; saved comparisons keep the exact contributor fingerprints. Person/Hendelse are generic module names here.
/// </summary>
public sealed class DocumentationDriftCompositeTests : BunitContext
{
    private readonly WorkspaceArtifactRepository _repository = new();
    private static readonly DataModelAnalysisService Parser = new();
    private int _revision;

    public DocumentationDriftCompositeTests()
    {
        Services.AddSingleton(_repository);
        Services.AddSingleton<IWorkspaceArtifactRepository>(_repository);
        Services.AddSingleton(Mock.Of<IWorkspaceAutoSaveService>(s => s.SaveNowAsync() == Task.FromResult(true)));
    }

    private static string Table(string entity, params (string Field, string Type, string Nullable, string Description)[] fields) =>
        $"### {entity}\n\n| Field | Type | Nullable | Description |\n|---|---|---|---|\n" + string.Concat(fields.Select(f => $"| {f.Field} | {f.Type} | {f.Nullable} | {f.Description} |\n")) + "\n";

    private static string Model(params string[] tables) => "# Data Model\n\n## Entities\n\n" + string.Concat(tables);

    private static readonly string Person = Table("Person", ("Id", "Guid", "No", "Key"), ("Navn", "string", "No", "Name"));
    private static readonly string BarnType = Table("BarnType", ("Id", "Guid", "No", "Key"), ("Status", "string", "Yes", "State"));

    private void ImportRecord(string importId, string project = "m2lb", DateTimeOffset? at = null)
    {
        var state = _repository.SddLifecycle;
        state.ProjectImports.Add(new SddProjectImportRecord { ImportId = importId, ProjectName = project, ProjectNameBasis = "ArchiveRoot", ArchiveFileName = project + ".zip", ImportedAt = at ?? DateTimeOffset.UnixEpoch, SourceSnapshotId = Guid.NewGuid() });
        state.CurrentProjectImportId = importId;
    }

    private void Authored(string importId, string path, string text, string role = "DataModel") =>
        _repository.SddLifecycle.Revisions.Add(new SddArtifactRevision
        {
            RevisionId = Guid.NewGuid(), Role = role, FileName = path, SourceReference = path, Content = text, Fingerprint = "fp-" + (++_revision) + "-" + text.GetHashCode().ToString("x"),
            Revision = 1, WorkspaceScope = "import:" + importId, ProjectImportId = importId,
        });

    private void Generated(string importId, string module, string text, string file = "data-model.md", string kind = "DataModel")
    {
        var state = _repository.SddLifecycle;
        var fingerprint = $"gen-{importId}-{module}-{file}-{text.GetHashCode():x}";
        var set = state.GeneratedDocumentationSets.FirstOrDefault(s => s.ProjectImportId == importId);
        if (set is null) state.GeneratedDocumentationSets.Add(set = new SddGeneratedDocumentationSet { SetId = "set-" + importId, ProjectImportId = importId, State = "Present" });
        set.Artifacts.Add(new SddGeneratedDocumentArtifact { ModuleId = "module:" + module.ToLowerInvariant(), ModuleRoot = module, RelativePath = $"{module}/autodoc/{file}", Kind = kind, Fingerprint = fingerprint, Generator = "maintain-docs", ContentCaptured = true });
        state.GeneratedDocumentContents[fingerprint] = text;
    }

    private IReadOnlyList<DriftDocument> Catalog() => DocumentationDrift.Catalog(_repository.SddLifecycle);
    private IReadOnlyList<DriftCandidateGroup> Groups(string importId = "i1") => DocumentationDriftComposite.CandidateGroups(Catalog(), _repository.SddLifecycle.DocumentationDriftComparisons, importId);
    private DriftCandidateGroup Group(string module = "Person", string importId = "i1") => Groups(importId).Single(g => g.Module == module);

    private DataModelConsistencyResult Compare(DriftCandidateGroup g, string? onlyContributor = null)
    {
        var state = _repository.SddLifecycle;
        var contributors = g.Contributors.Where(c => onlyContributor is null || c.Id == onlyContributor).Select(c => (c, DocumentationDrift.Text(state, c)!)).ToList();
        var intent = DocumentationDriftComposite.BuildDataModel(g.ImportId, g.Module, contributors, Parser);
        return DocumentationDriftComposite.CompareDataModel(intent, g.Generated!, DocumentationDrift.Text(state, g.Generated!)!, Parser, onlyContributor is not null);
    }

    private void PersonModule(string generated, params (string Path, string Text)[] authored)
    {
        ImportRecord("i1");
        foreach (var (path, text) in authored) Authored("i1", path, text);
        Generated("i1", "Person", generated);
    }

    // 1. One authored / one generated.
    [Fact]
    public void One_authored_one_generated_is_one_contributor_and_compares()
    {
        PersonModule(Model(Person), ("Person/specs/001-person-module/data-model.md", Model(Person)));
        var g = Group();
        g.State.Should().Be(DriftGroupState.ComparisonAvailable);
        g.Contributors.Should().ContainSingle();
        var r = Compare(g);
        r.Count(ConsistencyCategory.Consistent).Should().Be(1);
        r.Discrepancies.Should().Be(0);
    }

    // 2, 3, 4. Multiple authored contributors; a later feature adds — it never replaces what it does not mention.
    [Fact]
    public void Multiple_authored_contribute_additively_and_omission_never_removes()
    {
        PersonModule(Model(Person, BarnType),
            ("Person/specs/001-person-module/data-model.md", Model(Person)),
            ("Person/specs/002-barn-parent-feilkode/data-model.md", Model(BarnType)));
        var g = Group();
        g.Contributors.Select(DocumentationDrift.StablePathOf).Should().Equal("Person/specs/001-person-module/data-model.md", "Person/specs/002-barn-parent-feilkode/data-model.md");
        var intent = Compare(g).Intent;
        intent.Entities.Select(e => e.Name).Should().BeEquivalentTo(["BarnType", "Person"], "002 adds BarnType; Person from 001 is kept although 002 does not mention it");
        intent.Entities.Single(e => e.Name == "Person").Provenance.Should().OnlyContain(p => p.Path.Contains("001-person-module"));
    }

    // 5. Explicit modification: deterministic replacement, both statements kept.
    [Fact]
    public void Explicit_change_in_a_later_contribution_replaces_with_both_provenance_records()
    {
        PersonModule(Model(BarnType),
            ("Person/specs/001-person-module/data-model.md", Model(BarnType)),
            ("Person/specs/002-barn-parent-feilkode/data-model.md", Model(Table("BarnType", ("Status", "string", "No", "Status is now required (changed from nullable)")))));
        var intent = Compare(Group()).Intent;
        intent.Conflicts.Should().BeEmpty();
        var replacement = intent.Replacements.Should().ContainSingle().Subject;
        replacement.Concept.Should().Be("BarnType.Status");
        replacement.Previous.Provenance.Path.Should().Contain("001-person-module");
        replacement.Replacing.Provenance.Path.Should().Contain("002-barn-parent-feilkode");
        intent.Entities.Single().Fields.Single(f => f.Name == "Status").Requirement.Should().Be(FieldRequirement.Required);
    }

    // 6. Conflict without replacement semantics: authored conflict, no generated verdict.
    [Fact]
    public void Contradiction_without_replacement_is_an_authored_conflict_and_not_a_drift_verdict()
    {
        PersonModule(Model(Table("BarnType", ("Id", "Guid", "No", "Key"), ("Status", "string", "No", "State"))),
            ("Person/specs/001-person-module/data-model.md", Model(BarnType)),
            ("Person/specs/002-barn-parent-feilkode/data-model.md", Model(Table("BarnType", ("Status", "string", "No", "State")))));
        var r = Compare(Group());
        var conflict = r.Intent.Conflicts.Should().ContainSingle().Subject;
        conflict.Concept.Should().Be("BarnType.Status");
        conflict.Statements.Select(s => s.Provenance.Path).Should().Contain(p => p.Contains("001")).And.Contain(p => p.Contains("002"));
        r.Rows.Should().ContainSingle(x => x.Concept == "BarnType.Status").Which.Category.Should().Be(ConsistencyCategory.NotConcludedAuthoredConflict);
        r.Rows.Should().NotContain(x => x.Concept == "BarnType.Status" && x.Category == ConsistencyCategory.ConstraintDifference);
    }

    // 7, 8. Generated-only and authored-only are neutral documentation-consistency facts.
    [Fact]
    public void Generated_only_and_authored_only_are_never_defect_claims()
    {
        PersonModule(Model(Person, Table("OutboxMessage", ("Id", "Guid", "No", "Key"))),
            ("Person/specs/001-person-module/data-model.md", Model(Person, BarnType)));
        var r = Compare(Group());
        r.Rows.Single(x => x.Concept == "OutboxMessage").Category.Should().Be(ConsistencyCategory.GeneratedOnly);
        r.Rows.Single(x => x.Concept == "OutboxMessage").Explanation.Should().Contain("Not automatically a missing-specification defect");
        var authoredOnly = r.Rows.Single(x => x.Concept == "BarnType");
        authoredOnly.Category.Should().Be(ConsistencyCategory.AuthoredOnly);
        authoredOnly.Explanation.Should().Contain("not proof the entity is absent from source code");
    }

    // 9, 10, 11. Wrong module, wrong document type and another import never enter the composite.
    [Fact]
    public void Other_modules_document_types_and_imports_never_enter_the_composite()
    {
        ImportRecord("i0", at: DateTimeOffset.UnixEpoch);
        Authored("i0", "Person/specs/000-old/data-model.md", Model(Person));
        ImportRecord("i1", at: DateTimeOffset.UnixEpoch.AddDays(1));
        Authored("i1", "Person/specs/001-person-module/data-model.md", Model(Person));
        Authored("i1", "Hendelse/specs/001-hendelse/data-model.md", Model(Table("Hendelse", ("Id", "Guid", "No", "Key"))));
        Authored("i1", "Person/specs/001-person-module/plan.md", "# Plan\n\n## Phase A\n", role: "Plan");
        Authored("i1", "docs/data-model.md", Model(Person));
        Generated("i1", "Person", Model(Person));
        var g = Group();
        g.Contributors.Select(DocumentationDrift.StablePathOf).Should().Equal("Person/specs/001-person-module/data-model.md");
    }

    // 12, 13. Absence on either side is neutral and unavailable.
    [Fact]
    public void Missing_generated_or_authored_side_is_neutral_and_unavailable()
    {
        ImportRecord("i1");
        Authored("i1", "Person/specs/001-person-module/data-model.md", Model(Person));
        Generated("i1", "Hendelse", Model(Person));
        Group("Person").State.Should().Be(DriftGroupState.GeneratedNotPresent);
        Group("Person").StateLabel.Should().Contain("unavailable").And.NotContainAny("Missing", "Failed", "defect");
        Group("Hendelse").State.Should().Be(DriftGroupState.AuthoredNotPresent);
    }

    // 14. Two generated Data Models for one module + concern: review required, none chosen.
    [Fact]
    public void Multiple_generated_candidates_are_review_required()
    {
        PersonModule(Model(Person), ("Person/specs/001-person-module/data-model.md", Model(Person)));
        Generated("i1", "Person", Model(Person, BarnType), file: "entities.md");
        var g = Group();
        g.State.Should().Be(DriftGroupState.MultipleGeneratedCandidates);
        g.Generated.Should().BeNull("no automatic winner");
        g.StateLabel.Should().Be("Review required — multiple generated candidates");
    }

    // 15, 16. Saved comparisons keep exact contributor fingerprints; a new import builds a new composite and the old record stays.
    [Fact]
    public void Saved_composite_is_immutable_and_a_new_import_builds_a_new_composite()
    {
        PersonModule(Model(Person), ("Person/specs/001-person-module/data-model.md", Model(Person)));
        var first = Compare(Group());
        var saved = DocumentationDriftComposite.Save(_repository.SddLifecycle, first, null, DateTimeOffset.UnixEpoch);
        var fingerprints = saved.Contributors.Select(c => c.Fingerprint).ToList();
        saved.ComparisonKind.Should().Be(DocumentationDriftComposite.StructuredDataModel);
        saved.CompositeFingerprint.Should().Be(first.Intent.Fingerprint);
        Group().Status.Should().Be("Consistent");

        ImportRecord("i2", at: DateTimeOffset.UnixEpoch.AddDays(1));
        Authored("i2", "Person/specs/001-person-module/data-model.md", Model(Person));
        Authored("i2", "Person/specs/002-barn-parent-feilkode/data-model.md", Model(BarnType));
        Generated("i2", "Person", Model(Person, BarnType));
        var second = Compare(Group(importId: "i2"));
        second.Intent.Fingerprint.Should().NotBe(first.Intent.Fingerprint);
        Group(importId: "i2").Status.Should().Be("Comparison candidate", "the saved comparison belongs to the earlier contributors");
        _repository.SddLifecycle.DocumentationDriftComparisons.Single().Contributors.Select(c => c.Fingerprint).Should().Equal(fingerprints);
    }

    // 17. Formatting/order differences are not semantic discrepancies.
    [Fact]
    public void Formatting_and_order_differences_are_not_discrepancies()
    {
        var reordered = Model(Table("Person", ("Navn", "STRING", "No", "Name of the person"), ("Id", " guid ", "No", "Identifier")));
        PersonModule(reordered, ("Person/specs/001-person-module/data-model.md", Model(Person)));
        var r = Compare(Group());
        r.Discrepancies.Should().Be(0);
        r.Count(ConsistencyCategory.Consistent).Should().Be(1);
    }

    // 20. Specific contribution: only the concepts it states.
    [Fact]
    public void Specific_contribution_compares_only_its_own_concepts()
    {
        PersonModule(Model(Person, BarnType, Table("OutboxMessage", ("Id", "Guid", "No", "Key"))),
            ("Person/specs/001-person-module/data-model.md", Model(Person)),
            ("Person/specs/002-barn-parent-feilkode/data-model.md", Model(BarnType)));
        var g = Group();
        var only002 = g.Contributors.Single(c => c.Path.Contains("002")).Id;
        var r = Compare(g, only002);
        r.Intent.Entities.Select(e => e.Name).Should().Equal("BarnType");
        r.Rows.Should().NotContain(x => x.Category == ConsistencyCategory.GeneratedOnly, "unrelated generated concepts are out of scope for one contribution");
        r.Rows.Should().NotContain(x => x.Concept.StartsWith("Person"));
    }

    // 19. Overview grouping: several authored Data Models → one module card.
    [Fact]
    public void Drift_tab_shows_one_card_per_module_and_concern()
    {
        PersonModule(Model(Person, BarnType),
            ("Person/specs/001-person-module/data-model.md", Model(Person)),
            ("Person/specs/002-barn-parent-feilkode/data-model.md", Model(BarnType)));
        var cut = Render<DocumentationDriftPanel>(p => p.Add(x => x.View, "drift"));
        var card = cut.FindAll("[data-testid=dd-candidate-group]").Should().ContainSingle().Subject;
        card.QuerySelector(".dd-card-title")!.TextContent.Should().Be("Person");
        card.QuerySelector(".dd-card-area")!.TextContent.Should().Be("Data Model");
        card.QuerySelector("[data-testid=dd-card-authored]")!.TextContent.Should().Be("2 docs");
        card.QuerySelector("[data-testid=dd-card-generated]")!.TextContent.Should().Be("Available");
        card.QuerySelectorAll("[data-testid=dd-card-status]").Should().ContainSingle("one primary state").Which.TextContent.Should().Be("Ready to compare");
        card.QuerySelector("[data-testid=dd-card-secondary]")!.TextContent.Should().Be("Not yet reviewed");
        // Raw paths and the technical state stay available, under Show evidence (collapsed).
        var evidence = card.QuerySelector("[data-testid=dd-card-evidence]")!;
        evidence.HasAttribute("open").Should().BeFalse();
        evidence.TextContent.Should().Contain("2 related authored documents").And.Contain("Person/autodoc/data-model.md").And.Contain("Consistency comparison available");
        cut.FindAll("[data-testid=dd-candidate]").Should().BeEmpty("no per-file cards for a structured concern");
    }

    // UI: default composite mode, contributors, generated provenance, conflict section, technical details collapsed, save.
    [Fact]
    public void Compare_defaults_to_composite_and_shows_contributors_conflicts_and_technical_details_collapsed()
    {
        PersonModule(Model(Table("BarnType", ("Status", "string", "No", "State"))),
            ("Person/specs/001-person-module/data-model.md", Model(BarnType)),
            ("Person/specs/002-barn-parent-feilkode/data-model.md", Model(Table("BarnType", ("Status", "string", "No", "State")))));
        var cut = Render<DocumentationDriftPanel>(p => p.Add(x => x.View, "compare"));
        cut.Find("[data-testid=dd-mode]").GetAttribute("value");
        cut.FindAll("[data-testid=dd-composite]").Should().ContainSingle("composite is the default where a candidate exists");
        cut.Find("[data-testid=dd-composite-run]").HasAttribute("disabled").Should().BeTrue("nothing is chosen yet");
        cut.Find("[data-testid=dd-composite-group]").Change(Group().Key);
        cut.FindAll("[data-testid=dd-contributor]").Should().HaveCount(2);
        cut.Find("[data-testid=dd-authored-intent-summary]").TextContent.Should().Be("Combined from 2 relevant authored documents");
        cut.Find("[data-testid=dd-generated-path]").TextContent.Should().Contain("Person/autodoc/data-model.md");
        cut.Find("[data-testid=dd-composite-technical]").HasAttribute("open").Should().BeFalse("snapshot ids and fingerprints are technical details");
        cut.Find("[data-testid=dd-composite-run]").Click();
        cut.Find("[data-testid=dd-authored-conflicts]").TextContent.Should().Contain("BarnType.Status").And.Contain("Not concluded");
        cut.Find("[data-testid=dd-composite-save]").Click();
        _repository.SddLifecycle.DocumentationDriftComparisons.Should().ContainSingle().Which.Contributors.Should().HaveCount(2);
    }

    // UI: separate Module and Documentation area selectors with an availability card; a single available area is preselected.
    [Fact]
    public void Module_and_documentation_area_are_separate_selectors_with_availability()
    {
        PersonModule(Model(Person, BarnType), ("Person/specs/001-person-module/data-model.md", Model(Person)));
        var cut = Render<DocumentationDriftPanel>(p => p.Add(x => x.View, "compare"));
        cut.Find("[data-testid=dd-composite-area]").HasAttribute("disabled").Should().BeTrue("choose a module first");
        cut.Find("[data-testid=dd-composite-module]").Change("Person");
        cut.Find("[data-testid=dd-composite-area] option[selected]").GetAttribute("value").Should().Be(Group().Key);
        var availability = cut.Find("[data-testid=dd-availability]");
        availability.GetAttribute("data-state").Should().Be(nameof(DriftGroupState.ComparisonAvailable));
        availability.TextContent.Should().Contain("1 document").And.Contain("Present").And.Contain("Available");
        cut.Find("[data-testid=dd-composite-run]").HasAttribute("disabled").Should().BeFalse();
        cut.Find("[data-testid=dd-composite-run]").Click();
        cut.FindAll("[data-testid=dd-result-group]").Should().NotBeEmpty("rows are grouped by entity");
        cut.FindAll("[data-testid=dd-result-group]").Select(g => g.GetAttribute("data-group")).Should().OnlyHaveUniqueItems();
    }

    // UI: compare disabled when a side is absent.
    [Fact]
    public void Compare_is_disabled_when_the_generated_side_is_absent()
    {
        ImportRecord("i1");
        Authored("i1", "Person/specs/001-person-module/data-model.md", Model(Person));
        Generated("i1", "Hendelse", Model(Person));
        var cut = Render<DocumentationDriftPanel>(p => p.Add(x => x.View, "compare"));
        cut.Find("[data-testid=dd-mode]").Change(DriftIntent.CompositeConsistency.ToString());
        cut.Find("[data-testid=dd-composite-group]").Change(Group("Person").Key);
        cut.Find("[data-testid=dd-composite-state]").TextContent.Should().Contain("generated documentation not present");
        cut.Find("[data-testid=dd-composite-run]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=dd-generated-missing]").TextContent.Should().Contain("neutral state");
        cut.Find("[data-testid=dd-generated-missing-health]").GetAttribute("href").Should().EndWith("?import=i1");
    }

    // Drift UX: summary counts, status filters with aria-pressed and counts, search + clear, sort, and missing-evidence actions with context.
    [Fact]
    public void Drift_tab_filters_searches_sorts_and_links_missing_evidence()
    {
        PersonModule(Model(Person), ("Person/specs/001-person-module/data-model.md", Model(Person)));
        Authored("i1", "Frontend/specs/001-frontend/data-model.md", Model(Person));
        Generated("i1", "Hendelse", Model(Person));
        var cut = Render<DocumentationDriftPanel>(p => p.Add(x => x.View, "drift"));
        cut.FindAll("[data-testid=dd-candidate-group]").Select(c => c.QuerySelector(".dd-card-title")!.TextContent).Should().Equal(new[] { "Person", "Frontend", "Hendelse" }, "priority order: ready before missing evidence");
        cut.FindAll("[data-testid=dd-drift-count]").Single(c => c.GetAttribute("data-state") == "ReadyToCompare").TextContent.Should().StartWith("1");

        var filters = cut.FindAll("[data-testid=dd-drift-filter]");
        filters[0].TextContent.Should().Be("All (3)");
        filters[0].GetAttribute("aria-pressed").Should().Be("true");
        cut.FindAll("[data-testid=dd-drift-filter]").Single(f => f.GetAttribute("data-state") == "GeneratedMissing").Click();
        cut.FindAll("[data-testid=dd-drift-filter]").Single(f => f.GetAttribute("data-state") == "GeneratedMissing").GetAttribute("aria-pressed").Should().Be("true");
        var missing = cut.FindAll("[data-testid=dd-candidate-group]").Should().ContainSingle().Subject;
        missing.QuerySelector("[data-testid=dd-card-status]")!.TextContent.Should().Be("Generated missing");
        missing.QuerySelector("[data-testid=dd-card-resolve]")!.GetAttribute("href").Should().Be($"{NavigationCatalog.GeneratedDocumentationHealthRoute}?import=i1");
        missing.QuerySelector("[data-testid=dd-group-open]").Should().BeNull("missing evidence is never compared");
        cut.Find("[data-testid=dd-drift-visible]").TextContent.Should().Be("1 of 3 module areas shown");

        cut.FindAll("[data-testid=dd-drift-filter]")[0].Click();
        cut.Find("[data-testid=dd-drift-search]").Input("hendel");
        cut.FindAll("[data-testid=dd-candidate-group]").Should().ContainSingle().Which.QuerySelector("[data-testid=dd-card-resolve]")!.GetAttribute("href").Should().Be("data-model-explorer");
        cut.Find("[data-testid=dd-drift-search]").Input("no-such-module");
        cut.Find("[data-testid=dd-drift-none]").TextContent.Should().StartWith("No module area matches");
        cut.Find("[data-testid=dd-drift-clear]").Click();
        cut.FindAll("[data-testid=dd-candidate-group]").Should().HaveCount(3);

        cut.Find("[data-testid=dd-drift-sort]").Change(nameof(DriftCardSort.Module));
        cut.FindAll("[data-testid=dd-candidate-group]").Select(c => c.QuerySelector(".dd-card-title")!.TextContent).Should().Equal("Frontend", "Hendelse", "Person");
        _repository.SddLifecycle.DocumentationDriftComparisons.Should().BeEmpty("filtering never compares");
    }

    // Overview: summary cards, review-next list, and saved differences scoped to the selected import only.
    [Fact]
    public void Overview_review_next_and_differences_are_scoped_to_the_selected_import()
    {
        PersonModule(Model(Person, BarnType), ("Person/specs/001-person-module/data-model.md", Model(Person)));
        DocumentationDriftComposite.Save(_repository.SddLifecycle, Compare(Group()), null, DateTimeOffset.UnixEpoch);
        ImportRecord("i2", "other", DateTimeOffset.UnixEpoch.AddDays(1));
        Authored("i2", "Person/specs/001-person-module/data-model.md", Model(Person));
        Generated("i2", "Person", Model(Person));
        var cut = Render<DocumentationDriftPanel>(p => p.Add(x => x.View, "overview"));
        cut.Find("[data-testid=dd-overview-import]").Change("i2");
        cut.Find("[data-testid=dd-dash-comparable]").TextContent.Should().Be("1");
        cut.Find("[data-testid=dd-dash-waiting]").TextContent.Should().Be("1");
        cut.Find("[data-testid=dd-dash-differences]").TextContent.Should().Be("0", "the saved difference belongs to the other import");
        cut.Find("[data-testid=dd-dash-next-item]").GetAttribute("data-card-state").Should().Be(nameof(DriftCardState.ReadyToCompare));

        cut.Find("[data-testid=dd-overview-import]").Change("i1");
        cut.Find("[data-testid=dd-dash-differences]").TextContent.Should().Be("1");
        cut.Find("[data-testid=dd-dash-waiting]").TextContent.Should().Be("0");
        cut.Find("[data-testid=dd-dash-next-item]").GetAttribute("data-card-state").Should().Be(nameof(DriftCardState.Difference));
    }

    // Changes: one import → summary says revision comparison is unavailable and offers importing another version.
    [Fact]
    public void Changes_with_one_import_shows_an_empty_state_with_the_next_step()
    {
        PersonModule(Model(Person), ("Person/specs/001-person-module/data-model.md", Model(Person)));
        var cut = Render<DocumentationDriftPanel>(p => p.Add(x => x.View, "changes"));
        cut.Find("[data-testid=dd-changes-sum-imports]").TextContent.Should().Be("1");
        cut.Find("[data-testid=dd-changes-sum-state]").TextContent.Should().Be("Unavailable");
        cut.Find("[data-testid=dd-changes-sum-reason]").TextContent.Should().Be("Only one import exists");
        cut.Find("[data-testid=dd-changes-empty]").GetAttribute("role").Should().Be("status");
        cut.Find("[data-testid=dd-changes-import]").TextContent.Should().Be("Import another version of m2lb");
    }

    // Compare: area options carry authored/generated counts and the canonical card state; help is a collapsed disclosure.
    [Fact]
    public void Compare_area_options_show_counts_and_state_and_help_is_collapsed()
    {
        PersonModule(Model(Person), ("Person/specs/001-person-module/data-model.md", Model(Person)), ("Person/specs/002-x/data-model.md", Model(Person)));
        var cut = Render<DocumentationDriftPanel>(p => p.Add(x => x.View, "compare"));
        cut.Find("[data-testid=dd-how-compare]").HasAttribute("open").Should().BeFalse();
        cut.Find("[data-testid=dd-composite-module]").Change("Person");
        cut.Find("[data-testid=dd-composite-area] option[selected]").TextContent.Should().Be("Data Model — 2 authored · 1 generated · Ready to compare");
    }
}
