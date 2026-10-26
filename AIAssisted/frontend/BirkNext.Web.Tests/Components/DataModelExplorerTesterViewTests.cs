using BirkNext.DatabaseArchitecture;
using BirkNext.ProjectImport;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using BirkNext.Web.Services.Explorers;
using BirkNext.Web.Services.ProjectImport;
using BirkNext.Web.Tests.Pages;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using static BirkNext.Web.Tests.Services.ProjectImportActivationTests;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Data Model Explorer for testers: Feature model / Shared model / Combined view (derived, read-only); one logical structure per identity
/// with each origin's evidence; testable data rules and generic test conditions from explicit constraints only; reference data without
/// enums; source comparison collapsed, scoped, typed-alias aware (KjønnType ↔ KjoennType) and neutral; evidence origins kept apart.
/// </summary>
public sealed class DataModelExplorerTesterViewTests : BunitContext
{
    internal const string Feature = """
        # Data Model: Person Module Core

        ## 2. Core Entities

        ### 2.1 Person

        **Table**: `Person`

        | Column | Type | Nullable | Constraints | Notes |
        |--------|------|----------|-------------|-------|
        | PersonId | UNIQUEIDENTIFIER | No | PK | |
        | Foedselsnummer | NVARCHAR(11) | Yes | UNIQUE (filtered: NOT NULL), CHECK (LEN=11) | |
        | Navn | NVARCHAR(500) | No | NOT NULL | |
        | KjønnTypeId | UNIQUEIDENTIFIER | No | FK → KjønnType | |
        | Merknad | NVARCHAR(200) | | | |

        ### 2.2 BarnIAndrelinjeBarnevern

        **Table**: `BarnIAndrelinjeBarnevern`

        | Column | Type | Nullable | Constraints | Notes |
        |--------|------|----------|-------------|-------|
        | BarnRegistreringId | UNIQUEIDENTIFIER | No | PK | |
        | PersonId | UNIQUEIDENTIFIER | No | FK → Person, UNIQUE | |
        | BirkId | NVARCHAR(100) | No | UNIQUE | |
        | SikkerhetsnivaaTypeId | UNIQUEIDENTIFIER | No | FK → SikkerhetsnivaaType | |
        | KommuneNr | NVARCHAR(4) | No | FK → Kommune, CHECK (LEN=4) | |

        ## 3. Reference Data

        ### 3.1 KjønnType

        **Table**: `KjønnType`

        | Column | Type | Nullable | Constraints |
        |--------|------|----------|-------------|
        | KjønnTypeId | UNIQUEIDENTIFIER | No | PK |
        | Verdi | NVARCHAR(100) | No | UNIQUE |
        | ErAktiv | BIT | No | Default: 1 |
        | SorteringsRekkefoelge | INT | No | |

        ### 3.4 SikkerhetsnivaaType

        **Table**: `SikkerhetsnivaaType`

        | Column | Type | Nullable | Constraints |
        |--------|------|----------|-------------|
        | SikkerhetsnivaaTypeId | UNIQUEIDENTIFIER | No | PK |
        | Nivaa | INT | No | UNIQUE — critical for security comparisons |
        | BiRKKode | NVARCHAR(50) | Yes | |
        | KreverGradertTilgang | BIT | No | |

        ### 3.5 Kommune

        **Table**: `Kommune`

        | Column | Type | Nullable | Constraints |
        |--------|------|----------|-------------|
        | KommuneNr | NVARCHAR(4) | No | PK, CHECK (LEN=4) |
        | Navn | NVARCHAR(200) | No | |
        | ErAktiv | BIT | No | Default: 1 |
        """;

    internal const string Shared = """
        # Data Model

        ## Entities

        ### BarnIAndrelinjeBarnevern (table: BarnIAndrelinjeBarnevern)

        | Column | Type | Constraints |
        |--------|------|-------------|
        | BarnRegistreringId | Guid | PK |
        | PersonId | Guid | FK |
        | KommuneNr | string? | nullable |
        | EksternId | int? | nullable |

        ### KjoennType (table: KjoennType)

        | Column | Type | Constraints |
        |--------|------|-------------|
        | KjoennTypeId | Guid | PK |
        | Verdi | string | NOT NULL |

        ### InnmatingMetrikk (table: InnmatingMetrikk)

        | Column | Type | Constraints |
        |--------|------|-------------|
        | MetrikkId | Guid | PK |
        """;

    private readonly DataModelAnalysisService _parser = new();

    public DataModelExplorerTesterViewTests() => Services.AddSingleton<IDataModelAnalysisService>(_parser);

    // ── §85 Layer naming ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Layers_have_tester_names_while_internal_origins_stay_stable()
    {
        (DataModelLayers.Label(DataModelLayerOrigin.Pack), DataModelLayers.Label(DataModelLayerOrigin.SharedBase)).Should().Be(("Feature model", "Shared model"));
        DataModelLayers.CombinedLabel.Should().Be("Combined view");
        DataModelLayers.CombinedHelper.Should().Be("Read-only view of the feature model and shared model. It does not create a new authoritative data model.");
        var (aggregate, _) = DataModelLayers.Aggregate(_parser.Parse(Shared), _parser.Parse(Feature));
        aggregate.Title.Should().StartWith("Combined view:");
    }

    private readonly WorkspaceArtifactRepository _repository = new();
    private readonly MockSampleProjectDocumentResolver _samples = new();

    private async Task<IRenderedComponent<DataModelExplorer>> RenderPageAsync()
    {
        _samples.Repository = _repository;
        var explorers = new ArtifactExplorerContext(_repository, _samples, _samples);
        var preview = Preview(new string('e', 64), source: false,
            ("Person/autodoc/data-model.md", Shared), ("Person/specs/001-person-module/spec.md", Spec), ("Person/specs/001-person-module/data-model.md", Feature),
            ("Person/specs/002-other/spec.md", Spec.Replace("Cart", "Other")), ("Person/specs/002-other/data-model.md", "# Data Model: Other\n\n### Sibling\n\n| Field | Type | Nullable |\n|---|---|---|\n| Id | Guid | No |\n"));
        new ProjectImportActivation(_repository).Activate(preview, ProjectImportArtifactDiscovery.From(preview), Commit(preview, ProjectImportSourceState.NotDetected));
        await explorers.SelectPackAsync("001-person-module");

        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IWorkspaceSessionService>(_repository);
        Services.AddSingleton<ISampleProjectDocumentResolver>(_samples);
        Services.AddSingleton<BirkNext.Web.Services.SampleProjects.ISampleProjectArtifactDiscovery>(_samples);
        Services.AddSingleton<IArtifactExplorerContext>(explorers);
        Services.AddSingleton<IDataModelAnalysisService>(new DataModelAnalysisService());
        Services.AddSingleton<IReportExportService>(new ReportExportService());
        var cut = Render<DataModelExplorer>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=dme-layers]"));
        return cut;
    }

    [Fact]
    public async Task Page_names_layers_for_testers_keeps_combined_read_only_and_never_mixes_sibling_packs()
    {
        var cut = await RenderPageAsync();

        cut.Find("[data-testid=dme-layers]").TextContent.Should().Contain("Feature model").And.Contain("Shared model").And.Contain("Combined view")
            .And.NotContain("Pack model").And.NotContain("Derived aggregate");
        cut.Find("[data-testid=dme-layer-note]").TextContent.Should().Contain("This feature pack's authored data model.");
        cut.Find("[data-testid=artifact-explorer-availability]").TextContent.Should().Be("Feature model + shared model available");
        cut.FindAll("[data-testid=artifact-explorer-supporting]").Should().BeEmpty("the shared model is a layer, not a competing artifact");

        cut.Find("[data-testid=dme-layer-base]").Change("base");
        cut.Find("[data-testid=dme-layer-note]").TextContent.Should().Contain("Common data model used across feature packs in this module.")
            .And.Contain("Generated documentation — derived, not authoritative implementation evidence");

        cut.Find("[data-testid=dme-layer-aggregate]").Change("aggregate");
        cut.Find("[data-testid=dme-layer-note]").TextContent.Should().Be(DataModelLayers.CombinedHelper);
        cut.Find("[data-testid=dme-tab-model]").Click();
        cut.Find("[data-testid=dme-model-source]").TextContent.Should().Contain("Combined view — derived/read-only");
        cut.FindAll("[data-testid=dme-model-item]").Select(i => i.TextContent).Should().NotContain(t => t.Contains("Sibling"), "sibling pack 002 never leaks in");

        // §87: the source comparison is collapsed by default
        cut.Find("[data-testid=dme-source-evidence-toggle]").GetAttribute("aria-expanded").Should().Be("false");
        cut.Find("[data-testid=dme-source-evidence-toggle]").TextContent.Should().Contain("Compare with implementation source");
    }

    // ── §86 Combined origin ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Same_logical_structure_is_grouped_with_each_origin_kept_and_the_feature_definition_effective()
    {
        var shared = _parser.Parse(Shared);
        var feature = _parser.Parse(Feature);
        var structures = DataModelLayers.Structures(shared, feature);

        var barn = structures.Single(s => s.Name == "BarnIAndrelinjeBarnevern");
        barn.Origin.Should().Be(LogicalStructureOrigin.Both);
        barn.Effective.Should().BeSameAs(barn.Feature);
        barn.Shared!.Name.Should().Be("BarnIAndrelinjeBarnevern (table: BarnIAndrelinjeBarnevern)", "the shared evidence is kept as written");
        barn.Differences.Should().Contain(new OriginDifference("KommuneNr", "Feature model says required, shared model says optional"))
            .And.Contain(new OriginDifference("EksternId", "Only in the shared model")).And.Contain(new OriginDifference("BirkId", "Only in the feature model"));

        var kjonn = structures.Single(s => s.Name == "KjønnType");
        (kjonn.Origin, kjonn.Match).Should().Be((LogicalStructureOrigin.Both, DataModelNameMatch.NormalizedImplementationName));
        structures.Single(s => s.Name == "InnmatingMetrikk").OriginLabel.Should().Be("Shared model");
        structures.Single(s => s.Name == "Person").OriginLabel.Should().Be("Feature model");
        structures.Should().HaveCount(6, "five feature structures plus one shared-only — no duplicates");

        var (aggregate, origins) = DataModelLayers.Aggregate(shared, feature);
        aggregate.Entities.Select(e => e.Name).Should().NotContain(["BarnIAndrelinjeBarnevern (table: BarnIAndrelinjeBarnevern)", "KjoennType (table: KjoennType)"],
            "a shared definition with a feature counterpart is not listed twice");
        origins["BarnIAndrelinjeBarnevern"].Should().Be(DataModelLayerOrigin.OverriddenInPack);

        var cut = Render<DataModelExplorerPanel>(p => p.Add(x => x.ParsedDataModel, aggregate).Add(x => x.InitialView, "model").Add(x => x.Structures, structures)
            .Add(x => x.OriginOverride, DataModelLayers.CombinedOrigin).Add(x => x.BaseArtifactPath, "Person/autodoc/data-model.md"));
        cut.FindAll("[data-testid=dme-model-item]").Count(i => i.TextContent.Contains("BarnIAndrelinjeBarnevern")).Should().Be(1);
        cut.FindAll("[data-testid=dme-model-item]").Single(i => i.TextContent.Contains("BarnIAndrelinjeBarnevern")).Click();
        cut.Find("[data-testid=dme-model-detail] [data-testid=dme-model-layer]").TextContent.Should().Be("Feature + shared model");
        cut.Find("[data-testid=dme-origin-effective]").TextContent.Should().Contain("Feature definition used in combined view").And.Contain("Shared definition also available");
        cut.Find("[data-testid=dme-origin-shared]").TextContent.Should().Contain("Generated documentation").And.Contain("Only in the shared model");
        cut.FindAll("[data-testid=dme-model-item]").Single(i => i.TextContent.Contains("InnmatingMetrikk")).TextContent.Should().Contain("Shared model");
    }

    [Fact]
    public void Different_structures_are_never_grouped_on_similarity() =>
        DataModelIdentity.Match("BarnType", "BarnStatusType").Should().BeNull();

    // ── §88 Reference data ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Reference_data_comes_from_relationships_without_needing_an_enum()
    {
        var document = _parser.Parse(Feature);
        document.Enums.Should().BeEmpty();
        var references = DataModelTesterView.ReferenceData(document);
        references.Select(r => r.Name).Should().BeEquivalentTo(["KjønnType", "SikkerhetsnivaaType", "Kommune"]);
        var kjonn = references.Single(r => r.Name == "KjønnType");
        (kjonn.Key, kjonn.UniqueValue, kjonn.ActiveField, kjonn.SortField).Should().Be(("KjønnTypeId", "Verdi", "ErAktiv", "SorteringsRekkefoelge"));
        kjonn.UsedBy.Should().Equal("Person.KjønnTypeId");
        references.Single(r => r.Name == "SikkerhetsnivaaType").CodeFields.Should().Equal("BiRKKode");

        var cut = Render<DataModelExplorerPanel>(p => p.Add(x => x.ParsedDataModel, document).Add(x => x.InitialView, "reference"));
        cut.FindAll("[data-testid=dme-reference-item]").Select(i => i.GetAttribute("data-entity")).Should().BeEquivalentTo(["KjønnType", "SikkerhetsnivaaType", "Kommune"]);
        cut.Find("[data-testid=dme-reference-view]").TextContent.Should().Contain("inferred from field name").And.Contain("not turned into enums");
        Render<DataModelExplorerPanel>(p => p.Add(x => x.ParsedDataModel, document)).Find("[data-testid=dme-sum-reference]").TextContent.Should().Be("3");
    }

    // ── §89 Tester rules ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Rules_and_conditions_come_from_explicit_constraints_and_never_promise_an_outcome()
    {
        var document = _parser.Parse(Feature);
        var barn = document.Entities.Single(e => e.Name == "BarnIAndrelinjeBarnevern");
        var person = document.Entities.Single(e => e.Name == "Person");
        var rules = DataModelTesterView.Rules(barn, document).ToDictionary(r => r.Column.Name);

        rules["KommuneNr"].Summary.Should().Be("Required · Exactly 4 characters · references Kommune");
        rules["BirkId"].Summary.Should().Be("Required · Maximum 100 characters · Must be unique");
        rules["BirkId"].Identifier.Should().Be(IdentifierEvidence.Inferred, "BirkId is only named like an identifier");
        rules["BarnRegistreringId"].Identifier.Should().Be(IdentifierEvidence.Declared);
        rules["SikkerhetsnivaaTypeId"].Security.Should().Be(SecurityBasis.ReferencesSecurityStructure);
        var personRules = DataModelTesterView.Rules(person, document).ToDictionary(r => r.Column.Name);
        personRules["Merknad"].Requirement.Should().Be(FieldRequirement.Unknown, "unknown stays unknown");
        personRules["Foedselsnummer"].Summary.Should().Be("Optional · Exactly 11 characters · Must be unique when set");
        var security = DataModelTesterView.Rules(document.Entities.Single(e => e.Name == "SikkerhetsnivaaType"), document).ToDictionary(r => r.Column.Name);
        security["Nivaa"].Security.Should().Be(SecurityBasis.StatedInDocument);
        security["KreverGradertTilgang"].Security.Should().Be(SecurityBasis.FieldName);

        var conditions = DataModelTesterView.Conditions(barn, document).Concat(DataModelTesterView.Conditions(person, document)).ToList();
        conditions.Should().Contain(new TestCondition("Navn", "Test missing value", "Required"))
            .And.Contain(new TestCondition("BirkId", "Test duplicate value", "Must be unique"))
            .And.Contain(new TestCondition("Navn", "Test maximum length / over-limit value", "Maximum 500 characters"))
            .And.Contain(new TestCondition("KommuneNr", "Test exact length and a shorter / longer value", "Exactly 4 characters"))
            .And.Contain(new TestCondition("KommuneNr", "Test valid and invalid reference", "References Kommune"))
            .And.Contain(new TestCondition("KjønnTypeId", "Test active/inactive value", "KjønnType.ErAktiv"));
        conditions.Should().NotContain(c => c.Field == "Merknad" && c.Condition == "Test missing value", "an unknown requirement gives no condition");
        conditions.Select(c => c.Condition).Should().NotContain(c => c.Contains("should") || c.Contains("return") || c.Contains("400") || c.Contains("error"),
            "conditions never state an expected outcome");

        var cut = Render<DataModelExplorerPanel>(p => p.Add(x => x.ParsedDataModel, document).Add(x => x.InitialView, "model"));
        cut.FindAll("[data-testid=dme-model-item]").Single(i => i.GetAttribute("data-entity") == "BarnIAndrelinjeBarnevern").Click();
        cut.Find("[data-testid=dme-field-view-tester]").GetAttribute("aria-pressed").Should().Be("true", "the tester view is the default");
        cut.FindAll("[data-testid=dme-rule]").Single(r => r.GetAttribute("data-field") == "KommuneNr").TextContent.Should().Contain("Required · Exactly 4 characters · references Kommune");
        cut.FindAll("[data-testid=dme-rule]").Single(r => r.GetAttribute("data-field") == "BirkId").TextContent.Should().Contain("Likely identifier").And.Contain("Inferred from field name");
        cut.FindAll("[data-testid=dme-rule]").Single(r => r.GetAttribute("data-field") == "SikkerhetsnivaaTypeId").TextContent.Should().Contain("Security relevant").And.Contain("Not a vulnerability finding");
        cut.FindAll("[data-testid=dme-model-lines]").Should().BeEmpty("source lines are technical detail");
        cut.Find("[data-testid=dme-test-conditions]").TextContent.Should().Contain("not expected results").And.Contain("Test duplicate value");
        cut.Find("[data-testid=dme-model-relationships-friendly]").TextContent.Should().Contain("BarnIAndrelinjeBarnevern references Person").And.Contain("at most one BarnIAndrelinjeBarnevern per Person");
        cut.Find("[data-testid=dme-field-view-technical]").Click();
        cut.Find("[data-testid=dme-model-fields-table]");
        cut.Find("[data-testid=dme-model-lines]").TextContent.Should().StartWith("Line");
    }

    [Fact]
    public void Overview_is_the_default_and_zero_findings_never_reads_as_verified()
    {
        var document = _parser.Parse(Feature);
        var cut = Render<DataModelExplorerPanel>(p => p.Add(x => x.ParsedDataModel, document));
        cut.Find("[data-testid=dme-tab-overview]").GetAttribute("aria-selected").Should().Be("true");
        cut.FindAll(".dme-tab").Select(t => t.TextContent.Trim().Split(' ')[0]).Should().StartWith(["Overview", "Data", "Relationships", "Reference", "Traceability", "Findings"]);
        cut.Find("[data-testid=dme-sum-structures]").TextContent.Should().Be("5");
        cut.Find("[data-testid=dme-sum-unknown]").TextContent.Should().Be("1");
        if (document.FindingCount == 0)
            cut.Find("[data-testid=dme-findings-status]").TextContent.Should().Contain("No deterministic findings.").And.Contain("This does not mean the model has been fully verified.");
        cut.Find("[data-testid=dme-tab-findings]").Click();
        if (document.FindingCount == 0)
            cut.Find("[data-testid=dme-findings-none]").TextContent.Should().Contain("This does not mean the model has been fully verified.");
        cut.Find("[data-testid=dme-tab-indexes]").Click();
        cut.FindAll("[data-testid=dme-business-constraint]").Select(c => c.TextContent).Should().Contain("BirkId must be unique");
    }

    // ── §87 / §90 Source comparison and evidence hierarchy ───────────────────────────────────────

    private static DatabaseArchitectureSnapshot Source() => new()
    {
        SourceSnapshotId = Guid.NewGuid(),
        Databases =
        [
            new DatabaseModel { Id = "person", LogicalName = "Person", DbContext = "PersonDbContext", SourceProject = "M2LB.Person.Infrastructure", Schemas = [new SchemaModel { Name = "dbo", Tables =
            [
                new TableModel { Id = "p", LogicalName = "Person", PhysicalName = "Person", EntityTypeName = "Person", EvidenceState = DatabaseEvidenceState.Confirmed, Columns =
                [
                    new ColumnModel { Name = "PersonId", ClrType = "Guid", Nullable = false, IsPrimaryKey = true },
                    new ColumnModel { Name = "Navn", ClrType = "string", Nullable = true, MaxLength = 500 },
                    new ColumnModel { Name = "Foedselsnummer", ClrType = "string?", Nullable = true, MaxLength = 11 },
                    new ColumnModel { Name = "KjoennTypeId", ClrType = "Guid", Nullable = false, IsForeignKey = true },
                ] },
                new TableModel { Id = "k", LogicalName = "KjoennType", PhysicalName = "KjoennType", EntityTypeName = "KjoennType", EvidenceState = DatabaseEvidenceState.Confirmed,
                    Columns = [new ColumnModel { Name = "KjoennTypeId", ClrType = "Guid", Nullable = false, IsPrimaryKey = true }] },
            ] }] },
            new DatabaseModel { Id = "wolverine", LogicalName = "Wolverine", DbContext = "WolverineStore", SourceProject = "M2LB.Person.Api", Schemas = [new SchemaModel { Name = "wolverine", Tables =
            [
                new TableModel { Id = "w1", LogicalName = "wolverine_nodes", PhysicalName = "wolverine_nodes", EvidenceState = DatabaseEvidenceState.Confirmed },
                new TableModel { Id = "w2", LogicalName = "wolverine_dead_letters", PhysicalName = "wolverine_dead_letters", EvidenceState = DatabaseEvidenceState.Confirmed },
            ] }] },
            new DatabaseModel { Id = "billing", LogicalName = "Billing", DbContext = "BillingDbContext", SourceProject = "Billing", Schemas = [new SchemaModel { Name = "dbo", Tables =
                [new TableModel { Id = "i", LogicalName = "Invoice", PhysicalName = "Invoice", EntityTypeName = "Invoice", EvidenceState = DatabaseEvidenceState.Confirmed }] }] },
        ],
    };

    [Fact]
    public void Source_comparison_matches_the_documented_KjonnType_via_the_project_rule_scopes_and_stays_neutral()
    {
        var document = _parser.Parse(Feature);
        var cut = Render<DataModelEvidencePanel>(p => p.Add(x => x.Snapshot, Source()).Add(x => x.DocumentedEntities, document.Entities.Select(e => e.Name).ToList())
            .Add(x => x.DocumentedModel, document));

        cut.Find("[data-testid=data-model-evidence-documented]").TextContent.Should().Contain("2 of 5 documented structures found in source").And.Contain("1 via normalized implementation name");
        cut.Find("[data-testid=data-model-evidence-aliases]").TextContent.Should().Contain("KjønnType").And.Contain("KjoennType").And.Contain("Matched via normalized implementation name");
        cut.Find("[data-testid=data-model-evidence-not-matched]").TextContent.Should().NotContain("KjønnType").And.Contain("BarnIAndrelinjeBarnevern");
        cut.Find("[data-testid=data-model-comparison-scope]").TextContent.Should().Contain("Compared within PersonDbContext (2 source structures)")
            .And.Contain("1 structures in other stores").And.Contain("2 framework tables (e.g. Wolverine)");
        cut.Find("[data-testid=data-model-evidence-technical-toggle]").GetAttribute("aria-expanded").Should().Be("false", "the raw source dictionary is technical");
        cut.Find("[data-testid=data-model-comparison-differences-toggle]").GetAttribute("aria-expanded").Should().Be("false");
        var differences = cut.FindAll("[data-testid=data-model-comparison-difference]").Select(r => r.TextContent).ToList();
        differences.Should().Contain(d => d.Contains("Navn") && d.Contains("Authored says required, source says nullable") && d.Contains("Potential mismatch"));
        cut.Find("[data-testid=data-model-comparison]").TextContent.Should().Contain("A difference is something to review, not a defect")
            .And.Contain("not the deployed database schema").And.Contain("migrations declared are not migrations applied").And.NotContain("Database contains")
            .And.NotContain("inconsistenc");
    }

    [Fact]
    public void Generated_shared_model_authored_model_and_source_evidence_stay_distinct()
    {
        var shared = Render<DataModelExplorerPanel>(p => p.Add(x => x.ParsedDataModel, _parser.Parse(Shared)).Add(x => x.ArtifactPath, "Person/autodoc/data-model.md").Add(x => x.InitialView, "model"));
        shared.Find("[data-testid=dme-model-source]").TextContent.Should().Contain("Generated documentation — derived, not authoritative implementation evidence");
        var feature = Render<DataModelExplorerPanel>(p => p.Add(x => x.ParsedDataModel, _parser.Parse(Feature)).Add(x => x.ArtifactPath, "Person/specs/001-person-module/data-model.md").Add(x => x.InitialView, "model"));
        feature.Find("[data-testid=dme-model-source]").TextContent.Should().Contain("Authored document").And.NotContain("Generated");
        (shared.Markup + feature.Markup).Should().NotContain("Database contains").And.NotContain("Deployed schema");

        var (aggregate, _) = DataModelLayers.Aggregate(_parser.Parse(Shared), _parser.Parse(Feature));
        var html = new ReportExportService().ExportDataModel(aggregate, "M2LB", "Combined view — derived/read-only");
        html.Should().Contain("Combined view — derived/read-only");
    }
}
